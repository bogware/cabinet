using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>
/// A scheduled search that runs periodically on a cron schedule.
/// When alert conditions are met, fires alerts via the alert system.
/// </summary>
public sealed class ScheduledSearch
{
    /// <summary>Unique ULID identifier</summary>
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>Human-readable name for this scheduled search</summary>
    public required string Name { get; set; }

    /// <summary>The BQL/SPL query to execute</summary>
    public required string Query { get; set; }

    /// <summary>Optional description of what this search does</summary>
    public string? Description { get; set; }

    /// <summary>Principal who created this scheduled search</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When the scheduled search was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cron expression (5-field: minute hour dom month dow)</summary>
    public required string CronExpression { get; set; }

    /// <summary>Whether this scheduled search is active</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Relative time range for the search (e.g., "-15m", "-1h", "-24h")</summary>
    public string TimeRange { get; set; } = "-15m";

    /// <summary>When the search was last executed</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Number of results from the last execution</summary>
    public int LastRunResultCount { get; set; }

    /// <summary>Whether the last execution was successful</summary>
    public bool LastRunSuccess { get; set; } = true;

    /// <summary>Error message from the last failed execution</summary>
    public string? LastRunError { get; set; }

    /// <summary>Optional alert action to fire when conditions are met</summary>
    public ScheduledSearchAlert? AlertAction { get; set; }
}

/// <summary>
/// Alert configuration for a scheduled search.
/// Defines when and how alerts should fire based on search results.
/// </summary>
public sealed class ScheduledSearchAlert
{
    /// <summary>
    /// Condition expression (e.g., "count > 10", "count == 0").
    /// Compared against the result count.
    /// </summary>
    public string Condition { get; set; } = "count > 0";

    /// <summary>Threshold value for the condition</summary>
    public int Threshold { get; set; }

    /// <summary>
    /// Alert severity level (0=Info, 1=Warning, 2=Error, 3=Critical).
    /// Maps to Cabinet.Alerts.AlertSeverity at the service layer.
    /// </summary>
    public int Severity { get; set; } = 1; // Warning

    /// <summary>Minimum time between alerts to prevent alert storms</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>When the last alert was sent for this search</summary>
    public DateTime? LastAlertAt { get; set; }
}
