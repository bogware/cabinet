using System.Globalization;
using System.Runtime.CompilerServices;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Storage that aggregates itself must answer exactly as the engine would. Every query here runs
/// twice, on a backend and on in-memory storage, which can't aggregate, so the engine computes
/// that answer. The events are deliberately awkward: missing fields, numbers stored as text,
/// names in different case, empty values.
/// </summary>
public abstract class AggregatePushdownTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 2, 3, 4, 0, 0, DateTimeKind.Utc);

    private ReadCountingStorage _backend = null!;
    private InMemoryStorage _reference = null!;

    protected abstract bool IsAvailable { get; }

    /// <summary>Which of the queries below this backend is expected to answer itself.</summary>
    protected abstract bool PushesDown(string query);

    protected abstract ILogStorage CreateStorage();

    protected string RunIndex { get; } = $"agg-{Guid.NewGuid():N}";

    protected virtual Task OnSeededAsync() => Task.CompletedTask;

    public virtual Task DisposeAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        var storage = CreateStorage();
        await storage.InitializeAsync();
        _backend = new ReadCountingStorage(storage);
        _reference = new InMemoryStorage();

        var events = Events().ToList();
        await storage.StoreBatchAsync(events);
        await _reference.StoreBatchAsync(events);
        await OnSeededAsync();
    }

    private IEnumerable<LogEvent> Events()
    {
        var hosts = new[] { "web-1", "web-2", "db-1", null };
        for (var i = 0; i < 60; i++)
        {
            var logEvent = new LogEvent
            {
                // Unique to the run: storage keeps the first event with an ID, and runs share a database
                Id = $"{RunIndex}-{i:D3}",
                RawLine = $"event {i} {(i % 3 == 0 ? "alpha" : "beta")}",
                Timestamp = Start.AddMinutes(i * 7),
                Host = hosts[i % 4],
                Severity = (LogSeverity)(i % 5),
                Index = RunIndex,
                SourceType = i % 2 == 0 ? "app" : "web",
            };

            // Numbers, numbers as text, text that isn't a number, and nothing
            switch (i % 6)
            {
                case 0: logEvent.SetField("bytes", (long)(i * 10)); break;
                case 1: logEvent.SetField("bytes", i * 1.5); break;
                case 2: logEvent.SetField("bytes", $"{i}.25"); break;
                case 3: logEvent.SetField("bytes", "unknown"); break;
                case 4: break;
                case 5: logEvent.SetField("bytes", (long)-i); break;
            }

            if (i % 4 != 3)
            {
                logEvent.SetField("status", i % 2 == 0 ? (object)(long)(200 + (i % 3) * 100) : $"{200 + (i % 3) * 100}");
            }

            // The same field in different case, as different senders write it
            logEvent.SetField(i % 2 == 0 ? "user" : "User", i % 5 == 0 ? "Alice" : i % 5 == 1 ? "alice" : $"user{i % 4}");
            if (i % 7 == 0)
            {
                logEvent.SetField("flag", i % 2 == 0);
            }

            logEvent.SetField("name", $"name-{i % 4}");
            yield return logEvent;
        }
    }

    public static TheoryData<string> Queries => new()
    {
        "find * | stats count",
        "find * | stats count by host",
        "find * | stats count by status",
        "find * | stats count, sum(bytes), avg(bytes), min(bytes), max(bytes), dc(user) by host",
        "find * | stats count(bytes) by severity",
        "find * | stats dc(status)",
        "find * | stats count by User",
        "find * | stats count by host, status",
        "find * | stats count by flag",
        "find * | stats median(bytes), p90(bytes), stdev(bytes), stdevp(bytes), variance(bytes), varp(bytes) by host",
        "find * | stats min(name), max(name) by host",
        "find * | where severity >= 3 | stats count by host",
        "find \"alpha\" | stats count by sourcetype",
        "find * | stats count by host | sort - count | head 2",
        "find * | timechart span=1h count",
        "find * | timechart span=1h count by host",
        "find * | timechart span=30m sum(bytes) by status",
        "find * | timechart span=7d count",
        "find * | where status = \"none\" | stats count by host",
    };

    private async Task<(List<Dictionary<string, object?>> Rows, int Reads)> RunAsync(ILogStorage storage, string query)
    {
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var before = _backend.Reads;
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query, Index = RunIndex, Limit = 10_000 });
        var rows = result.Events.Select(row => ((IDictionary<string, object?>)row).ToDictionary(pair => pair.Key, pair => pair.Value)).ToList();
        return (rows, _backend.Reads - before);
    }

    /// <summary>Values as the user sees them: numbers compare as numbers, to within rounding.</summary>
    private static string Normalize(Dictionary<string, object?> row) => string.Join(
        " | ",
        row.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value switch
        {
            null => "null",
            DateTime time => time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            double or float or int or long or decimal => Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture).ToString("G10", CultureInfo.InvariantCulture),
            _ => pair.Value.ToString(),
        }}"));

    [SkippableTheory]
    [MemberData(nameof(Queries))]
    public async Task Pushdown_GivesTheEnginesAnswer(string query)
    {
        Skip.IfNot(IsAvailable, "Backend not available; set its CABINET_TEST_* variable to enable.");

        var (expected, _) = await RunAsync(_reference, query);
        var (actual, reads) = await RunAsync(_backend, query);

        // Order matters when it's chosen: sort, timechart's time order
        if (query.Contains("sort", StringComparison.Ordinal) || query.Contains("timechart", StringComparison.Ordinal))
        {
            actual.Select(Normalize).Should().Equal(expected.Select(Normalize), query);
        }
        else
        {
            actual.Select(Normalize).Should().BeEquivalentTo(expected.Select(Normalize), query);
        }

        if (PushesDown(query))
        {
            reads.Should().Be(0, $"storage should have answered {query} itself");
        }
    }

    /// <summary>Counts reads of events, which an aggregate in storage doesn't need.</summary>
    private sealed class ReadCountingStorage : ILogStorage, IAggregatingLogStorage
    {
        private readonly ILogStorage _inner;

        public ReadCountingStorage(ILogStorage inner)
        {
            _inner = inner;
        }

        public int Reads { get; private set; }

        public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Reads++;
            await foreach (var logEvent in _inner.QueryAsync(context, cancellationToken))
            {
                yield return logEvent;
            }
        }

        public Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default) =>
            _inner is IAggregatingLogStorage aggregating ? aggregating.AggregateAsync(request, cancellationToken) : Task.FromResult<AggregateResult?>(null);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

        public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) => _inner.StoreAsync(logEvent, cancellationToken);

        public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default) => _inner.StoreBatchAsync(events, cancellationToken);

        public Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);

        public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default) => _inner.GetCountAsync(context, cancellationToken);

        public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeOldEventsAsync(before, cancellationToken);

        public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeIndexEventsAsync(indexName, before, cancellationToken);

        public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default) => _inner.GetStatsAsync(cancellationToken);
    }
}

public sealed class SqliteAggregatePushdownTests : AggregatePushdownTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cabinet-agg-{Guid.NewGuid():N}.db");

    protected override bool IsAvailable => true;

    protected override bool PushesDown(string query) =>
        !query.Contains("median", StringComparison.Ordinal) && !query.Contains("min(name)", StringComparison.Ordinal)
        && !query.Contains("where", StringComparison.Ordinal);

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
public sealed class PostgresAggregatePushdownTests : AggregatePushdownTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");

    protected override bool IsAvailable => !string.IsNullOrEmpty(ConnectionString);

    protected override bool PushesDown(string query) =>
        !query.Contains("min(name)", StringComparison.Ordinal) && !query.Contains("where", StringComparison.Ordinal);

    private PostgresLogStorage? _storage;

    // Each storage has its own connection pool, which would otherwise stay open
    protected override ILogStorage CreateStorage() => _storage = new PostgresLogStorage(ConnectionString!, NullLogger<PostgresLogStorage>.Instance);

    public override Task DisposeAsync()
    {
        _storage?.Dispose();
        return Task.CompletedTask;
    }
}

[Trait("Category", "Integration")]
public sealed class OpenSearchAggregatePushdownTests : AggregatePushdownTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_URL");

    private readonly string _indexPrefix = $"cabinet-agg-{Guid.NewGuid():N}";

    protected override bool IsAvailable => !string.IsNullOrEmpty(Url);

    protected override bool PushesDown(string query) => query is
        "find * | stats count" or "find * | stats count by host" or "find * | timechart span=1h count" or "find * | timechart span=1h count by host";

    protected override ILogStorage CreateStorage() =>
        new OpenSearchLogStorage(Url!, NullLogger<OpenSearchLogStorage>.Instance, indexPrefix: _indexPrefix, allowInsecureSsl: true);

    protected override async Task OnSeededAsync()
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{Url!.TrimEnd('/')}/{_indexPrefix}-*/_refresh", null)).EnsureSuccessStatusCode();
    }

    public override async Task DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        using var client = new HttpClient();
        var url = Url!.TrimEnd('/');
        await client.DeleteAsync($"{url}/{_indexPrefix}-*");
        await client.DeleteAsync($"{url}/_template/{_indexPrefix}-template");
    }
}

/// <summary>The engine with events still in memory, before any segment is written.</summary>
public sealed class EngineInMemoryAggregatePushdownTests : AggregatePushdownTests, IDisposable
{
    private readonly string _path = Directory.CreateTempSubdirectory("cabinet-engine-agg-").FullName;
    private Cabinet.Storage.Engine.EngineLogStorage? _engine;

    protected override bool IsAvailable => true;

    // Min and max of text alone stay in the pipeline
    protected override bool PushesDown(string query) =>
        !query.Contains("min(name)", StringComparison.Ordinal) && !query.Contains("where", StringComparison.Ordinal);

    protected override ILogStorage CreateStorage() => _engine = new Cabinet.Storage.Engine.EngineLogStorage(_path);

    public void Dispose()
    {
        _engine?.Dispose();
        Directory.Delete(_path, recursive: true);
    }
}

/// <summary>The engine reading compressed segments, with small ones so groups span several.</summary>
public sealed class EngineSegmentAggregatePushdownTests : AggregatePushdownTests, IDisposable
{
    private readonly string _path = Directory.CreateTempSubdirectory("cabinet-engine-agg-").FullName;
    private Cabinet.Storage.Engine.EngineLogStorage? _engine;

    protected override bool IsAvailable => true;

    protected override bool PushesDown(string query) =>
        !query.Contains("min(name)", StringComparison.Ordinal) && !query.Contains("where", StringComparison.Ordinal);

    protected override ILogStorage CreateStorage() =>
        _engine = new Cabinet.Storage.Engine.EngineLogStorage(_path, new Cabinet.Storage.Engine.EngineOptions { FlushEvents = 1000 });

    protected override Task OnSeededAsync() => _engine!.FlushAsync();

    public void Dispose()
    {
        _engine?.Dispose();
        Directory.Delete(_path, recursive: true);
    }
}
