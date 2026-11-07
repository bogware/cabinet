using Cabinet.API.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;

namespace Cabinet.API.Controllers;

/// <summary>
/// Scheduled search management API.
/// Provides CRUD operations for scheduled searches that run BQL queries on cron schedules
/// and fire alerts when conditions are met.
/// </summary>
[ApiController]
[Route("api/v1/scheduled-searches")]
[Authorize]
public class ScheduledSearchController : ControllerBase
{
    private readonly IndexAccessScope? _indexScope;

    private readonly ScheduledSearchService _scheduledSearchService;
    private readonly IQueryParser _queryParser;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ScheduledSearchController> _logger;

    public ScheduledSearchController(
        ScheduledSearchService scheduledSearchService,
        IQueryParser queryParser,
        IAuditLogger auditLogger,
        ILogger<ScheduledSearchController> logger, IndexAccessScope? indexScope = null)
    {
        _indexScope = indexScope;
        _scheduledSearchService = scheduledSearchService;
        _queryParser = queryParser;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// List all scheduled searches
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<ScheduledSearchSummaryDto>), StatusCodes.Status200OK)]
    public IActionResult ListScheduledSearches(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var searches = _scheduledSearchService.GetAll();
        var total = searches.Count();

        var summaries = searches
            .Skip(offset)
            .Take(count)
            .Select(s => new ScheduledSearchSummaryDto
            {
                Id = s.Id,
                Name = s.Name,
                Query = s.Query,
                CronExpression = s.CronExpression,
                Enabled = s.Enabled,
                TimeRange = s.TimeRange,
                LastRunAt = s.LastRunAt,
                LastRunSuccess = s.LastRunSuccess,
                LastRunResultCount = s.LastRunResultCount,
                LastRunError = s.LastRunError,
                CreatedBy = s.CreatedBy,
                CreatedAt = s.CreatedAt
            }).ToList();

        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(summaries);
    }

    /// <summary>
    /// Get a scheduled search by ID
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(ScheduledSearch), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetScheduledSearch(string id)
    {
        var search = _scheduledSearchService.Get(id);
        if (search == null)
            return Problem(detail: "Scheduled search not found", statusCode: 404);

        return Ok(search);
    }

    /// <summary>
    /// Create a new scheduled search
    /// </summary>
    [HttpPost]
    [RequireCapability(Capability.Alerts)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(typeof(ScheduledSearch), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateScheduledSearch([FromBody] CreateScheduledSearchRequest request)
    {
        // It runs with no caller to limit it, and its alerts would reveal what it finds
        if (_indexScope is { CanReadEveryIndex: false })
        {
            return Problem(detail: "Scheduled searches run over every index, so they need Search on all of them", statusCode: 403);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Name is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.CronExpression))
            return Problem(detail: "CronExpression is required", statusCode: 400);

        // Validate BQL syntax before scheduling to prevent repeated parse errors at execution time
        try
        {
            _queryParser.Parse(request.Query);
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }

        // Validate cron expression syntax at creation time (not just at execution time)
        var cronParts = request.CronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (cronParts.Length < 5)
            return Problem(detail: "Invalid cron expression: must have 5 fields (minute hour day month weekday)", statusCode: 400);

        // Validate each cron field has valid syntax (digits, *, -, /, ,) and ranges
        var cronMaxValues = new[] { 59, 23, 31, 12, 6 }; // minute, hour, day, month, weekday
        var cronMinValues = new[] { 0, 0, 1, 1, 0 };
        for (var ci = 0; ci < 5; ci++)
        {
            var part = cronParts[ci];
            if (!System.Text.RegularExpressions.Regex.IsMatch(part, @"^[\d\*\-/,]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
                return Problem(detail: $"Invalid cron expression: field '{part}' contains invalid characters", statusCode: 400);

            // Check numeric values are in valid range for each field
            if (part != "*")
            {
                var nums = part.Replace("*/", "").Split(new[] { ',', '-', '/' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var num in nums)
                {
                    if (int.TryParse(num, out var val) && (val < cronMinValues[ci] || val > cronMaxValues[ci]))
                        return Problem(detail: $"Invalid cron expression: value {val} out of range ({cronMinValues[ci]}-{cronMaxValues[ci]})", statusCode: 400);
                }
            }
        }

        var search = new ScheduledSearch
        {
            Name = request.Name,
            Query = request.Query,
            Description = request.Description,
            CronExpression = request.CronExpression,
            Enabled = request.Enabled,
            TimeRange = request.TimeRange ?? "-15m",
            CreatedBy = User.Identity?.Name ?? "unknown"
        };

        if (request.AlertAction != null)
        {
            search.AlertAction = new ScheduledSearchAlert
            {
                Condition = request.AlertAction.Condition ?? "count > 0",
                Threshold = request.AlertAction.Threshold,
                Severity = request.AlertAction.Severity,
                Cooldown = request.AlertAction.CooldownMinutes.HasValue
                    ? TimeSpan.FromMinutes(request.AlertAction.CooldownMinutes.Value)
                    : TimeSpan.FromMinutes(15)
            };
        }

        await _scheduledSearchService.RegisterAsync(search);
        _logger.LogInformation("Created scheduled search '{Name}' (id: {Id})", search.Name, search.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "ScheduledSearch",
            ResourceId = search.Id,
            Description = $"Created scheduled search: {search.Name}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetScheduledSearch), new { id = search.Id }, search);
    }

    /// <summary>
    /// Update a scheduled search
    /// </summary>
    [HttpPut("{id}")]
    [RequireCapability(Capability.Alerts)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(typeof(ScheduledSearch), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateScheduledSearch(string id, [FromBody] UpdateScheduledSearchRequest request)
    {
        // It runs with no caller to limit it, and its alerts would reveal what it finds
        if (_indexScope is { CanReadEveryIndex: false })
        {
            return Problem(detail: "Scheduled searches run over every index, so they need Search on all of them", statusCode: 403);
        }

        var search = _scheduledSearchService.Get(id);
        if (search == null)
            return Problem(detail: "Scheduled search not found", statusCode: 404);

        // Validate BQL syntax if query is being updated
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            try
            {
                _queryParser.Parse(request.Query);
            }
            catch (QueryParseException ex)
            {
                return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
            }
        }

        // Validate cron expression if being updated
        if (!string.IsNullOrWhiteSpace(request.CronExpression))
        {
            var cronParts = request.CronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cronParts.Length < 5)
                return Problem(detail: "Invalid cron expression: must have 5 fields (minute hour day month weekday)", statusCode: 400);

            foreach (var part in cronParts.Take(5))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(part, @"^[\d\*\-/,]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
                    return Problem(detail: $"Invalid cron expression: field '{part}' contains invalid characters", statusCode: 400);
            }
        }

        if (request.Name != null) search.Name = request.Name;
        if (request.Query != null) search.Query = request.Query;
        if (request.Description != null) search.Description = request.Description;
        if (request.CronExpression != null) search.CronExpression = request.CronExpression;
        if (request.Enabled.HasValue) search.Enabled = request.Enabled.Value;
        if (request.TimeRange != null) search.TimeRange = request.TimeRange;

        if (request.AlertAction != null)
        {
            search.AlertAction = new ScheduledSearchAlert
            {
                Condition = request.AlertAction.Condition ?? "count > 0",
                Threshold = request.AlertAction.Threshold,
                Severity = request.AlertAction.Severity,
                Cooldown = request.AlertAction.CooldownMinutes.HasValue
                    ? TimeSpan.FromMinutes(request.AlertAction.CooldownMinutes.Value)
                    : TimeSpan.FromMinutes(15)
            };
        }

        await _scheduledSearchService.RegisterAsync(search);
        _logger.LogInformation("Updated scheduled search '{Name}' (id: {Id})", search.Name, search.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "ScheduledSearch",
            ResourceId = id,
            Description = $"Updated scheduled search: {search.Name}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(search);
    }

    /// <summary>
    /// Delete a scheduled search
    /// </summary>
    [HttpDelete("{id}")]
    [RequireCapability(Capability.Alerts)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteScheduledSearch(string id)
    {
        var removed = await _scheduledSearchService.UnregisterAsync(id);
        if (!removed)
            return Problem(detail: "Scheduled search not found", statusCode: 404);

        _logger.LogInformation("Deleted scheduled search (id: {Id})", id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "ScheduledSearch",
            ResourceId = id,
            Description = $"Deleted scheduled search: {id}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    /// <summary>
    /// Enable a scheduled search
    /// </summary>
    [HttpPost("{id}/enable")]
    [RequireCapability(Capability.Alerts)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> EnableScheduledSearch(string id)
    {
        var search = _scheduledSearchService.Get(id);
        if (search == null)
            return Problem(detail: "Scheduled search not found", statusCode: 404);

        search.Enabled = true;
        await _scheduledSearchService.RegisterAsync(search);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "ScheduledSearch",
            ResourceId = id,
            Description = $"Enabled scheduled search '{search.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new { message = $"Scheduled search '{search.Name}' enabled" });
    }

    /// <summary>
    /// Disable a scheduled search
    /// </summary>
    [HttpPost("{id}/disable")]
    [RequireCapability(Capability.Alerts)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DisableScheduledSearch(string id)
    {
        var search = _scheduledSearchService.Get(id);
        if (search == null)
            return Problem(detail: "Scheduled search not found", statusCode: 404);

        search.Enabled = false;
        await _scheduledSearchService.RegisterAsync(search);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "ScheduledSearch",
            ResourceId = id,
            Description = $"Disabled scheduled search '{search.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new { message = $"Scheduled search '{search.Name}' disabled" });
    }
}

// DTOs

public class ScheduledSearchSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string TimeRange { get; set; } = string.Empty;
    public DateTime? LastRunAt { get; set; }
    public bool LastRunSuccess { get; set; }
    public int LastRunResultCount { get; set; }
    public string? LastRunError { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class CreateScheduledSearchRequest
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string? TimeRange { get; set; }
    public AlertActionRequest? AlertAction { get; set; }
}

public class UpdateScheduledSearchRequest
{
    public string? Name { get; set; }
    public string? Query { get; set; }
    public string? Description { get; set; }
    public string? CronExpression { get; set; }
    public bool? Enabled { get; set; }
    public string? TimeRange { get; set; }
    public AlertActionRequest? AlertAction { get; set; }
}

public class AlertActionRequest
{
    public string? Condition { get; set; }
    public int Threshold { get; set; }
    public int Severity { get; set; } = 1;
    public int? CooldownMinutes { get; set; }
}
