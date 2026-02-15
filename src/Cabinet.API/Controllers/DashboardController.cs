using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.API.Middleware;

namespace Cabinet.API.Controllers;

/// <summary>
/// Dashboard management API
/// </summary>
[ApiController]
[Route("api/v1/dashboards")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardStorage _storage;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IDashboardStorage storage, IAuditLogger auditLogger, ILogger<DashboardController> logger)
    {
        _storage = storage;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// List all dashboards
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<DashboardSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDashboards(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var dashboards = await _storage.GetAllAsync(cancellationToken);

        var summaries = dashboards
            .Skip(offset)
            .Take(count)
            .Select(d => new DashboardSummary
            {
                Id = d.Id,
                Title = d.Title,
                Description = d.Description,
                CreatedBy = d.CreatedBy,
                CreatedAt = d.CreatedAt,
                UpdatedAt = d.UpdatedAt,
                IsShared = d.IsShared,
                PanelCount = d.Panels.Count
            })
            .ToList();

        Response.Headers["X-Total-Count"] = dashboards.Count().ToString();
        return Ok(summaries);
    }

    /// <summary>
    /// Get a dashboard by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(Dashboard), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDashboard(string id, CancellationToken cancellationToken = default)
    {
        var dashboard = await _storage.GetByIdAsync(id, cancellationToken);
        if (dashboard == null)
            return Problem(detail: "Dashboard not found", statusCode: 404);

        return Ok(dashboard);
    }

    /// <summary>
    /// Create a new dashboard
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Dashboard), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> CreateDashboard([FromBody] CreateDashboardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Problem(detail: "Title is required", statusCode: 400);

        if (request.Title.Length > 200)
            return Problem(detail: "Title must be 200 characters or fewer", statusCode: 400);

        if (request.RefreshIntervalSeconds < 0 || request.RefreshIntervalSeconds > 86400)
            return Problem(detail: "RefreshIntervalSeconds must be between 0 and 86400", statusCode: 400);

        if (request.Panels is { Count: > 50 })
            return Problem(detail: "Maximum 50 panels per dashboard", statusCode: 400);

        var dashboard = new Dashboard
        {
            Title = request.Title,
            Description = request.Description,
            CreatedBy = User.Identity?.Name ?? "unknown",
            IsShared = request.IsShared,
            RefreshIntervalSeconds = request.RefreshIntervalSeconds,
            DefaultTimeRange = request.DefaultTimeRange ?? "-24h"
        };

        if (request.Panels != null)
        {
            foreach (var p in request.Panels)
            {
                if (p.Width < 1 || p.Width > 12 || p.Height < 1 || p.Height > 12)
                    return Problem(detail: "Panel Width and Height must be between 1 and 12", statusCode: 400);
                if (p.Row < 0 || p.Column < 0)
                    return Problem(detail: "Panel Row and Column must be non-negative", statusCode: 400);
            }

            dashboard.Panels = request.Panels.Select(p => new DashboardPanel
            {
                Id = Ulid.NewUlid(),
                Title = p.Title,
                Query = p.Query,
                Type = p.Type,
                Row = p.Row,
                Column = p.Column,
                Width = p.Width,
                Height = p.Height
            }).ToList();
        }

        await _storage.SaveAsync(dashboard);
        _logger.LogInformation("Created dashboard '{Title}' (id: {Id})", dashboard.Title, dashboard.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "Dashboard",
            ResourceId = dashboard.Id,
            Description = $"Created dashboard: {dashboard.Title}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetDashboard), new { id = dashboard.Id }, dashboard);
    }

    /// <summary>
    /// Update a dashboard
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Dashboard), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> UpdateDashboard(string id, [FromBody] UpdateDashboardRequest request)
    {
        var dashboard = await _storage.GetByIdAsync(id);
        if (dashboard == null)
            return Problem(detail: "Dashboard not found", statusCode: 404);

        if (request.Title != null)
        {
            if (request.Title.Length > 200)
                return Problem(detail: "Title must be 200 characters or fewer", statusCode: 400);
            dashboard.Title = request.Title;
        }
        if (request.Description != null)
            dashboard.Description = request.Description;
        if (request.IsShared.HasValue)
            dashboard.IsShared = request.IsShared.Value;
        if (request.RefreshIntervalSeconds.HasValue)
        {
            if (request.RefreshIntervalSeconds.Value < 0 || request.RefreshIntervalSeconds.Value > 86400)
                return Problem(detail: "RefreshIntervalSeconds must be between 0 and 86400", statusCode: 400);
            dashboard.RefreshIntervalSeconds = request.RefreshIntervalSeconds.Value;
        }
        if (request.DefaultTimeRange != null)
            dashboard.DefaultTimeRange = request.DefaultTimeRange;

        if (request.Panels != null)
        {
            if (request.Panels.Count > 50)
                return Problem(detail: "Maximum 50 panels per dashboard", statusCode: 400);

            foreach (var p in request.Panels)
            {
                if (p.Width < 1 || p.Width > 12 || p.Height < 1 || p.Height > 12)
                    return Problem(detail: "Panel Width and Height must be between 1 and 12", statusCode: 400);
                if (p.Row < 0 || p.Column < 0)
                    return Problem(detail: "Panel Row and Column must be non-negative", statusCode: 400);
            }

            dashboard.Panels = request.Panels.Select(p => new DashboardPanel
            {
                Id = p.Id ?? Ulid.NewUlid(),
                Title = p.Title,
                Query = p.Query,
                Type = p.Type,
                Row = p.Row,
                Column = p.Column,
                Width = p.Width,
                Height = p.Height
            }).ToList();
        }

        dashboard.UpdatedAt = DateTime.UtcNow;
        await _storage.SaveAsync(dashboard);
        _logger.LogInformation("Updated dashboard '{Title}' (id: {Id})", dashboard.Title, dashboard.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "Dashboard",
            ResourceId = id,
            Description = $"Updated dashboard: {dashboard.Title}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(dashboard);
    }

    /// <summary>
    /// Delete a dashboard
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteDashboard(string id)
    {
        var deleted = await _storage.DeleteAsync(id);
        if (!deleted)
            return Problem(detail: "Dashboard not found", statusCode: 404);

        _logger.LogInformation("Deleted dashboard (id: {Id})", id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "Dashboard",
            ResourceId = id,
            Description = $"Deleted dashboard: {id}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }
}

// DTOs
public class DashboardSummary
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsShared { get; set; }
    public int PanelCount { get; set; }
}

public class CreateDashboardRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsShared { get; set; }
    public int RefreshIntervalSeconds { get; set; }
    public string? DefaultTimeRange { get; set; }
    public List<CreatePanelRequest>? Panels { get; set; }
}

public class CreatePanelRequest
{
    public string Title { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public VisualizationType Type { get; set; } = VisualizationType.Table;
    public int Row { get; set; }
    public int Column { get; set; }
    public int Width { get; set; } = 6;
    public int Height { get; set; } = 4;
}

public class UpdateDashboardRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public bool? IsShared { get; set; }
    public int? RefreshIntervalSeconds { get; set; }
    public string? DefaultTimeRange { get; set; }
    public List<UpdatePanelRequest>? Panels { get; set; }
}

public class UpdatePanelRequest
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public VisualizationType Type { get; set; } = VisualizationType.Table;
    public int Row { get; set; }
    public int Column { get; set; }
    public int Width { get; set; } = 6;
    public int Height { get; set; } = 4;
}
