namespace Cabinet.API.Infrastructure;

/// <summary>
/// Limits and intervals that used to be fixed in code (<c>Limits:*</c>). The defaults are the
/// values the code always had; each is described in the settings catalog.
/// </summary>
public sealed class ServerLimits
{
    /// <summary>Largest request body, in MB, for any request.</summary>
    public int MaxRequestBodyMB { get; set; } = 50;

    /// <summary>Largest HTTP Event Collector request, in MB.</summary>
    public int HecMaxRequestMB { get; set; } = 5;

    /// <summary>Largest OTLP request as sent, in MB.</summary>
    public int OtlpMaxRequestMB { get; set; } = 50;

    /// <summary>Largest OTLP request once decompressed, in MB.</summary>
    public int OtlpMaxDecompressedMB { get; set; } = 64;

    /// <summary>Time a search on the search page may run, in seconds.</summary>
    public int QueryTimeoutSeconds { get; set; } = 60;

    /// <summary>Most results one search returns.</summary>
    public int QueryMaxResults { get; set; } = 50_000;

    /// <summary>Longest search, in characters.</summary>
    public int MaxQueryLength { get; set; } = 10_000;

    /// <summary>Most results a streamed search sends.</summary>
    public int StreamMaxResults { get; set; } = 100_000;

    /// <summary>Most rows one export can hold.</summary>
    public int ExportMaxRows { get; set; } = 100_000;

    /// <summary>Events the field summary looks at.</summary>
    public int FieldSummaryMaxEvents { get; set; } = 10_000;

    /// <summary>Time a streamed search or log stream may run, in minutes.</summary>
    public int StreamTimeoutMinutes { get; set; } = 5;

    /// <summary>Time an export may run, in seconds.</summary>
    public int ExportTimeoutSeconds { get; set; } = 120;

    /// <summary>Longest live tail, in minutes.</summary>
    public int LiveTailMaxMinutes { get; set; } = 60;

    /// <summary>Search jobs one user can have waiting or running at once.</summary>
    public int MaxActiveSearchJobsPerUser { get; set; } = 10;

    /// <summary>Time a search's regular expression may spend on one event, in milliseconds.</summary>
    public int QueryRegexTimeoutMs { get; set; } = 250;

    /// <summary>Time a detection rule's regular expression may spend on one event, in milliseconds.</summary>
    public int DetectionRegexTimeoutMs { get; set; } = 250;

    /// <summary>How often detection rules are reloaded, so changes on other instances apply, in seconds.</summary>
    public int DetectionRuleReloadSeconds { get; set; } = 10;

    /// <summary>Timeouts after which a rule's slow regular expression stops being logged.</summary>
    public int DetectionMaxRegexTimeouts { get; set; } = 100;

    /// <summary>Longest period a backtest can cover, in hours.</summary>
    public int BacktestMaxHours { get; set; } = 24 * 90;

    /// <summary>Most events a backtest can read.</summary>
    public long BacktestMaxEvents { get; set; } = 10_000_000;

    /// <summary>Alerts kept in history; older ones are removed.</summary>
    public int AlertHistoryMax { get; set; } = 10_000;

    /// <summary>Time a field extraction may spend on one event, in milliseconds.</summary>
    public int FieldExtractionRegexTimeoutMs { get; set; } = 100;

    /// <summary>How often field extractions are reloaded, so changes on other instances apply, in seconds.</summary>
    public int FieldExtractionReloadSeconds { get; set; } = 30;

    /// <summary>Shortest time between manual backups, in minutes.</summary>
    public int BackupMinimumIntervalMinutes { get; set; } = 5;

    /// <summary>How often retention removes old events, in minutes.</summary>
    public int RetentionCheckMinutes { get; set; } = 60;

    /// <summary>How often scheduled searches are checked for being due, in seconds.</summary>
    public int ScheduledSearchCheckSeconds { get; set; } = 15;

    /// <summary>How often data model summaries are rebuilt, in minutes.</summary>
    public int DataModelRebuildMinutes { get; set; } = 15;

    /// <summary>How often each instance reports that it's running, in seconds.</summary>
    public int ClusterHeartbeatSeconds { get; set; } = 10;

    /// <summary>An instance not heard from for this long is no longer counted, in seconds.</summary>
    public int ClusterInstanceTimeoutSeconds { get; set; } = 30;

    /// <summary>Audit records between copies of the chain head written to the log output.</summary>
    public int AuditHeadLogInterval { get; set; } = 1000;

    /// <summary>How long /metrics reuses its storage figures, in seconds.</summary>
    public int MetricsCacheSeconds { get; set; } = 60;

    /// <summary>Bytes in a megabyte, for the MB settings above.</summary>
    public const long Megabyte = 1024 * 1024;
}
