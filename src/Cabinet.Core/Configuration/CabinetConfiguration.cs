namespace Cabinet.Core.Configuration;

/// <summary>
/// Main configuration model for Cabinet
/// </summary>
public class CabinetConfiguration
{
    /// <summary>
    /// Storage configuration
    /// </summary>
    public StorageConfiguration Storage { get; set; } = new();

    /// <summary>
    /// Ingestion configuration
    /// </summary>
    public IngestionConfiguration Ingestion { get; set; } = new();

    /// <summary>
    /// Parser profiles
    /// </summary>
    public Dictionary<string, ParserProfile> Profiles { get; set; } = new()
    {
        ["steamcmd"] = new ParserProfile { Name = "steamcmd", Parser = "SteamCMD", FilePatterns = ["**/steamcmd*.log", "**/Steam*.log"] },
        ["sourcemod"] = new ParserProfile { Name = "sourcemod", Parser = "SourceMod", FilePatterns = ["**/sourcemod/logs/*.log", "**/addons/sourcemod/logs/*.log"] },
        ["l4d2"] = new ParserProfile { Name = "l4d2", Parser = "L4D2", FilePatterns = ["**/left4dead2/*.log", "**/l4d2/*.log"] },
        ["generic"] = new ParserProfile { Name = "generic", Parser = "Generic", FilePatterns = ["**/*.log", "**/*.txt"] }
    };

    /// <summary>
    /// Detection suite configuration
    /// </summary>
    public DetectionConfiguration Detection { get; set; } = new();

    /// <summary>
    /// Output configuration
    /// </summary>
    public OutputConfiguration Output { get; set; } = new();

    /// <summary>
    /// Plugin directories
    /// </summary>
    public List<string> PluginDirectories { get; set; } = ["./plugins"];

    /// <summary>
    /// AWS configuration
    /// </summary>
    public AWSConfiguration AWS { get; set; } = new();

    /// <summary>
    /// Network share configuration
    /// </summary>
    public NetworkShareConfiguration NetworkShare { get; set; } = new();

    /// <summary>
    /// Alert configuration
    /// </summary>
    public AlertConfiguration Alerts { get; set; } = new();

    /// <summary>
    /// CLI configuration
    /// </summary>
    public CLIConfiguration CLI { get; set; } = new();

    /// <summary>
    /// Distributed deployment configuration (Redis, leader election, caching)
    /// </summary>
    public DistributedConfiguration Distributed { get; set; } = new();

    /// <summary>
    /// Syslog listener configuration
    /// </summary>
    public SyslogListenerConfiguration Syslog { get; set; } = new();

    /// <summary>
    /// Email alert configuration
    /// </summary>
    public EmailConfiguration Email { get; set; } = new();

    /// <summary>
    /// Encryption at rest configuration
    /// </summary>
    public EncryptionAtRestConfiguration Encryption { get; set; } = new();
}

/// <summary>
/// Syslog listener configuration
/// </summary>
public class SyslogListenerConfiguration
{
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "0.0.0.0";
    public int UdpPort { get; set; } = 1514;
    public int TcpPort { get; set; } = 1514;
}

/// <summary>
/// Email alert channel configuration
/// </summary>
public class EmailConfiguration
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "cabinet@localhost";
    public string FromName { get; set; } = "Cabinet Alerts";
    public List<string> Recipients { get; set; } = new();
}

/// <summary>
/// Encryption at rest configuration
/// </summary>
public class EncryptionAtRestConfiguration
{
    public bool Enabled { get; set; }
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string KeyProvider { get; set; } = "Local";
    public string? KeyFilePath { get; set; }
    public List<string> EncryptedFields { get; set; } = new();
    public int KeyRotationDays { get; set; }
}

public class StorageConfiguration
{
    /// <summary>
    /// Storage provider: SQLite, Memory, PostgreSQL, OpenSearch
    /// </summary>
    public string Provider { get; set; } = "SQLite";

    /// <summary>
    /// Connection string or URI (format depends on provider)
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=cabinet.db";

    /// <summary>
    /// Maximum events to store (0 = unlimited)
    /// </summary>
    public long MaxEvents { get; set; } = 0;

    /// <summary>
    /// Days to retain events (0 = unlimited)
    /// </summary>
    public int RetentionDays { get; set; } = 0;

    /// <summary>
    /// Enable time-based partitioning
    /// </summary>
    public bool EnablePartitioning { get; set; } = true;

    /// <summary>
    /// Partition size in hours
    /// </summary>
    public int PartitionHours { get; set; } = 24;
}

public class IngestionConfiguration
{
    /// <summary>
    /// Batch size for bulk operations
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Number of parallel parser threads
    /// </summary>
    public int ParallelParsers { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Channel buffer size
    /// </summary>
    public int ChannelCapacity { get; set; } = 10000;

    /// <summary>
    /// Checkpoint interval in seconds
    /// </summary>
    public int CheckpointIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Enable incremental ingestion
    /// </summary>
    public bool EnableIncremental { get; set; } = true;

    /// <summary>
    /// Follow file changes (tail mode)
    /// </summary>
    public bool FollowFiles { get; set; } = false;

    /// <summary>
    /// Max file size to process in MB (0 = unlimited)
    /// </summary>
    public long MaxFileSizeMB { get; set; } = 1024;
}

public class ParserProfile
{
    /// <summary>
    /// Profile name
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Parser to use
    /// </summary>
    public required string Parser { get; set; }

    /// <summary>
    /// File patterns to match
    /// </summary>
    public List<string> FilePatterns { get; set; } = new();

    /// <summary>
    /// Custom parser settings
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();
}

public class DetectionConfiguration
{
    /// <summary>
    /// Enable automatic detection runs
    /// </summary>
    public bool AutoDetect { get; set; } = false;

    /// <summary>
    /// Scumscan configuration
    /// </summary>
    public ScumscanConfiguration Scumscan { get; set; } = new();

    /// <summary>
    /// Hackwatch configuration
    /// </summary>
    public HackwatchConfiguration Hackwatch { get; set; } = new();

    /// <summary>
    /// Adminabuse configuration
    /// </summary>
    public AdminabuseConfiguration Adminabuse { get; set; } = new();
}

public class ScumscanConfiguration
{
    /// <summary>
    /// Enable scumscan detection
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum severity to report
    /// </summary>
    public int MinSeverity { get; set; } = 3;

    /// <summary>
    /// Context window size (lines before/after)
    /// </summary>
    public int ContextWindow { get; set; } = 5;

    /// <summary>
    /// Custom word lists path
    /// </summary>
    public string? CustomWordListPath { get; set; }
}

public class HackwatchConfiguration
{
    /// <summary>
    /// Enable hackwatch detection
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// VAC detection patterns
    /// </summary>
    public List<string> VacPatterns { get; set; } = new()
    {
        "VAC", "banned", "cheat", "exploit", "aimbot", "wallhack", "speedhack"
    };

    /// <summary>
    /// Anomaly threshold
    /// </summary>
    public double AnomalyThreshold { get; set; } = 0.95;

    /// <summary>
    /// Suspicious K/D ratio threshold
    /// </summary>
    public double SuspiciousKDRatio { get; set; } = 10.0;

    /// <summary>
    /// Suspicious headshot percentage
    /// </summary>
    public int SuspiciousHeadshotPercent { get; set; } = 80;

    /// <summary>
    /// Minimum kills for analysis
    /// </summary>
    public int MinKillsForAnalysis { get; set; } = 10;

    /// <summary>
    /// Maximum reconnects per hour
    /// </summary>
    public int MaxReconnectsPerHour { get; set; } = 10;
}

public class AdminabuseConfiguration
{
    /// <summary>
    /// Enable adminabuse detection
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Actions per hour threshold
    /// </summary>
    public int ActionsPerHourThreshold { get; set; } = 20;

    /// <summary>
    /// Repeated target threshold
    /// </summary>
    public int RepeatedTargetThreshold { get; set; } = 5;

    /// <summary>
    /// Action thresholds by type
    /// </summary>
    public Dictionary<string, AdminActionThreshold> ActionThresholds { get; set; } = new()
    {
        ["ban"] = new AdminActionThreshold { Normal = 2, Warning = 5, Critical = 10 },
        ["kick"] = new AdminActionThreshold { Normal = 10, Warning = 20, Critical = 50 },
        ["mute"] = new AdminActionThreshold { Normal = 5, Warning = 10, Critical = 20 },
        ["warn"] = new AdminActionThreshold { Normal = 20, Warning = 50, Critical = 100 },
        ["slay"] = new AdminActionThreshold { Normal = 5, Warning = 10, Critical = 20 }
    };
}

public class AdminActionThreshold
{
    public int Normal { get; set; }
    public int Warning { get; set; }
    public int Critical { get; set; }
}

public class OutputConfiguration
{
    /// <summary>
    /// Default output format
    /// </summary>
    public OutputFormat DefaultFormat { get; set; } = OutputFormat.Table;

    /// <summary>
    /// Enable color output
    /// </summary>
    public bool EnableColor { get; set; } = true;

    /// <summary>
    /// Page size for results
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Timestamp format
    /// </summary>
    public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
}

public enum OutputFormat
{
    Table,
    Json,
    NdJson,
    Csv,
    Raw
}

/// <summary>
/// AWS configuration
/// </summary>
public class AWSConfiguration
{
    public string? DefaultProfile { get; set; }
    public string? DefaultRegion { get; set; } = "us-east-1";
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cabinet", "cache", "aws");
    public int CloudWatchPollIntervalMs { get; set; } = 2000;
    public int CloudWatchDefaultLookbackMinutes { get; set; } = 5;
    public int CloudWatchDefaultLookbackDays { get; set; } = 7;
    public int CloudWatchQueryTimeoutSeconds { get; set; } = 60;
    public int CloudWatchStreamLimit { get; set; } = 50;
}

/// <summary>
/// Network share configuration
/// </summary>
public class NetworkShareConfiguration
{
    public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "cabinet", "cache", "shares");
    public string LinuxMountPoint { get; set; } = "/mnt/cabinet";
    public int CacheExpirationMinutes { get; set; } = 60;
}

/// <summary>
/// Alert configuration
/// </summary>
public class AlertConfiguration
{
    public string AlertLogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cabinet",
        "alerts.log"
    );
    public List<string> EnabledChannels { get; set; } = new() { "Console", "File" };
}

/// <summary>
/// CLI configuration
/// </summary>
public class CLIConfiguration
{
    public int MaxLineDisplayLength { get; set; } = 120;
    public int MaxErrorsBeforeAbort { get; set; } = 10;
    public bool ShowProgressBar { get; set; } = true;
    public bool UseColorOutput { get; set; } = true;
}

/// <summary>
/// Configuration for distributed deployment (multi-instance, Redis coordination)
/// </summary>
public class DistributedConfiguration
{
    /// <summary>
    /// Enable distributed mode. When false, single-instance in-memory patterns are used.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Redis connection string for coordination, caching, and rate limiting.
    /// Required when Enabled is true.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Unique instance identifier. Defaults to machine name.
    /// In Kubernetes, set to the pod name via downward API.
    /// </summary>
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Leader election configuration for background services
    /// </summary>
    public LeaderElectionConfiguration LeaderElection { get; set; } = new();

    /// <summary>
    /// Cache TTL configuration
    /// </summary>
    public CacheConfiguration Cache { get; set; } = new();
}

/// <summary>
/// Leader election settings for coordinating background services across instances
/// </summary>
public class LeaderElectionConfiguration
{
    /// <summary>
    /// Enable leader election for background services (retention, etc.)
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lease duration in seconds. If the leader fails to renew, another instance can take over.
    /// </summary>
    public int LeaseTimeSeconds { get; set; } = 30;

    /// <summary>
    /// How often to renew the lease (should be less than LeaseTimeSeconds)
    /// </summary>
    public int RenewalIntervalSeconds { get; set; } = 10;
}

/// <summary>
/// Cache TTL settings for distributed caching
/// </summary>
public class CacheConfiguration
{
    /// <summary>
    /// Default cache TTL in seconds
    /// </summary>
    public int DefaultTtlSeconds { get; set; } = 300;

    /// <summary>
    /// TTL for cached API key validations
    /// </summary>
    public int ApiKeyTtlSeconds { get; set; } = 60;

    /// <summary>
    /// TTL for cached query results
    /// </summary>
    public int QueryResultTtlSeconds { get; set; } = 30;
}