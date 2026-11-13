using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Tokens identity providers use to call SCIM. A token is shown once, when it's made; only its
/// first characters afterwards. A SCIM token can only call SCIM.
/// </summary>
[ApiController]
[Route("api/v1/scim-tokens")]
[Authorize]
[RequireCapability(Capability.UserManagement)]
public sealed class ScimTokensController : ControllerBase
{
    private readonly ScimTokenService _tokens;
    private readonly IAuditLogger _audit;

    public ScimTokensController(ScimTokenService tokens, IAuditLogger audit)
    {
        _tokens = tokens;
        _audit = audit;
    }

    private Task AuditAsync(AuditAction action, string id, string description, CancellationToken cancellationToken) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "ScimToken",
            ResourceId = id,
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, cancellationToken);

    /// <summary>
    /// Every SCIM token, revoked ones included. Never the tokens themselves.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ScimTokenListing>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok((await _tokens.ListAsync(cancellationToken)).Select(ScimTokenListing.From).ToList());

    /// <summary>
    /// Make a SCIM token. The response holds the token, which is never shown again.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreatedScimToken), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateScimTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Name.Any(char.IsControl))
        {
            return Problem(detail: "Give the token a name of up to 100 characters", statusCode: 400);
        }

        var (token, info) = await _tokens.CreateAsync(request.Name.Trim(), cancellationToken);
        await AuditAsync(AuditAction.Create, info.Id, $"Made SCIM token '{info.Name}'", cancellationToken);
        return Created($"/api/v1/scim-tokens/{info.Id}", new CreatedScimToken { Token = token, Listing = ScimTokenListing.From(info) });
    }

    /// <summary>
    /// Revoke a SCIM token. It stops working at once on this instance, and within 30 seconds on others.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ScimTokenListing), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken = default)
    {
        var info = await _tokens.RevokeAsync(id, cancellationToken);
        if (info == null)
        {
            return Problem(detail: "No such token", statusCode: 404);
        }

        await AuditAsync(AuditAction.Delete, id, $"Revoked SCIM token '{AuditName.Sanitize(info.Name)}'", cancellationToken);
        return Ok(ScimTokenListing.From(info));
    }
}

/// <summary>A SCIM token as listed: never the token.</summary>
public sealed class ScimTokenListing
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Prefix { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public bool Revoked { get; init; }
    public DateTime? RevokedAt { get; init; }

    public static ScimTokenListing From(ScimToken token) => new()
    {
        Id = token.Id,
        Name = token.Name,
        Prefix = token.Prefix,
        CreatedAt = token.CreatedAt,
        LastUsedAt = token.LastUsedAt,
        Revoked = token.Revoked,
        RevokedAt = token.RevokedAt,
    };
}

/// <summary>A SCIM token just made, with the token itself.</summary>
public sealed class CreatedScimToken
{
    /// <summary>The token. Shown this once.</summary>
    public required string Token { get; init; }
    public required ScimTokenListing Listing { get; init; }
}

/// <summary>A SCIM token to make.</summary>
public sealed class CreateScimTokenRequest
{
    public string Name { get; set; } = string.Empty;
}
