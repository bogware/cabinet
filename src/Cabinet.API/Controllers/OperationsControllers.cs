using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Backups of the SQLite database. PostgreSQL and OpenSearch have their own tools for this; see
/// the deployment guide.
/// </summary>
[ApiController]
[Route("api/v1/backups")]
[Authorize]
public class BackupController : ControllerBase
{
    private readonly LocalBackupService? _backups;

    public BackupController(LocalBackupService? backups = null)
    {
        _backups = backups;
    }

    private ObjectResult NotAvailable() => Problem(
        detail: "Built-in backups are for SQLite storage. Use pg_dump for PostgreSQL and snapshots for OpenSearch.",
        statusCode: 400);

    /// <summary>
    /// List backups, newest first
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(typeof(IReadOnlyList<BackupManifestEntry>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        _backups == null ? NotAvailable() : Ok(await _backups.ListBackupsAsync(cancellationToken));

    /// <summary>
    /// Create a backup now
    /// </summary>
    [HttpPost]
    [RequireCapability(Capability.IndexManagement)]
    [RequireEveryIndex(Capability.IndexManagement)]
    [ProducesResponseType(typeof(BackupManifestEntry), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken = default)
    {
        if (_backups == null)
        {
            return NotAvailable();
        }

        // Each is a full copy of the database, so repeated requests could fill the disk
        var last = await _backups.GetLastBackupTimeAsync(cancellationToken);
        if (last is { } at && DateTime.UtcNow - at < MinimumInterval)
        {
            return Problem(detail: $"A backup was taken at {at:u}. Wait {MinimumInterval.TotalMinutes:N0} minutes between them.", statusCode: 429);
        }

        return Ok(await _backups.CreateBackupAsync("manual", cancellationToken));
    }

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Check that a backup is intact and holds a healthy database
    /// </summary>
    [HttpPost("{id}/verify")]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(typeof(BackupVerification), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(string id, CancellationToken cancellationToken = default) =>
        _backups == null ? NotAvailable() : Ok(new BackupVerification { Id = id, Valid = await _backups.VerifyBackupAsync(id, cancellationToken) });
}

/// <summary>The result of checking a backup.</summary>
public class BackupVerification
{
    public string Id { get; set; } = string.Empty;
    public bool Valid { get; set; }
}

/// <summary>
/// Reads the audit log, and checks that nobody has changed it.
/// </summary>
[ApiController]
[Route("api/v1/audit")]
[Authorize]
public class AuditController : ControllerBase
{
    private readonly IAuditLogger _auditLogger;

    public AuditController(IAuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    /// <summary>
    /// Search audit events, newest first
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(IReadOnlyList<AuditEvent>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search(
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null,
        [FromQuery] string? principalId = null,
        [FromQuery] AuditAction? action = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _auditLogger.QueryAsync(startTime, endTime, principalId, action, Math.Clamp(limit, 1, 10_000), cancellationToken));
    }

    /// <summary>
    /// Check every audit record against its hash chain
    /// </summary>
    [HttpGet("verify")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(AuditVerificationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken = default)
    {
        return _auditLogger is TamperEvidentAuditLogger chain
            ? Ok(await chain.VerifyAsync(cancellationToken))
            : Problem(detail: "This audit logger keeps no chain", statusCode: 400);
    }
}
