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
    private readonly IDataModelStorage _storage;
    private readonly ILogger<DataModelAccelerationService> _logger;
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastPersistTime = DateTime.MinValue;

    public DataModelAccelerationService(
        IServiceScopeFactory scopeFactory,
        IDataModelStorage storage,
        ILogger<DataModelAccelerationService> logger)
    {
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
            try
            {
                foreach (var model in _models.Values.Where(m => m.AccelerationEnabled))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await RebuildModelAsync(model, stoppingToken);
                }

                // Periodically persist summaries to storage
                await PersistIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in data model acceleration loop");
            }

            try
            {
                await Task.Delay(RebuildInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // Final persistence before shutdown
        await PersistAllAsync(CancellationToken.None);

        _logger.LogInformation("Data model acceleration service stopped");
    }

    /// <summary>
    /// Load all models and their summaries from persistent storage into memory.
    /// </summary>
    private async Task LoadFromStorageAsync(CancellationToken ct)
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

            if (loaded > 0)
            {
                _logger.LogInformation(
                    "Loaded {Count} data models from persistent storage", loaded);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load data models from storage, starting with empty state");
        }
    }

    /// <summary>
    /// Persist all summaries to storage if enough time has elapsed since last persist.
    /// </summary>
    private async Task PersistIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPersistTime < PersistInterval) return;
        await PersistAllAsync(ct);
    }

    /// <summary>
    /// Persist all current model definitions and summaries to storage.
    /// </summary>
    private async Task PersistAllAsync(CancellationToken ct)
    {
        try
        {
            foreach (var kvp in _models)
            {
                if (ct.IsCancellationRequested) break;

                await _storage.SaveModelAsync(kvp.Value, ct);

                if (_summaries.TryGetValue(kvp.Key, out var summaries))
                {
                    await _storage.SaveSummariesAsync(kvp.Key, summaries, ct);
                }
            }

            _lastPersistTime = DateTime.UtcNow;
            _logger.LogDebug("Persisted {Count} data models to storage", _models.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist data models to storage");
        }
    }

    private async Task RebuildModelAsync(DataModel model, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var queryEngine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();

            var context = new QueryExecutionContext
            {
                Query = model.BaseQuery,
                Limit = 100_000,
                Timeout = TimeSpan.FromMinutes(5)
            };

            var result = await queryEngine.ExecuteAsync(context, ct);

            // Build summary buckets
            var groups = new Dictionary<string, DataModelSummary>();

            foreach (var evt in result.Events)
            {
                if (evt is not IDictionary<string, object?> dict) continue;

                // Get timestamp for bucketing
                var timestamp = DateTime.UtcNow;
                if (dict.TryGetValue("Timestamp", out var ts) && ts is DateTime dt)
                    timestamp = dt;
                else if (dict.TryGetValue("_time", out var t) && t is DateTime dt2)
                    timestamp = dt2;

                var bucket = new DateTime(
                    timestamp.Year, timestamp.Month, timestamp.Day,
                    timestamp.Hour / (int)model.BucketSpan.TotalHours * (int)Math.Max(1, model.BucketSpan.TotalHours),
                    0, 0, DateTimeKind.Utc);

                // Build group key from configured fields
                var fieldValues = new Dictionary<string, string>();
                foreach (var field in model.Fields)
                {
                    if (dict.TryGetValue(field, out var val))
                        fieldValues[field] = val?.ToString() ?? "";
                    else
                        fieldValues[field] = "";
                }

                var groupKey = $"{bucket:O}|{string.Join("|", fieldValues.Values)}";

                if (!groups.TryGetValue(groupKey, out var summary))
                {
                    summary = new DataModelSummary
                    {
                        TimeBucket = bucket,
                        FieldValues = fieldValues,
                        Count = 0
                    };
                    groups[groupKey] = summary;
                }

                summary.Count++;
            }

            _summaries[model.Id] = groups.Values.ToList();
            model.LastRebuiltAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Rebuilt data model '{Name}': {Buckets} buckets from {Events} events",
                model.Name, groups.Count, result.Events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rebuild data model '{Name}'", model.Name);
        }
    }

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
