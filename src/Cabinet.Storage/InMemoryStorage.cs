using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Storage;

/// <summary>
/// In-memory implementation of log storage for testing and development
/// </summary>
public class InMemoryStorage : ILogStorage
{
    private readonly ConcurrentDictionary<string, LogEvent> _events = new();
    private readonly ILogger<InMemoryStorage>? _logger;
    private readonly int _maxEvents;
    private readonly object _evictionLock = new();

    /// <summary>
    /// Creates a new InMemoryStorage instance.
    /// </summary>
    /// <param name="logger">Optional logger</param>
    /// <param name="maxEvents">Maximum number of events to store (default 1,000,000). Oldest events are evicted when full.</param>
    public InMemoryStorage(ILogger<InMemoryStorage>? logger = null, int maxEvents = 1_000_000)
    {
        _logger = logger;
        _maxEvents = maxEvents;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("In-memory storage initialized");
        return Task.CompletedTask;
    }

    public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        EvictIfNeeded(1);
        return Task.FromResult(_events.TryAdd(logEvent.Id, logEvent));
    }

    public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        EvictIfNeeded(eventList.Count);
        var count = 0;
        foreach (var evt in eventList)
        {
            if (_events.TryAdd(evt.Id, evt))
                count++;
        }
        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Snapshot values to avoid InvalidOperationException during concurrent modification
        var query = _events.Values.ToList().AsEnumerable();

        // Apply filters
        if (!string.IsNullOrWhiteSpace(context.SearchText))
        {
            var terms = SearchTerms.Parse(context.SearchText);
            query = query.Where(e => SearchTerms.Matches(e.RawLine, terms));
        }

        if (context.AllowedIndexes is { } allowed)
        {
            query = query.Where(e => allowed.Any(pattern => IndexPermission.Matches(pattern, e.Index)));
        }

        if (context.StartTime.HasValue)
        {
            query = query.Where(e => e.Timestamp >= context.StartTime.Value);
        }

        if (context.EndTime.HasValue)
        {
            query = query.Where(e => e.Timestamp <= context.EndTime.Value);
        }

        if (!string.IsNullOrEmpty(context.SourceType))
        {
            query = query.Where(e => string.Equals(e.SourceType, context.SourceType, StringComparison.OrdinalIgnoreCase));
        }

        if (context.MinSeverity.HasValue)
        {
            query = query.Where(e => e.Severity >= context.MinSeverity.Value);
        }

        if (!string.IsNullOrWhiteSpace(context.Host))
        {
            query = query.Where(e => string.Equals(e.Host, context.Host, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(context.Index))
        {
            query = query.Where(e => e.Index == context.Index);
        }

        // Apply field filters
        if (context.FieldFilters is { Count: > 0 })
        {
            foreach (var filter in context.FieldFilters)
            {
                var fieldName = filter.Key;
                if (filter.Value is object?[] orValues)
                {
                    // OR pushdown: array value means IN query
                    var stringValues = orValues.Select(v => v?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    query = query.Where(e =>
                        e.Fields.TryGetValue(fieldName, out var v) &&
                        stringValues.Contains(v?.ToString()));
                }
                else
                {
                    var fieldValue = filter.Value?.ToString();
                    query = query.Where(e =>
                        e.Fields.TryGetValue(fieldName, out var v) &&
                        string.Equals(v?.ToString(), fieldValue, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        // Apply tag filters
        if (context.Tags is { Count: > 0 })
        {
            query = query.Where(e => context.Tags.All(t => e.Tags.Contains(t)));
        }

        // Apply sorting
        if (context.After is { } after)
        {
            query = context.SortDescending
                ? query.Where(e => e.Timestamp < after.Timestamp || (e.Timestamp == after.Timestamp && string.CompareOrdinal(e.Id, after.Id) < 0))
                : query.Where(e => e.Timestamp > after.Timestamp || (e.Timestamp == after.Timestamp && string.CompareOrdinal(e.Id, after.Id) > 0));
        }

        // ID breaks ties, so pages of events with the same time are stable
        query = context.SortField.ToLower() switch
        {
            "severity" => context.SortDescending
                ? query.OrderByDescending(e => e.Severity).ThenByDescending(e => e.Id, StringComparer.Ordinal)
                : query.OrderBy(e => e.Severity).ThenBy(e => e.Id, StringComparer.Ordinal),
            _ => context.SortDescending
                ? query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id, StringComparer.Ordinal)
                : query.OrderBy(e => e.Timestamp).ThenBy(e => e.Id, StringComparer.Ordinal)
        };

        // Apply pagination
        query = query.Skip(context.Skip).Take(context.Limit);

        await Task.CompletedTask; // Satisfy async requirement for IAsyncEnumerable

        foreach (var evt in query)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return evt;
        }
    }

    public Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_events.TryGetValue(id, out var evt) ? evt : null);
    }

    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var (id, logEvent) in _events)
        {
            if (logEvent.Index == indexName && logEvent.Timestamp < before && _events.TryRemove(id, out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        if (context == null)
        {
            return Task.FromResult((long)_events.Count);
        }

        // Snapshot to avoid concurrent modification during filtering
        var query = _events.Values.ToList().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(context.SearchText))
        {
            var terms = SearchTerms.Parse(context.SearchText);
            query = query.Where(e => SearchTerms.Matches(e.RawLine, terms));
        }

        if (context.AllowedIndexes is { } allowed)
        {
            query = query.Where(e => allowed.Any(pattern => IndexPermission.Matches(pattern, e.Index)));
        }

        if (context.StartTime.HasValue)
        {
            query = query.Where(e => e.Timestamp >= context.StartTime.Value);
        }

        if (context.EndTime.HasValue)
        {
            query = query.Where(e => e.Timestamp <= context.EndTime.Value);
        }

        if (!string.IsNullOrEmpty(context.SourceType))
        {
            query = query.Where(e => string.Equals(e.SourceType, context.SourceType, StringComparison.OrdinalIgnoreCase));
        }

        if (context.MinSeverity.HasValue)
        {
            query = query.Where(e => e.Severity >= context.MinSeverity.Value);
        }

        if (!string.IsNullOrWhiteSpace(context.Host))
        {
            query = query.Where(e => string.Equals(e.Host, context.Host, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(context.Index))
        {
            query = query.Where(e => e.Index == context.Index);
        }

        // Apply field filters (matching QueryAsync behavior)
        if (context.FieldFilters is { Count: > 0 })
        {
            foreach (var filter in context.FieldFilters)
            {
                var fieldName = filter.Key;
                if (filter.Value is object?[] orValues)
                {
                    // OR pushdown: array value means IN query
                    var stringValues = orValues.Select(v => v?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    query = query.Where(e =>
                        e.Fields.TryGetValue(fieldName, out var v) &&
                        stringValues.Contains(v?.ToString()));
                }
                else
                {
                    var fieldValue = filter.Value?.ToString();
                    query = query.Where(e =>
                        e.Fields.TryGetValue(fieldName, out var v) &&
                        string.Equals(v?.ToString(), fieldValue, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        // Apply tag filters
        if (context.Tags is { Count: > 0 })
        {
            query = query.Where(e => context.Tags.All(t => e.Tags.Contains(t)));
        }

        return Task.FromResult((long)query.Count());
    }

    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        var toRemove = _events.Where(kvp => kvp.Value.Timestamp < before).Select(kvp => kvp.Key).ToList();
        var count = 0;

        foreach (var key in toRemove)
        {
            if (_events.TryRemove(key, out _))
                count++;
        }

        _logger?.LogInformation("Purged {Count} events older than {Before}", count, before);
        return Task.FromResult(count);
    }

    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        var toRemove = _events
            .Where(kvp => string.Equals(kvp.Value.Index, indexName, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        var count = 0;
        foreach (var key in toRemove)
        {
            if (_events.TryRemove(key, out _))
                count++;
        }

        _logger?.LogInformation("Deleted {Count} events from index '{Index}'", count, indexName);
        return Task.FromResult(count);
    }

    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot to avoid concurrent modification across multiple iterations
        var snapshot = _events.Values.ToList();

        var stats = new StorageStats
        {
            TotalEvents = snapshot.Count
        };

        if (snapshot.Count > 0)
        {
            stats.OldestEvent = snapshot.Min(e => e.Timestamp);
            stats.NewestEvent = snapshot.Max(e => e.Timestamp);

            stats.EventsBySourceType = snapshot
                .GroupBy(e => e.SourceType)
                .ToDictionary(g => g.Key, g => (long)g.Count());

            stats.EventsBySeverity = snapshot
                .GroupBy(e => e.Severity)
                .ToDictionary(g => g.Key, g => (long)g.Count());

            stats.EventsByIndex = snapshot
                .GroupBy(e => e.Index)
                .ToDictionary(g => g.Key, g => (long)g.Count());
        }

        // Estimate memory usage: ~200 bytes overhead + raw line (2 bytes/char) + ~50 bytes per field
        long estimatedSize = 0;
        foreach (var evt in snapshot)
        {
            estimatedSize += 200 + (evt.RawLine?.Length ?? 0) * 2 + evt.Fields.Count * 50;
        }
        stats.StorageSizeBytes = estimatedSize;

        return Task.FromResult(stats);
    }

    private void EvictIfNeeded(int incomingCount)
    {
        if (_events.Count + incomingCount <= _maxEvents) return;

        lock (_evictionLock)
        {
            // Re-check after acquiring lock
            var excess = _events.Count + incomingCount - _maxEvents;
            if (excess <= 0) return;

            // Evict oldest events by timestamp.
            // NOTE: This sorts all events — O(n log n) — which is acceptable for the in-memory
            // backend used in testing/development. For production workloads at scale, consider a
            // different eviction strategy (e.g., maintaining an insertion-order linked list or
            // using a min-heap by timestamp) to achieve O(k log n) where k = excess count.
            var toRemove = _events
                .OrderBy(kvp => kvp.Value.Timestamp)
                .Take(excess)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _events.TryRemove(key, out _);
            }

            _logger?.LogDebug("Evicted {Count} oldest events (max {Max})", toRemove.Count, _maxEvents);
        }
    }
}