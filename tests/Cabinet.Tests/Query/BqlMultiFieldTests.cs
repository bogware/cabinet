using Cabinet.Query.Bql;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlMultiFieldTests
{
    private readonly BqlParser _parser = new();

    [Fact]
    public void ParseBql_MultiEval_ParsesMultipleAssignments()
    {
        var query = _parser.ParseBql("find * | eval x = 1 + 2, y = 3 * 4");

        var evalStage = query.Stages.OfType<EvalStage>().First();
        evalStage.Assignments.Should().HaveCount(2);
        evalStage.Assignments[0].FieldName.Should().Be("x");
        evalStage.Assignments[1].FieldName.Should().Be("y");
    }

    [Fact]
    public void ParseBql_MultiDedup_ParsesMultipleFields()
    {
        var query = _parser.ParseBql("find * | dedup host, status, source");

        var dedupStage = query.Stages.OfType<DedupStage>().First();
        dedupStage.Fields.Should().HaveCount(3);
        dedupStage.Fields.Should().Contain(new[] { "host", "status", "source" });
    }

    [Fact]
    public void ParseBql_MultiRename_ParsesMultipleMappings()
    {
        var query = _parser.ParseBql("find * | rename old1 as new1, old2 as new2");

        var renameStage = query.Stages.OfType<RenameStage>().First();
        renameStage.Mappings.Should().HaveCount(2);
        renameStage.Mappings[0].OldName.Should().Be("old1");
        renameStage.Mappings[0].NewName.Should().Be("new1");
        renameStage.Mappings[1].OldName.Should().Be("old2");
        renameStage.Mappings[1].NewName.Should().Be("new2");
    }

    [Fact]
    public void ParseBql_SingleEval_StillWorks()
    {
        var query = _parser.ParseBql("find * | eval x = 42");

        var evalStage = query.Stages.OfType<EvalStage>().First();
        evalStage.Assignments.Should().HaveCount(1);
        evalStage.Assignments[0].FieldName.Should().Be("x");
    }

    [Fact]
    public void ParseBql_EventStats_ParsesCorrectly()
    {
        var query = _parser.ParseBql("find * | eventstats count by host");

        var stage = query.Stages.OfType<EventStatsStage>().First();
        stage.Aggregations.Should().HaveCountGreaterThan(0);
        stage.GroupByFields.Should().Contain("host");
    }

    [Fact]
    public void ParseBql_StreamStats_ParsesCorrectly()
    {
        var query = _parser.ParseBql("find * | streamstats count by host");

        var stage = query.Stages.OfType<StreamStatsStage>().First();
        stage.Aggregations.Should().HaveCountGreaterThan(0);
        stage.GroupByFields.Should().Contain("host");
    }
}
