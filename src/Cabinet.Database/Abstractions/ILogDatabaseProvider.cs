using Cabinet.Core.Models;

namespace Cabinet.Database.Abstractions;

/// <summary>
/// Abstraction for database providers to support multiple backend storage systems
/// </summary>
[Obsolete("Use ILogStorage from Cabinet.Core.Interfaces instead. ILogDatabaseProvider will be removed in a future version.")]
public interface ILogDatabaseProvider : IDisposable
{
    /// <summary>
    /// The type of database provider
    /// </summary>
    DatabaseProviderType ProviderType { get; }

    /// <summary>
    /// Initialize the database (create tables, indexes, etc.)
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert a batch of log events
    /// </summary>
    Task<int> InsertBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query logs with filtering
    /// </summary>
    Task<IEnumerable<LogEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? searchText = null,
        Dictionary<string, string>? filters = null,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get total count of logs matching criteria
    /// </summary>
    Task<long> GetCountAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? searchText = null,
        Dictionary<string, string>? filters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete logs older than specified date
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archive logs to a backup location
    /// </summary>
    Task<string> ArchiveLogsAsync(
        DateTime startDate,
        DateTime endDate,
        string archivePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Export logs to various formats
    /// </summary>
    Task<string> ExportAsync(
        DateTime? startTime,
        DateTime? endTime,
        ExportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get database statistics
    /// </summary>
    Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimize database (vacuum, analyze, etc.)
    /// </summary>
    Task OptimizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check database health
    /// </summary>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public enum DatabaseProviderType
{
    SQLite,
    PostgreSQL,
    MySQL,
    DynamoDB,
    MongoDB,
    ElasticSearch
}

public enum ExportFormat
{
    JSON,
    CSV,
    Parquet,
    JSONL,
    TSV,
    XML
}

public class DatabaseStatistics
{
    public long TotalEvents { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public DateTime OldestEvent { get; set; }
    public DateTime NewestEvent { get; set; }
    public Dictionary<string, long> EventsByType { get; set; } = new();
    public Dictionary<string, long> EventsBySeverity { get; set; } = new();
    public double AverageEventSizeBytes { get; set; }
    public long IndexSizeBytes { get; set; }
}

public class HealthCheckResult
{
    public bool IsHealthy { get; set; }
    public string Status { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}