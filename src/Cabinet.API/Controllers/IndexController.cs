using Cabinet.API.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;

namespace Cabinet.API.Controllers;

/// <summary>
/// Index management API — list, create metadata for, and manage logical indexes.
/// Indexes are logical partitions in Cabinet (analogous to Splunk indexes).
/// </summary>
[ApiController]
[Route("api/v1/indexes")]
[Authorize]
public class IndexController : ControllerBase
{
    private readonly ILogStorage _storage;
    private readonly IndexAccessScope? _indexScope;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<IndexController> _logger;

    public IndexController(ILogStorage storage, IAuditLogger auditLogger, ILogger<IndexController> logger, IndexAccessScope? indexScope = null)
    {
        _indexScope = indexScope;
        _storage = indexScope?.Restrict(storage) ?? storage;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// List all indexes with event counts and time ranges.
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<IndexInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListIndexes(CancellationToken cancellationToken = default)
    {
        var stats = await _storage.GetStatsAsync(cancellationToken);

        var indexes = stats.EventsByIndex.Select(kvp => new IndexInfo
        {
            Name = kvp.Key,
            EventCount = kvp.Value
        }).ToList();

        // If no indexes are tracked yet, return at least "main"
        if (indexes.Count == 0)
        {
            var totalCount = await _storage.GetCountAsync(null, cancellationToken);
            indexes.Add(new IndexInfo
            {
                Name = "main",
                EventCount = totalCount
            });
        }

        Response.Headers["X-Total-Count"] = indexes.Count.ToString();
        return Ok(indexes);
    }

    /// <summary>
    /// Get details for a specific index.
    /// </summary>
    [HttpGet("{name}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(IndexDetail), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetIndex(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
            return Problem(detail: "Invalid index name", statusCode: 400);

        var context = new QueryContext { Index = name, Limit = 0 };
        var count = await _storage.GetCountAsync(context, cancellationToken);

        return Ok(new IndexDetail
        {
            Name = name,
            EventCount = count,
            IsDefault = name == "main"
        });
    }

    /// <summary>
    /// Delete all events in a specific index.
    /// Uses index-scoped query + batch delete to avoid clearing other indexes.
    /// </summary>
    [HttpDelete("{name}")]
    [RequireCapability(Capability.IndexManagement)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ClearIndex(string name, CancellationToken cancellationToken = default)
    {
        if (_indexScope?.Principal != null && !_indexScope.Can(Capability.IndexManagement, name))
        {
            return Problem(detail: $"Not allowed to manage index '{name}'", statusCode: 403);
        }

        if (string.IsNullOrWhiteSpace(name) || name.Length > 256)
            return Problem(detail: "Invalid index name", statusCode: 400);

        // Verify the index exists by checking event count
        var indexContext = new QueryContext { Index = name, Limit = 0 };
        var eventCount = await _storage.GetCountAsync(indexContext, cancellationToken);

        if (eventCount == 0)
        {
            return Ok(new { index = name, deletedCount = 0, message = $"Index '{name}' is empty or does not exist" });
        }

        var deletedCount = await _storage.DeleteByIndexAsync(name, cancellationToken);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "Index",
            ResourceId = name,
            Description = $"Cleared index '{name}' ({deletedCount} events deleted)",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new { index = name, deletedCount, message = $"Index '{name}' cleared" });
    }
}

// DTOs

public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public long EventCount { get; set; }
}

public class IndexDetail
{
    public string Name { get; set; } = string.Empty;
    public long EventCount { get; set; }
    public bool IsDefault { get; set; }
}
