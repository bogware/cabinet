using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Archive;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>Archived events: what's there, and bringing some back to search.</summary>
[ApiController]
[Route("api/v1/archive")]
public sealed class ArchiveController : ControllerBase
{
    private readonly EventArchiver? _archiver;
    private readonly IEventArchive? _archive;
    private readonly ILogStorage _storage;
    private readonly IndexAccessScope _scope;
    private readonly IAuditLogger _audit;

    public ArchiveController(ILogStorage storage, IndexAccessScope scope, IAuditLogger audit, EventArchiver? archiver = null, IEventArchive? archive = null)
    {
        _storage = storage;
        _scope = scope;
        _audit = audit;
        _archiver = archiver;
        _archive = archive;
    }

    /// <summary>The archived days of each index the caller can read.</summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<ArchivedIndex>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List([FromQuery] string? index = null, CancellationToken cancellationToken = default)
    {
        if (_archive == null)
        {
            return Problem(detail: "No archive is set up. An administrator can set one with Archive:Type.", statusCode: StatusCodes.Status404NotFound);
        }

        var chunks = await _archive.ListAsync(index, cancellationToken);
        return Ok(chunks
            .Where(c => _scope.CanRead(c.Index))
            .GroupBy(c => c.Index, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArchivedIndex
            {
                Index = g.Key,
                FirstDay = g.Min(c => c.Day),
                LastDay = g.Max(c => c.Day),
                Files = g.Count(),
                SizeBytes = g.Sum(c => c.SizeBytes),
            })
            .OrderBy(a => a.Index, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Brings an index's archived events between two times back into an index to search,
    /// <c>{index}-restored</c> unless another is named. Retention treats it like any index.
    /// </summary>
    [HttpPost("rehydrate")]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(typeof(RehydrateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rehydrate([FromBody] RehydrateRequest request, CancellationToken cancellationToken)
    {
        if (_archiver == null)
        {
            return Problem(detail: "No archive is set up. An administrator can set one with Archive:Type.", statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Index) || request.From >= request.To || request.To - request.From > TimeSpan.FromDays(31))
        {
            return Problem(detail: "Name an index and a time range of up to 31 days.", statusCode: StatusCodes.Status400BadRequest);
        }

        var target = string.IsNullOrWhiteSpace(request.TargetIndex) ? $"{request.Index}-restored" : request.TargetIndex.Trim();
        if (!_scope.CanRead(request.Index) || !_scope.Can(Capability.Ingest, target))
        {
            return Problem(detail: $"Rehydrating needs reading {request.Index} and writing {target}.", statusCode: StatusCodes.Status403Forbidden);
        }

        var restored = await _archiver.RehydrateAsync(_storage, request.Index, request.From.ToUniversalTime(), request.To.ToUniversalTime(), target, cancellationToken);
        await _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Ingest,
            ResourceType = "Archive",
            Description = $"Rehydrated {restored} events of {request.Index} from {request.From:O} to {request.To:O} into {target}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
        return Ok(new RehydrateResult { Restored = restored, Index = target });
    }
}

/// <summary>An index's archive.</summary>
public sealed class ArchivedIndex
{
    public required string Index { get; init; }
    public DateTime FirstDay { get; init; }
    public DateTime LastDay { get; init; }
    public int Files { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>What to bring back.</summary>
public sealed class RehydrateRequest
{
    public string? Index { get; set; }
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string? TargetIndex { get; set; }
}

/// <summary>What came back, and where.</summary>
public sealed class RehydrateResult
{
    public long Restored { get; init; }
    public required string Index { get; init; }
}
