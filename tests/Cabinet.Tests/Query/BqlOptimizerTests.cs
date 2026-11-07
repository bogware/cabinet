using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlOptimizerTests
{
    private readonly BqlParser _parser = new();
    private readonly BqlOptimizer _optimizer = new();

    [Fact]
    public void Optimize_SearchText_PushedToStorageContext()
    {
        var query = _parser.ParseBql("find errors");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.SearchText.Should().Be("errors");
        // Search stage should be consumed (pushed down)
        optimized.RemainingStages.Should().NotContain(s => s is SearchStage);
    }

    [Fact]
    public void Optimize_WildcardSearch_NoPredicate()
    {
        var query = _parser.ParseBql("find *");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.SearchText.Should().BeNull();
    }

    [Fact]
    public void Optimize_TimeRange_PushedToStorageContext()
    {
        var query = _parser.ParseBql("find errors from last 24h");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.StartTime.Should().NotBeNull();
        optimized.StorageContext.EndTime.Should().NotBeNull();
    }

    [Fact]
    public void Optimize_SimpleSeverityWhere_PushedDown()
    {
        var query = _parser.ParseBql("find * | where Severity >= 4");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.MinSeverity.Should().Be(LogSeverity.Error);
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_SimpleFieldEquality_PushedDown()
    {
        var query = _parser.ParseBql("find * | where status = \"200\"");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.FieldFilters.Should().NotBeNull();
        optimized.StorageContext.FieldFilters!["status"].Should().Be("200");
    }

    [Fact]
    public void Optimize_SimpleSort_PushedDown()
    {
        var query = _parser.ParseBql("find * | sort by Timestamp desc");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.SortField.Should().Be("Timestamp");
        optimized.StorageContext.SortDescending.Should().BeTrue();
        optimized.RemainingStages.Should().NotContain(s => s is SortStage);
    }

    [Fact]
    public void Optimize_OrSameField_PushedDownAsInFilter()
    {
        var query = _parser.ParseBql("find * | where status = 200 or status = 500");
        var optimized = _optimizer.Optimize(query);

        // OR on same field pushed down as IN filter
        optimized.StorageContext.FieldFilters.Should().NotBeNull();
        optimized.StorageContext.FieldFilters!["status"].Should().BeOfType<object?[]>();
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_OrDifferentFields_RemainsInPipeline()
    {
        var query = _parser.ParseBql("find * | where status = 200 or host = \"web-01\"");
        var optimized = _optimizer.Optimize(query);

        // OR on different fields cannot be pushed down
        optimized.RemainingStages.Should().Contain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_StatsStage_RemainsInPipeline()
    {
        var query = _parser.ParseBql("find * | stats count by host");
        var optimized = _optimizer.Optimize(query);

        optimized.RemainingStages.Should().Contain(s => s is StatsStage);
    }

    [Fact]
    public void Optimize_EvalStage_RemainsInPipeline()
    {
        var query = _parser.ParseBql("find * | eval total = price * quantity");
        var optimized = _optimizer.Optimize(query);

        optimized.RemainingStages.Should().Contain(s => s is EvalStage);
    }

    [Fact]
    public void Optimize_ExecutionContextOverridesTimeRange()
    {
        var query = _parser.ParseBql("find * from last 24h");
        var executionContext = new QueryExecutionContext
        {
            Query = "find * from last 24h",
            StartTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var optimized = _optimizer.Optimize(query, executionContext);

        optimized.StorageContext.StartTime.Should().Be(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Optimize_AndWhereWithPushableFields_BothPushedDown()
    {
        var query = _parser.ParseBql("find * | where Severity >= 3 and host = \"web-01\"");
        var optimized = _optimizer.Optimize(query);

        // Both sides of AND should be pushed down
        optimized.StorageContext.MinSeverity.Should().Be(LogSeverity.Warning);
        optimized.StorageContext.Host.Should().Be("web-01");
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_LimitSetsAppropriateOverfetch()
    {
        var query = _parser.ParseBql("find * | limit 10");
        var executionContext = new QueryExecutionContext { Query = "find *", Limit = 100 };
        var optimized = _optimizer.Optimize(query, executionContext);

        // Should over-fetch since there's a limit stage
        optimized.StorageContext.Limit.Should().BeGreaterThan(10);
    }

    [Fact]
    public void Optimize_AggregationStage_UsesMaxFetchLimit()
    {
        var query = _parser.ParseBql("find * | stats count by host");
        var executionContext = new QueryExecutionContext { Query = "find *", Limit = 100 };
        var optimized = _optimizer.Optimize(query, executionContext);

        // Aggregation stages need all data — optimizer requests 500K but QueryContext.Limit
        // clamps to QueryContext.MaxLimit (100K)
        optimized.StorageContext.Limit.Should().Be(QueryContext.MaxLimit);
    }

    [Fact]
    public void Optimize_TimechartStage_UsesMaxFetchLimit()
    {
        var query = _parser.ParseBql("find * | timechart span=1h count by host");
        var executionContext = new QueryExecutionContext { Query = "find *", Limit = 100 };
        var optimized = _optimizer.Optimize(query, executionContext);

        // Optimizer requests 500K but QueryContext.Limit clamps to MaxLimit (100K)
        optimized.StorageContext.Limit.Should().Be(QueryContext.MaxLimit);
    }

    [Fact]
    public void Optimize_NoAggregation_UsesNormalFetchLimit()
    {
        var query = _parser.ParseBql("find * | where severity >= 3");
        var executionContext = new QueryExecutionContext { Query = "find *", Limit = 100 };
        var optimized = _optimizer.Optimize(query, executionContext);

        // Without aggregation or limit, should use limit * 10
        optimized.StorageContext.Limit.Should().BeLessThan(500_000);
    }

    [Fact]
    public void Optimize_IndexFromExecutionContext_PropagatedToStorage()
    {
        var query = _parser.ParseBql("find errors");
        var executionContext = new QueryExecutionContext
        {
            Query = "find errors",
            Index = "app_main"
        };
        var optimized = _optimizer.Optimize(query, executionContext);

        optimized.StorageContext.Index.Should().Be("app_main");
    }

    [Fact]
    public void Optimize_HostFilter_PushedDown()
    {
        var query = _parser.ParseBql("find * | where host = \"server-1\"");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.Host.Should().Be("server-1");
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_SourceTypeFilter_PushedDown()
    {
        var query = _parser.ParseBql("find * | where sourcetype = \"L4D2\"");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.SourceType.Should().Be("L4D2");
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }

    [Fact]
    public void Optimize_SearchFieldEquals_PushedAsSearchText()
    {
        // The BQL lexer tokenizes "status=200" as three tokens (status, =, 200),
        // and the parser reconstructs them with spaces as "status = 200".
        // Since the text contains spaces, TryPushSearch treats it as full-text
        // search rather than a field=value filter.
        var query = _parser.ParseBql("find status=200");
        var optimized = _optimizer.Optimize(query);

        optimized.StorageContext.SearchText.Should().Be("status = 200");
        optimized.StorageContext.FieldFilters.Should().BeNull();
    }

    [Fact]
    public void Optimize_AndWithOrSameField_BothPushedDown()
    {
        // AND with severity + OR on same field — both pushed down
        var query = _parser.ParseBql("find * | where Severity >= 3 and (status = 200 or status = 500)");
        var optimized = _optimizer.Optimize(query);

        // Severity should be pushed down
        optimized.StorageContext.MinSeverity.Should().Be(LogSeverity.Warning);
        // OR on same field also pushed down as IN filter
        optimized.StorageContext.FieldFilters.Should().NotBeNull();
        optimized.StorageContext.FieldFilters!["status"].Should().BeOfType<object?[]>();
        optimized.RemainingStages.Should().NotContain(s => s is WhereStage);
    }
}
