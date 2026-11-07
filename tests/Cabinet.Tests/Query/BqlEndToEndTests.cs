using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Cabinet.Core.Configuration;

namespace Cabinet.Tests.Query;

/// <summary>
/// Runs queries through the real parser, optimizer, engine and storage against events produced
/// by the real log parsers. Hand-built fixtures hid bugs here because they didn't have the field
/// names real ingestion produces. Every backend must give the same answers.
/// </summary>
public abstract class BqlEndToEndTests : IAsyncLifetime
{
    private static readonly string[] Lines =
    [
        """{"timestamp":"2026-01-01T10:00:00Z","level":"ERROR","message":"disk full","host":"web1","status":500,"user":"Alice"}""",
        """{"timestamp":"2026-01-01T10:01:00Z","level":"INFO","message":"request ok","host":"web2","status":200,"user":"bob"}""",
        """{"timestamp":"2026-01-01T10:02:00Z","level":"WARN","message":"slow request","host":"web1","status":200,"user":"bob"}""",
        """{"timestamp":"2026-01-01T10:03:00Z","level":"ERROR","message":"bad gateway","host":"db1","status":502,"user":"carol"}""",
        """{"timestamp":"2026-01-01T10:04:00Z","level":"INFO","message":"newest event","host":"web2","status":200,"user":"carol"}""",
    ];

    private BqlQueryEngine _engine = null!;
    private ILogStorage _storage = null!;
    private List<LogEvent> _seeded = [];

    protected abstract bool IsAvailable { get; }

    protected abstract ILogStorage CreateStorage();

    /// <summary>Isolates this run from data other runs left in a shared backend.</summary>
    protected string RunIndex { get; } = $"e2e-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
            return;

        var storage = CreateStorage();
        _storage = storage;
        await storage.InitializeAsync();

        var parser = new JsonLogParser();
        var events = new List<LogEvent>();
        foreach (var line in Lines)
        {
            var parsed = (await parser.ParseAsync(line))!;
            parsed.Index = RunIndex;
            events.Add(parsed);
        }

        var stored = await storage.StoreBatchAsync(events);
        stored.Should().Be(events.Count, "every seeded event must be stored");
        _seeded = events;
        await OnSeededAsync();
        _engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
    }

    protected virtual Task OnSeededAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private void RequireBackend() =>
        Skip.IfNot(IsAvailable, "Backend not available; set its CABINET_TEST_* variable to enable.");

    private async Task<List<IDictionary<string, object?>>> RunAsync(string query)
    {
        RequireBackend();
        var result = await _engine.ExecuteAsync(new QueryExecutionContext
        {
            Query = query,
            Limit = 1000,
            Index = RunIndex,
        });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    private async Task<long> CountAsync(string query)
    {
        var rows = await RunAsync($"{query} | stats count");
        return Convert.ToInt64(rows.Single()["count"]);
    }

    private static string? Text(IDictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out var value) ? value?.ToString() : null;

    // ─── Idempotent writes: a sender retrying a batch must not create duplicates ───

    [SkippableFact]
    public async Task StoreBatch_SameIdsAgain_StoresNothingNew()
    {
        RequireBackend();

        var storedAgain = await _storage.StoreBatchAsync(_seeded);
        await OnSeededAsync();

        storedAgain.Should().Be(0);
        (await CountAsync("find *")).Should().Be(_seeded.Count);
    }

    [SkippableFact]
    public async Task StoreBatch_SameIdWithDifferentContent_KeepsTheOriginal()
    {
        RequireBackend();

        var original = _seeded[0];
        var replacement = new LogEvent
        {
            Id = original.Id,
            RawLine = "overwritten",
            Timestamp = original.Timestamp,
            Index = original.Index,
        };

        await _storage.StoreBatchAsync([replacement]);
        await OnSeededAsync();

        (await CountAsync("find overwritten")).Should().Be(0);
    }

    // ─── Field names: what the guide documents must find what ingestion stores ───

    [SkippableFact]
    public async Task StatsByLowercaseHost_GroupsByRealHost()
    {
        var rows = await RunAsync("find * | stats count by host");

        rows.ToDictionary(r => Text(r, "host")!, r => Convert.ToInt32(r["count"]))
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["web1"] = 2, ["web2"] = 2, ["db1"] = 1 });
    }

    [SkippableFact]
    public async Task Fields_ProjectsDocumentedNames()
    {
        var rows = await RunAsync("find * | fields timestamp, host, message | sort by timestamp desc | head 1");

        Text(rows.Single(), "host").Should().Be("web2");
        Text(rows.Single(), "message").Should().Be("newest event");
        rows.Single().ContainsKey("timestamp").Should().BeTrue();
    }

    [SkippableFact]
    public async Task Dedup_UsesRealHost()
    {
        (await RunAsync("find * | dedup host")).Should().HaveCount(3);
    }

    [SkippableFact]
    public async Task Top_UsesRealHost()
    {
        var rows = await RunAsync("find * | top 1 user");

        rows.Should().ContainSingle();
    }

    [SkippableFact]
    public async Task Rename_MovesRealField()
    {
        var rows = await RunAsync("find * | rename host as server | fields server");

        rows.Select(r => Text(r, "server")).Should().Contain("web1").And.NotContainNulls();
    }

    [SkippableFact]
    public async Task SourceTypeAlias_FindsSourceType()
    {
        (await CountAsync("find * | where source_type = \"JSON\"")).Should().Be(5);
    }

    [SkippableFact]
    public async Task LevelAlias_FindsParsedLevel()
    {
        (await CountAsync("find * | where level = \"ERROR\"")).Should().Be(2);
    }

    // ─── Types: numeric JSON fields must compare as numbers on every backend ───

    [SkippableFact]
    public async Task Where_NumericJsonField_Matches()
    {
        (await CountAsync("find * | where status = 500")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Where_NumericComparison_Matches()
    {
        (await CountAsync("find * | where status >= 500")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Stats_SumOfNumericJsonField()
    {
        var rows = await RunAsync("find * | stats sum(status) as total");

        Convert.ToDouble(rows.Single()["total"]).Should().Be(1602);
    }

    [SkippableFact]
    public async Task Where_StringFieldComparison_IsCaseInsensitive()
    {
        (await CountAsync("find * | where user = \"ALICE\"")).Should().Be(1);
    }

    // ─── Stage order: a filter applies to the output of the stages before it ───

    [SkippableFact]
    public async Task WhereAfterHead_FiltersOnlyTheHeadRows()
    {
        var rows = await RunAsync("find * | sort by timestamp desc | head 1 | where severity >= 4");

        rows.Should().BeEmpty("the newest event is INFO");
    }

    [SkippableFact]
    public async Task WhereAfterEval_SeesTheComputedField()
    {
        (await CountAsync("find * | eval marker = 1 | where marker = 1")).Should().Be(5);
    }

    [SkippableFact]
    public async Task WhereAfterEval_SeesTheOverwrittenField()
    {
        (await CountAsync("find * | eval host = \"x\" | where host = \"web1\"")).Should().Be(0);
    }

    [SkippableFact]
    public async Task WhereAfterStats_FiltersTheAggregates()
    {
        var rows = await RunAsync("find * | stats count as c by host | where c = 2");

        rows.Select(r => Text(r, "host")).Should().BeEquivalentTo("web1", "web2");
    }

    [SkippableFact]
    public async Task SortAfterStats_SortsTheAggregates()
    {
        var rows = await RunAsync("find * | stats count by host | sort by count asc");

        Text(rows[0], "host").Should().Be("db1");
    }

    [SkippableFact]
    public async Task SortByField_IsNotReplacedByTimeOrder()
    {
        var rows = await RunAsync("find * | sort by host asc");

        Text(rows[0], "host").Should().Be("db1");
    }

    // ─── Boolean logic ───

    [SkippableFact]
    public async Task Or_DifferentFields_MatchesEitherSide()
    {
        (await CountAsync("find * | where host = \"db1\" or status = 500")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Or_SameField_MatchesEitherValue()
    {
        (await CountAsync("find * | where host = \"db1\" or host = \"web1\"")).Should().Be(3);
    }

    [SkippableFact]
    public async Task And_ConflictingValues_MatchesNothing()
    {
        (await CountAsync("find * | where host = \"web1\" | where host = \"web2\"")).Should().Be(0);
    }

    [SkippableFact]
    public async Task Severity_RepeatedFilters_BothApply()
    {
        (await CountAsync("find * | where severity >= 4 | where severity >= 2")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Severity_GreaterThan_ExcludesTheBoundary()
    {
        (await CountAsync("find * | where severity > 3")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Severity_Equality_Matches()
    {
        (await CountAsync("find * | where severity = 4")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Host_Equality_IsCaseInsensitive()
    {
        (await CountAsync("find * | where host = \"WEB1\"")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Not_NegatesTheCondition()
    {
        (await CountAsync("find * | where NOT host = \"web1\"")).Should().Be(3);
    }

    // ─── SPL-style field searches ───

    [SkippableFact]
    public async Task Search_FieldEquals_FiltersOnTheField()
    {
        (await CountAsync("search status=500")).Should().Be(1);
    }

    [SkippableFact]
    public async Task Search_BareValue_IsAString()
    {
        (await CountAsync("search level=ERROR")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Search_FieldOr_MatchesEitherSide()
    {
        (await CountAsync("search level=ERROR OR level=INFO")).Should().Be(4);
    }

    [SkippableFact]
    public async Task Search_FieldNot_NegatesTheField()
    {
        (await CountAsync("search NOT level=ERROR")).Should().Be(3);
    }

    [SkippableFact]
    public async Task Search_InPipeline_KeepsEarlierTerm()
    {
        (await CountAsync("find request | search slow")).Should().Be(1);
    }

    // ─── Result shape ───

    [SkippableFact]
    public async Task StatsCount_IsNamedCount()
    {
        var rows = await RunAsync("find * | stats count");

        Convert.ToInt32(rows.Single()["count"]).Should().Be(5);
    }

    [SkippableFact]
    public async Task Message_FallsBackForPlainText()
    {
        var rows = await RunAsync("find disk | fields message");

        Text(rows.Single(), "message").Should().Be("disk full");
    }
}

public sealed class MemoryBqlEndToEndTests : BqlEndToEndTests
{
    protected override bool IsAvailable => true;

    protected override ILogStorage CreateStorage() => new InMemoryStorage();
}

public sealed class SqliteBqlEndToEndTests : BqlEndToEndTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cabinet-e2e-{Guid.NewGuid():N}.db");

    protected override bool IsAvailable => true;

    protected override ILogStorage CreateStorage()
    {
        var configuration = new CabinetConfiguration();
        configuration.Storage.ConnectionString = $"Data Source={_path}";
        return new SqliteLogStorage(Options.Create(configuration), NullLogger<SqliteLogStorage>.Instance);
    }

    public override Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresBqlEndToEndTests : BqlEndToEndTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    protected override bool IsAvailable => !string.IsNullOrEmpty(ConnectionString);

    protected override ILogStorage CreateStorage() =>
        new PostgresLogStorage(ConnectionString!, NullLogger<PostgresLogStorage>.Instance);
}

[Trait("Category", "Integration")]
public sealed class OpenSearchBqlEndToEndTests : BqlEndToEndTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_URL");

    private readonly string _indexPrefix = $"cabinet-e2e-{Guid.NewGuid():N}";

    protected override bool IsAvailable => !string.IsNullOrEmpty(Url);

    protected override ILogStorage CreateStorage() =>
        new OpenSearchLogStorage(
            Url!,
            NullLogger<OpenSearchLogStorage>.Instance,
            indexPrefix: _indexPrefix,
            username: Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_USER"),
            password: Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_PASS"),
            allowInsecureSsl: true);

    // Documents aren't searchable until the index refreshes.
    protected override async Task OnSeededAsync()
    {
        using var client = new HttpClient();
        var response = await client.PostAsync($"{Url!.TrimEnd('/')}/{_indexPrefix}-*/_refresh", null);
        response.EnsureSuccessStatusCode();
    }

    // Each test creates its own indices. Left behind, they reach the node's shard limit,
    // after which new indices can't be created and writes fail.
    public override async Task DisposeAsync()
    {
        if (!IsAvailable)
            return;

        using var client = new HttpClient();
        var url = Url!.TrimEnd('/');
        (await client.DeleteAsync($"{url}/{_indexPrefix}-*")).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"{url}/_template/{_indexPrefix}-template")).EnsureSuccessStatusCode();
    }
}
