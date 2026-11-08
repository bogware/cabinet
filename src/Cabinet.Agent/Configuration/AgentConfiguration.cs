namespace Cabinet.Agent.Configuration;

/// <summary>
/// Root configuration for the Cabinet forwarding agent.
/// </summary>
public sealed class AgentConfiguration
{
    /// <summary>
    /// Log file inputs to monitor.
    /// </summary>
    public List<InputConfiguration> Inputs { get; set; } = [];

    /// <summary>
    /// Output destination (Cabinet API server).
    /// </summary>
    public OutputConfiguration Output { get; set; } = new();

    /// <summary>
    /// File state registry settings for checkpoint persistence.
    /// </summary>
    public RegistryConfiguration Registry { get; set; } = new();

    /// <summary>
    /// How often to scan for new files matching input patterns (seconds).
    /// </summary>
    public int ScanIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Override hostname reported with events. Empty = auto-detect.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Time zone that files without offsets in their times were written in, IANA or Windows
    /// name. Empty = this machine's.
    /// </summary>
    public string TimeZone { get; set; } = string.Empty;

    /// <summary>
    /// How often each file is checked for new lines (milliseconds).
    /// </summary>
    public int PollIntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Longest line kept, in bytes. Longer lines are truncated, and the rest isn't buffered.
    /// </summary>
    public int MaxLineBytes { get; set; } = 1024 * 1024;
}

/// <summary>
/// Configuration for a set of log file inputs.
/// </summary>
public sealed class InputConfiguration
{
    /// <summary>
    /// File paths or glob patterns to monitor (e.g., "/var/log/*.log", "C:\Logs\app.log").
    /// </summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>
    /// Whether to recurse into subdirectories for glob patterns.
    /// </summary>
    public bool Recursive { get; set; }

    /// <summary>
    /// Source type label sent with events (e.g., "syslog", "apache", "app").
    /// </summary>
    public string SourceType { get; set; } = "generic";

    /// <summary>
    /// Target index in Cabinet (e.g., "main", "security").
    /// </summary>
    public string Index { get; set; } = "main";

    /// <summary>
    /// Glob patterns for files to exclude (e.g., "*.gz", "*.zip").
    /// </summary>
    public List<string> ExcludePatterns { get; set; } = [];

    /// <summary>
    /// Multi-line event configuration.
    /// </summary>
    public MultiLineConfiguration? MultiLine { get; set; }

    /// <summary>
    /// Don't read files last written longer ago than this, unless they're already being followed.
    /// For folders holding years of history. 0 reads every file.
    /// </summary>
    public int IgnoreOlderThanHours { get; set; }

    /// <summary>
    /// Stop following a file once it's fully read and hasn't changed for this long, so programs
    /// that start a new file for each session don't leave thousands open. Reading resumes from
    /// the checkpoint if it changes again. 0 follows files until they're removed.
    /// </summary>
    public int CloseInactiveSeconds { get; set; } = 300;
}

/// <summary>
/// Multi-line log event merging configuration.
/// </summary>
public sealed class MultiLineConfiguration
{
    /// <summary>
    /// Regex pattern that identifies the start of a new log event.
    /// Lines not matching this pattern are merged with the previous event.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// If true, lines NOT matching the pattern are considered continuation lines.
    /// If false, lines matching the pattern are considered continuation lines.
    /// Default: true (pattern identifies the start of a new event).
    /// </summary>
    public bool Negate { get; set; } = true;

    /// <summary>
    /// Maximum number of lines to merge into a single event.
    /// </summary>
    public int MaxLines { get; set; } = 500;

    /// <summary>
    /// How long an event stays open with no new lines before it's sent (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Configuration for the Cabinet API output destination.
/// </summary>
public sealed class OutputConfiguration
{
    /// <summary>
    /// Cabinet API server URL (e.g., "https://cabinet.example.com").
    /// </summary>
    public string ServerUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// API key for authentication.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Number of events to batch before sending.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum time to wait before flushing a partial batch (seconds).
    /// </summary>
    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Retries before a batch goes to the spool.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Delay before the first retry, doubling after each one (milliseconds).
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Largest batch to send, in bytes. Must stay under the server's 5 MB request limit.
    /// </summary>
    public long MaxBatchBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// HTTP request timeout (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to verify SSL/TLS certificates.
    /// </summary>
    public bool VerifySsl { get; set; } = true;

    /// <summary>
    /// SHA-256 fingerprint of the server's certificate. When set, the agent accepts that
    /// certificate and no other, so a server can use its own self-signed one. The Windows
    /// installer prints it.
    /// </summary>
    public string? CertificateSha256 { get; set; }

    /// <summary>
    /// Events to queue in memory. When full, file reading pauses until there's room.
    /// </summary>
    public int MaxBufferSize { get; set; } = 10000;

    /// <summary>
    /// Directory for batches the server wouldn't accept in time. Rejected events go to its
    /// <c>dead-letter</c> subdirectory. Use a persistent location.
    /// </summary>
    public string SpillDirectory { get; set; } = "spool";

    /// <summary>
    /// Largest size of the spool, in bytes. When full, delivery keeps retrying and file reading
    /// pauses, so data waits in the source files.
    /// </summary>
    public long MaxSpoolBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>
    /// How often to try delivering spooled batches (seconds).
    /// </summary>
    public int SpoolRetryIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// How long to keep sending at shutdown before spooling the rest (seconds).
    /// </summary>
    public int ShutdownFlushSeconds { get; set; } = 20;
}

/// <summary>
/// Configuration for the file state registry.
/// </summary>
public sealed class RegistryConfiguration
{
    /// <summary>
    /// Path to the registry file (JSON) for persisting file positions.
    /// </summary>
    public string Path { get; set; } = "registry.json";

    /// <summary>
    /// How often to save the registry to disk (seconds).
    /// </summary>
    public int SaveIntervalSeconds { get; set; } = 10;
}
