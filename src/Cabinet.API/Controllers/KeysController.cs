using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// API keys: listing them, making new ones, and revoking them. Keys are never shown again after
/// they're made; only their first characters, to tell them apart.
/// </summary>
[ApiController]
[Route("api/v1/keys")]
[Authorize]
[RequireCapability(Capability.UserManagement)]
public class KeysController : ControllerBase
{
    private readonly ApiKeyService _keys;
    private readonly IAuditLogger _audit;

    public KeysController(ApiKeyService keys, IAuditLogger audit)
    {
        _keys = keys;
        _audit = audit;
    }

    private Task AuditAsync(AuditAction action, string id, string description, CancellationToken cancellationToken) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "ApiKey",
            ResourceId = id,
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, cancellationToken);

    /// <summary>
    /// Every key, including revoked and expired ones
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ApiKeyListing>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok((await _keys.ListAsync(cancellationToken)).Select(ApiKeyListing.From).ToList());

    /// <summary>
    /// Make a key. The response holds the key itself, which is never shown again.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreatedApiKey), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            return Problem(detail: "Give the key a name of up to 100 characters", statusCode: 400);
        }

        if (!ApiKeyService.Tiers.Contains(request.Tier, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(detail: $"Tier must be one of: {string.Join(", ", ApiKeyService.Tiers)}", statusCode: 400);
        }

        if (request.ExpiresAt is { } expires && expires <= DateTime.UtcNow)
        {
            return Problem(detail: "The expiry must be in the future", statusCode: 400);
        }

        try
        {
            var (key, info) = await _keys.CreateAsync(request.Name.Trim(), request.Tier, request.ExpiresAt, cancellationToken);
            await AuditAsync(AuditAction.ApiKeyCreated, info.Id, $"Created {info.Tier} key '{info.ClientName}'", cancellationToken);
            var created = CreatedApiKey.From(info, key);
            return Created($"/api/v1/keys/{info.Id}", created);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
    }

    /// <summary>
    /// Revoke a key. It stops working on every instance within the refresh interval, at once on
    /// this one.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ApiKeyListing), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken = default)
    {
        if (id == User.FindFirst("ClientId")?.Value)
        {
            return Problem(detail: "That's the key you're signed in with. Sign in with another admin key to revoke it.", statusCode: 409);
        }

        var info = await _keys.RevokeAsync(id, cancellationToken);
        if (info == null)
        {
            return Problem(detail: "No such key", statusCode: 404);
        }

        await AuditAsync(AuditAction.ApiKeyRevoked, id, $"Revoked {info.Tier} key '{info.ClientName}'", cancellationToken);
        return Ok(ApiKeyListing.From(info));
    }
}

/// <summary>A key as listed: never the key itself.</summary>
public class ApiKeyListing
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Tier { get; init; }
    public string? Prefix { get; init; }

    /// <summary>settings for keys in configuration, dashboard for keys made in the dashboard.</summary>
    public required string Source { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
    public bool Revoked { get; init; }
    public DateTime? RevokedAt { get; init; }
    public bool Expired { get; init; }

    public static ApiKeyListing From(ApiKeyInfo info) => new()
    {
        Id = info.Id,
        Name = info.ClientName,
        Tier = info.Tier,
        Prefix = info.Prefix,
        Source = info.Source,
        CreatedAt = info.CreatedAt,
        ExpiresAt = info.ExpiresAt,
        LastUsedAt = info.LastUsedAt,
        Revoked = !info.IsActive,
        RevokedAt = info.RevokedAt,
        Expired = info.ExpiresAt is { } at && at < DateTime.UtcNow,
    };
}

/// <summary>A key just made, with the key itself.</summary>
public sealed class CreatedApiKey : ApiKeyListing
{
    /// <summary>The key. Shown this once.</summary>
    public required string Key { get; init; }

    public static CreatedApiKey From(ApiKeyInfo info, string key)
    {
        var listing = ApiKeyListing.From(info);
        return new CreatedApiKey
        {
            Key = key,
            Id = listing.Id,
            Name = listing.Name,
            Tier = listing.Tier,
            Prefix = listing.Prefix,
            Source = listing.Source,
            CreatedAt = listing.CreatedAt,
            ExpiresAt = listing.ExpiresAt,
            LastUsedAt = listing.LastUsedAt,
            Revoked = listing.Revoked,
            RevokedAt = listing.RevokedAt,
            Expired = listing.Expired,
        };
    }
}

/// <summary>A key to make.</summary>
public class CreateApiKeyRequest
{
    public string Name { get; set; } = string.Empty;
    public string Tier { get; set; } = "Ingest";
    public DateTime? ExpiresAt { get; set; }
}
