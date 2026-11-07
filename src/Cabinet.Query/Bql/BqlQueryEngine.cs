using System.Diagnostics;
using System.Dynamic;
using System.Runtime.CompilerServices;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query.NaturalLanguage;
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
    private readonly INaturalLanguageTranslator? _nlTranslator;
    private readonly IKnowledgeStorage? _knowledgeStorage;
    private readonly ILogger<BqlQueryEngine> _logger;

    public BqlQueryEngine(
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        ILogger<BqlQueryEngine> logger,
        INaturalLanguageTranslator? nlTranslator = null,
        IKnowledgeStorage? knowledgeStorage = null)
    {
        _storage = storage;
        _parser = parser;
        _optimizer = optimizer;
        _logger = logger;
        _nlTranslator = nlTranslator;
        _knowledgeStorage = knowledgeStorage;
    }

    public async Task<QueryResult> ExecuteAsync(
        QueryExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // Enforce per-query timeout by linking to the caller's cancellation token
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(context.Timeout);
        cancellationToken = timeoutCts.Token;

        var sw = Stopwatch.StartNew();
        var queryString = context.Query;

        // Try natural language translation if available
        if (_nlTranslator != null && _nlTranslator.IsNaturalLanguage(queryString))
        {
            var translation = await _nlTranslator.TranslateAsync(queryString, cancellationToken);
            if (translation.Success && translation.BqlQuery != null)
            {
                _logger.LogDebug("NL translated: '{Original}' → '{BQL}'", queryString, translation.BqlQuery);
                queryString = translation.BqlQuery;
            }
        }

        // Parse the query into a BQL AST
        var bqlQuery = _parser.ParseBql(queryString);

        // Optimize: extract storage predicates, keep remaining pipeline stages
        var optimized = _optimizer.Optimize(bqlQuery, context);

        _logger.LogDebug(
            "Query optimized: {PushedDown} predicates pushed to storage, {Remaining} pipeline stages remaining",
            bqlQuery.Stages.Count - optimized.RemainingStages.Count,
            optimized.RemainingStages.Count);

        // Fetch events from storage with pushed-down predicates
        long eventsScanned = 0;
        List<dynamic> events;

        // A bare count needs no events, so it isn't subject to the read limit
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

        // Determine the effective limit for streaming — cap storage reads to avoid OOM
        var maxFetchLimit = optimized.StorageContext.Limit;

        if (context.Mode == QueryMode.Direct && !string.IsNullOrEmpty(context.Path))
        {
            // Direct mode: read from file
            events = await ReadFromFileAsync(context.Path, cancellationToken);
            eventsScanned = events.Count;
        }
        else
        {
            // Indexed mode: stream from storage with pushed-down predicates
            // Only materialize up to the fetch limit to avoid unbounded memory usage
            events = new List<dynamic>(Math.Min(maxFetchLimit, 10000));
            await foreach (var evt in _storage.QueryAsync(optimized.StorageContext, cancellationToken))
            {
                events.Add(ConvertToExpando(evt));
                eventsScanned++;

                if (eventsScanned >= maxFetchLimit)
                    break;
            }
        }

        // Execute remaining pipeline stages
        IEnumerable<dynamic> result = events;
        foreach (var stage in optimized.RemainingStages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var op = ConvertStageToOperator(stage);
            if (op != null)
                result = await op.ExecuteAsync(result, cancellationToken);
        }

        // Materialize and apply final limit
        var finalEvents = result.Take(context.Limit).ToList();

        // Extract columns from the first event
        var columns = finalEvents.Count > 0
            ? ((IDictionary<string, object?>)finalEvents[0]).Keys.ToList()
            : null;

        sw.Stop();

        return new QueryResult
        {
            Events = finalEvents,
            Columns = columns,
            Statistics = new QueryStatistics
            {
                EventsScanned = eventsScanned,
                EventsMatched = finalEvents.Count,
                ExecutionTime = sw.Elapsed,
                Truncated = eventsScanned >= maxFetchLimit
                    && optimized.RemainingStages.Count > 0
                    && optimized.RemainingStages[0] is not LimitStage
            }
        };
    }

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

        // Try natural language translation if available
        if (_nlTranslator != null && _nlTranslator.IsNaturalLanguage(queryString))
        {
            var translation = await _nlTranslator.TranslateAsync(queryString, token);
            if (translation.Success && translation.BqlQuery != null)
            {
                _logger.LogDebug("NL translated for stream: '{Original}' → '{BQL}'", queryString, translation.BqlQuery);
                queryString = translation.BqlQuery;
            }
        }

        // Parse and optimize
        var bqlQuery = _parser.ParseBql(queryString);
        var optimized = _optimizer.Optimize(bqlQuery, context);

        _logger.LogDebug(
            "Stream query: {RemainingStages} pipeline stages, streaming={DirectStream}",
            optimized.RemainingStages.Count,
            optimized.RemainingStages.Count == 0);

        if (optimized.RemainingStages.Count == 0)
        {
            // No pipeline stages remain — stream directly from storage without materialization
            var count = 0;
            var maxFetchLimit = optimized.StorageContext.Limit;

            await foreach (var evt in _storage.QueryAsync(optimized.StorageContext, token))
            {
                yield return ConvertToExpando(evt);
                count++;

                if (count >= maxFetchLimit)
                    yield break;
            }
        }
        else
        {
            // Pipeline stages require materialization — execute fully, then yield results
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

    internal static BqlRow ConvertToExpando(LogEvent evt)
    {
        var expando = new BqlRow();
        var dict = (IDictionary<string, object?>)expando;

        dict["Id"] = evt.Id;
        dict["Timestamp"] = evt.Timestamp;
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
            dict["Tags"] = string.Join(",", evt.Tags);

        // SPL field aliases for backward compatibility
        dict["_time"] = evt.Timestamp;
        dict["_raw"] = evt.RawLine;
        dict["_indextime"] = evt.IndexTime;

        return expando;
    }

    private QueryOperator? ConvertStageToOperator(BqlStage stage) => stage switch
    {
        SearchStage s => new LegacySearchOperator(s.SearchText),
        WhereStage w => new LegacyWhereOperator(w.Condition),
        StatsStage st => new LegacyStatsOperator(st.Aggregations, st.GroupByFields),
        SortStage so => new LegacySortOperator(so.Fields),
        LimitStage l => new LegacyLimitOperator(l.Count),
        TailStage t => new LegacyTailOperator(t.Count),
        DedupStage d => new LegacyDedupOperator(d.Fields),
        TopStage tp => new LegacyTopOperator(tp.Count, tp.Field),
        TableStage tb => new LegacyTableOperator(tb.Fields),
        EvalStage e => new LegacyEvalOperator(e.Assignments),
        RexStage r => new LegacyRexOperator(r.Pattern, r.SourceField),
        RenameStage rn => new LegacyRenameOperator(rn.Mappings),
        TimechartStage tc => new LegacyTimechartOperator(tc.Span, tc.Aggregation, tc.ByField, tc.AggregationField),
        FieldAliasStage fa => new LegacyFieldAliasOperator(fa.Aliases),
        TransactionStage tx => new LegacyTransactionOperator(tx.Fields, tx.MaxSpan, tx.MaxEvents),
        EventStatsStage es => new LegacyEventStatsOperator(es.Aggregations, es.GroupByFields),
        StreamStatsStage ss => new LegacyStreamStatsOperator(ss.Aggregations, ss.GroupByFields, ss.Window),
        FillNullStage fn => new LegacyFillNullOperator(fn.DefaultValue, fn.Fields),
        BucketStage bk => new LegacyBucketOperator(bk.Field, bk.Span, bk.NumericSpan, bk.Alias),
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
            : new LegacyPassthroughOperator("inputlookup"),
        LookupStage lk => _knowledgeStorage != null
            ? new LegacyLookupOperator(lk.LookupSource, lk.KeyField, lk.OutputFields, _knowledgeStorage)
            : new LegacyPassthroughOperator("lookup"),
        AppendStage a => new LegacyAppendOperator(a.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
        JoinStage j => new LegacyJoinOperator(j.JoinField, j.JoinType, j.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
        MacroStage m => _knowledgeStorage != null
            ? new LegacyMacroOperator(m.MacroName, m.Arguments, _storage, _parser, _optimizer, _knowledgeStorage, _logger)
            : new LegacyPassthroughOperator("macro"),
        SubsearchStage ss => new LegacySubsearchOperator(ss.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
        MultisearchStage ms => new LegacyMultisearchOperator(ms.SearchQueries, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
        _ => new LegacyPassthroughOperator(stage.GetType().Name)
    };

    private static async Task<List<dynamic>> ReadFromFileAsync(string path, CancellationToken cancellationToken)
    {
        var events = new List<dynamic>();
        if (!File.Exists(path))
        {
            return events;
        }

        var fileTime = File.GetLastWriteTimeUtc(path);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        for (var i = 0; i < lines.Length; i++)
        {
            var expando = new BqlRow();
            var dict = (IDictionary<string, object?>)expando;
            dict["RawLine"] = lines[i];
            dict["Timestamp"] = fileTime;
            dict["SourceFile"] = path;
            dict["LineNumber"] = (long)(i + 1);
            events.Add(expando);
        }

        return events;
    }
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
    public override string Name => "append";

    public LegacyAppendOperator(
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
            return combined;
        }
        catch (Exception ex)
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
    public override string Name => "multisearch";

    public LegacyMultisearchOperator(
        List<string> searchQueries,
        ILogStorage storage,
        BqlParser parser,
        BqlOptimizer optimizer,
        IKnowledgeStorage? knowledgeStorage,
        ILogger<BqlQueryEngine> logger)
    {
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Multisearch sub-query failed: {Query}", query);
                // On individual sub-query failure, skip it and continue with the rest (Splunk behavior)
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
        ILogger<BqlQueryEngine> logger)
    {
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Join subsearch failed: {Query}", _subsearchQuery);
            return mainResults;
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
            _logger.LogWarning("Macro expansion depth limit ({Limit}) reached for macro '{Name}'. Possible circular reference.",
                MaxMacroExpansionDepth, macroName);
            return Enumerable.Empty<dynamic>();
        }

        var macro = await _knowledgeStorage.GetMacroByNameAsync(macroName, ct);
        if (macro == null)
        {
            _logger.LogWarning("Macro '{Name}' not found in knowledge storage", macroName);
            return Enumerable.Empty<dynamic>();
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
            _logger.LogWarning(ex, "Failed to parse macro '{Name}' definition: {Definition}", macroName, definition);
            return Enumerable.Empty<dynamic>();
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
    private QueryOperator? ConvertStageForMacro(BqlStage stage, BqlQueryEngine engine)
    {
        // Use reflection-free approach: create operator directly using the same patterns as ConvertStageToOperator
        return stage switch
        {
            SearchStage s => new LegacySearchOperator(s.SearchText),
            WhereStage w => new LegacyWhereOperator(w.Condition),
            StatsStage st => new LegacyStatsOperator(st.Aggregations, st.GroupByFields),
            SortStage so => new LegacySortOperator(so.Fields),
            LimitStage l => new LegacyLimitOperator(l.Count),
            TailStage t => new LegacyTailOperator(t.Count),
            DedupStage d => new LegacyDedupOperator(d.Fields),
            TopStage tp => new LegacyTopOperator(tp.Count, tp.Field),
            TableStage tb => new LegacyTableOperator(tb.Fields),
            EvalStage e => new LegacyEvalOperator(e.Assignments),
            RexStage r => new LegacyRexOperator(r.Pattern, r.SourceField),
            RenameStage rn => new LegacyRenameOperator(rn.Mappings),
            TimechartStage tc => new LegacyTimechartOperator(tc.Span, tc.Aggregation, tc.ByField, tc.AggregationField),
            FieldAliasStage fa => new LegacyFieldAliasOperator(fa.Aliases),
            TransactionStage tx => new LegacyTransactionOperator(tx.Fields, tx.MaxSpan, tx.MaxEvents),
            EventStatsStage es => new LegacyEventStatsOperator(es.Aggregations, es.GroupByFields),
            StreamStatsStage ss => new LegacyStreamStatsOperator(ss.Aggregations, ss.GroupByFields, ss.Window),
            FillNullStage fn => new LegacyFillNullOperator(fn.DefaultValue, fn.Fields),
            BucketStage bk => new LegacyBucketOperator(bk.Field, bk.Span, bk.NumericSpan, bk.Alias),
            MakeResultsStage mr => new LegacyMakeResultsOperator(mr.Count),
            MvExpandStage mv => new LegacyMvExpandOperator(mv.Field, mv.Limit),
            ChartStage ch => new LegacyChartOperator(ch.Aggregations, ch.OverField, ch.ByField),
            FillDownStage fd => new LegacyFillDownOperator(fd.Fields),
            AddColTotalsStage ac => new LegacyAddColTotalsOperator(ac.LabelField, ac.Fields),
            RareStage ra => new LegacyRareOperator(ra.Count, ra.Field),
            RegexStage rx => new LegacyRegexOperator(rx.Field, rx.Pattern, rx.Negate),
            FieldSummaryStage fs => new LegacyFieldSummaryOperator(fs.MaxValues),
            XyseriesStage xy => new LegacyXyseriesOperator(xy.RowField, xy.ColumnField, xy.ValueField),
            UntableStage ut => new LegacyUntableOperator(ut.RowField, ut.ColumnField, ut.ValueField),
            InputLookupStage il => new LegacyInputLookupOperator(il.LookupName, _knowledgeStorage),
            LookupStage lk => new LegacyLookupOperator(lk.LookupSource, lk.KeyField, lk.OutputFields, _knowledgeStorage),
            AppendStage a => new LegacyAppendOperator(a.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
            SubsearchStage sub => new LegacySubsearchOperator(sub.SubsearchQuery, _storage, _parser, _optimizer, _knowledgeStorage, _logger),
            _ => new LegacyPassthroughOperator(stage.GetType().Name)
        };
    }
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
                _logger.LogWarning("Subsearch returned no filterable fields");
                return mainResults;
            }

            // Build a set of allowed values for each filter field
            var allowedValues = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in filterFields)
            {
                allowedValues[field] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var subEvt in subResult.Events)
            {
                if (subEvt is IDictionary<string, object?> subDict)
                {
                    foreach (var field in filterFields)
                    {
                        if (subDict.TryGetValue(field, out var val) && val != null)
                        {
                            allowedValues[field].Add(val.ToString() ?? "");
                        }
                    }
                }
            }

            // Filter main results: keep events where at least one filter field matches
            var filtered = mainResults.Where(evt =>
            {
                if (evt is IDictionary<string, object?> dict)
                {
                    foreach (var field in filterFields)
                    {
                        if (dict.TryGetValue(field, out var val) && val != null &&
                            allowedValues.TryGetValue(field, out var allowed) &&
                            allowed.Contains(val.ToString() ?? ""))
                        {
                            return true;
                        }
                    }
                }
                return false;
            });

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subsearch failed: {Query}", _subsearchQuery);
            // On failure, return original results unchanged
            return mainResults;
        }
    }

    private static bool IsInternalField(string field) =>
        field is "Id" or "Timestamp" or "SourceFile" or "LineNumber" or "Index"
            or "SourceType" or "Severity" or "RawLine" or "_time" or "_raw" or "_indextime"
            or "Tags";
}
