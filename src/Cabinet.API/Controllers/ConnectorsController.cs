using System.Text.RegularExpressions;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Named destinations for alerts: Slack, Teams, Discord, PagerDuty, Opsgenie, Jira, ServiceNow,
/// email or a plain webhook. Rules refer to them by name. Secrets are write-only.
/// </summary>
[ApiController]
[Route("api/v1/connectors")]
[Authorize]
public partial class ConnectorsController : ControllerBase
{
    private readonly ConnectorStore _store;
    private readonly ConnectorRunner _runner;
    private readonly OutboundUrlGuard _guard;
    private readonly IAuditLogger? _audit;

    public ConnectorsController(ConnectorStore store, ConnectorRunner runner, OutboundUrlGuard guard, IAuditLogger? audit = null)
    {
        _store = store;
        _runner = runner;
        _guard = guard;
        _audit = audit;
    }

    /// <summary>
    /// List connectors, without their secrets
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(List<ActionConnector>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok((await _store.ListAsync(cancellationToken)).Select(Redact).ToList());

    /// <summary>
    /// Get a connector, without its secret
    /// </summary>
    [HttpGet("{name}")]
    [RequireCapability(Capability.Alerts)]
    [ProducesResponseType(typeof(ActionConnector), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string name, CancellationToken cancellationToken = default) =>
        await _store.GetAsync(name, cancellationToken) is { } connector ? Ok(Redact(connector)) : Problem(detail: "Connector not found", statusCode: 404);

    /// <summary>
    /// Create or replace a connector. Leaving the secret empty keeps the one already saved.
    /// </summary>
    [HttpPut("{name}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(ActionConnector), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(string name, [FromBody] ActionConnector connector, CancellationToken cancellationToken = default)
    {
        connector.Name = name;
        if (Validate(connector) is { } problem)
        {
            return problem;
        }

        var existing = await _store.GetAsync(name, cancellationToken);
        if (string.IsNullOrEmpty(connector.Secret))
        {
            connector.Secret = existing?.Secret;
        }

        connector.HasSecret = false;
        await _store.SaveAsync(connector, cancellationToken);
        await AuditAsync(existing == null ? AuditAction.Create : AuditAction.Update, name, $"{(existing == null ? "Added" : "Changed")} {connector.Type} connector {name}");
        return Ok(Redact(connector));
    }

    /// <summary>
    /// Delete a connector. Rules that use it log a warning and skip it.
    /// </summary>
    [HttpDelete("{name}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken = default)
    {
        if (!await _store.DeleteAsync(name, cancellationToken))
        {
            return Problem(detail: "Connector not found", statusCode: 404);
        }

        await AuditAsync(AuditAction.Delete, name, $"Deleted connector {name}");
        return NoContent();
    }

    /// <summary>
    /// Send a test notification through a saved connector
    /// </summary>
    [HttpPost("{name}/test")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(ConnectorTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Test(string name, CancellationToken cancellationToken = default)
    {
        var connector = await _store.GetAsync(name, cancellationToken);
        if (connector == null)
        {
            return Problem(detail: "Connector not found", statusCode: 404);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await _runner.SendAsync(connector, new ConnectorNotification
            {
                Title = "Cabinet test notification",
                Message = $"This is a test from the '{name}' connector. If you can read it, alerts will arrive here.",
                Severity = "Info",
                Facts = new Dictionary<string, string> { ["connector"] = name, ["type"] = connector.Type },
                DedupKey = $"cabinet-test-{name}",
            }, timeout.Token);
            return Ok(new ConnectorTestResult { Ok = true, Message = "Sent" });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or System.Net.Mail.SmtpException or FormatException)
        {
            return Ok(new ConnectorTestResult { Ok = false, Message = ex.Message });
        }
    }

    private ObjectResult? Validate(ActionConnector connector)
    {
        if (!NamePattern().IsMatch(connector.Name))
        {
            return Problem(detail: "Name must be 1-64 letters, digits, dashes, dots or underscores", statusCode: 400);
        }

        connector.Type = connector.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!ActionConnector.Types.Contains(connector.Type))
        {
            return Problem(detail: $"Type must be one of {string.Join(", ", ActionConnector.Types)}", statusCode: 400);
        }

        var urlOptional = connector.Type is "pagerduty" or "opsgenie" or "email";
        if (string.IsNullOrWhiteSpace(connector.Url))
        {
            return urlOptional ? null : Problem(detail: "This connector needs a URL", statusCode: 400);
        }

        return _guard.Check(connector.Url) is { } reason ? Problem(detail: reason, statusCode: 400) : null;
    }

    private static ActionConnector Redact(ActionConnector connector) => new()
    {
        Name = connector.Name,
        Type = connector.Type,
        Url = connector.Url,
        Settings = connector.Settings,
        Enabled = connector.Enabled,
        HasSecret = !string.IsNullOrEmpty(connector.Secret),
        Secret = null,
    };

    private Task AuditAsync(AuditAction action, string name, string description) => _audit?.LogAsync(new AuditEvent
    {
        PrincipalId = User.FindFirst("ClientId")?.Value ?? User.Identity?.Name ?? "unknown",
        Action = action,
        ResourceType = "Connector",
        ResourceId = name,
        Description = description,
        SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
    }) ?? Task.CompletedTask;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex NamePattern();
}

/// <summary>Whether a test notification went through.</summary>
public sealed class ConnectorTestResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}
