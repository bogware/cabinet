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
        var context = new QueryContext
        {
            SearchText = search,
            Skip = skip,
            Limit = limit,
            SortDescending = descending,
            FieldFilters = filter?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
        };

        if (!string.IsNullOrEmpty(sortBy))
            context.SortField = sortBy;

        // Parse common filters
        if (filter != null && filter.TryGetValue("source_type", out var sourceType))
        {
            context.SourceType = sourceType?.ToString();
        }

        if (filter != null && filter.TryGetValue("severity", out var severity) && Enum.TryParse<LogSeverity>(severity?.ToString(), out var sev))
        {
            context.MinSeverity = sev;
        }

        if (filter != null && filter.TryGetValue("server_id", out var serverId))
        {
            context.Host = serverId?.ToString();
        }

        if (filter != null && filter.TryGetValue("index", out var index))
        {
            context.Index = index?.ToString();
        }

        // Extract time range filters into QueryContext for native storage-level filtering
        if (filter != null && filter.TryGetValue("timestamp_gte", out var startTime) && startTime is DateTime start)
        {
            context.StartTime = start;
            context.FieldFilters?.Remove("timestamp_gte");
        }

        if (filter != null && filter.TryGetValue("timestamp_lte", out var endTime) && endTime is DateTime end)
        {
            context.EndTime = end;
            context.FieldFilters?.Remove("timestamp_lte");
        }

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
    public static Task<long> GetEventCountAsync(this ILogStorage storage, Dictionary<string, object> filter, CancellationToken cancellationToken = default)
    {
        var context = new QueryContext
        {
            FieldFilters = filter?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value),
            Limit = 0 // We only want count
        };

        return storage.GetCountAsync(context, cancellationToken);
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