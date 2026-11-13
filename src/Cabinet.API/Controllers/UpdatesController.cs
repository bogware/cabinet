using Cabinet.API.Middleware;
using Cabinet.API.Services.Updates;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Updating Cabinet from its GitHub releases: this server on request, and agents to match it.
/// </summary>
[ApiController]
[Route("api/v1/updates")]
[Authorize]
public class UpdatesController : ControllerBase
{
    private readonly UpdateService _updates;
    private readonly UpdateOptions _options;

    public UpdatesController(UpdateService updates, UpdateOptions? options = null)
    {
        _updates = updates;
        _options = options ?? new UpdateOptions();
    }

    /// <summary>
    /// This server's version, the newest release, and the agents that have checked in
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.SystemHealth)]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status200OK)]
    public IActionResult Status() => Ok(_updates.GetStatus());

    /// <summary>
    /// Look for a new release now
    /// </summary>
    [HttpPost("check")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status200OK)]
    public async Task<IActionResult> Check(CancellationToken cancellationToken = default) =>
        Ok(await _updates.CheckAsync(cancellationToken));

    /// <summary>
    /// Download, verify and install the newest release. The server restarts on it, and goes back
    /// to this version if the new one doesn't start. Agents follow within their check interval.
    /// </summary>
    [HttpPost("apply")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(UpdateStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Apply(CancellationToken cancellationToken = default)
    {
        try
        {
            return Accepted(await _updates.ApplyAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// For agents: the agent build matching this server, if it's newer than the caller's
    /// </summary>
    [HttpGet("agent")]
    [RequireCapability(Capability.Ingest)]
    [ProducesResponseType(typeof(AgentUpdateOffer), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AgentUpdate(
        [FromQuery] string platform,
        [FromQuery] string version,
        [FromQuery] string? host = null,
        CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersions.Platforms.Contains(platform) || ReleaseVersions.Parse(version) is not { } agentVersion)
        {
            return Problem(detail: "Unknown platform or version", statusCode: 400);
        }

        _updates.RecordAgent(string.IsNullOrWhiteSpace(host) ? HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown" : host[..Math.Min(host.Length, 128)], platform, version);
        var server = ReleaseVersions.Parse(ReleaseVersions.CurrentFull)!.Value;
        if (ReleaseVersions.Compare((ReleaseVersions.Normalize(agentVersion.Core), agentVersion.Pre), server) >= 0
            || !_options.OffersAgentTo(host ?? string.Empty))
        {
            return NoContent();
        }

        var package = await _updates.GetAgentPackageAsync(platform, cancellationToken);

        // Agents before 3.0 read only plain versions, and would skip a pre-release they can't parse
        var offered = agentVersion.Pre == null ? server.Core.ToString() : package?.Version;
        return package == null
            ? NoContent()
            : Ok(new AgentUpdateOffer { Version = offered!, Sha256 = package.Sha256, Size = package.Size, Signature = package.Signature });
    }

    /// <summary>
    /// For agents: download the agent build for a platform
    /// </summary>
    [HttpGet("agent/{platform}")]
    [RequireCapability(Capability.Ingest)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AgentBinary(string platform, CancellationToken cancellationToken = default)
    {
        var package = await _updates.GetAgentPackageAsync(platform, cancellationToken);
        return package == null
            ? Problem(detail: "No agent build for that platform", statusCode: 404)
            : PhysicalFile(package.Path, "application/octet-stream");
    }
}

/// <summary>A newer agent build an agent should install.</summary>
public class AgentUpdateOffer
{
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>
    /// The publisher's signature over the agent program (base64 of a DER ECDSA P-256 / SHA-256
    /// signature), from the release's <c>cabinet-agent-&lt;platform&gt;.sig</c>. Agents from 3.0.1
    /// check it against the key built into them; earlier agents ignore it.
    /// </summary>
    public string? Signature { get; set; }
}
