using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlMultisearchTests
{
    private readonly InMemoryStorage _storage;
    private readonly InMemoryKnowledgeStorage _knowledgeStorage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly BqlQueryEngine _engine;

    public BqlMultisearchTests()
    {
        _storage = new InMemoryStorage();
        _storage.InitializeAsync().GetAwaiter().GetResult();

        _knowledgeStorage = new InMemoryKnowledgeStorage();
        _knowledgeStorage.InitializeAsync().GetAwaiter().GetResult();

        _parser = new BqlParser();
        _optimizer = new BqlOptimizer();

        _engine = new BqlQueryEngine(
            _storage,
            _parser,
            _optimizer,
            NullLogger<BqlQueryEngine>.Instance,
            knowledgeStorage: _knowledgeStorage);

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
            Timestamp = DateTime.UtcNow.AddMinutes(-new Random().Next(0, 60))
        };
        evt.SetField("host", host);
        return evt;
    }

    // ─── Parser tests ───────────────────────────────────────────────

    [Fact]
    public void ParseBql_MultisearchStage_ParsesTwoSearchClauses()
    {
        // Queries starting with "|" get an implicit SearchStage("*") prepended
        var query = _parser.ParseBql("| multisearch [search \"error\"] [search \"warning\"]");

        query.Stages.Should().HaveCount(2);
        query.Stages[0].Should().BeOfType<SearchStage>();
        query.Stages[1].Should().BeOfType<MultisearchStage>();
        var ms = (MultisearchStage)query.Stages[1];
        ms.SearchQueries.Should().HaveCount(2);
        ms.SearchQueries[0].Should().Contain("error");
        ms.SearchQueries[1].Should().Contain("warning");
    }

    [Fact]
    public void ParseBql_MultisearchStage_ParsesThreeSearchClauses()
    {
        var query = _parser.ParseBql("| multisearch [search \"error\"] [search \"timeout\"] [search \"denied\"]");

        query.Stages.Should().HaveCount(2);
        var ms = (MultisearchStage)query.Stages[1];
        ms.SearchQueries.Should().HaveCount(3);
        ms.SearchQueries[0].Should().Contain("error");
        ms.SearchQueries[1].Should().Contain("timeout");
        ms.SearchQueries[2].Should().Contain("denied");
    }

    [Fact]
    public void ParseBql_MultisearchStage_FollowedByPipe()
    {
        var query = _parser.ParseBql("| multisearch [search \"error\"] [search \"warning\"] | limit 5");

        // SearchStage("*") + MultisearchStage + LimitStage
        query.Stages.Should().HaveCount(3);
        query.Stages[0].Should().BeOfType<SearchStage>();
        query.Stages[1].Should().BeOfType<MultisearchStage>();
        query.Stages[2].Should().BeOfType<LimitStage>();
        ((LimitStage)query.Stages[2]).Count.Should().Be(5);
    }

    [Fact]
    public void ParseBql_MultisearchStage_NoBrackets_Throws()
    {
        var act = () => _parser.ParseBql("| multisearch error warning");

        act.Should().Throw<QueryParseException>()
            .WithMessage("*multisearch requires at least one*");
    }

    [Fact]
    public void ParseBql_MultisearchStage_EmptyBrackets_Throws()
    {
        // Empty brackets produce no queries -> throws
        var act = () => _parser.ParseBql("| multisearch [] []");

        act.Should().Throw<QueryParseException>()
            .WithMessage("*multisearch requires at least one*");
    }

    [Fact]
    public void ParseBql_MultisearchStage_StageName_IsCorrect()
    {
        var query = _parser.ParseBql("| multisearch [search \"error\"] [search \"warning\"]");

        // Stage[0] is implicit SearchStage("*"), stage[1] is MultisearchStage
        query.Stages[1].StageName.Should().Be("multisearch");
    }

    [Fact]
    public void ParseBql_MultisearchStage_WithFindKeyword()
    {
        var query = _parser.ParseBql("| multisearch [find \"error\"] [find \"warning\"]");

        query.Stages.Should().HaveCount(2);
        var ms = (MultisearchStage)query.Stages[1];
        ms.SearchQueries.Should().HaveCount(2);
        ms.SearchQueries[0].Should().Contain("error");
        ms.SearchQueries[1].Should().Contain("warning");
    }

    [Fact]
    public void ParseBql_MultisearchStage_AfterExplicitSearch()
    {
        // multisearch after an explicit find — multisearch replaces pipeline at execution time
        var query = _parser.ParseBql("find * | multisearch [search \"error\"] [search \"warning\"]");

        query.Stages.Should().HaveCount(2);
        query.Stages[0].Should().BeOfType<SearchStage>();
        query.Stages[1].Should().BeOfType<MultisearchStage>();
    }

    // ─── Engine tests ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Multisearch_UnionsTwoQueries()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "| multisearch [search \"Database error\"] [search \"Permission denied\"]",
                Limit = 100
            });

        // "Database error: connection refused" and "Permission denied for user admin"
        result.Events.Should().HaveCount(2);
        var rawLines = result.Events
            .Select(e => ((IDictionary<string, object?>)e)["RawLine"]?.ToString())
            .ToList();
        rawLines.Should().Contain(l => l!.Contains("Database error"));
        rawLines.Should().Contain(l => l!.Contains("Permission denied"));
    }

    [Fact]
    public async Task ExecuteAsync_Multisearch_ThreeQueries()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "| multisearch [search \"Database error\"] [search \"Permission denied\"] [search \"Health check\"]",
                Limit = 100
            });

        result.Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_Multisearch_FollowedByLimit()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "| multisearch [search \"error\"] [search \"timeout\"] | limit 2",
                Limit = 100
            });

        result.Events.Should().HaveCountLessOrEqualTo(2);
    }

    [Fact]
    public async Task ExecuteAsync_Multisearch_NoMatchingResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "| multisearch [search \"nonexistent_xyz\"] [search \"also_nonexistent_abc\"]",
                Limit = 100
            });

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_Multisearch_OverlappingResults_IncludesDuplicates()
    {
        // Both queries match "Database error: connection refused"
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "| multisearch [search \"Database error\"] [search \"connection refused\"]",
                Limit = 100
            });

        // Both sub-searches match the same event, so union produces duplicates (like Splunk)
        result.Events.Should().HaveCount(2);
    }
}
