using System.Text.Json;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Settings;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// The server's settings: every one, what it does, its value and where that comes from, and
/// saving changes made in the dashboard.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly ServiceRestarter _restarter;
    private readonly IServer _server;

    public SettingsController(SettingsService settings, ServiceRestarter restarter, IServer server)
    {
        _settings = settings;
        _restarter = restarter;
        _server = server;
    }

    private string Principal => User.FindFirst("ClientId")?.Value ?? "unknown";

    private string? SourceIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private SettingsView View() =>
        _settings.GetView(_server.Features.Get<IServerAddressesFeature>()?.Addresses.ToList() ?? []);

    /// <summary>
    /// Every setting by group, with its description, allowed values, value in effect and source.
    /// Secrets show only whether they're set.
    /// </summary>
    [HttpGet]
    // Admins only: settings describe the whole deployment, which agents' keys shouldn't read
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(SettingsView), StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(View());

    /// <summary>
    /// Saves settings: <c>{"values": {"Storage:RetentionDays": 30}}</c>. All are checked first,
    /// and nothing is saved if any is wrong. A null value returns a setting to its file or default.
    /// </summary>
    [HttpPut]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(SettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SettingsErrors), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save([FromBody] SettingsUpdate update, CancellationToken cancellationToken = default)
    {
        if (update.Values == null || update.Values.Count == 0)
        {
            return BadRequest(new SettingsErrors { Errors = new Dictionary<string, string> { [""] = "Send at least one value" } });
        }

        try
        {
            await _settings.SaveAsync(update.Values, Principal, SourceIp, cancellationToken);
            return Ok(View());
        }
        catch (SettingsValidationException ex)
        {
            return BadRequest(new SettingsErrors { Errors = ex.Errors });
        }
    }

    /// <summary>
    /// Returns a setting to the value in the appsettings files, or its default
    /// </summary>
    [HttpDelete("{**key}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(SettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SettingsErrors), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revert(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _settings.RevertAsync(key, Principal, SourceIp, cancellationToken)
                ? Ok(View())
                : Problem(detail: $"There's no setting '{key}'", statusCode: 404);
        }
        catch (SettingsValidationException ex)
        {
            return BadRequest(new SettingsErrors { Errors = ex.Errors });
        }
    }

    /// <summary>
    /// Restarts the server so saved settings take effect. Only for a server running as a Windows
    /// or systemd service, which starts it again.
    /// </summary>
    [HttpPost("restart")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restart([FromServices] IAuditLogger auditLogger, CancellationToken cancellationToken = default)
    {
        var (canRestart, reason) = _restarter.CanRestart();
        if (!canRestart)
        {
            return Problem(detail: reason, statusCode: StatusCodes.Status409Conflict);
        }

        await auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = Principal,
            Action = AuditAction.Update,
            ResourceType = "Server",
            Description = "Restarted from the dashboard",
            SourceIp = SourceIp,
        }, cancellationToken);
        _restarter.Restart(Principal);
        return Accepted(new { message = "Restarting. The server will be back in a few seconds." });
    }

    /// <summary>
    /// Where events are stored, how big the database is, how many events it holds and how much
    /// disk is left
    /// </summary>
    [HttpGet("storage")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(StorageInfo), StatusCodes.Status200OK)]
    public async Task<IActionResult> Storage(
        [FromServices] IConfiguration configuration,
        [FromServices] ILogStorage storage,
        [FromServices] Cabinet.API.Authentication.IndexAccessScope scope,
        CancellationToken cancellationToken = default) =>
        Ok(await StorageInfoService.DescribeAsync(configuration, scope.Restrict(storage), cancellationToken));
}

/// <summary>Settings to save, by key.</summary>
public class SettingsUpdate
{
    public Dictionary<string, JsonElement>? Values { get; set; }
}

/// <summary>Why settings weren't saved, by key.</summary>
public class SettingsErrors
{
    public required IReadOnlyDictionary<string, string> Errors { get; set; }
}
