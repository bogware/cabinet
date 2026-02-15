using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>
/// Represents an auditable action performed in the system.
/// Captures who did what, when, and on which resource.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>Unique ULID identifier</summary>
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>When the action occurred</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>The principal who performed the action (API key hash or "system")</summary>
    public required string PrincipalId { get; set; }

    /// <summary>Category of action</summary>
    public required AuditAction Action { get; set; }

    /// <summary>Resource type affected (e.g., "LogEvent", "SavedQuery", "ApiKey")</summary>
    public required string ResourceType { get; set; }

    /// <summary>Specific resource identifier</summary>
    public string? ResourceId { get; set; }

    /// <summary>Human-readable description of what happened</summary>
    public string? Description { get; set; }

    /// <summary>IP address of the requester</summary>
    public string? SourceIp { get; set; }

    /// <summary>Whether the action succeeded</summary>
    public bool Success { get; set; } = true;

    /// <summary>Error message if the action failed</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Categories of auditable actions
/// </summary>
public enum AuditAction
{
    // Authentication
    AuthSuccess,
    AuthFailure,

    // Data access
    Search,
    Export,

    // Data mutation
    Create,
    Update,
    Ingest,
    Delete,
    Purge,

    // Configuration
    ApiKeyCreated,
    ApiKeyRevoked,
    SavedQueryCreated,
    SavedQueryDeleted,

    // System
    StorageInitialized,
    RetentionExecuted,
    DetectionRun
}
