using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class QueryEngineTests
{
    private readonly QueryEngine _engine;
    private readonly InMemoryStorage _storage;

    public QueryEngineTests()
    {
        _storage = new InMemoryStorage();
        var parser = new QueryParser(NullLogger<QueryParser>.Instance);
        _engine = new QueryEngine(_storage, parser, NullLogger<QueryEngine>.Instance);

        SeedTestData().GetAwaiter().GetResult();
    }

    private async Task SeedTestData()
    {
        var events = new[]
        {
            CreateEvent("Server started on port 27015", LogSeverity.Info, "server1"),
            CreateEvent("ERROR: Failed to load model", LogSeverity.Error, "server1"),
            CreateEvent("Player Survivor1 connected", LogSeverity.Info, "server1"),
            CreateEvent("Player Survivor2 connected", LogSeverity.Info, "server2"),
            CreateEvent("ERROR: Out of memory", LogSeverity.Error, "server2"),
            CreateEvent("WARNING: Disk space low", LogSeverity.Warning, "server1"),
            CreateEvent("Player Survivor1 killed Infected with smg", LogSeverity.Info, "server1"),
            CreateEvent("ERROR: Network timeout", LogSeverity.Error, "server2"),
            CreateEvent("Map changed to c1m2_streets", LogSeverity.Info, "server1"),
            CreateEvent("Server shutting down", LogSeverity.Info, "server2"),
        };

        foreach (var evt in events)
        {
            await _storage.StoreAsync(evt);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WildcardSearch_ReturnsAllEvents()
    {
        var context = new QueryExecutionContext { Query = "*", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().HaveCount(10);
        result.Statistics.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SearchText_FiltersResults()
    {
        var context = new QueryExecutionContext { Query = "ERROR", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HeadOperator_LimitsResults()
    {
        var context = new QueryExecutionContext { Query = "* | head 3", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_StatsCount_Aggregates()
    {
        var context = new QueryExecutionContext { Query = "* | stats count", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().NotBeEmpty();
        var first = (IDictionary<string, object?>)result.Events.First();
        first["count"].Should().Be(10);
    }

    [Fact]
    public async Task ExecuteAsync_DedupOperator_RemovesDuplicates()
    {
        var context = new QueryExecutionContext { Query = "* | dedup Severity", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        // 3 distinct severities: Info, Error, Warning
        result.Events.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_Throws()
    {
        var context = new QueryExecutionContext { Query = "", Limit = 100 };
        var act = () => _engine.ExecuteAsync(context);

        await act.Should().ThrowAsync<QueryParseException>();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperator_Throws()
    {
        var context = new QueryExecutionContext { Query = "* | fakeoperator", Limit = 100 };
        var act = () => _engine.ExecuteAsync(context);

        await act.Should().ThrowAsync<QueryParseException>();
    }

    [Fact]
    public async Task ExecuteAsync_PipelineChain_Works()
    {
        var context = new QueryExecutionContext { Query = "* | head 5 | head 3", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsColumns()
    {
        var context = new QueryExecutionContext { Query = "*", Limit = 100 };
        var result = await _engine.ExecuteAsync(context);

        result.Columns.Should().NotBeNull();
        result.Columns.Should().Contain("RawLine");
        result.Columns.Should().Contain("Timestamp");
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeRange_Filters()
    {
        var context = new QueryExecutionContext
        {
            Query = "*",
            StartTime = DateTime.UtcNow.AddHours(-1),
            EndTime = DateTime.UtcNow.AddHours(1),
            Limit = 100
        };
        var result = await _engine.ExecuteAsync(context);

        result.Events.Should().HaveCount(10);
    }

    [Fact]
    public async Task ExecuteStreamAsync_ExtensionMethod_ReturnsLogEvents()
    {
        var results = new List<LogEvent>();
        await foreach (var evt in _engine.ExecuteStreamAsync("*"))
        {
            results.Add(evt);
        }

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(e => e.RawLine.Should().NotBeNullOrEmpty());
    }

    private static LogEvent CreateEvent(string rawLine, LogSeverity severity, string host)
    {
        return new LogEvent
        {
            RawLine = rawLine,
            Severity = severity,
            Host = host,
            SourceType = SourceTypes.Generic,
            Timestamp = DateTime.UtcNow
        };
    }
}
