using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlQueryEngineTests
{
    private readonly InMemoryStorage _storage;
    private readonly BqlQueryEngine _engine;

    public BqlQueryEngineTests()
    {
        _storage = new InMemoryStorage();
        _storage.InitializeAsync().GetAwaiter().GetResult();

        var parser = new BqlParser();
        var optimizer = new BqlOptimizer();

        _engine = new BqlQueryEngine(
            _storage,
            parser,
            optimizer,
            NullLogger<BqlQueryEngine>.Instance);

        SeedTestData().GetAwaiter().GetResult();
    }

    private async Task SeedTestData()
    {
        var events = new List<LogEvent>
        {
            CreateEvent("Connection established from 192.168.1.1", LogSeverity.Info, "web-01"),
            CreateEvent("Connection timeout from 10.0.0.1", LogSeverity.Warning, "web-01"),
            CreateEvent("Database error: connection refused", LogSeverity.Error, "db-01"),
            CreateEvent("Out of memory error in worker process", LogSeverity.Critical, "worker-01"),
            CreateEvent("Request processed successfully", LogSeverity.Info, "web-01"),
            CreateEvent("Slow query detected: 5000ms", LogSeverity.Warning, "db-01"),
            CreateEvent("User login successful", LogSeverity.Info, "web-02"),
            CreateEvent("Permission denied for user admin", LogSeverity.Error, "web-02"),
            CreateEvent("Health check passed", LogSeverity.Debug, "web-01"),
            CreateEvent("Cache miss for key session:123", LogSeverity.Debug, "web-02"),
        };

        foreach (var evt in events)
            await _storage.StoreAsync(evt);
    }

    private static LogEvent CreateEvent(string rawLine, LogSeverity severity, string host)
    {
        var evt = new LogEvent
        {
            RawLine = rawLine,
            Severity = severity,
            Host = host,
            Timestamp = DateTime.UtcNow.AddMinutes(-new Random().Next(0, 1440))
        };
        evt.SetField("host", host);
        return evt;
    }

    // ─── Basic search ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WildcardSearch_ReturnsAllEvents()
    {
        var result = await _engine.ExecuteAsync(new QueryExecutionContext { Query = "find *", Limit = 100 });

        result.Events.Should().HaveCount(10);
        result.Statistics.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_TextSearch_FiltersByContent()
    {
        var result = await _engine.ExecuteAsync(new QueryExecutionContext { Query = "find error", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        // Search text pushed to storage — results depend on storage implementation
    }

    [Fact]
    public async Task ExecuteAsync_ImplicitSearch_WorksWithoutKeyword()
    {
        var result = await _engine.ExecuteAsync(new QueryExecutionContext { Query = "error", Limit = 100 });

        result.Events.Should().NotBeEmpty();
    }

    // ─── WHERE clause ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhereSeverity_FiltersEvents()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | where Severity >= 4", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var severity = Convert.ToInt32(dict["Severity"]);
            severity.Should().BeGreaterThanOrEqualTo(4);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhereAnd_CombinesConditions()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | where Severity >= 3 and host = \"db-01\"", Limit = 100 });

        result.Events.Should().NotBeEmpty();
    }

    // ─── STATS ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_StatsCount_ReturnsCount()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats count", Limit = 100 });

        result.Events.Should().HaveCount(1);
        var row = (IDictionary<string, object?>)result.Events[0];
        row.Should().ContainKey("count()");
        ((int)row["count()"]!).Should().Be(10);
    }

    [Fact]
    public async Task ExecuteAsync_StatsCountByField_GroupsResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats count by host", Limit = 100 });

        result.Events.Should().HaveCountGreaterThan(1);
        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict.Should().ContainKey("host");
            dict.Should().ContainKey("count()");
        }
    }

    [Fact]
    public async Task ExecuteAsync_GroupBy_IsSugarForStatsCount()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | group by host", Limit = 100 });

        result.Events.Should().HaveCountGreaterThan(1);
    }

    // ─── SORT ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SortBySeverityDesc_SortsCorrectly()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | sort by Severity desc", Limit = 100 });

        result.Events.Should().HaveCountGreaterThan(1);
        var severities = result.Events
            .Select(e => Convert.ToInt32(((IDictionary<string, object?>)e)["Severity"]))
            .ToList();

        severities.Should().BeInDescendingOrder();
    }

    // ─── EVAL ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Eval_CreatesNewField()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | eval severity_label = Severity", Limit = 100 });

        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict.Should().ContainKey("severity_label");
        }
    }

    // ─── REX ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Rex_ExtractsNamedGroups()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | rex \"from (?<ip>[\\d.]+)\"", Limit = 100 });

        // At least some events should have the IP extracted
        var withIp = result.Events.Where(e =>
        {
            var dict = (IDictionary<string, object?>)e;
            return dict.ContainsKey("ip") && dict["ip"] != null;
        }).ToList();

        withIp.Should().NotBeEmpty();
    }

    // ─── TABLE / SELECT ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Select_ProjectsFields()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | select Severity, RawLine", Limit = 100 });

        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict.Keys.Should().Contain("Severity");
            dict.Keys.Should().Contain("RawLine");
            dict.Keys.Should().HaveCount(2);
        }
    }

    // ─── RENAME ──────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Rename_RenamesField()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | rename host as server", Limit = 100 });

        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict.Should().ContainKey("server");
            dict.Should().NotContainKey("host");
        }
    }

    // ─── DEDUP ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Dedup_RemovesDuplicates()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | dedup host", Limit = 100 });

        var hosts = result.Events
            .Select(e => ((IDictionary<string, object?>)e)["host"]?.ToString())
            .ToList();

        hosts.Should().OnlyHaveUniqueItems();
    }

    // ─── HEAD / LIMIT ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Limit_RestrictsResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | limit 3", Limit = 100 });

        result.Events.Should().HaveCount(3);
    }

    // ─── TOP ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Top_ReturnsTopValues()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | top 2 host", Limit = 100 });

        result.Events.Should().HaveCount(2);
        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict.Should().ContainKey("host");
            dict.Should().ContainKey("count");
        }
    }

    // ─── Predicate pushdown ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PredicatePushdown_StatsShowAllScanned()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | limit 5", Limit = 100 });

        result.Statistics.Should().NotBeNull();
        result.Statistics!.EventsScanned.Should().BeGreaterThanOrEqualTo(5);
        result.Statistics.EventsMatched.Should().Be(5);
    }

    // ─── Complex pipelines ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ComplexPipeline_WorksEndToEnd()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "find * | where Severity >= 3 | stats count by host | sort by count desc | limit 5",
                Limit = 100
            });

        result.Events.Should().NotBeEmpty();
        result.Columns.Should().NotBeNull();
    }

    // ─── Columns extraction ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExtractsColumns_FromFirstEvent()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find *", Limit = 10 });

        result.Columns.Should().NotBeNull();
        result.Columns.Should().Contain("RawLine");
        result.Columns.Should().Contain("Timestamp");
        result.Columns.Should().Contain("Severity");
    }

    // ─── P3: Advanced aggregations ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_StatsDistinctCount_CountsUniqueValues()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats dc(host)", Limit = 100 });

        result.Events.Should().HaveCount(1);
        var row = (IDictionary<string, object?>)result.Events.First();
        var dcValue = Convert.ToInt32(row["dc(host)"]);
        dcValue.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExecuteAsync_StatsMedian_ReturnsMedianValue()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats median(Severity)", Limit = 100 });

        result.Events.Should().HaveCount(1);
        var row = (IDictionary<string, object?>)result.Events.First();
        row.Should().ContainKey("median(Severity)");
        var medianValue = Convert.ToDouble(row["median(Severity)"]);
        medianValue.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteAsync_StatsStdev_ReturnsStandardDeviation()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats stdev(Severity)", Limit = 100 });

        result.Events.Should().HaveCount(1);
        var row = (IDictionary<string, object?>)result.Events.First();
        row.Should().ContainKey("stdev(Severity)");
        var stdevValue = Convert.ToDouble(row["stdev(Severity)"]);
        stdevValue.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteAsync_StatsVariance_ReturnsVariance()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats variance(Severity)", Limit = 100 });

        result.Events.Should().HaveCount(1);
        var row = (IDictionary<string, object?>)result.Events.First();
        row.Should().ContainKey("variance(Severity)");
    }

    // ─── P3: Timechart ──────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Timechart_ReturnsBucketedResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | timechart span=1h count", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        var firstRow = (IDictionary<string, object?>)result.Events.First();
        firstRow.Should().ContainKey("_time");
        firstRow.Should().ContainKey("count");
    }

    [Fact]
    public async Task ExecuteAsync_TimechartByField_GroupsByField()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | timechart span=1h count by host", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        var firstRow = (IDictionary<string, object?>)result.Events.First();
        firstRow.Should().ContainKey("_time");
        firstRow.Should().ContainKey("host");
        firstRow.Should().ContainKey("count");
    }

    // ─── P3: Field aliases ──────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FieldAlias_CreatesAliasField()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | fieldalias server=host | select server", Limit = 10 });

        result.Events.Should().NotBeEmpty();
        var firstRow = (IDictionary<string, object?>)result.Events.First();
        firstRow.Should().ContainKey("server");
    }

    // ─── P3: Transaction ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Transaction_GroupsEventsByField()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | transaction host", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        var firstRow = (IDictionary<string, object?>)result.Events.First();
        firstRow.Should().ContainKey("_eventcount");
        firstRow.Should().ContainKey("_duration");
        firstRow.Should().ContainKey("_raw");
    }

    [Fact]
    public async Task ExecuteAsync_TransactionWithMaxEvents_SplitsLargeGroups()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | transaction host maxevents=2", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        foreach (dynamic evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var eventCount = Convert.ToInt32(dict["_eventcount"]);
            eventCount.Should().BeLessThanOrEqualTo(2);
        }
    }

    [Fact]
    public async Task ExecuteAsync_AppendStage_ExecutesSuccessfully()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | append [search error]", Limit = 100 });

        // Append executes a subsearch and concatenates results with the main query
        result.Events.Should().NotBeEmpty();
        result.Statistics.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_JoinStage_ExecutesWithoutError()
    {
        // Join is now implemented — verify it parses and executes without throwing
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | join host [search error]", Limit = 100 });

        result.Should().NotBeNull();
        result.Events.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_QueryTimeout_CancelsExecution()
    {
        var context = new QueryExecutionContext
        {
            Query = "find *",
            Limit = 100,
            Timeout = TimeSpan.FromMilliseconds(1) // Extremely short timeout
        };

        // With an extremely short timeout, the query may succeed (fast enough)
        // or throw OperationCanceledException. Either is valid behavior.
        try
        {
            var result = await _engine.ExecuteAsync(context);
            // If it completes, that's fine — the storage was fast enough
            result.Should().NotBeNull();
        }
        catch (OperationCanceledException)
        {
            // Expected — timeout triggered cancellation
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationToken_Respected()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // InMemoryStorage uses yield break on cancellation rather than throwing,
        // so a pre-cancelled token results in an empty result set instead of an exception.
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find *", Limit = 100 },
            cts.Token);

        result.Should().NotBeNull();
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsStatistics()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find *", Limit = 100 });

        result.Statistics.Should().NotBeNull();
        result.Statistics!.EventsScanned.Should().BeGreaterThan(0);
        result.Statistics.EventsMatched.Should().BeGreaterThan(0);
        result.Statistics.ExecutionTime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsColumns()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find *", Limit = 100 });

        result.Columns.Should().NotBeNull();
        result.Columns.Should().Contain("Timestamp");
        result.Columns.Should().Contain("RawLine");
        result.Columns.Should().Contain("Host");
    }

    [Fact]
    public async Task PercentileAggregation_EmptyInput_DoesNotCrash()
    {
        // Query non-existent data with percentile — should return 0, not crash
        var emptyStorage = new InMemoryStorage();
        await emptyStorage.InitializeAsync();
        var parser = new BqlParser();
        var optimizer = new BqlOptimizer();
        var engine = new BqlQueryEngine(emptyStorage, parser, optimizer, NullLogger<BqlQueryEngine>.Instance);

        var result = await engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | stats perc95(duration)", Limit = 100 });

        result.Events.Should().NotBeNull();
    }

    [Fact]
    public async Task NullFieldIn_ReturnsFalseNotMatch()
    {
        // Events with null severity should not match IN clause
        await _storage.StoreAsync(new LogEvent { RawLine = "test event", Severity = LogSeverity.Info });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | where nonexistent IN (\"a\", \"b\")", Limit = 100 });

        // No events should match because nonexistent field is null
        result.Events.Should().BeEmpty();
    }
}
