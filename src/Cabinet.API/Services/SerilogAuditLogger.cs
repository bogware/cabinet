using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.API.Services;

/// <summary>
/// Audit logger that writes events to the application's structured logging pipeline (Serilog).
/// Audit events are logged at Information level with structured properties for filtering.
/// Production deployments should consider a storage-backed implementation.
/// </summary>
public sealed class SerilogAuditLogger : IAuditLogger
{
    private readonly ILogger<SerilogAuditLogger> _logger;

    public SerilogAuditLogger(ILogger<SerilogAuditLogger> logger)
    {
        _logger = logger;
    }

    public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "AUDIT [{Action}] Principal={PrincipalId} Resource={ResourceType}/{ResourceId} Success={Success} IP={SourceIp} {Description}",
                auditEvent.Action,
                auditEvent.PrincipalId,
                auditEvent.ResourceType,
                auditEvent.ResourceId ?? "-",
                auditEvent.Success,
                auditEvent.SourceIp ?? "-",
                auditEvent.Description ?? "");
        }
        catch (Exception ex)
        {
            // Audit failures must never break the request pipeline
            _logger.LogWarning(ex, "Failed to write audit event {AuditId}", auditEvent.Id);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? principalId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        // Serilog-based implementation cannot query past events
        // Production should use a storage-backed implementation
        _logger.LogWarning("Audit query not supported by SerilogAuditLogger. Use a storage-backed implementation.");
        IReadOnlyList<AuditEvent> empty = Array.Empty<AuditEvent>();
        return Task.FromResult(empty);
    }
}
