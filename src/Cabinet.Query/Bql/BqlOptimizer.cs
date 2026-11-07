using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Query.Bql;

/// <summary>
/// Moves filtering into the storage layer where that can't change a query's answer.
/// <para>
/// Two rules keep it correct. Only the leading run of filters is pushed, because a filter after
/// <c>head</c>, <c>eval</c> or <c>stats</c> applies to that stage's output, not to stored events.
/// And a predicate is only pushed when every backend evaluates it exactly as the pipeline would;
/// anything else stays in the pipeline.
/// </para>
/// </summary>
public sealed class BqlOptimizer
{
    private const int MaxSearchFilterLength = 2048;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex QuotedText = new("\"[^\"]*\"", RegexOptions.None, RegexTimeout);

    private static readonly Regex FieldComparison = new(
        @"(?<![\w.])[A-Za-z_][\w.]*\s*(==|!=|>=|<=|=|>|<)",
        RegexOptions.None,
        RegexTimeout);

    private static readonly Regex BareEqualityValue = new(
        @"(?<field>(?<![\w.])[A-Za-z_][\w.]*)\s*(?<op>==|!=|=)\s*(?<value>[A-Za-z_][\w.\-]*)",
        RegexOptions.None,
        RegexTimeout);

    /// <summary>
    /// Optimize a BQL query: extract predicates pushable to storage, leave the rest as pipeline stages.
    /// </summary>
    public OptimizedQuery Optimize(BqlQuery query, QueryExecutionContext? executionContext = null)
    {
        var storageContext = new QueryContext
        {
            SortDescending = true,
            SortField = "Timestamp"
        };

        // Apply time range from BQL query
        if (query.TimeRange != null)
        {
            storageContext.StartTime = query.TimeRange.Start;
            storageContext.EndTime = query.TimeRange.End;
        }

        // Apply time range from execution context (overrides if set)
        if (executionContext?.StartTime != null)
            storageContext.StartTime = executionContext.StartTime;
        if (executionContext?.EndTime != null)
            storageContext.EndTime = executionContext.EndTime;

        // Apply index scope from execution context
        if (!string.IsNullOrEmpty(executionContext?.Index))
            storageContext.Index = executionContext.Index;

        var remainingStages = new List<BqlStage>();

        // Direct mode reads a file rather than storage, so nothing can be pushed.
        var canPush = executionContext?.Mode != QueryMode.Direct;

        foreach (var original in query.Stages)
        {
            var stage = RewriteFieldSearch(original);

            if (canPush && TryPushDown(stage, storageContext))
                continue;

            // Filters commute with each other. Anything else changes what later stages see.
            canPush = canPush && stage is WhereStage or SearchStage;
            remainingStages.Add(stage);
        }

        // Set a reasonable fetch limit with upper bound to prevent OOM.
        // Aggregation stages (stats, timechart, chart, eventstats) need ALL matching data
        // to produce correct results — a truncated input yields wrong aggregates.
        // Use QueryContext.MaxLimit as the ceiling since the Limit setter clamps to it.
        var maxFetchLimit = QueryContext.MaxLimit;
        var hasAggregation = remainingStages.Any(s => s is StatsStage or TimechartStage or ChartStage or EventStatsStage or StreamStatsStage or FieldSummaryStage or RareStage);
        var hasLimitStage = remainingStages.Any(s => s is LimitStage or TailStage or TopStage);

        int computedLimit;
        if (hasAggregation)
        {
            // Aggregations need complete data — use the max fetch limit
            computedLimit = maxFetchLimit;
        }
        else if (!hasLimitStage)
        {
            computedLimit = (int)Math.Min((long)(executionContext?.Limit ?? 100) * 10, maxFetchLimit);
        }
        else
        {
            // Over-fetch to allow pipeline stages to filter
            computedLimit = (int)Math.Min((long)(executionContext?.Limit ?? 100) * 20, maxFetchLimit);
        }

        storageContext.Limit = computedLimit;

        return new OptimizedQuery
        {
            StorageContext = storageContext,
            RemainingStages = remainingStages,
            OriginalQuery = query
        };
    }

    /// <summary>
    /// Turns SPL-style <c>search status=500</c> or <c>search a=1 OR b=2</c> into the equivalent
    /// <c>where</c>. Bare values are strings, as in SPL. Text that doesn't parse as a condition
    /// stays a text search.
    /// </summary>
    private static BqlStage RewriteFieldSearch(BqlStage stage)
    {
        if (stage is not SearchStage search || search.SearchText.Length > MaxSearchFilterLength)
            return stage;

        var withoutQuotedText = QuotedText.Replace(search.SearchText, "\"\"");
        if (!FieldComparison.IsMatch(withoutQuotedText))
            return stage;

        var condition = BareEqualityValue.Replace(search.SearchText, match =>
            match.Groups["field"].Value.Equals("severity", StringComparison.OrdinalIgnoreCase)
                ? match.Value
                : $"{match.Groups["field"].Value} {match.Groups["op"].Value} \"{match.Groups["value"].Value}\"");

        try
        {
            var parsed = new BqlParser().ParseBql($"* | where {condition}");
            return parsed.Stages.OfType<WhereStage>().LastOrDefault() ?? stage;
        }
        catch (QueryParseException)
        {
            return stage;
        }
    }

    /// <summary>
    /// Try to push a stage down into the storage context.
    /// Returns true if fully pushed down (stage can be removed from pipeline).
    /// </summary>
    private static bool TryPushDown(BqlStage stage, QueryContext ctx)
    {
        switch (stage)
        {
            case SearchStage search:
                return TryPushSearch(search, ctx);

            case WhereStage where:
                return TryPushCondition(where.Condition, ctx);

            case SortStage sort when sort.Fields.Count == 1 && IsTimestampField(sort.Fields[0].Field):
                // Backends sort natively only on time. Other sorts run in the pipeline.
                ctx.SortField = "Timestamp";
                ctx.SortDescending = sort.Fields[0].Descending;
                return true;

            default:
                return false;
        }
    }

    private static bool IsTimestampField(string field) =>
        field.Equals("timestamp", StringComparison.OrdinalIgnoreCase)
        || field.Equals("_time", StringComparison.OrdinalIgnoreCase);

    private static bool TryPushSearch(SearchStage search, QueryContext ctx)
    {
        var text = search.SearchText.Trim();
        if (text == "*")
            return true;

        // A second search term can't share the single storage search slot
        if (ctx.SearchText != null)
            return false;

        ctx.SearchText = text.Trim('"');
        return true;
    }

    /// <summary>
    /// Pushes what it can of a condition. Returns true only if the whole condition was pushed,
    /// so a partly pushed AND stays in the pipeline for the remainder.
    /// </summary>
    private static bool TryPushCondition(BqlExpression expr, QueryContext ctx)
    {
        if (expr is LogicalExpression { Operator: "AND" } and)
        {
            var leftPushed = TryPushCondition(and.Left, ctx);
            var rightPushed = TryPushCondition(and.Right, ctx);
            return leftPushed && rightPushed;
        }

        return expr is ComparisonExpression { Left: FieldExpression field, Right: LiteralExpression literal } comparison
            && TryPushComparison(field.Name.ToLowerInvariant(), comparison.Operator, literal.Value, ctx);
    }

    private static bool TryPushComparison(string field, string op, object? value, QueryContext ctx)
    {
        if (field == "severity" && op is ">=" or ">" && value is int or long or double)
        {
            var numeric = Convert.ToDouble(value);
            var minimum = op == ">" ? (int)Math.Floor(numeric) + 1 : (int)Math.Ceiling(numeric);
            ctx.MinSeverity = (LogSeverity)Math.Max(minimum, (int)(ctx.MinSeverity ?? 0));
            return true;
        }

        if (op is not ("=" or "==") || value is not string text)
            return false;

        // A second, conflicting value stays in the pipeline, which correctly matches nothing.
        if (field is "sourcetype" or "source_type" && ctx.SourceType == null)
        {
            ctx.SourceType = text;
            return true;
        }

        if (field is "host" && ctx.Host == null)
        {
            ctx.Host = text;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Result of query optimization: storage-level predicates + remaining pipeline stages
/// </summary>
public sealed class OptimizedQuery
{
    public required QueryContext StorageContext { get; init; }
    public required List<BqlStage> RemainingStages { get; init; }
    public required BqlQuery OriginalQuery { get; init; }
}
