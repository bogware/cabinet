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

        foreach (var stage in query.Stages.SelectMany(SplitTextAndFieldSearch).Select(RewriteFieldSearch))
        {
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
        var hasAggregation = remainingStages.Any(s => s is StatsStage or TimechartStage or ChartStage or EventStatsStage or StreamStatsStage or FieldSummaryStage or RareStage or ClusterStage);
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

    private static readonly Regex FieldTerm = new(
        @"^[A-Za-z_][\w.]*(==|!=|>=|<=|=|>|<)\S",
        RegexOptions.None,
        RegexTimeout);

    /// <summary>
    /// Splits <c>search error host=web</c> into a text search for <c>error</c> and a separate
    /// field search for <c>host=web</c>, which together mean both, as in SPL. Searches with
    /// OR, NOT or parentheses are left whole.
    /// </summary>
    private static IEnumerable<BqlStage> SplitTextAndFieldSearch(BqlStage stage)
    {
        if (stage is not SearchStage search || search.SearchText.Length > MaxSearchFilterLength
            || SplitTerms(search.SearchText) is not { } terms)
        {
            return [stage];
        }

        var fields = terms.Where(term => FieldTerm.IsMatch(term)).ToList();
        if (fields.Count == 0 || fields.Count == terms.Count)
        {
            return [stage];
        }

        return
        [
            new SearchStage(string.Join(' ', terms.Where(term => !FieldTerm.IsMatch(term)))),
            new SearchStage(string.Join(' ', fields)),
        ];
    }

    /// <summary>
    /// The space-separated terms of a search, keeping quoted text together, or null when it uses
    /// boolean operators or grouping.
    /// </summary>
    private static List<string>? SplitTerms(string text)
    {
        var terms = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c is '(' or ')')
            {
                return null;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    terms.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return quoted || terms.Any(term => term is "OR" or "NOT" or "AND" or "or" or "not" or "and") ? null : terms;
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

        // Words would read as field names in a where; the search evaluates them itself
        try
        {
            if (BqlTextSearch.Parse(search.SearchText).HasText)
                return stage;
        }
        catch (QueryParseException)
        {
            return stage;
        }

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
        // Storage finds lines with all of some words. That narrows any search whose words are all
        // required; the pipeline checks the rest (OR, NOT, phrases, wildcards, fields).
        var parsed = BqlTextSearch.Parse(search.SearchText);
        if (parsed.RequiredWords != null)
        {
            ctx.SearchText = ctx.SearchText == null ? parsed.RequiredWords : $"{ctx.SearchText} {parsed.RequiredWords}";
        }

        return parsed.IsPlainWords;
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

        // A time bound narrows what storage reads. Storage bounds are inclusive and the pipeline
        // still checks the exact comparison, so the stage stays.
        if (field is "_time" or "timestamp" && op is ">" or ">=" or "<" or "<=" && TimeLiteral(value) is { } time)
        {
            if (op is ">" or ">=")
                ctx.StartTime = ctx.StartTime is { } start && start > time ? start : time;
            else
                ctx.EndTime = ctx.EndTime is { } end && end < time ? end : time;
            return false;
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

    /// <summary>A literal as a UTC time: a date string, or epoch seconds as SPL writes them.</summary>
    private static DateTime? TimeLiteral(object? value) => value switch
    {
        DateTime time => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc),
        int or long or double => BqlTime.FromValue(null, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
        string text => BqlTime.Parse(text, null),
        _ => null,
    };
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
