using Cabinet.Query;
using Cabinet.Query.Bql;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Query;

public class BqlParserTests
{
    private readonly BqlParser _parser = new();

    // ─── Basic search ────────────────────────────────────────

    [Fact]
    public void ParseBql_SimpleSearch_ProducesSearchStage()
    {
        var query = _parser.ParseBql("find errors");

        query.Stages.Should().HaveCount(1);
        query.Stages[0].Should().BeOfType<SearchStage>();
        ((SearchStage)query.Stages[0]).SearchText.Should().Be("errors");
    }

    [Fact]
    public void ParseBql_WildcardSearch_ProducesWildcard()
    {
        var query = _parser.ParseBql("find *");

        query.Stages.Should().HaveCount(1);
        ((SearchStage)query.Stages[0]).SearchText.Should().Be("*");
    }

    [Fact]
    public void ParseBql_ImplicitSearch_WithoutFindKeyword()
    {
        var query = _parser.ParseBql("error timeout");

        query.Stages.Should().HaveCount(1);
        query.Stages[0].Should().BeOfType<SearchStage>();
        ((SearchStage)query.Stages[0]).SearchText.Should().Be("error timeout");
    }

    [Fact]
    public void ParseBql_QuotedSearch_PreservesQuotes()
    {
        var query = _parser.ParseBql("find \"connection timeout\"");

        var search = (SearchStage)query.Stages[0];
        search.SearchText.Should().Contain("connection timeout");
    }

    [Fact]
    public void ParseBql_SplSearchKeyword_Works()
    {
        var query = _parser.ParseBql("search error");

        query.Stages.Should().HaveCount(1);
        ((SearchStage)query.Stages[0]).SearchText.Should().Be("error");
    }

    // ─── Time ranges ─────────────────────────────────────────

    [Fact]
    public void ParseBql_FromLast_SetsTimeRange()
    {
        var query = _parser.ParseBql("find errors from last 24h");

        query.TimeRange.Should().NotBeNull();
        query.TimeRange!.Start.Should().BeCloseTo(DateTime.UtcNow.AddHours(-24), TimeSpan.FromSeconds(5));
        query.TimeRange.End.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ParseBql_FromLastDays_SetsTimeRange()
    {
        var query = _parser.ParseBql("find * from last 7d");

        query.TimeRange.Should().NotBeNull();
        query.TimeRange!.Start.Should().BeCloseTo(DateTime.UtcNow.AddDays(-7), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ParseBql_Between_SetsTimeRange()
    {
        var query = _parser.ParseBql("find errors between \"2024-01-01\" and \"2024-01-31\"");

        query.TimeRange.Should().NotBeNull();
        query.TimeRange!.Start.Should().HaveYear(2024);
        query.TimeRange.Start.Should().HaveMonth(1);
        query.TimeRange.Start.Should().HaveDay(1);
    }

    // ─── Pipeline stages ─────────────────────────────────────

    [Fact]
    public void ParseBql_Pipeline_ParsesMultipleStages()
    {
        var query = _parser.ParseBql("find errors | where severity >= 3 | head 10");

        query.Stages.Should().HaveCount(3);
        query.Stages[0].Should().BeOfType<SearchStage>();
        query.Stages[1].Should().BeOfType<WhereStage>();
        query.Stages[2].Should().BeOfType<LimitStage>();
    }

    // ─── WHERE with AND/OR/NOT ───────────────────────────────

    [Fact]
    public void ParseBql_WhereEquals_ParsesComparison()
    {
        var query = _parser.ParseBql("find * | where host = \"web-01\"");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<ComparisonExpression>();
        var comp = (ComparisonExpression)where.Condition;
        comp.Left.Should().BeOfType<FieldExpression>();
        comp.Operator.Should().Be("=");
    }

    [Fact]
    public void ParseBql_WhereAnd_ParsesLogicalExpression()
    {
        var query = _parser.ParseBql("find * | where host = \"web-01\" and status != 200");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<LogicalExpression>();
        var logical = (LogicalExpression)where.Condition;
        logical.Operator.Should().Be("AND");
    }

    [Fact]
    public void ParseBql_WhereOr_ParsesLogicalExpression()
    {
        var query = _parser.ParseBql("find * | where status = 400 or status = 500");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<LogicalExpression>();
        ((LogicalExpression)where.Condition).Operator.Should().Be("OR");
    }

    [Fact]
    public void ParseBql_WhereNot_ParsesNotExpression()
    {
        var query = _parser.ParseBql("find * | where not status = 200");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<NotExpression>();
    }

    [Fact]
    public void ParseBql_WhereParentheses_ParsesGrouping()
    {
        var query = _parser.ParseBql("find * | where (status = 400 or status = 500) and host = \"web-01\"");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<LogicalExpression>();
        var logical = (LogicalExpression)where.Condition;
        logical.Operator.Should().Be("AND");
        logical.Left.Should().BeOfType<LogicalExpression>(); // The OR group
    }

    [Fact]
    public void ParseBql_WhereIn_ParsesInExpression()
    {
        var query = _parser.ParseBql("find * | where status in (400, 401, 403)");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<InExpression>();
        var inExpr = (InExpression)where.Condition;
        inExpr.Values.Should().HaveCount(3);
    }

    [Fact]
    public void ParseBql_WhereLike_ParsesLikeExpression()
    {
        var query = _parser.ParseBql("find * | where message like \"%timeout%\"");

        var where = (WhereStage)query.Stages[1];
        where.Condition.Should().BeOfType<LikeExpression>();
    }

    // ─── STATS / GROUP BY ────────────────────────────────────

    [Fact]
    public void ParseBql_StatsCount_ParsesSingleAggregation()
    {
        var query = _parser.ParseBql("find * | stats count");

        var stats = (StatsStage)query.Stages[1];
        stats.Aggregations.Should().HaveCount(1);
        stats.Aggregations[0].Name.Should().Be("count");
        stats.GroupByFields.Should().BeEmpty();
    }

    [Fact]
    public void ParseBql_StatsCountByField_ParsesGroupBy()
    {
        var query = _parser.ParseBql("find * | stats count by host");

        var stats = (StatsStage)query.Stages[1];
        stats.Aggregations[0].Name.Should().Be("count");
        stats.GroupByFields.Should().ContainSingle("host");
    }

    [Fact]
    public void ParseBql_StatsMultipleAggsByMultipleFields_Parses()
    {
        var query = _parser.ParseBql("find * | stats count, avg(duration), max(bytes) by host, status");

        var stats = (StatsStage)query.Stages[1];
        stats.Aggregations.Should().HaveCount(3);
        stats.Aggregations[0].Name.Should().Be("count");
        stats.Aggregations[1].Name.Should().Be("avg");
        stats.Aggregations[1].Field.Should().Be("duration");
        stats.Aggregations[2].Name.Should().Be("max");
        stats.Aggregations[2].Field.Should().Be("bytes");
        stats.GroupByFields.Should().HaveCount(2);
    }

    [Fact]
    public void ParseBql_GroupBy_IsSugarForStatsCount()
    {
        var query = _parser.ParseBql("find * | group by host");

        var stats = (StatsStage)query.Stages[1];
        stats.Aggregations.Should().ContainSingle(a => a.Name == "count");
        stats.GroupByFields.Should().ContainSingle("host");
    }

    // ─── SORT ────────────────────────────────────────────────

    [Fact]
    public void ParseBql_SortByBqlStyle_ParsesDescending()
    {
        var query = _parser.ParseBql("find * | sort by count desc");

        var sort = (SortStage)query.Stages[1];
        sort.Fields.Should().ContainSingle();
        sort.Fields[0].Field.Should().Be("count");
        sort.Fields[0].Descending.Should().BeTrue();
    }

    [Fact]
    public void ParseBql_SortSplStyle_ParsesMinusDescending()
    {
        var query = _parser.ParseBql("find * | sort -timestamp");

        var sort = (SortStage)query.Stages[1];
        sort.Fields[0].Field.Should().Be("timestamp");
        sort.Fields[0].Descending.Should().BeTrue();
    }

    // ─── EVAL / COMPUTE ──────────────────────────────────────

    [Fact]
    public void ParseBql_Eval_ParsesFieldAssignment()
    {
        var query = _parser.ParseBql("find * | eval duration = end_time - start_time");

        var eval = (EvalStage)query.Stages[1];
        eval.Assignments[0].FieldName.Should().Be("duration");
        eval.Assignments[0].Expression.Should().BeOfType<ArithmeticExpression>();
    }

    [Fact]
    public void ParseBql_Compute_IsSugarForEval()
    {
        var query = _parser.ParseBql("find * | compute total = price * quantity");

        var eval = (EvalStage)query.Stages[1];
        eval.Assignments[0].FieldName.Should().Be("total");
    }

    // ─── REX / EXTRACT ───────────────────────────────────────

    [Fact]
    public void ParseBql_Rex_ParsesPattern()
    {
        var query = _parser.ParseBql("find * | rex \"user=(?<username>\\w+)\"");

        var rex = (RexStage)query.Stages[1];
        rex.Pattern.Should().Be("user=(?<username>\\w+)");
        rex.SourceField.Should().Be("RawLine"); // default
    }

    [Fact]
    public void ParseBql_Extract_FromSourceField()
    {
        var query = _parser.ParseBql("find * | extract \"(?<ip>\\d+\\.\\d+\\.\\d+\\.\\d+)\" from message");

        var rex = (RexStage)query.Stages[1];
        rex.SourceField.Should().Be("message");
    }

    // ─── RENAME / TABLE / DEDUP ──────────────────────────────

    [Fact]
    public void ParseBql_Rename_ParsesOldAndNew()
    {
        var query = _parser.ParseBql("find * | rename old_field as new_field");

        var rename = (RenameStage)query.Stages[1];
        rename.Mappings[0].OldName.Should().Be("old_field");
        rename.Mappings[0].NewName.Should().Be("new_field");
    }

    [Fact]
    public void ParseBql_Select_ParsesFieldList()
    {
        var query = _parser.ParseBql("find * | select timestamp, severity, message");

        var table = (TableStage)query.Stages[1];
        table.Fields.Should().HaveCount(3);
        table.Fields.Should().Contain("timestamp");
        table.Fields.Should().Contain("severity");
        table.Fields.Should().Contain("message");
    }

    [Fact]
    public void ParseBql_SplTable_Works()
    {
        var query = _parser.ParseBql("find * | table timestamp, host");

        query.Stages[1].Should().BeOfType<TableStage>();
    }

    [Fact]
    public void ParseBql_UniqueBy_IsSugarForDedup()
    {
        var query = _parser.ParseBql("find * | unique by host");

        query.Stages[1].Should().BeOfType<DedupStage>();
        ((DedupStage)query.Stages[1]).Fields[0].Should().Be("host");
    }

    [Fact]
    public void ParseBql_SplDedup_Works()
    {
        var query = _parser.ParseBql("find * | dedup host");

        query.Stages[1].Should().BeOfType<DedupStage>();
    }

    // ─── LIMIT / HEAD / TAIL / TOP ──────────────────────────

    [Fact]
    public void ParseBql_Limit_ParsesCount()
    {
        var query = _parser.ParseBql("find * | limit 50");

        var limit = (LimitStage)query.Stages[1];
        limit.Count.Should().Be(50);
    }

    [Fact]
    public void ParseBql_SplHead_Works()
    {
        var query = _parser.ParseBql("find * | head 20");

        query.Stages[1].Should().BeOfType<LimitStage>();
        ((LimitStage)query.Stages[1]).Count.Should().Be(20);
    }

    [Fact]
    public void ParseBql_Tail_ParsesCount()
    {
        var query = _parser.ParseBql("find * | tail 5");

        query.Stages[1].Should().BeOfType<TailStage>();
        ((TailStage)query.Stages[1]).Count.Should().Be(5);
    }

    [Fact]
    public void ParseBql_Top_ParsesCountAndField()
    {
        var query = _parser.ParseBql("find * | top 10 host");

        var top = (TopStage)query.Stages[1];
        top.Count.Should().Be(10);
        top.Field.Should().Be("host");
    }

    // ─── Legacy compatibility ────────────────────────────────

    [Fact]
    public void Parse_ReturnsLegacyQueryPlan()
    {
        var plan = _parser.Parse("find errors | where severity >= 3 | head 10");

        plan.Operators.Should().HaveCount(3);
        plan.Operators[0].Name.Should().Be("search");
        plan.Operators[1].Name.Should().Be("where");
        plan.Operators[2].Name.Should().Be("head");
    }

    // ─── Error handling ──────────────────────────────────────

    [Fact]
    public void ParseBql_EmptyQuery_Throws()
    {
        var act = () => _parser.ParseBql("");
        act.Should().Throw<QueryParseException>().WithMessage("*empty*");
    }

    [Fact]
    public void ParseBql_UnknownStage_Throws()
    {
        var act = () => _parser.ParseBql("find * | foobar");
        act.Should().Throw<QueryParseException>().WithMessage("*Unknown stage*");
    }

    // ─── Complex pipelines ───────────────────────────────────

    [Fact]
    public void ParseBql_ComplexPipeline_ParsesAllStages()
    {
        var query = _parser.ParseBql(
            "find errors from last 24h | where severity >= 4 and host = \"web-01\" | stats count, avg(response_time) by endpoint | sort by count desc | limit 20");

        query.Stages.Should().HaveCount(5);
        query.Stages[0].Should().BeOfType<SearchStage>();
        query.Stages[1].Should().BeOfType<WhereStage>();
        query.Stages[2].Should().BeOfType<StatsStage>();
        query.Stages[3].Should().BeOfType<SortStage>();
        query.Stages[4].Should().BeOfType<LimitStage>();
        query.TimeRange.Should().NotBeNull();
    }

    // ─── P3: New aggregation functions ───────────────────────

    [Fact]
    public void ParseBql_StatsDistinctCount_ParsesDc()
    {
        var query = _parser.ParseBql("find * | stats dc(host)");
        query.Stages.Should().HaveCount(2);
        var stats = query.Stages[1].Should().BeOfType<StatsStage>().Subject;
        stats.Aggregations.Should().HaveCount(1);
        stats.Aggregations[0].Name.Should().Be("dc");
        stats.Aggregations[0].Field.Should().Be("host");
    }

    [Fact]
    public void ParseBql_StatsMedian_ParsesMedian()
    {
        var query = _parser.ParseBql("find * | stats median(response_time)");
        var stats = query.Stages[1].Should().BeOfType<StatsStage>().Subject;
        stats.Aggregations[0].Name.Should().Be("median");
        stats.Aggregations[0].Field.Should().Be("response_time");
    }

    [Fact]
    public void ParseBql_StatsStdev_ParsesStdev()
    {
        var query = _parser.ParseBql("find * | stats stdev(latency)");
        var stats = query.Stages[1].Should().BeOfType<StatsStage>().Subject;
        stats.Aggregations[0].Name.Should().Be("stdev");
        stats.Aggregations[0].Field.Should().Be("latency");
    }

    [Fact]
    public void ParseBql_StatsVariance_ParsesVariance()
    {
        var query = _parser.ParseBql("find * | stats variance(latency)");
        var stats = query.Stages[1].Should().BeOfType<StatsStage>().Subject;
        stats.Aggregations[0].Name.Should().Be("variance");
        stats.Aggregations[0].Field.Should().Be("latency");
    }

    [Fact]
    public void ParseBql_StatsMultipleAdvanced_ParsesAll()
    {
        var query = _parser.ParseBql("find * | stats count, dc(host), avg(duration), median(latency) by source");
        var stats = query.Stages[1].Should().BeOfType<StatsStage>().Subject;
        stats.Aggregations.Should().HaveCount(4);
        stats.Aggregations[0].Name.Should().Be("count");
        stats.Aggregations[1].Name.Should().Be("dc");
        stats.Aggregations[2].Name.Should().Be("avg");
        stats.Aggregations[3].Name.Should().Be("median");
        stats.GroupByFields.Should().Contain("source");
    }

    // ─── P3: Timechart with field ────────────────────────────

    [Fact]
    public void ParseBql_TimechartWithAggField_CapturesField()
    {
        var query = _parser.ParseBql("find * | timechart span=1h avg(response_time) by host");
        var tc = query.Stages[1].Should().BeOfType<TimechartStage>().Subject;
        tc.Span.Should().Be(TimeSpan.FromHours(1));
        tc.Aggregation.Should().Be("avg");
        tc.AggregationField.Should().Be("response_time");
        tc.ByField.Should().Be("host");
    }

    [Fact]
    public void ParseBql_TimechartCount_WorksWithoutField()
    {
        var query = _parser.ParseBql("find * | timechart span=1d count");
        var tc = query.Stages[1].Should().BeOfType<TimechartStage>().Subject;
        tc.Span.Should().Be(TimeSpan.FromDays(1));
        tc.Aggregation.Should().Be("count");
        tc.AggregationField.Should().BeNull();
    }

    // ─── P3: Field alias ─────────────────────────────────────

    [Fact]
    public void ParseBql_FieldAlias_ParsesAliases()
    {
        var query = _parser.ParseBql("find * | fieldalias server=host");
        query.Stages.Should().HaveCount(2);
        var alias = query.Stages[1].Should().BeOfType<FieldAliasStage>().Subject;
        alias.Aliases.Should().ContainKey("server");
        alias.Aliases["server"].Should().Be("host");
    }

    [Fact]
    public void ParseBql_FieldAliasMultiple_ParsesAll()
    {
        var query = _parser.ParseBql("find * | fieldalias server=host, msg=RawLine");
        var alias = query.Stages[1].Should().BeOfType<FieldAliasStage>().Subject;
        alias.Aliases.Should().HaveCount(2);
        alias.Aliases["server"].Should().Be("host");
        alias.Aliases["msg"].Should().Be("RawLine");
    }

    // ─── P3: Transaction ─────────────────────────────────────

    [Fact]
    public void ParseBql_Transaction_ParsesField()
    {
        var query = _parser.ParseBql("find * | transaction host");
        query.Stages.Should().HaveCount(2);
        var txn = query.Stages[1].Should().BeOfType<TransactionStage>().Subject;
        txn.Fields.Should().Contain("host");
    }

    [Fact]
    public void ParseBql_TransactionMultipleFields_ParsesAll()
    {
        var query = _parser.ParseBql("find * | transaction host, session_id");
        var txn = query.Stages[1].Should().BeOfType<TransactionStage>().Subject;
        txn.Fields.Should().HaveCount(2);
        txn.Fields.Should().Contain("host");
        txn.Fields.Should().Contain("session_id");
    }

    // ─── Time span overflow protection ───────────────────────

    [Fact]
    public void ParseBql_TimeSpanOverflowValue_DoesNotThrow()
    {
        // Extremely large time value should not cause integer overflow
        var query = _parser.ParseBql("find * from last 999999999999999999d");
        // Should parse without throwing; the time range may be null/ignored due to overflow protection
        query.Should().NotBeNull();
    }

    [Theory]
    [InlineData("1s")]
    [InlineData("60m")]
    [InlineData("24h")]
    [InlineData("30d")]
    [InlineData("52w")]
    public void ParseBql_ValidTimeSpan_ParsesSuccessfully(string timeValue)
    {
        var query = _parser.ParseBql($"find errors from last {timeValue}");
        query.Should().NotBeNull();
        query.Stages.Should().NotBeEmpty();
    }

    // ─── Transaction maxevents bounds ───────────────────────

    [Fact]
    public void ParseBql_TransactionWithMaxevents_Parses()
    {
        var query = _parser.ParseBql("find * | transaction host maxevents=500");
        var txn = query.Stages[1].Should().BeOfType<TransactionStage>().Subject;
        txn.Fields.Should().Contain("host");
    }
}
