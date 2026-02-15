using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Audit logging interface. Implementations may write to storage, Serilog, or external SIEM.
/// Fire-and-forget by design — audit failures must not break the request pipeline.
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Log an audit event. Implementations should handle failures gracefully.
    /// </summary>
    Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query audit events with optional filtering
    /// </summary>
    Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? principalId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
