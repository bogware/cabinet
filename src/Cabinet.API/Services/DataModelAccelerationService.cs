using System.Collections.Concurrent;
using System.Dynamic;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>
/// Data model acceleration service — pre-aggregates events into summary tables
/// for fast tstats-like queries. Background service rebuilds summaries periodically.
/// On startup, loads persisted models and summaries from IDataModelStorage so that
/// acceleration state survives restarts.
/// </summary>
public sealed class DataModelAccelerationService : BackgroundService
{
    private readonly ConcurrentDictionary<string, DataModel> _models = new();
    private readonly ConcurrentDictionary<string, List<DataModelSummary>> _summaries = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILeaderLease _lease;
    private readonly IDataModelStorage _storage;
    private readonly ILogger<DataModelAccelerationService> _logger;
    private readonly TimeSpan RebuildInterval;

    public DataModelAccelerationService(
        IServiceScopeFactory scopeFactory,
        IDataModelStorage storage,
        ILogger<DataModelAccelerationService> logger,
        ILeaderLease? lease = null,
        Infrastructure.ServerLimits? limits = null)
    {
        RebuildInterval = TimeSpan.FromMinutes(Math.Max(1, (limits ?? new Infrastructure.ServerLimits()).DataModelRebuildMinutes));
        _lease = lease ?? new SingleInstanceLease();
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>Register a data model for acceleration and persist to storage</summary>
    public async Task<DataModel> RegisterModelAsync(DataModel model, CancellationToken cancellationToken = default)
    {
        _models[model.Id] = model;
        await _storage.SaveModelAsync(model, cancellationToken);
        _logger.LogInformation("Registered data model '{Name}' (id: {Id})", model.Name, model.Id);
        return model;
    }

    /// <summary>Get a registered data model</summary>
    public DataModel? GetModel(string id) => _models.TryGetValue(id, out var m) ? m : null;

    /// <summary>List all registered data models</summary>
    public IReadOnlyList<DataModel> ListModels() => _models.Values.ToList();

    /// <summary>Delete a data model and its summaries</summary>
    public async Task<bool> DeleteModelAsync(string id, CancellationToken cancellationToken = default)
    {
        _summaries.TryRemove(id, out _);
        var removed = _models.TryRemove(id, out _);
        await _storage.DeleteModelAsync(id, cancellationToken);
        return removed;
    }

    /// <summary>
    /// Execute a tstats-like query against pre-aggregated summaries.
    /// Returns aggregated results from the summary table rather than scanning raw events.
    /// </summary>
    public Task<QueryResult> ExecuteTstatsAsync(
        string modelId,
        string? whereClause = null,
        List<string>? groupByFields = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        if (!_summaries.TryGetValue(modelId, out var summaries))
        {
            return Task.FromResult(new QueryResult
            {
                Events = new List<dynamic>(),
                Statistics = new QueryStatistics
                {
                    EventsScanned = 0,
                    EventsMatched = 0,
                    ExecutionTime = TimeSpan.Zero
                }
            });
        }

        var filtered = summaries.AsEnumerable();

        // Apply time range filter
        if (startTime.HasValue)
            filtered = filtered.Where(s => s.TimeBucket >= startTime.Value);
        if (endTime.HasValue)
            filtered = filtered.Where(s => s.TimeBucket <= endTime.Value);

        // Apply field-based grouping
        List<dynamic> results;
        if (groupByFields != null && groupByFields.Count > 0)
        {
            results = filtered
                .GroupBy(s =>
                {
                    var key = new Dictionary<string, string>();
                    foreach (var field in groupByFields)
                    {
                        s.FieldValues.TryGetValue(field, out var val);
                        key[field] = val ?? "";
                    }
                    return string.Join("|", key.Values);
                })
                .Select(g =>
                {
                    var expando = new ExpandoObject();
                    var dict = (IDictionary<string, object?>)expando;
                    dict["count"] = g.Sum(s => s.Count);

                    // Extract field values from the first entry in the group
                    var first = g.First();
                    foreach (var field in groupByFields)
                    {
                        first.FieldValues.TryGetValue(field, out var val);
                        dict[field] = val ?? "";
                    }

                    dict["_time"] = g.Min(s => s.TimeBucket);
                    return (dynamic)expando;
                })
                .ToList();
        }
        else
        {
            results = new List<dynamic>
            {
                CreateSummaryExpando(filtered.Sum(s => s.Count),
                    filtered.MinBy(s => s.TimeBucket)?.TimeBucket,
                    filtered.MaxBy(s => s.TimeBucket)?.TimeBucket)
            };
        }

        return Task.FromResult(new QueryResult
        {
            Events = results,
            Statistics = new QueryStatistics
            {
                EventsScanned = summaries.Count,
                EventsMatched = results.Count,
                ExecutionTime = TimeSpan.Zero
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data model acceleration service started");

        // Load persisted models and summaries from storage on startup
        await LoadFromStorageAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(RebuildInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Data model acceleration service stopped");
    }

    /// <summary>
    /// One pass. The leader rebuilds and saves each model's summaries. The others only read what
    /// the leader saved: saving their own copies would put back summaries older than the leader's.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_lease.IsLeader)
            {
                await LoadFromStorageAsync(stoppingToken, replace: true);
                return;
            }

            foreach (var model in _models.Values.Where(m => m.AccelerationEnabled))
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (await RebuildModelAsync(model, stoppingToken))
                {
                    await _storage.SaveModelAsync(model, stoppingToken);
                    if (_summaries.TryGetValue(model.Id, out var summaries))
                    {
                        await _storage.SaveSummariesAsync(model.Id, summaries, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Data model acceleration pass cancelled by shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in data model acceleration loop");
        }
    }

    /// <summary>
    /// Load all models and their summaries from persistent storage into memory. With
    /// <paramref name="replace"/>, models deleted elsewhere are dropped too.
    /// </summary>
    private async Task LoadFromStorageAsync(CancellationToken ct, bool replace = false)
    {
        try
        {
            var models = await _storage.GetAllModelsAsync(ct);
            var loaded = 0;

            foreach (var model in models)
            {
                _models[model.Id] = model;

                var summaries = await _storage.GetSummariesAsync(model.Id, ct);
                if (summaries.Count > 0)
                {
                    _summaries[model.Id] = summaries;
                }

                loaded++;
            }

            if (replace)
            {
                var ids = models.Select(m => m.Id).ToHashSet();
                foreach (var gone in _models.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    _models.TryRemove(gone, out _);
                    _summaries.TryRemove(gone, out _);
                }
            }
            else if (loaded > 0)
            {
                _logger.LogInformation(
                    "Loaded {Count} data models from persistent storage", loaded);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Failed to load data models from storage, keeping what's in memory");
        }
    }

    /// <summary>Most summary rows one model keeps.</summary>
    internal const int MaxSummaryRows = 100_000;

    /// <summary>Whether a field name can go into the summary query as written.</summary>
    internal static bool IsValidFieldName(string field) =>
        System.Text.RegularExpressions.Regex.IsMatch(field, @"^[A-Za-z_][A-Za-z0-9_.]{0,127}$");

    /// <summary>
    /// The query that counts the model's events per time bucket and field values. The engine
    /// aggregates it, so it covers every event rather than the first rows read.
    /// </summary>
    internal static string SummaryQuery(DataModel model)
    {
        var minutes = Math.Max(1, (int)Math.Round(model.BucketSpan.TotalMinutes));
        var fields = string.Join(", ", model.Fields);
        return $"{model.BaseQuery} | bin _time span={minutes}m | fillnull value=\"\" {fields} | stats count by _time, {fields}";
    }

    /// <summary>The start of the bucket a time falls in. Spans under a minute count as a minute.</summary>
    internal static DateTime BucketStart(DateTime time, TimeSpan span)
    {
        var ticks = Math.Max(TimeSpan.TicksPerMinute, span.Ticks);
        var utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;
        return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
    }

    /// <summary>Rebuilds one model's summaries. Returns whether it did.</summary>
    internal async Task<bool> RebuildModelAsync(DataModel model, CancellationToken ct)
    {
        try
        {
            if (model.Fields.FirstOrDefault(f => !IsValidFieldName(f)) is { } invalid)
            {
                _logger.LogError("Data model '{Name}' has a field name that can't be summarized: {Field}", model.Name, invalid);
                return false;
            }

            using var scope = _scopeFactory.CreateScope();
            var queryEngine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();

            var context = new QueryExecutionContext
            {
                Query = SummaryQuery(model),
                StartTime = DateTime.UtcNow.AddDays(-Math.Max(1, model.RetentionDays)),
                Limit = MaxSummaryRows,
                Timeout = TimeSpan.FromMinutes(5)
            };

            var result = await queryEngine.ExecuteAsync(context, ct);
            if (result.Statistics?.Truncated == true || result.Events.Count >= MaxSummaryRows)
            {
                _logger.LogWarning(
                    "Data model '{Name}' has more than its engine or row limits allow, so its summaries are incomplete; use fewer fields or a wider bucket",
                    model.Name);
            }

            var groups = new Dictionary<string, DataModelSummary>();
            foreach (var evt in result.Events)
            {
                if (evt is not IDictionary<string, object?> dict) continue;

                var time = dict.TryGetValue("_time", out var t) ? AsTime(t) : null;
                if (time == null) continue;

                var bucket = BucketStart(time.Value, model.BucketSpan);
                var fieldValues = model.Fields.ToDictionary(
                    f => f,
                    f => dict.TryGetValue(f, out var v) ? v?.ToString() ?? string.Empty : string.Empty);
                var count = dict.TryGetValue("count", out var c) && c != null ? Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture) : 0;

                var groupKey = $"{bucket:O}\u001f{string.Join('\u001f', fieldValues.Values)}";
                if (!groups.TryGetValue(groupKey, out var summary))
                {
                    summary = new DataModelSummary { TimeBucket = bucket, FieldValues = fieldValues, Count = 0 };
                    groups[groupKey] = summary;
                }

                summary.Count += count;
            }

            _summaries[model.Id] = groups.Values.ToList();
            model.LastRebuiltAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Rebuilt data model '{Name}': {Buckets} buckets",
                model.Name, groups.Count);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to rebuild data model '{Name}'", model.Name);
            return false;
        }
    }

    private static DateTime? AsTime(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        long or int or double => DateTimeOffset.FromUnixTimeMilliseconds((long)(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) * 1000)).UtcDateTime,
        string s when DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
        _ => null,
    };

    private static dynamic CreateSummaryExpando(long count, DateTime? minTime, DateTime? maxTime)
    {
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        dict["count"] = count;
        dict["earliest_time"] = minTime;
        dict["latest_time"] = maxTime;
        return expando;
    }
}
