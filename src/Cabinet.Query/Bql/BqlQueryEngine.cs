using System.Diagnostics;
using System.Dynamic;
using System.Runtime.CompilerServices;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Query.Bql;

/// <summary>
/// BQL Query Engine with predicate pushdown optimization.
/// Replaces the default QueryEngine with storage-aware query execution.
/// Supports BQL, SPL, and natural language queries.
/// </summary>
public sealed class BqlQueryEngine : IQueryEngine
{
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private readonly QueryLimits _limits;

    /// <summary>The limits this engine applies, which sub-queries it starts inherit.</summary>
    internal QueryLimits Limits => _limits;

    /// <summary>Events read from storage at a time.</summary>
    private const int PageSize = 10_000;

    public BqlQueryEngine(
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        ILogger<BqlQueryEngine> logger,
        IKnowledgeStorage? knowledgeStorage = null,
        QueryLimits? limits = null)
    {
        _limits = limits ?? new QueryLimits();
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _logger = logger;
        _knowledgeStorage = knowledgeStorage;
    }

    public async Task<QueryResult> ExecuteAsync(
        QueryExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        using var span = Cabinet.Core.Utilities.CabinetDiagnostics.Source.StartActivity("query.execute");

        // Enforce per-query timeout by linking to the caller's cancellation token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(context.Timeout);
        cancellationToken = timeoutCts.Token;

        var sw = Stopwatch.StartNew();
        var queryString = context.Query;


        // Parse the query into a BQL AST
        var bqlQuery = _parser.ParseBql(queryString);

        // Optimize: extract storage predicates, keep remaining pipeline stages
        var optimized = _optimizer.Optimize(bqlQuery, context);

        _logger.LogDebug(
            "Query optimized: {PushedDown} predicates pushed to storage, {Remaining} pipeline stages remaining",
            bqlQuery.Stages.Count - optimized.RemainingStages.Count,
            optimized.RemainingStages.Count);

        // A bare count needs no events
        if (context.Mode != QueryMode.Direct && IsBareCount(optimized.RemainingStages, out var countColumn))
        {
            var count = await _storage.GetCountAsync(optimized.StorageContext, cancellationToken);
            var countRow = new BqlRow { [countColumn] = count };
            sw.Stop();
            return new QueryResult
            {
                Events = new List<dynamic> { countRow },
                Columns = [countColumn],
                Statistics = new QueryStatistics
                {
                    EventsScanned = count,
                    EventsMatched = 1,
                    ExecutionTime = sw.Elapsed
                }
            };
        }

        var budget = new QueryBudget(_limits);

        // Storage that can aggregate itself saves reading every event
        if (await TryAggregateInStorageAsync(optimized, context, budget, sw, span, cancellationToken) is { } aggregated)
        {
            return aggregated;
        }

        var plan = CreatePlan(optimized.RemainingStages, context.Limit, budget);

        // Read only until the first stateful stage has what it needs. It holds what it computes,
        // not the events, so the number of events read isn't limited by memory.
        var eventsScanned = await FeedAsync(plan, optimized.StorageContext, context, budget, cancellationToken);

        // The stages left work on that stage's output, which is bounded
        var result = plan.Sink.Complete();
        foreach (var op in plan.RemainingOperators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await op.ExecuteAsync(result, cancellationToken);
        }

        var finalEvents = Cancellable(result, cancellationToken).Take(context.Limit).ToList();

        // Extract columns from the first event
        var columns = finalEvents.Count > 0
            ? ((IDictionary<string, object?>)finalEvents[0]).Keys.ToList()
            : null;

        sw.Stop();

        span?.SetTag("cabinet.events_scanned", eventsScanned);
        span?.SetTag("cabinet.rows", finalEvents.Count);
        span?.SetTag("cabinet.truncated", budget.Truncated);

        return new QueryResult
        {
            Events = finalEvents,
            Columns = columns,
            Statistics = new QueryStatistics
            {
                EventsScanned = eventsScanned,
                EventsMatched = finalEvents.Count,
                ExecutionTime = sw.Elapsed,
                Truncated = budget.Truncated,
                Approximate = budget.Approximate
            }
        };
    }

    /// <summary>
    /// Runs a leading <c>stats</c> or <c>timechart</c> in storage, if storage can give the same
    /// answer. Returns null to run the query here instead.
    /// </summary>
    private async Task<QueryResult?> TryAggregateInStorageAsync(
        OptimizedQuery optimized,
        QueryExecutionContext context,
        QueryBudget budget,
        Stopwatch sw,
        System.Diagnostics.Activity? span,
        CancellationToken cancellationToken)
    {
        if (context.Mode == QueryMode.Direct || _storage is not IAggregatingLogStorage storage || optimized.RemainingStages.Count == 0)
        {
            return null;
        }

        var request = optimized.RemainingStages[0] switch
        {
            StatsStage stats => BuildAggregateRequest(optimized.StorageContext, stats.Aggregations, stats.GroupByFields, null),
            TimechartStage timechart => BuildAggregateRequest(
                optimized.StorageContext,
                [new AggregateFunction(timechart.Aggregation, timechart.AggregationField)],
                timechart.ByField == null ? [] : [timechart.ByField],
                timechart.Span),

            // top and rare count each value as text, so only storage that writes values as .NET does can
            TopStage { ByField: null } top => CountValuesRequest(optimized.StorageContext, top.Field),
            RareStage rare => CountValuesRequest(optimized.StorageContext, rare.Field),
            _ => null,
        };

        if (request == null)
        {
            return null;
        }

        var aggregate = await storage.AggregateAsync(request, cancellationToken);
        if (aggregate == null)
        {
            return null;
        }

        // As in the pipeline, stats by leaves out events without a by-field
        var rows = optimized.RemainingStages[0] is StatsStage { GroupByFields.Count: > 0 } grouped
            ? aggregate.Rows.Where(values => grouped.GroupByFields.All(f => values.TryGetValue(f, out var v) && v != null)).ToList()
            : aggregate.Rows;
        budget.CheckGroupCount(rows.Count);
        IEnumerable<dynamic> result = optimized.RemainingStages[0] switch
        {
            TopStage top => new LegacyTopOperator(top.Count, top.Field).FromCounts(ValueCounts(rows, top.Field)),
            RareStage rare => new LegacyRareOperator(rare.Count, rare.Field).FromCounts(ValueCounts(rows, rare.Field)),
            _ => rows.Select(values =>
            {
                var row = new BqlRow();
                foreach (var (key, value) in values)
                {
                    row[key] = value;
                }

                return (dynamic)row;
            }).ToList(),
        };

        foreach (var stage in optimized.RemainingStages.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ConvertStageToOperator(stage, budget) is { } op)
            {
                result = await op.ExecuteAsync(result, cancellationToken);
            }
        }

        var finalEvents = Cancellable(result, cancellationToken).Take(context.Limit).ToList();
        sw.Stop();
        span?.SetTag("cabinet.pushed_down", true);
        span?.SetTag("cabinet.events_scanned", aggregate.EventsScanned);

        return new QueryResult
        {
            Events = finalEvents,
            Columns = finalEvents.Count > 0 ? ((IDictionary<string, object?>)finalEvents[0]).Keys.ToList() : null,
            Statistics = new QueryStatistics
            {
                EventsScanned = aggregate.EventsScanned,
                EventsMatched = finalEvents.Count,
                ExecutionTime = sw.Elapsed,
                Truncated = budget.Truncated,
                Approximate = budget.Approximate || aggregate.Approximate,
                PushedDown = true,
            },
        };
    }

    /// <summary>A count of each value of a field, grouped as top and rare group them.</summary>
    private AggregateRequest CountValuesRequest(QueryContext filter, string field)
    {
        var unbounded = filter.Clone();
        unbounded.Limit = QueryContext.MaxLimit;
        return new AggregateRequest
        {
            Filter = unbounded,
            GroupBy = [field],
            Aggregates = [new AggregateSpec(AggregateKind.Count, null, 0, "count")],
            MaxGroups = _limits.MaxGroups,
            ExactTextKeys = true,
        };
    }

    private static IEnumerable<KeyValuePair<string, long>> ValueCounts(IEnumerable<Dictionary<string, object?>> rows, string field) =>
        rows.Where(row => row.TryGetValue(field, out var value) && value != null)
            .Select(row => new KeyValuePair<string, long>(row[field]!.ToString() ?? string.Empty, Convert.ToInt64(row["count"], System.Globalization.CultureInfo.InvariantCulture)));

    private AggregateRequest? BuildAggregateRequest(
        QueryContext filter,
        IReadOnlyList<AggregateFunction> functions,
        IReadOnlyList<string> groupBy,
        TimeSpan? bucket)
    {
        var specs = new List<AggregateSpec>();
        foreach (var function in functions)
        {
            if (ToAggregateSpec(function) is not { } spec)
            {
                return null;
            }

            specs.Add(spec);
        }

        var unbounded = filter.Clone();
        unbounded.Limit = QueryContext.MaxLimit;
        return new AggregateRequest
        {
            Filter = unbounded,
            GroupBy = groupBy,
            Bucket = bucket,
            Aggregates = specs,
            MaxGroups = _limits.MaxGroups,
        };
    }

    /// <summary>The storage form of an aggregate, or null for one storage isn't asked to do.</summary>
    internal static AggregateSpec? ToAggregateSpec(AggregateFunction function)
    {
        if (function.Expression != null)
        {
            return null;
        }

        var name = function.Name.ToLowerInvariant();
        double percentile = 0;
        AggregateKind? kind = name switch
        {
            "count" => AggregateKind.Count,
            "dc" or "distinct_count" => AggregateKind.DistinctCount,
            "sum" => AggregateKind.Sum,
            "avg" or "mean" => AggregateKind.Average,
            "min" => AggregateKind.Min,
            "max" => AggregateKind.Max,
            "stdev" => AggregateKind.SampleDeviation,
            "stdevp" => AggregateKind.PopulationDeviation,
            "variance" => AggregateKind.SampleVariance,
            "varp" => AggregateKind.PopulationVariance,
            _ => null,
        };

        if (name == "median")
        {
            (kind, percentile) = (AggregateKind.Percentile, 50);
        }
        else if (name == "percentile")
        {
            (kind, percentile) = (AggregateKind.Percentile, 95);
        }
        else if ((name.StartsWith("perc", StringComparison.Ordinal) && double.TryParse(name[4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var perc))
            || (name.Length > 1 && name[0] == 'p' && char.IsDigit(name[1]) && double.TryParse(name[1..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out perc)))
        {
            (kind, percentile) = (AggregateKind.Percentile, Math.Clamp(perc, 0, 100));
        }

        if (kind == null || (kind != AggregateKind.Count && function.Field == null))
        {
            return null;
        }

        return new AggregateSpec(kind.Value, function.Field, percentile, function.OutputName);
    }

    /// <summary>
    /// Splits a pipeline into the stages that work row by row, the first stage that holds state,
    /// and whatever follows it.
    /// </summary>
    private PipelinePlan CreatePlan(List<BqlStage> stages, int resultLimit, QueryBudget budget)
    {
        var rowOperators = new List<QueryOperator>();
        var index = 0;
        var readsEvents = stages.Count == 0 || stages[0] is not (MakeResultsStage or InputLookupStage or MultisearchStage);

        if (readsEvents)
        {
            while (index < stages.Count && IsRowStage(stages[index]))
            {
                rowOperators.Add(ConvertStageToOperator(stages[index], budget)!);
                index++;
            }
        }

        IRowSink sink;
        var stage = index < stages.Count ? stages[index] : null;
        var following = index + 1 < stages.Count ? stages[index + 1] : null;
        var maxRows = _limits.MaxRowsInMemory;

        switch (stage)
        {
            case null:
                sink = new TakeSink(resultLimit);
                break;
            case LimitStage limit when limit.Count <= maxRows:
                sink = new TakeSink(limit.Count);
                index++;
                break;
            case TailStage tail when tail.Count <= maxRows:
                sink = new TailSink(tail.Count);
                index++;
                break;
            case SortStage sort when following is LimitStage head && head.Count <= maxRows:
                sink = new TopRowsSink(new LegacySortOperator(sort.Fields), head.Count);
                index += 2;
                break;
            case SortStage sort when following == null && resultLimit <= maxRows:
                sink = new TopRowsSink(new LegacySortOperator(sort.Fields), resultLimit);
                index++;
                break;
            case StatsStage stats:
                sink = new StatsSink(stats.Aggregations, stats.GroupByFields, budget);
                index++;
                break;
            case TimechartStage timechart:
                sink = new LegacyTimechartOperator(timechart.Span, timechart.Aggregation, timechart.ByField, timechart.AggregationField).CreateSink(budget);
                index++;
                break;
            case ChartStage chart:
                sink = new LegacyChartOperator(chart.Aggregations, chart.OverField, chart.ByField).CreateSink(budget);
                index++;
                break;
            case TopStage top:
                sink = new LegacyTopOperator(top.Count, top.Field, top.ByField).CreateSink(budget);
                index++;
                break;
            case RareStage rare:
                sink = new LegacyRareOperator(rare.Count, rare.Field).CreateSink(budget);
                index++;
                break;
            default:
                sink = new MaterializeSink(budget);
                break;
        }

        var remaining = stages
            .Skip(index)
            .Select(remainingStage => ConvertStageToOperator(remainingStage, budget))
            .Where(op => op != null)
            .Select(op => op!)
            .ToList();

        return new PipelinePlan(readsEvents, rowOperators, sink, remaining);
    }

    /// <summary>Stages that handle each row on its own, so they can run a batch at a time.</summary>
    private static bool IsRowStage(BqlStage stage) => stage is
        SearchStage or WhereStage or EvalStage or RexStage or RenameStage or RegexStage or IplocationStage
        or FieldAliasStage or FillNullStage or BucketStage or TableStage;

    private async Task<long> FeedAsync(
        PipelinePlan plan,
        QueryContext storageContext,
        QueryExecutionContext context,
        QueryBudget budget,
        CancellationToken cancellationToken)
    {
        if (!plan.ReadsEvents)
        {
            return 0;
        }

        var scanned = 0L;
        var batches = context.Mode == QueryMode.Direct && !string.IsNullOrEmpty(context.Path)
            ? ReadFileBatchesAsync(context.Path, cancellationToken)
            : ReadStorageBatchesAsync(storageContext, plan, cancellationToken);

        await foreach (var batch in batches.WithCancellation(cancellationToken))
        {
            scanned += batch.Count;

            IEnumerable<dynamic> rows = batch;
            foreach (var op in plan.RowOperators)
            {
                rows = await op.ExecuteAsync(rows, cancellationToken);
            }

            foreach (object row in Cancellable(rows, cancellationToken))
            {
                plan.Sink.Add(row);
                if (plan.Sink.IsDone)
                {
                    return scanned;
                }
            }

            if (_limits.MaxEventsScanned > 0 && scanned >= _limits.MaxEventsScanned)
            {
                budget.Truncated = true;
                return scanned;
            }
        }

        return scanned;
    }

    /// <summary>
    /// Reads every match a page at a time. Each page continues from the last event of the one
    /// before, so a deep page costs the same as the first, and events arriving during the read
    /// can't shift rows between pages.
    /// </summary>
    private async IAsyncEnumerable<List<dynamic>> ReadStorageBatchesAsync(
        QueryContext template,
        PipelinePlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EventCursor? after = null;

        // With filters ahead of a sink that needs few rows, pages start near that many and grow,
        // so "find x | search ... | head 100" doesn't read 10,000 matches to keep 100
        var growing = plan.Sink.Remaining is { } needed ? Math.Clamp(needed, 100, PageSize) : PageSize;

        while (true)
        {
            // Without filters ahead of it, a sink needing few rows needs only that many events.
            var wanted = plan.RowOperators.Count == 0 ? plan.Sink.Remaining : null;
            var pageSize = wanted is { } remaining ? Math.Clamp(remaining, 1, PageSize) : growing;
            growing = Math.Min(PageSize, growing * 4);

            var page = template.Clone();
            page.Limit = pageSize;
            page.After = after;

            var batch = new List<dynamic>(pageSize);
            LogEvent? last = null;
            await foreach (var evt in _storage.QueryAsync(page, cancellationToken))
            {
                batch.Add(ConvertToExpando(evt));
                last = evt;
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }

            if (batch.Count < pageSize || last == null)
            {
                yield break;
            }

            after = new EventCursor(last.Timestamp, last.Id);
        }
    }

    private static async IAsyncEnumerable<List<dynamic>> ReadFileBatchesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        var fileTime = File.GetLastWriteTimeUtc(path);
        var lineNumber = 0L;
        var batch = new List<dynamic>(PageSize);
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            lineNumber++;
            var row = new BqlRow();
            var dict = (IDictionary<string, object?>)row;
            dict["RawLine"] = line;
            dict["Timestamp"] = fileTime;
            dict["SourceFile"] = path;
            dict["LineNumber"] = lineNumber;
            batch.Add(row);

            if (batch.Count == PageSize)
            {
                yield return batch;
                batch = new List<dynamic>(PageSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Stages evaluate lazily, so this is where a slow one notices the query's time is up.
    /// </summary>
    private static IEnumerable<dynamic> Cancellable(IEnumerable<dynamic> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private sealed record PipelinePlan(
        bool ReadsEvents,
        List<QueryOperator> RowOperators,
        IRowSink Sink,
        List<QueryOperator> RemainingOperators);

    /// <summary>
    /// Stream query results without materializing all events in memory.
    /// For simple queries (all predicates pushed to storage, no remaining pipeline stages),
    /// events are yielded directly from storage as they arrive.
    /// For queries with pipeline stages (stats, sort, eval, etc.), events are materialized
    /// internally to apply the pipeline, then yielded one at a time.
    /// </summary>
    public async IAsyncEnumerable<dynamic> ExecuteStreamAsync(
        QueryExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Enforce per-query timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(context.Timeout);
        var token = timeoutCts.Token;

        var queryString = context.Query;


        // Parse and optimize
        var bqlQuery = _parser.ParseBql(queryString);
        var optimized = _optimizer.Optimize(bqlQuery, context);

        _logger.LogDebug(
            "Stream query: {RemainingStages} pipeline stages, streaming={DirectStream}",
            optimized.RemainingStages.Count,
            optimized.RemainingStages.Count == 0);

        if (optimized.RemainingStages.All(IsRowStage))
        {
            // Only row stages remain, so nothing has to be held in memory
            var count = 0;
            var budget = new QueryBudget(_limits);
            var plan = CreatePlan(optimized.RemainingStages, context.Limit, budget);
            await foreach (var batch in ReadStorageBatchesAsync(optimized.StorageContext, plan with { Sink = new MaterializeSink(budget) }, token))
            {
                IEnumerable<dynamic> rows = batch;
                foreach (var op in plan.RowOperators)
                {
                    rows = await op.ExecuteAsync(rows, token);
                }

                foreach (var row in Cancellable(rows, token))
                {
                    yield return row;
                    if (++count >= context.Limit)
                    {
                        yield break;
                    }
                }
            }
        }
        else
        {
            // A stage holds state, so the results only exist once it has seen its whole input
            var result = await ExecuteAsync(context, token);
            foreach (var evt in result.Events)
            {
                token.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }

    private static bool IsBareCount(List<BqlStage> stages, out string column)
    {
        column = string.Empty;
        if (stages is not [StatsStage { GroupByFields.Count: 0, Aggregations: [{ Name: "count", Field: null } aggregate] }])
        {
            return false;
        }

        column = aggregate.OutputName;
        return true;
    }

    /// <summary>Builds a query row from an event.</summary>
    public static BqlRow ConvertToExpando(LogEvent evt)
    {
        var expando = new BqlRow();
        var dict = (IDictionary<string, object?>)expando;

        dict["Id"] = evt.Id;
        // Every stored time is UTC; say so, so the zone is written out
        var timestamp = evt.Timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(evt.Timestamp, DateTimeKind.Utc) : evt.Timestamp.ToUniversalTime();
        dict["Timestamp"] = timestamp;
        dict["SourceType"] = evt.SourceType;
        dict["Host"] = evt.Host ?? "";
        dict["Index"] = evt.Index;
        dict["Severity"] = (int)evt.Severity;
        dict["RawLine"] = evt.RawLine;
        dict["SourceFile"] = evt.SourceFile ?? "";
        dict["LineNumber"] = evt.LineNumber;

        // Merge custom fields. Built-in columns keep their values, since names match case-insensitively.
        foreach (var field in evt.Fields)
            dict.TryAdd(field.Key, field.Value);

        // Every event has a message, even when its parser didn't extract one
        dict.TryAdd("Message", evt.RawLine);

        // Tags as comma-separated string
        if (evt.Tags.Count > 0)
            dict["Tags"] = evt.Tags.ToArray();

        // SPL field aliases for backward compatibility
        dict["_time"] = timestamp;
        dict["_raw"] = evt.RawLine;
        dict["_indextime"] = evt.IndexTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(evt.IndexTime, DateTimeKind.Utc) : evt.IndexTime.ToUniversalTime();

        return expando;
    }

    internal QueryOperator? ConvertStageToOperator(BqlStage stage, QueryBudget budget)
    {
        var op = CreateOperator(stage, budget);
        if (op != null)
        {
            op.Budget = budget;
        }

        return op;
    }

    private QueryOperator? CreateOperator(BqlStage stage, QueryBudget budget) => stage switch
    {
        SearchStage s => new LegacySearchOperator(s.SearchText),
        WhereStage w => new LegacyWhereOperator(w.Condition),
        StatsStage st => new LegacyStatsOperator(st.Aggregations, st.GroupByFields),
        SortStage so => new LegacySortOperator(so.Fields),
        LimitStage l => new LegacyLimitOperator(l.Count),
        TailStage t => new LegacyTailOperator(t.Count),
        DedupStage d => new LegacyDedupOperator(d.Fields),
        TopStage tp => new LegacyTopOperator(tp.Count, tp.Field, tp.ByField),
        TableStage tb => new LegacyTableOperator(tb.Fields),
        EvalStage e => new LegacyEvalOperator(e.Assignments),
        RexStage r => new LegacyRexOperator(r.Pattern, r.SourceField),
        RenameStage rn => new LegacyRenameOperator(rn.Mappings),
        TimechartStage tc => new LegacyTimechartOperator(tc.Span, tc.Aggregation, tc.ByField, tc.AggregationField),
        FieldAliasStage fa => new LegacyFieldAliasOperator(fa.Aliases),
        TransactionStage tx => new LegacyTransactionOperator(tx.Fields, tx.MaxSpan, tx.MaxEvents, tx.StartsWith, tx.EndsWith),
        EventStatsStage es => new LegacyEventStatsOperator(es.Aggregations, es.GroupByFields),
        StreamStatsStage ss => new LegacyStreamStatsOperator(ss.Aggregations, ss.GroupByFields, ss.Window),
        FillNullStage fn => new LegacyFillNullOperator(fn.DefaultValue, fn.Fields),
        BucketStage bk => new LegacyBucketOperator(bk.Field, bk.Span, bk.NumericSpan, bk.Alias),
        IplocationStage ip => new LegacyIplocationOperator(ip),
        ClusterStage cl => new LegacyClusterOperator(cl),
        MakeResultsStage mr => new LegacyMakeResultsOperator(mr.Count),
        MvExpandStage mv => new LegacyMvExpandOperator(mv.Field, mv.Limit),
        ChartStage ch => new LegacyChartOperator(ch.Aggregations, ch.OverField, ch.ByField),
        FillDownStage fd => new LegacyFillDownOperator(fd.Fields),
        AddColTotalsStage ac => new LegacyAddColTotalsOperator(ac.LabelField, ac.Fields),
        RareStage r => new LegacyRareOperator(r.Count, r.Field),
        RegexStage rx => new LegacyRegexOperator(rx.Field, rx.Pattern, rx.Negate),
        FieldSummaryStage fs => new LegacyFieldSummaryOperator(fs.MaxValues),
        XyseriesStage xy => new LegacyXyseriesOperator(xy.RowField, xy.ColumnField, xy.ValueField),
        UntableStage ut => new LegacyUntableOperator(ut.RowField, ut.ColumnField, ut.ValueField),
        InputLookupStage il => _knowledgeStorage != null
            ? new LegacyInputLookupOperator(il.LookupName, _knowledgeStorage)
            : throw new QueryExecutionException("inputlookup needs knowledge objects, which aren't available here"),
        LookupStage lk => _knowledgeStorage != null
            ? new LegacyLookupOperator(lk.LookupSource, lk.KeyField, lk.OutputFields, _knowledgeStorage)
            : throw new QueryExecutionException("lookup needs knowledge objects, which aren't available here"),
        AppendStage a => new LegacyAppendOperator(a.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger, budget),
        JoinStage j => new LegacyJoinOperator(j.JoinField, j.JoinType, j.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger, budget),
        MacroStage m => _knowledgeStorage != null
            ? new LegacyMacroOperator(m.MacroName, m.Arguments, _storage, _parser, _optimizer, _knowledgeStorage, _logger)
            : throw new QueryExecutionException("macros need knowledge objects, which aren't available here"),
        SubsearchStage ss => new LegacySubsearchOperator(ss.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
        MultisearchStage ms => new LegacyMultisearchOperator(ms.SearchQueries, _storage, _parser, _optimizer, _knowledgeStorage, _logger, budget),
        _ => throw new QueryExecutionException($"The {stage.GetType().Name} stage isn't supported here")
    };

}

/// <summary>
/// Append operator — executes a subsearch and appends its results to the current result set.
/// Equivalent to Splunk's | append [subsearch].
/// </summary>
internal sealed class LegacyAppendOperator : QueryOperator
{
    private readonly string _subsearchQuery;
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private readonly QueryBudget _budget;
    public override string Name => "append";

    public LegacyAppendOperator(
        string subsearchQuery,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage? knowledgeStorage,
        ILogger<BqlQueryEngine> logger,
        QueryBudget budget)
    {
        _budget = budget;
        _subsearchQuery = subsearchQuery;
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _knowledgeStorage = knowledgeStorage;
        _logger = logger;
    }

    private List<dynamic> CapRows(List<dynamic> rows)
    {
        if (rows.Count <= _budget.Limits.MaxRowsInMemory)
        {
            return rows;
        }

        _budget.Truncated = true;
        rows.RemoveRange(_budget.Limits.MaxRowsInMemory, rows.Count - _budget.Limits.MaxRowsInMemory);
        return rows;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        try
        {
            // Parse and execute the subsearch using a new engine instance
            var subEngine = new BqlQueryEngine(_storage, _parser, _optimizer,
                _logger, knowledgeStorage: _knowledgeStorage);

            var context = new QueryExecutionContext
            {
                Query = _subsearchQuery,
                Limit = 50_000
            };

            var subResult = await subEngine.ExecuteAsync(context, ct);

            // Append subsearch results to the main results
            var combined = new List<dynamic>(mainResults.Count + subResult.Events.Count);
            combined.AddRange(mainResults);
            combined.AddRange(subResult.Events);
            return CapRows(combined);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or QueryLimitExceededException))
        {
            _logger.LogWarning(ex, "Append subsearch failed: {Query}", _subsearchQuery);
            // On subsearch failure, return original results unchanged (Splunk behavior)
            return mainResults;
        }
    }
}

/// <summary>
/// Multisearch operator — runs multiple independent search queries and unions their results.
/// Equivalent to Splunk's | multisearch [search "q1"] [search "q2"].
/// Unlike append, multisearch ignores the incoming pipeline events and produces
/// a fresh result set from the union of all sub-searches.
/// </summary>
internal sealed class LegacyMultisearchOperator : QueryOperator
{
    private readonly List<string> _searchQueries;
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private readonly QueryBudget _budget;
    public override string Name => "multisearch";

    public LegacyMultisearchOperator(
        List<string> searchQueries,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage? knowledgeStorage,
        ILogger<BqlQueryEngine> logger,
        QueryBudget budget)
    {
        _budget = budget;
        _searchQueries = searchQueries;
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _knowledgeStorage = knowledgeStorage;
        _logger = logger;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        // Multisearch replaces the incoming pipeline with the union of all sub-searches
        var combined = new List<dynamic>();

        foreach (var query in _searchQueries)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Prefix with "find" so the sub-query is a valid BQL search
                var fullQuery = $"find {query}";

                var subEngine = new BqlQueryEngine(_storage, _parser, _optimizer,
                    _logger, knowledgeStorage: _knowledgeStorage);

                var context = new QueryExecutionContext
                {
                    Query = fullQuery,
                    Limit = 50_000
                };

                var subResult = await subEngine.ExecuteAsync(context, ct);
                combined.AddRange(subResult.Events);

                // Each sub-search is limited, but their sum has to be as well.
                if (combined.Count > _budget.Limits.MaxRowsInMemory)
                {
                    _budget.Truncated = true;
                    combined.RemoveRange(_budget.Limits.MaxRowsInMemory, combined.Count - _budget.Limits.MaxRowsInMemory);
                    break;
                }
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or QueryLimitExceededException))
            {
                // A missing leg would quietly leave its results out of the answer
                throw new QueryExecutionException($"A multisearch search failed ({query}): {ex.Message}");
            }
        }

        return combined;
    }
}

/// <summary>
/// Join operator — executes a subsearch and performs a hash join with the main results.
/// Supports inner, left, and outer join types. Equivalent to Splunk's | join.
/// </summary>
internal sealed class LegacyJoinOperator : QueryOperator
{
    private readonly string _joinField;
    private readonly string _joinType;
    private readonly string _subsearchQuery;
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private readonly QueryBudget _budget;
    private const int MaxSubsearchResults = 50_000;
    public override string Name => "join";

    public LegacyJoinOperator(
        string joinField,
        string joinType,
        string subsearchQuery,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage? knowledgeStorage,
        ILogger<BqlQueryEngine> logger,
        QueryBudget budget)
    {
        _budget = budget;
        _joinField = joinField;
        _joinType = joinType;
        _subsearchQuery = subsearchQuery;
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _knowledgeStorage = knowledgeStorage;
        _logger = logger;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        try
        {
            // Execute the subsearch
            var subEngine = new BqlQueryEngine(_storage, _parser, _optimizer,
                _logger, knowledgeStorage: _knowledgeStorage);

            var context = new QueryExecutionContext
            {
                Query = _subsearchQuery,
                Limit = MaxSubsearchResults
            };

            var subResult = await subEngine.ExecuteAsync(context, ct);

            // Build a hash lookup from subsearch results keyed by the join field
            var subLookup = new Dictionary<string, List<IDictionary<string, object?>>>();
            foreach (var subEvt in subResult.Events)
            {
                if (subEvt is IDictionary<string, object?> subDict)
                {
                    var keyVal = GetFieldValue(subDict, _joinField);
                    if (keyVal != null)
                    {
                        var key = keyVal.ToString() ?? "";
                        if (!subLookup.TryGetValue(key, out var list))
                        {
                            list = new List<IDictionary<string, object?>>();
                            subLookup[key] = list;
                        }
                        list.Add(subDict);
                    }
                }
            }

            // Perform the join
            var results = new List<dynamic>();

            foreach (var mainEvt in mainResults)
            {
                if (mainEvt is IDictionary<string, object?> mainDict)
                {
                    var keyVal = GetFieldValue(mainDict, _joinField);
                    var key = keyVal?.ToString() ?? "";

                    if (subLookup.TryGetValue(key, out var matches))
                    {
                        // For each match, merge the subsearch fields into the main event
                        foreach (var match in matches)
                        {
                            var merged = MergeEvents(mainDict, match);
                            results.Add(merged);
                        }
                    }
                    else if (_joinType is "left" or "outer")
                    {
                        // Left/outer join: keep main event even without match
                        results.Add(mainEvt);
                    }
                    // Inner join: skip events without matches
                }
            }

            // Outer join: add unmatched subsearch results
            if (_joinType == "outer")
            {
                var mainKeys = new HashSet<string>();
                foreach (var mainEvt in mainResults)
                {
                    if (mainEvt is IDictionary<string, object?> mainDict)
                    {
                        var kv = GetFieldValue(mainDict, _joinField);
                        if (kv != null) mainKeys.Add(kv.ToString() ?? "");
                    }
                }

                foreach (var kvp in subLookup)
                {
                    if (!mainKeys.Contains(kvp.Key))
                    {
                        foreach (var subEvt in kvp.Value)
                        {
                            var expando = new BqlRow();
                            var dict = (IDictionary<string, object?>)expando;
                            foreach (var field in subEvt)
                                dict[field.Key] = field.Value;
                            results.Add(expando);
                        }
                    }
                }
            }

            return results;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or QueryLimitExceededException))
        {
            throw new QueryExecutionException($"The join's search failed: {ex.Message}");
        }
    }

    private static object? GetFieldValue(IDictionary<string, object?> dict, string field)
    {
        if (dict.TryGetValue(field, out var val)) return val;
        // Case-insensitive fallback
        foreach (var kvp in dict)
        {
            if (kvp.Key.Equals(field, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }
        return null;
    }

    private static dynamic MergeEvents(IDictionary<string, object?> main, IDictionary<string, object?> sub)
    {
        var expando = new BqlRow();
        var dict = (IDictionary<string, object?>)expando;

        // Copy main event fields first
        foreach (var field in main)
            dict[field.Key] = field.Value;

        // Overlay subsearch fields (don't overwrite existing main fields)
        foreach (var field in sub)
        {
            if (!dict.ContainsKey(field.Key))
                dict[field.Key] = field.Value;
        }

        return expando;
    }
}

/// <summary>
/// Macro operator — expands a named macro from knowledge storage and executes its pipeline stages.
/// The macro definition is a BQL pipeline fragment (e.g., "where severity >= 4 | stats count by host")
/// which is parsed, and its stages are executed sequentially on the input events.
/// Supports $param$ substitution for macro parameters.
/// </summary>
internal sealed class LegacyMacroOperator : QueryOperator
{
    private readonly string _macroName;
    private readonly List<string> _arguments;
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private const int MaxMacroExpansionDepth = 10;
    public override string Name => "macro";

    public LegacyMacroOperator(
        string macroName,
        List<string> arguments,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage knowledgeStorage,
        ILogger<BqlQueryEngine> logger)
    {
        _macroName = macroName;
        _arguments = arguments;
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _knowledgeStorage = knowledgeStorage;
        _logger = logger;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        return await ExpandAndExecuteAsync(input, _macroName, _arguments, 0, ct);
    }

    private async Task<IEnumerable<dynamic>> ExpandAndExecuteAsync(
        IEnumerable<dynamic> input,
        string macroName,
        List<string> arguments,
        int depth,
        CancellationToken ct)
    {
        if (depth >= MaxMacroExpansionDepth)
        {
            throw new QueryExecutionException($"Macro '{macroName}' expands more than {MaxMacroExpansionDepth} levels deep; it probably refers to itself");
        }

        var macro = await _knowledgeStorage.GetMacroByNameAsync(macroName, ct);
        if (macro == null)
        {
            throw new QueryExecutionException($"There's no macro named '{macroName}'");
        }

        // Substitute parameters: replace $param_name$ with argument values
        var definition = SubstituteParameters(macro.Definition, macro.Parameters, arguments);

        _logger.LogDebug("Expanding macro '{Name}' with definition: {Definition}", macroName, definition);

        try
        {
            // Parse the macro definition as a BQL pipeline fragment.
            // Prepend "find *" to make it a valid full query, then extract only the pipeline stages.
            var wrappedQuery = $"find * | {definition}";
            var parsed = _parser.ParseBql(wrappedQuery);

            // Execute each stage of the macro pipeline on the input
            IEnumerable<dynamic> result = input;
            foreach (var stage in parsed.Stages)
            {
                ct.ThrowIfCancellationRequested();

                // Handle nested macro expansion
                if (stage is MacroStage nestedMacro)
                {
                    result = await ExpandAndExecuteAsync(
                        result, nestedMacro.MacroName, nestedMacro.Arguments, depth + 1, ct);
                    continue;
                }

                // Skip the initial search stage we prepended
                if (stage is SearchStage s && s.SearchText == "*" && stage == parsed.Stages[0])
                    continue;

                var engine = new BqlQueryEngine(_storage, _parser, _optimizer, _logger, knowledgeStorage: _knowledgeStorage);
                var op = ConvertStageForMacro(stage, engine);
                if (op != null)
                    result = await op.ExecuteAsync(result, ct);
            }

            return result;
        }
        catch (QueryParseException ex)
        {
            throw new QueryExecutionException($"Macro '{macroName}' doesn't parse: {ex.Message}");
        }
    }

    private static string SubstituteParameters(string definition, List<string> parameterNames, List<string> arguments)
    {
        var result = definition;
        for (var i = 0; i < parameterNames.Count && i < arguments.Count; i++)
        {
            // Sanitize argument: strip pipe characters to prevent BQL injection
            var sanitized = arguments[i].Replace("|", "");
            result = result.Replace($"${parameterNames[i]}$", sanitized, StringComparison.OrdinalIgnoreCase);
        }

        // Guard against macro expansion producing excessively long queries
        if (result.Length > 100_000)
            throw new QueryParseException("Macro expansion produced an excessively large query", "macro expansion");

        return result;
    }

    /// <summary>
    /// Creates a QueryOperator for a stage within a macro expansion.
    /// Uses the engine instance's ConvertStageToOperator via a helper engine.
    /// </summary>
    private static QueryOperator? ConvertStageForMacro(BqlStage stage, BqlQueryEngine engine) =>
        // The engine's own map, so a macro supports every stage a query does (join, multisearch, lookups...)
        engine.ConvertStageToOperator(stage, new QueryBudget(engine.Limits));
}

/// <summary>
/// Subsearch operator — executes an inner query and filters the outer result set
/// by matching field values from the subsearch results.
/// If the subsearch returns a single column, events are filtered where that column's values
/// match. If multiple columns, events must match all field values in at least one subsearch row.
/// </summary>
internal sealed class LegacySubsearchOperator : QueryOperator
{
    private readonly string _subsearchQuery;
    private readonly ILogStorage _storage;
    private readonly BqlParser _parser;
    private readonly BqlOptimizer _optimizer;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;
    private const int MaxSubsearchResults = 50_000;
    public override string Name => "subsearch";

    public LegacySubsearchOperator(
        string subsearchQuery,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage? knowledgeStorage,
        ILogger<BqlQueryEngine> logger)
    {
        _subsearchQuery = subsearchQuery;
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _knowledgeStorage = knowledgeStorage;
        _logger = logger;
    }

    public override async Task<IEnumerable<dynamic>> ExecuteAsync(IEnumerable<dynamic> input, CancellationToken ct = default)
    {
        var mainResults = input.ToList();

        if (string.IsNullOrWhiteSpace(_subsearchQuery))
            return mainResults;

        try
        {
            // Execute the subsearch
            var subEngine = new BqlQueryEngine(_storage, _parser, _optimizer,
                _logger, knowledgeStorage: _knowledgeStorage);

            var context = new QueryExecutionContext
            {
                Query = _subsearchQuery,
                Limit = MaxSubsearchResults
            };

            var subResult = await subEngine.ExecuteAsync(context, ct);

            if (subResult.Events.Count == 0)
            {
                _logger.LogDebug("Subsearch returned no results, filtering out all events");
                return Enumerable.Empty<dynamic>();
            }

            // Determine the filter fields from the subsearch results
            var firstEvent = (IDictionary<string, object?>)subResult.Events[0];

            // Use non-internal fields from the subsearch result as filter criteria.
            // Exclude common metadata fields that wouldn't be useful for filtering.
            var filterFields = firstEvent.Keys
                .Where(k => !IsInternalField(k))
                .ToList();

            if (filterFields.Count == 0)
            {
                throw new QueryExecutionException("The subsearch returned no fields to filter by; end it with | fields name or | table name");
            }

            // Each subsearch row means "all of these fields have these values"; rows are alternatives
            static string Key(IDictionary<string, object?> row, List<string> fields) =>
                string.Join('\u001f', fields.Select(f => row.TryGetValue(f, out var v) && v != null ? v.ToString()?.ToLowerInvariant() ?? string.Empty : "\u0000"));

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var subEvt in subResult.Events)
            {
                if (subEvt is IDictionary<string, object?> subDict)
                {
                    allowed.Add(Key(subDict, filterFields));
                }
            }

            var filtered = mainResults.Where(evt => evt is IDictionary<string, object?> dict && allowed.Contains(Key(dict, filterFields)));

            return filtered;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or QueryLimitExceededException))
        {
            throw new QueryExecutionException($"The subsearch failed: {ex.Message}");
        }
    }

    private static bool IsInternalField(string field) =>
        field is "Id" or "Timestamp" or "SourceFile" or "LineNumber" or "Index"
            or "SourceType" or "Severity" or "RawLine" or "Message" or "_time" or "_raw" or "_indextime"
            or "Tags";
}
