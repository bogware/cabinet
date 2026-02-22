using Cabinet.API.Middleware;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Alert history and management API.
/// </summary>
[ApiController]
[Route("api/v1/alerts")]
[Authorize]
[RequireCapability(Capability.Search)]
public class AlertsController : ControllerBase
{
    private readonly IAlertHistoryStorage _storage;

    public AlertsController(IAlertHistoryStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// List alerts with optional filtering by severity and acknowledgment status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<AlertRecord>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100,
        [FromQuery] string? severity = null,
        [FromQuery] bool? acknowledged = null)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);
        var alerts = await _storage.GetAlertsAsync(offset, count, severity, acknowledged);
        var total = await _storage.GetAlertCountAsync(severity, acknowledged);
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(alerts);
    }

    /// <summary>
    /// Get a single alert by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(AlertRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAlert(string id)
    {
        var alert = await _storage.GetAlertByIdAsync(id);
        if (alert == null) return NotFound();
        return Ok(alert);
    }

    /// <summary>
    /// Acknowledge an alert.
    /// </summary>
    [HttpPost("{id}/acknowledge")]
    [ProducesResponseType(typeof(AlertRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAlert(string id)
    {
        var userId = User.FindFirst("ClientId")?.Value ?? User.Identity?.Name ?? "unknown";
        var alert = await _storage.AcknowledgeAlertAsync(id, userId);
        if (alert == null) return NotFound();
        return Ok(alert);
    }
}
