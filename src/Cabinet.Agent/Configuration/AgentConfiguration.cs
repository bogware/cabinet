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
    /// Maximum time to wait for continuation lines (seconds).
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
    /// Maximum number of retries for failed sends.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// HTTP request timeout (seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to verify SSL/TLS certificates.
    /// </summary>
    public bool VerifySsl { get; set; } = true;

    /// <summary>
    /// Maximum number of events to buffer in memory when the server is unreachable.
    /// </summary>
    public int MaxBufferSize { get; set; } = 10000;

    /// <summary>
    /// Path for spilling buffered events to disk when memory buffer is full.
    /// </summary>
    public string SpillDirectory { get; set; } = "spool";
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
