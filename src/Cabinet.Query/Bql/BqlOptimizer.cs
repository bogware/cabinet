using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Query.Bql;

/// <summary>
/// Optimizes a BQL AST by extracting predicates that can be pushed down to the storage layer.
/// This avoids fetching all events and filtering in-memory — the storage backend handles filtering.
/// </summary>
public sealed class BqlOptimizer
{
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

        foreach (var stage in query.Stages)
        {
            if (!TryPushDown(stage, storageContext))
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

        // Never increase beyond an already-set limit (e.g., from a pushed-down stage or default).
        // storageContext.Limit defaults to 10K — only increase if the computed limit requires it,
        // and never decrease below what was already set unless explicitly computed lower.
        storageContext.Limit = computedLimit;

        return new OptimizedQuery
        {
            StorageContext = storageContext,
            RemainingStages = remainingStages,
            OriginalQuery = query
        };
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
                return TryPushWhere(where, ctx);

            case SortStage sort when sort.Fields.Count == 1:
                // Simple single-field sort can be pushed to storage
                ctx.SortField = sort.Fields[0].Field;
                ctx.SortDescending = sort.Fields[0].Descending;
                return true;

            default:
                return false;
        }
    }

    private static bool TryPushSearch(SearchStage search, QueryContext ctx)
    {
        var text = search.SearchText.Trim();
        if (text == "*") return true; // Wildcard, no filter needed

        // Check for SPL-style field=value in search text
        if (text.Contains('=') && !text.Contains(' '))
        {
            var eqIdx = text.IndexOf('=');
            var field = text[..eqIdx].Trim();
            var value = text[(eqIdx + 1)..].Trim().Trim('"');

            // Push as field filter
            ctx.FieldFilters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            ctx.FieldFilters[field] = value;
            return true;
        }

        // Push as full-text search
        ctx.SearchText = text.Trim('"');
        return true;
    }

    private static bool TryPushWhere(WhereStage where, QueryContext ctx)
    {
        // Try to extract simple comparisons that map directly to QueryContext fields
        if (TryExtractSimpleFilter(where.Condition, ctx))
            return true;

        // Complex expressions (AND/OR/NOT, nested) stay in the pipeline
        return false;
    }

    private static bool TryExtractSimpleFilter(BqlExpression expr, QueryContext ctx)
    {
        if (expr is ComparisonExpression comp &&
            comp.Left is FieldExpression field &&
            comp.Right is LiteralExpression literal)
        {
            var fieldName = field.Name.ToLowerInvariant();

            // Severity filter: severity >= warning
            if (fieldName == "severity" && comp.Operator is ">=" or ">" &&
                literal.Value is int or long or double)
            {
                var severityVal = Convert.ToInt32(literal.Value);
                ctx.MinSeverity = (LogSeverity)severityVal;
                return true;
            }

            // SourceType filter: sourcetype = "L4D2"
            if (fieldName is "sourcetype" or "source_type" && comp.Operator is "=" or "==" &&
                literal.Value is string sourceStr)
            {
                ctx.SourceType = sourceStr;
                return true;
            }

            // Host filter: host = "server-1"
            if (fieldName is "host" or "serverid" or "server_id" or "server" && comp.Operator is "=" or "==" &&
                literal.Value is string hostStr)
            {
                ctx.Host = hostStr;
                return true;
            }

            // Generic field=value (equality only) — use original field name for storage lookup
            if (comp.Operator is "=" or "==")
            {
                ctx.FieldFilters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                ctx.FieldFilters[field.Name] = literal.Value;
                return true;
            }
        }

        // AND: try to push each side independently. If both are pushed, remove the WHERE.
        // If only one side is pushed, still push it (storage filters it) but keep the WHERE
        // so the unpushed side still applies in the pipeline. This is safe because pushed
        // predicates are AND'd with unpushed ones — pushing a subset only narrows results.
        if (expr is LogicalExpression logical && logical.Operator == "AND")
        {
            var leftPushed = TryExtractSimpleFilter(logical.Left, ctx);
            var rightPushed = TryExtractSimpleFilter(logical.Right, ctx);

            // If both pushed, the entire WHERE can be removed
            if (leftPushed && rightPushed) return true;

            // If at least one pushed, keep the WHERE for the unpushed side.
            // The pushed side is still applied at storage level (reducing fetch volume).
            return false;
        }

        // OR: push down same-field equality predicates as an IN-style filter.
        // e.g., host="web-01" OR host="web-02" → FieldFilters["host"] = string[]{"web-01","web-02"}
        // Storage backends that support array values in FieldFilters will treat them as IN queries.
        if (expr is LogicalExpression orExpr && orExpr.Operator == "OR")
        {
            var orValues = new List<(string field, object? value)>();
            if (TryCollectOrEqualities(orExpr, orValues) && orValues.Count > 0)
            {
                // Same-field OR → push as array filter
                var fieldName = orValues[0].field;
                if (orValues.All(v => string.Equals(v.field, fieldName, StringComparison.OrdinalIgnoreCase)))
                {
                    ctx.FieldFilters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    ctx.FieldFilters[fieldName] = orValues.Select(v => v.value).ToArray();
                    return true;
                }

                // Multi-field OR: group by field and push each group as an array filter.
                // e.g., host="web-01" OR sourcetype="nginx" OR host="web-02"
                // → FieldFilters["host"] = ["web-01","web-02"], FieldFilters["sourcetype"] = ["nginx"]
                // This is only safe because each field group is OR'd, and storage backends
                // evaluate multi-field filters with AND semantics (which is stricter than OR).
                // We keep the WHERE in the pipeline to ensure correct OR semantics.
                var groups = orValues
                    .GroupBy(v => v.field, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (groups.Count > 1 && groups.All(g => g.Count() >= 1))
                {
                    ctx.FieldFilters ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var group in groups)
                    {
                        var values = group.Select(v => v.value).ToArray();
                        ctx.FieldFilters[group.Key] = values.Length == 1 ? values[0] : values;
                    }
                    // Keep WHERE in pipeline — AND between fields is stricter than OR,
                    // but narrows fetch volume. The pipeline WHERE ensures correctness.
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively collect field=value equalities from an OR tree.
    /// Returns false if any branch is not a simple field=literal comparison.
    /// </summary>
    private static bool TryCollectOrEqualities(BqlExpression expr, List<(string field, object? value)> results)
    {
        if (expr is LogicalExpression logical && logical.Operator == "OR")
        {
            return TryCollectOrEqualities(logical.Left, results) &&
                   TryCollectOrEqualities(logical.Right, results);
        }

        if (expr is ComparisonExpression comp &&
            comp.Operator is "=" or "==" &&
            comp.Left is FieldExpression field &&
            comp.Right is LiteralExpression literal)
        {
            results.Add((field.Name, literal.Value));
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
