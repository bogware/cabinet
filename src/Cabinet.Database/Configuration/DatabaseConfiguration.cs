namespace Cabinet.Database.Configuration;

/// <summary>
/// Database configuration for Cabinet
/// </summary>
public class DatabaseConfiguration
{
    /// <summary>
    /// The database provider to use
    /// </summary>
    public string Provider { get; set; } = "SQLite";

    /// <summary>
    /// Connection string for the database
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=cabinet.db";

    /// <summary>
    /// Enable automatic database migration
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// Maximum batch size for inserts
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>
    /// Command timeout in seconds
    /// </summary>
    public int CommandTimeout { get; set; } = 60;

    /// <summary>
    /// Retention policy configuration
    /// </summary>
    public RetentionPolicyConfiguration RetentionPolicy { get; set; } = new();

    /// <summary>
    /// Archive configuration
    /// </summary>
    public ArchiveConfiguration Archive { get; set; } = new();

    /// <summary>
    /// Export configuration
    /// </summary>
    public ExportConfiguration Export { get; set; } = new();

    /// <summary>
    /// DynamoDB specific configuration
    /// </summary>
    public DynamoDBConfiguration? DynamoDB { get; set; }
}

/// <summary>
/// Retention policy configuration
/// </summary>
public class RetentionPolicyConfiguration
{
    /// <summary>
    /// Enable automatic retention policy
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Number of days to retain logs
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// Run retention cleanup interval in hours
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Archive before deletion
    /// </summary>
    public bool ArchiveBeforeDelete { get; set; } = true;

    /// <summary>
    /// Compress archived logs
    /// </summary>
    public bool CompressArchives { get; set; } = true;
}

/// <summary>
/// Archive configuration
/// </summary>
public class ArchiveConfiguration
{
    /// <summary>
    /// Archive storage path
    /// </summary>
    public string ArchivePath { get; set; } = "./archives";

    /// <summary>
    /// Archive format
    /// </summary>
    public string Format { get; set; } = "jsonl.gz";

    /// <summary>
    /// S3 bucket for archive storage (optional)
    /// </summary>
    public string? S3Bucket { get; set; }

    /// <summary>
    /// S3 prefix for archives
    /// </summary>
    public string S3Prefix { get; set; } = "cabinet-archives/";

    /// <summary>
    /// Maximum archive file size in MB
    /// </summary>
    public int MaxArchiveFileSizeMB { get; set; } = 100;
}

/// <summary>
/// Export configuration
/// </summary>
public class ExportConfiguration
{
    /// <summary>
    /// Default export format
    /// </summary>
    public string DefaultFormat { get; set; } = "json";

    /// <summary>
    /// Export path
    /// </summary>
    public string ExportPath { get; set; } = "./exports";

    /// <summary>
    /// Include metadata in exports
    /// </summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>
    /// Compress exports
    /// </summary>
    public bool CompressExports { get; set; } = false;

    /// <summary>
    /// Maximum rows per export file
    /// </summary>
    public int MaxRowsPerFile { get; set; } = 100000;
}

/// <summary>
/// DynamoDB specific configuration
/// </summary>
public class DynamoDBConfiguration
{
    /// <summary>
    /// DynamoDB table name
    /// </summary>
    public string TableName { get; set; } = "CabinetLogs";

    /// <summary>
    /// AWS region
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Read capacity units
    /// </summary>
    public int ReadCapacityUnits { get; set; } = 5;

    /// <summary>
    /// Write capacity units
    /// </summary>
    public int WriteCapacityUnits { get; set; } = 10;

    /// <summary>
    /// Enable auto-scaling
    /// </summary>
    public bool EnableAutoScaling { get; set; } = true;

    /// <summary>
    /// Enable point-in-time recovery
    /// </summary>
    public bool EnablePointInTimeRecovery { get; set; } = true;

    /// <summary>
    /// TTL attribute name for automatic expiration
    /// </summary>
    public string? TtlAttributeName { get; set; } = "ttl";

    /// <summary>
    /// Global Secondary Indexes configuration
    /// </summary>
    public List<DynamoDBIndexConfiguration> GlobalSecondaryIndexes { get; set; } = new();
}

/// <summary>
/// DynamoDB index configuration
/// </summary>
public class DynamoDBIndexConfiguration
{
    public string IndexName { get; set; } = string.Empty;
    public string PartitionKey { get; set; } = string.Empty;
    public string? SortKey { get; set; }
    public int ReadCapacityUnits { get; set; } = 5;
    public int WriteCapacityUnits { get; set; } = 5;
}