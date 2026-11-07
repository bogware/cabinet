using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Storage;

/// <summary>
/// Extension methods for ILogStorage to provide compatibility with legacy API
/// </summary>
public static class ILogStorageExtensions
{
    /// <summary>
    /// Get events with filters (legacy API)
    /// </summary>
    public static async Task<IEnumerable<LogEvent>> GetEventsAsync(
        this ILogStorage storage,
        Dictionary<string, object> filter,
        string? search = null,
        int skip = 0,
        int limit = 100,
        CancellationToken cancellationToken = default,
        string? sortBy = null,
        bool descending = true)
    {
        var context = ToQueryContext(filter, search);
        context.Skip = skip;
        context.Limit = limit;
        context.SortDescending = descending;

        if (!string.IsNullOrEmpty(sortBy))
            context.SortField = sortBy;

        var results = new List<LogEvent>();
        await foreach (var evt in storage.QueryAsync(context, cancellationToken))
        {
            results.Add(evt);
            if (results.Count >= limit) break;
        }

        return results;
    }

    /// <summary>
    /// Get event by ID (legacy API)
    /// </summary>
    public static Task<LogEvent?> GetEventAsync(this ILogStorage storage, string id, CancellationToken cancellationToken = default)
    {
        return storage.GetByIdAsync(id, cancellationToken);
    }

    /// <summary>
    /// Get event count with filter (legacy API)
    /// </summary>
    public static Task<long> GetEventCountAsync(
        this ILogStorage storage,
        Dictionary<string, object> filter,
        CancellationToken cancellationToken = default,
        string? search = null)
    {
        var context = ToQueryContext(filter, search);
        context.Limit = 0; // We only want count

        return storage.GetCountAsync(context, cancellationToken);
    }

    /// <summary>
    /// Turns a filter into a context. Names with their own column become native filters, so that
    /// they aren't also looked for as custom fields, which no event would have.
    /// </summary>
    private static QueryContext ToQueryContext(Dictionary<string, object>? filter, string? search)
    {
        var context = new QueryContext { SearchText = search };
        if (filter == null)
        {
            return context;
        }

        var fields = new Dictionary<string, object?>();
        foreach (var (key, value) in filter)
        {
            switch (key)
            {
                case "source_type":
                    context.SourceType = value?.ToString();
                    break;
                case "severity":
                    if (Enum.TryParse<LogSeverity>(value?.ToString(), ignoreCase: true, out var severity))
                    {
                        context.MinSeverity = severity;
                    }

                    break;
                case "server_id":
                    context.Host = value?.ToString();
                    break;
                case "index":
                    context.Index = value?.ToString();
                    break;
                case "timestamp_gte" when value is DateTime start:
                    context.StartTime = start;
                    break;
                case "timestamp_lte" when value is DateTime end:
                    context.EndTime = end;
                    break;
                default:
                    fields[key] = value;
                    break;
            }
        }

        context.FieldFilters = fields.Count > 0 ? fields : null;
        return context;
    }

    /// <summary>
    /// Delete events with filter (legacy API)
    /// </summary>
    public static async Task<int> DeleteEventsAsync(this ILogStorage storage, Dictionary<string, object> filter, CancellationToken cancellationToken = default)
    {
        // For now, only support deletion by date
        if (filter.TryGetValue("before", out var before) && before is DateTime beforeDate)
        {
            return await storage.PurgeOldEventsAsync(beforeDate, cancellationToken);
        }

        // Deletion by source_type, index, or other filters requires ILogStorage.DeleteByQueryAsync()
        // which is not yet part of the interface. For now, only date-based purge is supported.
        throw new NotSupportedException("DeleteEventsAsync currently only supports 'before' date filter");
    }

    /// <summary>
    /// Get storage statistics (legacy API)
    /// </summary>
    public static async Task<Dictionary<string, object>> GetStatisticsAsync(this ILogStorage storage, CancellationToken cancellationToken = default)
    {
        var stats = await storage.GetStatsAsync(cancellationToken);

        var result = new Dictionary<string, object>
        {
            ["total_events"] = stats.TotalEvents,
            ["storage_size_bytes"] = stats.StorageSizeBytes,
            ["events_by_source"] = stats.EventsBySourceType,
            ["events_by_severity"] = stats.EventsBySeverity
        };

        if (stats.OldestEvent.HasValue)
            result["oldest_event"] = stats.OldestEvent.Value;
        if (stats.NewestEvent.HasValue)
            result["newest_event"] = stats.NewestEvent.Value;

        return result;
    }
}