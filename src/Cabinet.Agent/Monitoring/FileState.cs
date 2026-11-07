namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Tracks the identity and delivered position of a monitored log file, so the agent resumes
/// from where it left off.
/// </summary>
public sealed class FileState
{
    /// <summary>
    /// Absolute path to the file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Random key for this file's contents, kept when the file is renamed and replaced when it's
    /// rotated. Event IDs derive from it, so re-reading a line produces the same event ID.
    /// </summary>
    public string FileKey { get; set; } = string.Empty;

    /// <summary>
    /// Offset of the first byte not yet delivered. Everything before it has been accepted by the
    /// server or written to the spool.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Hash of the file's first line, which doesn't change as the file grows.
    /// See <see cref="FileStateRegistry.ComputeFingerprint"/>.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// Where the last delivered event starts. With <see cref="AnchorHash"/>, this proves a file
    /// is really the one checkpointed, even if a replacement starts with the same first line.
    /// </summary>
    public long AnchorStart { get; set; }

    /// <summary>
    /// Hash of the last delivered event's text.
    /// </summary>
    public string? AnchorHash { get; set; }

    /// <summary>
    /// True if this path no longer holds these contents. Kept for a while, because the contents
    /// may reappear under a rotated name that the input's pattern also matches.
    /// </summary>
    public bool Displaced { get; set; }

    /// <summary>
    /// Number of lines delivered so far.
    /// </summary>
    public long LinesRead { get; set; }

    /// <summary>
    /// When this state was last updated.
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
