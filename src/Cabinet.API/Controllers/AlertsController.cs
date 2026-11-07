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
    private readonly Cabinet.API.Authentication.IndexAccessScope? _scope;

    public AlertsController(IAlertHistoryStorage storage, Cabinet.API.Authentication.IndexAccessScope? scope = null)
    {
        _storage = storage;
        _scope = scope;
    }

    /// <summary>
    /// Whether the caller may see this alert: one about an event needs read access to its index,
    /// and one about no particular index needs every index.
    /// </summary>
    private bool Visible(AlertRecord alert) =>
        _scope == null || _scope.CanReadEveryIndex || (alert.Index != null && _scope.CanRead(alert.Index));

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
        if (_scope == null || _scope.CanReadEveryIndex)
        {
            var alerts = await _storage.GetAlertsAsync(offset, count, severity, acknowledged);
            var total = await _storage.GetAlertCountAsync(severity, acknowledged);
            Response.Headers["X-Total-Count"] = total.ToString();
            return Ok(alerts);
        }

        // History is capped, so filtering all of it here stays bounded
        var all = await _storage.GetAlertsAsync(0, int.MaxValue, severity, acknowledged);
        var visible = all.Where(Visible).ToList();
        Response.Headers["X-Total-Count"] = visible.Count.ToString();
        return Ok(visible.Skip(offset).Take(count).ToList());
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
        if (alert == null || !Visible(alert)) return NotFound();
        return Ok(alert);
    }

    /// <summary>
    /// Acknowledge an alert.
    /// </summary>
    [HttpPost("{id}/acknowledge")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(AlertRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAlert(string id)
    {
        var userId = User.FindFirst("ClientId")?.Value ?? User.Identity?.Name ?? "unknown";
        if (_scope != null && !_scope.CanReadEveryIndex && await _storage.GetAlertByIdAsync(id) is { } existing && !Visible(existing))
        {
            return NotFound();
        }

        var alert = await _storage.AcknowledgeAlertAsync(id, userId);
        if (alert == null) return NotFound();
        return Ok(alert);
    }
}
