using Cabinet.Core.Utilities;

namespace Cabinet.Alerts;

/// <summary>
/// Enhanced alert rule model for defining automated alert conditions.
/// Alert rules are evaluated against query results and trigger notifications
/// when conditions are met.
/// </summary>
public sealed class AlertRule
{
    /// <summary>Unique ULID identifier</summary>
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>Human-readable name for this alert rule</summary>
    public required string Name { get; set; }

    /// <summary>Optional description of what this rule monitors</summary>
    public string? Description { get; set; }

    /// <summary>Whether this rule is active</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The BQL/SPL query that produces results for evaluation</summary>
    public required string Query { get; set; }

    /// <summary>Type of condition to evaluate against query results</summary>
    public required AlertConditionType ConditionType { get; set; }

    /// <summary>Comparison operator for threshold conditions</summary>
    public AlertComparison Comparison { get; set; } = AlertComparison.GreaterThan;

    /// <summary>Threshold value for comparison (result count or aggregation value)</summary>
    public double ThresholdValue { get; set; }

    /// <summary>Alert severity when this rule triggers</summary>
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;

    /// <summary>Minimum time between alerts to prevent alert storms</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>When this rule last triggered an alert</summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>
    /// Fields used for deduplication. Only one alert per unique combination
    /// of these field values will fire within the cooldown window.
    /// </summary>
    public List<string> DedupFields { get; set; } = new();

    /// <summary>Maximum number of dedup entries to track (prevents unbounded memory)</summary>
    public int MaxDedupEntries { get; set; } = 10000;

    /// <summary>Names of alert channels to send notifications through</summary>
    public List<string> Channels { get; set; } = new();

    /// <summary>
    /// Optional message template with placeholders (e.g., "Found {count} errors on {host}").
    /// Supports field names from query results as placeholders.
    /// </summary>
    public string? MessageTemplate { get; set; }

    /// <summary>When this rule was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Type of condition to evaluate for alert triggering
/// </summary>
public enum AlertConditionType
{
    /// <summary>Alert fires every time the query runs (unconditional)</summary>
    Always,

    /// <summary>Alert fires when result count crosses a threshold</summary>
    Threshold,

    /// <summary>Alert fires when result count changes from previous run</summary>
    Change,

    /// <summary>Alert fires when the query returns zero results</summary>
    NoResults
}

/// <summary>
/// Comparison operator for threshold-based alert conditions
/// </summary>
public enum AlertComparison
{
    /// <summary>Result count greater than threshold</summary>
    GreaterThan,

    /// <summary>Result count greater than or equal to threshold</summary>
    GreaterThanOrEqual,

    /// <summary>Result count less than threshold</summary>
    LessThan,

    /// <summary>Result count less than or equal to threshold</summary>
    LessThanOrEqual,

    /// <summary>Result count equal to threshold</summary>
    Equal,

    /// <summary>Result count not equal to threshold</summary>
    NotEqual
}
