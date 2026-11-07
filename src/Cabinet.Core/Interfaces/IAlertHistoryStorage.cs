namespace Cabinet.Core.Interfaces;

/// <summary>
/// Persistent storage for alert history records.
/// </summary>
public interface IAlertHistoryStorage
{
    /// <summary>Initialize storage (create tables if needed)</summary>
    Task InitializeAsync();

    /// <summary>Save an alert record</summary>
    Task<AlertRecord> SaveAlertAsync(AlertRecord alert);

    /// <summary>Get alert records with pagination and optional filtering</summary>
    Task<List<AlertRecord>> GetAlertsAsync(int offset = 0, int count = 100, string? severity = null, bool? acknowledged = null);

    /// <summary>Get a single alert by ID</summary>
    Task<AlertRecord?> GetAlertByIdAsync(string id);

    /// <summary>Acknowledge an alert</summary>
    Task<AlertRecord?> AcknowledgeAlertAsync(string id, string acknowledgedBy);

    /// <summary>Get total count of alerts matching filters</summary>
    Task<int> GetAlertCountAsync(string? severity = null, bool? acknowledged = null);
}

/// <summary>
/// Record of a fired alert.
/// </summary>
public class AlertRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SearchName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public int ResultCount { get; set; }
    public string? Message { get; set; }
    public string Channel { get; set; } = string.Empty;
    public bool Acknowledged { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public DateTime FiredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The detection rule that fired, if a rule did.</summary>
    public string? RuleId { get; set; }

    /// <summary>The index of the event that made it fire. Null for alerts about every index.</summary>
    public string? Index { get; set; }

    /// <summary>The event that made it fire.</summary>
    public string? EventId { get; set; }

    /// <summary>The value being counted by, such as a source address.</summary>
    public string? GroupValue { get; set; }

    /// <summary>The MITRE ATT&amp;CK technique it relates to.</summary>
    public string? Technique { get; set; }
}
