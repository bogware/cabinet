using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Interface for log storage implementations
/// </summary>
public interface ILogStorage
{
    /// <summary>
    /// Initialize storage
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Store a single log event
    /// </summary>
    Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store multiple log events in batch
    /// </summary>
    Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query events
    /// </summary>
    IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get event by ID
    /// </summary>
    Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get events count
    /// </summary>
    Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete old events
    /// </summary>
    Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all events in a specific index. Returns number of events deleted.
    /// </summary>
    Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        // Default interface method for backward compatibility — backends should override
        return Task.FromResult(0);
    }

    /// <summary>
    /// Get storage statistics
    /// </summary>
    Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Query context for filtering and pagination
/// </summary>
public class QueryContext
{
    /// <summary>
    /// Text search query
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Start time filter
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time filter
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Index filter — scope query to a specific logical index
    /// </summary>
    public string? Index { get; set; }

    /// <summary>
    /// Source type filter (string-based, e.g., "SteamCMD", "Generic")
    /// </summary>
    public string? SourceType { get; set; }

    /// <summary>
    /// Severity filter
    /// </summary>
    public LogSeverity? MinSeverity { get; set; }

    /// <summary>
    /// Host filter (was ServerId)
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// Tags filter
    /// </summary>
    public HashSet<string>? Tags { get; set; }

    /// <summary>
    /// Field filters
    /// </summary>
    public Dictionary<string, object?>? FieldFilters { get; set; }

    /// <summary>
    /// Sort field
    /// </summary>
    public string SortField { get; set; } = "Timestamp";

    /// <summary>
    /// Sort descending
    /// </summary>
    public bool SortDescending { get; set; } = true;

    /// <summary>
    /// Skip records
    /// </summary>
    public int Skip { get; set; }

    /// <summary>
    /// Maximum allowed limit for any query to prevent DoS
    /// </summary>
    public const int MaxLimit = 100_000;

    /// <summary>
    /// Limit records (capped at MaxLimit)
    /// </summary>
    public int Limit
    {
        get => _limit;
        set => _limit = Math.Clamp(value, 0, MaxLimit);
    }
    private int _limit = 10_000;
}

/// <summary>
/// Storage statistics
/// </summary>
public class StorageStats
{
    public long TotalEvents { get; set; }
    public long StorageSizeBytes { get; set; }
    public DateTime? OldestEvent { get; set; }
    public DateTime? NewestEvent { get; set; }
    public Dictionary<string, long> EventsBySourceType { get; set; } = new();
    public Dictionary<LogSeverity, long> EventsBySeverity { get; set; } = new();
    public Dictionary<string, long> EventsByIndex { get; set; } = new();
}