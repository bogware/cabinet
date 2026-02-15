using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Interface for log ingestion engine
/// </summary>
public interface IIngestionEngine
{
    /// <summary>
    /// Ingest logs from a directory
    /// </summary>
    Task<IngestionResult> IngestDirectoryAsync(
        string path,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingest a single file
    /// </summary>
    Task<IngestionResult> IngestFileAsync(
        string filePath,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream logs from a file (tail mode)
    /// </summary>
    IAsyncEnumerable<LogEvent> StreamFileAsync(
        string filePath,
        StreamOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Ingestion options
/// </summary>
public class IngestionOptions
{
    /// <summary>
    /// Parser profile to use
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Recursive directory scan
    /// </summary>
    public bool Recursive { get; set; } = true;

    /// <summary>
    /// File patterns to include
    /// </summary>
    public List<string> IncludePatterns { get; set; } = new() { "*.log" };

    /// <summary>
    /// File patterns to exclude
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = new();

    /// <summary>
    /// Use incremental ingestion
    /// </summary>
    public bool Incremental { get; set; } = true;

    /// <summary>
    /// Force re-ingestion
    /// </summary>
    public bool Force { get; set; } = false;

    /// <summary>
    /// Host identifier to assign (was ServerId)
    /// </summary>
    public string? Host { get; set; }
}

/// <summary>
/// Stream options for tail mode
/// </summary>
public class StreamOptions
{
    /// <summary>
    /// Follow file changes
    /// </summary>
    public bool Follow { get; set; } = true;

    /// <summary>
    /// Start from end of file
    /// </summary>
    public bool FromEnd { get; set; } = true;

    /// <summary>
    /// Poll interval in milliseconds
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Filter expression
    /// </summary>
    public string? Filter { get; set; }
}

/// <summary>
/// Ingestion result
/// </summary>
public class IngestionResult
{
    public int FilesProcessed { get; set; }
    public long LinesProcessed { get; set; }
    public long EventsStored { get; set; }
    public long BytesProcessed { get; set; }
    public TimeSpan Duration { get; set; }
    public List<IngestionError> Errors { get; set; } = new();

    public double LinesPerSecond => Duration.TotalSeconds > 0 ? LinesProcessed / Duration.TotalSeconds : 0;
    public double MBPerSecond => Duration.TotalSeconds > 0 ? (BytesProcessed / 1024.0 / 1024.0) / Duration.TotalSeconds : 0;
}

/// <summary>
/// Ingestion progress
/// </summary>
public class IngestionProgress
{
    public string CurrentFile { get; set; } = "";
    public int FilesCompleted { get; set; }
    public int TotalFiles { get; set; }
    public long LinesProcessed { get; set; }
    public long EventsStored { get; set; }
    public double PercentComplete { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
/// Ingestion error
/// </summary>
public class IngestionError
{
    public string File { get; set; } = "";
    public long LineNumber { get; set; }
    public string Error { get; set; } = "";
    public string? Details { get; set; }
}

/// <summary>
/// Checkpoint manager for incremental ingestion
/// </summary>
public interface ICheckpointManager
{
    /// <summary>
    /// Get checkpoint for a file
    /// </summary>
    Task<FileCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save checkpoint for a file
    /// </summary>
    Task SaveCheckpointAsync(FileCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete checkpoint for a file
    /// </summary>
    Task DeleteCheckpointAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all checkpoints
    /// </summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// File checkpoint for incremental processing
/// </summary>
public class FileCheckpoint
{
    public required string FilePath { get; set; }
    public long FileSize { get; set; }
    public DateTime LastModified { get; set; }
    public long LastPosition { get; set; }
    public long LinesProcessed { get; set; }
    public string? FileHash { get; set; }
    public DateTime CheckpointTime { get; set; }
}