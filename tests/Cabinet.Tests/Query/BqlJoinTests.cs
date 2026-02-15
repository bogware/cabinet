using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlJoinTests
{
    private readonly BqlParser _parser = new();
    private readonly InMemoryStorage _storage = new();
    private readonly BqlQueryEngine _engine;

    public BqlJoinTests()
    {
        _engine = new BqlQueryEngine(
            _storage,
            _parser,
            new BqlOptimizer(),
            NullLogger<BqlQueryEngine>.Instance);

        // Seed test data
        var events = new List<LogEvent>
        {
            new() { RawLine = "web request", Host = "web-01", SourceType = "access", Fields = { ["status"] = "200", ["user_id"] = "u1" } },
            new() { RawLine = "web request", Host = "web-02", SourceType = "access", Fields = { ["status"] = "404", ["user_id"] = "u2" } },
            new() { RawLine = "web request", Host = "web-01", SourceType = "access", Fields = { ["status"] = "500", ["user_id"] = "u1" } },
        };
        _storage.StoreBatchAsync(events).GetAwaiter().GetResult();
    }

    [Fact]
    public void ParseJoinStage_InnerJoin()
    {
        var query = _parser.ParseBql("find * | join host [search error]");
        query.Stages.Should().HaveCount(2);
        var joinStage = query.Stages[1].Should().BeOfType<JoinStage>().Subject;
        joinStage.JoinField.Should().Be("host");
        joinStage.JoinType.Should().Be("inner");
        joinStage.SubsearchQuery.Should().Be("search error");
    }

    [Fact]
    public void ParseJoinStage_LeftJoin()
    {
        var query = _parser.ParseBql("find * | join type=left host [search error]");
        var joinStage = query.Stages[1].Should().BeOfType<JoinStage>().Subject;
        joinStage.JoinType.Should().Be("left");
        joinStage.JoinField.Should().Be("host");
    }

    [Fact]
    public void ParseJoinStage_OuterJoin()
    {
        var query = _parser.ParseBql("find * | join type=outer host [search *]");
        var joinStage = query.Stages[1].Should().BeOfType<JoinStage>().Subject;
        joinStage.JoinType.Should().Be("outer");
    }

    [Fact]
    public async Task ExecuteAsync_JoinWithSubsearch_ReturnsResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | join Host [find *]", Limit = 100 });

        result.Should().NotBeNull();
        result.Events.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_JoinWithNoMatchingKeys_InnerReturnsEmpty()
    {
        // Search for something that doesn't exist — inner join should produce no matches
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | join Host [find nonexistent_xyz_12345]", Limit = 100 });

        result.Should().NotBeNull();
        // Inner join with no matching subsearch results should return empty
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_LeftJoinWithNoMatch_PreservesMainResults()
    {
        var result = await _engine.ExecuteAsync(
            new QueryExecutionContext { Query = "find * | join type=left Host [find nonexistent_xyz_12345]", Limit = 100 });

        result.Should().NotBeNull();
        // Left join should preserve all main results even without matches
        result.Events.Should().HaveCount(3);
    }
}
