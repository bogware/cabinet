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
    private readonly IConfiguration? _configuration;

    public ArchiveController(ILogStorage storage, IndexAccessScope scope, IAuditLogger audit, EventArchiver? archiver = null, IEventArchive? archive = null, IConfiguration? configuration = null)
    {
        _configuration = configuration;
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

        if (index != null && !EventArchiver.CanArchive(index))
        {
            return Problem(detail: "An archived index's name has only letters, digits, '-', '_' and '.'.", statusCode: StatusCodes.Status400BadRequest);
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
    /// Searches an index's archived events between two times where they are, without bringing
    /// them back: BQL, SPL or plain words, with the caller's index access and masking. Reads only
    /// the archived days in the range, and refuses a range with more events than
    /// <c>Archive:SearchMaxEvents</c> (500,000 by default).
    /// </summary>
    [HttpPost("search")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(QueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Search([FromBody] ArchiveSearchRequest request, CancellationToken cancellationToken)
    {
        if (_archiver == null)
        {
            return Problem(detail: "No archive is set up. An administrator can set one with Archive:Type.", statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.Index) || request.From >= request.To || string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 10_000)
        {
            return Problem(detail: "Name an index, a time range, and a query.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!EventArchiver.CanArchive(request.Index))
        {
            return Problem(detail: "An archived index's name has only letters, digits, '-', '_' and '.'.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_scope.CanRead(request.Index))
        {
            return Problem(detail: $"Searching the archive of {request.Index} needs reading {request.Index}.", statusCode: StatusCodes.Status403Forbidden);
        }

        var query = request.Query;
        if (!request.Exact)
        {
            var understanding = new Cabinet.Query.Natural.QueryUnderstander().Understand(query);
            if (!understanding.WillRun)
            {
                return Problem(detail: understanding.Problems.FirstOrDefault()?.Message ?? "Couldn't tell what this asks for.", statusCode: StatusCodes.Status400BadRequest);
            }

            query = understanding.Query!;
        }

        var started = DateTime.UtcNow;
        Cabinet.Storage.InMemoryStorage archived;
        try
        {
            archived = await _archiver.LoadAsync(
                request.Index,
                request.From.ToUniversalTime(),
                request.To.ToUniversalTime(),
                _configuration?.GetValue("Archive:SearchMaxEvents", 500_000) ?? 500_000,
                cancellationToken);
        }
        catch (ArchiveTooLargeException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var engine = new Cabinet.Query.Bql.BqlQueryEngine(
                _scope.Restrict(archived),
                new Cabinet.Query.Bql.BqlParser(),
                new Cabinet.Query.Bql.BqlOptimizer(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Cabinet.Query.Bql.BqlQueryEngine>.Instance);
            var result = await engine.ExecuteAsync(new Cabinet.Query.QueryExecutionContext { Query = query, Limit = Math.Clamp(request.Limit ?? 10_000, 1, 10_000) }, timeout.Token);
            await _audit.LogAsync(new AuditEvent
            {
                PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
                Action = AuditAction.Search,
                ResourceType = "Archive",
                Description = $"Searched the archive of {request.Index} from {request.From:O} to {request.To:O}: {query[..Math.Min(200, query.Length)]}",
                SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            });
            return Ok(new QueryResponse
            {
                Query = query,
                Results = result.Events.Cast<object>().ToList(),
                Count = result.Events.Count,
                ExecutionTimeMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
                Timestamp = DateTime.UtcNow,
                EventsScanned = result.Statistics?.EventsScanned ?? 0,
                Truncated = result.Statistics?.Truncated == true,
                Approximate = result.Statistics?.Approximate == true,
            });
        }
        catch (Cabinet.Query.QueryParseException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
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

        if (!EventArchiver.CanArchive(request.Index))
        {
            return Problem(detail: "An archived index's name has only letters, digits, '-', '_' and '.'.", statusCode: StatusCodes.Status400BadRequest);
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

/// <summary>A search of archived events.</summary>
public sealed class ArchiveSearchRequest
{
    /// <summary>The archived index.</summary>
    public string? Index { get; set; }

    public DateTime From { get; set; }

    public DateTime To { get; set; }

    /// <summary>BQL, SPL or plain words, as on the search page.</summary>
    public string Query { get; set; } = "find *";

    /// <summary>Run the query exactly as typed.</summary>
    public bool Exact { get; set; }

    /// <summary>Most rows to return.</summary>
    public int? Limit { get; set; }
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
