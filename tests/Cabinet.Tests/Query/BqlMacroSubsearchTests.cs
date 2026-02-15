using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlMacroSubsearchTests
{
    private readonly InMemoryStorage _storage;
    private readonly InMemoryKnowledgeStorage _knowledgeStorage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly BqlQueryEngine _engine;

    public BqlMacroSubsearchTests()
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

    // ─── Macro parser tests ─────────────────────────────────────

    [Fact]
    public void ParseBql_MacroStage_ParsesMacroName()
    {
        var query = _parser.ParseBql("find * | macro my_filter");

        query.Stages.Should().HaveCount(2);
        query.Stages[1].Should().BeOfType<MacroStage>();
        var macro = (MacroStage)query.Stages[1];
        macro.MacroName.Should().Be("my_filter");
        macro.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void ParseBql_MacroStage_WithArguments()
    {
        var query = _parser.ParseBql("find * | macro my_filter \"web-01\", 100");

        query.Stages.Should().HaveCount(2);
        var macro = (MacroStage)query.Stages[1];
        macro.MacroName.Should().Be("my_filter");
        macro.Arguments.Should().HaveCount(2);
        macro.Arguments[0].Should().Be("web-01");
        macro.Arguments[1].Should().Be("100");
    }

    [Fact]
    public void ParseBql_MacroStage_WithStringArguments()
    {
        var query = _parser.ParseBql("find * | macro error_filter \"connection refused\"");

        query.Stages.Should().HaveCount(2);
        var macro = (MacroStage)query.Stages[1];
        macro.MacroName.Should().Be("error_filter");
        macro.Arguments.Should().HaveCount(1);
        macro.Arguments[0].Should().Be("connection refused");
    }

    [Fact]
    public void ParseBql_MacroStage_FollowedByPipe()
    {
        var query = _parser.ParseBql("find * | macro my_filter | limit 10");

        query.Stages.Should().HaveCount(3);
        query.Stages[1].Should().BeOfType<MacroStage>();
        query.Stages[2].Should().BeOfType<LimitStage>();
    }

    // ─── Subsearch parser tests ─────────────────────────────────

    [Fact]
    public void ParseBql_SubsearchStage_ParsesBracketedQuery()
    {
        var query = _parser.ParseBql("find * | subsearch [find errors | stats count by host | where count > 5 | fields host]");

        query.Stages.Should().HaveCount(2);
        query.Stages[1].Should().BeOfType<SubsearchStage>();
        var subsearch = (SubsearchStage)query.Stages[1];
        subsearch.SubsearchQuery.Should().Contain("find errors");
        subsearch.SubsearchQuery.Should().Contain("stats count by host");
        subsearch.SubsearchQuery.Should().Contain("fields host");
    }

    [Fact]
    public void ParseBql_SubsearchStage_FollowedByPipe()
    {
        var query = _parser.ParseBql("find * | subsearch [find errors | fields host] | limit 5");

        query.Stages.Should().HaveCount(3);
        query.Stages[1].Should().BeOfType<SubsearchStage>();
        query.Stages[2].Should().BeOfType<LimitStage>();
    }

    [Fact]
    public void ParseBql_SubsearchStage_MissingBracket_Throws()
    {
        var act = () => _parser.ParseBql("find * | subsearch find errors");

        act.Should().Throw<QueryParseException>()
            .WithMessage("*Expected '[' after 'subsearch'*");
    }

    [Fact]
    public void ParseBql_SubsearchStage_EmptyBrackets_Throws()
    {
        var act = () => _parser.ParseBql("find * | subsearch []");

        act.Should().Throw<QueryParseException>()
            .WithMessage("*Subsearch query cannot be empty*");
    }

    // ─── Macro engine tests ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Macro_ExpandsDefinition()
    {
        // Create a macro that filters to errors only
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "errors_only",
            Definition = "where Severity >= 3"
        });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro errors_only", Limit = 100 });

        // Severity 3 = Error, 4 = Critical. We have: db error, memory error, permission denied = 3 error events + 1 critical
        result.Events.Should().NotBeEmpty();
        foreach (var evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var severity = Convert.ToInt32(dict["Severity"]);
            severity.Should().BeGreaterOrEqualTo(3);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Macro_WithMultipleStages()
    {
        // Create a macro that filters and limits
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "top_errors",
            Definition = "where Severity >= 3 | limit 2"
        });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro top_errors", Limit = 100 });

        result.Events.Should().HaveCountLessOrEqualTo(2);
        foreach (var evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            var severity = Convert.ToInt32(dict["Severity"]);
            severity.Should().BeGreaterOrEqualTo(3);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Macro_WithParameterSubstitution()
    {
        // Create a macro with a parameter
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "filter_host",
            Definition = "where host = \"$hostname$\"",
            Parameters = new List<string> { "hostname" }
        });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro filter_host \"web-01\"", Limit = 100 });

        result.Events.Should().NotBeEmpty();
        foreach (var evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict["host"]?.ToString().Should().Be("web-01");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Macro_NotFound_ReturnsEmpty()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro nonexistent_macro", Limit = 100 });

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_Macro_FollowedByStage()
    {
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "errors_only",
            Definition = "where Severity >= 3"
        });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro errors_only | limit 1", Limit = 100 });

        result.Events.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExecuteAsync_Macro_WithStatsStage()
    {
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "count_by_host",
            Definition = "stats count by host"
        });

        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro count_by_host", Limit = 100 });

        // Should group by host — we have web-01, db-01, worker-01, web-02
        result.Events.Should().NotBeEmpty();
        result.Events.Count.Should().BeGreaterOrEqualTo(2);
    }

    // ─── Subsearch engine tests ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Subsearch_FiltersOuterByInnerResults()
    {
        // Subsearch: find events from host web-01, then filter main results to only include web-01 events
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "find * | subsearch [find * | where host = \"web-01\" | fields host]",
                Limit = 100
            });

        result.Events.Should().NotBeEmpty();
        foreach (var evt in result.Events)
        {
            var dict = (IDictionary<string, object?>)evt;
            dict["host"]?.ToString().Should().Be("web-01");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Subsearch_EmptyInnerResult_ReturnsEmpty()
    {
        // Subsearch with no matches
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "find * | subsearch [find nonexistent_xyz_content | fields host]",
                Limit = 100
            });

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_Subsearch_FollowedByLimit()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext
            {
                Query = "find * | subsearch [find * | where host = \"web-01\" | fields host] | limit 2",
                Limit = 100
            });

        result.Events.Should().HaveCountLessOrEqualTo(2);
    }

    // ─── GetMacroByNameAsync interface tests ────────────────────

    [Fact]
    public async Task GetMacroByNameAsync_InMemory_FindsByName()
    {
        var saved = await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "test_macro",
            Definition = "where Severity >= 3"
        });

        var found = await _knowledgeStorage.GetMacroByNameAsync("test_macro");
        found.Should().NotBeNull();
        found!.Name.Should().Be("test_macro");
        found.Definition.Should().Be("where Severity >= 3");
        found.Id.Should().Be(saved.Id);
    }

    [Fact]
    public async Task GetMacroByNameAsync_InMemory_CaseInsensitive()
    {
        await _knowledgeStorage.SaveMacroAsync(new QueryMacro
        {
            Name = "MY_MACRO",
            Definition = "where Severity >= 3"
        });

        var found = await _knowledgeStorage.GetMacroByNameAsync("my_macro");
        found.Should().NotBeNull();
        found!.Name.Should().Be("MY_MACRO");
    }

    [Fact]
    public async Task GetMacroByNameAsync_InMemory_ReturnsNullForNonexistent()
    {
        var found = await _knowledgeStorage.GetMacroByNameAsync("does_not_exist");
        found.Should().BeNull();
    }

    // ─── Engine without knowledge storage ───────────────────────

    [Fact]
    public async Task ExecuteAsync_Macro_WithoutKnowledgeStorage_PassesThrough()
    {
        // Engine without knowledge storage configured
        var engineNoKnowledge = new BqlQueryEngine(
            _storage,
            _parser,
            _optimizer,
            NullLogger<BqlQueryEngine>.Instance);

        var result = await engineNoKnowledge.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | macro some_macro", Limit = 100 });

        // Should passthrough (return all events) since there's no knowledge storage
        result.Events.Should().HaveCount(10);
    }
}
