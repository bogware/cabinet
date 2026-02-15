namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Tracks the read position and identity of a monitored log file.
/// Used for checkpointing so the agent resumes from where it left off.
/// </summary>
public sealed class FileState
{
    /// <summary>
    /// Absolute path to the file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Last read byte offset in the file.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// File size at last read. Used to detect truncation (size decreased = rotated/truncated).
    /// </summary>
    public long LastKnownSize { get; set; }

    /// <summary>
    /// Last modification time of the file.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Hash of the first 256 bytes. Used to detect file rotation (same path, different file).
    /// </summary>
    public string? HeaderHash { get; set; }

    /// <summary>
    /// Number of lines read so far.
    /// </summary>
    public long LinesRead { get; set; }

    /// <summary>
    /// When this state was last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
