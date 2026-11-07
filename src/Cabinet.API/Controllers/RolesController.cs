using Cabinet.API.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;

namespace Cabinet.API.Controllers;

/// <summary>
/// Users and roles management API.
/// Provides CRUD operations for RBAC principals (users tied to API keys).
/// Requires SuperAdmin capability.
/// </summary>
[ApiController]
[Route("api/v1/roles")]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly IRbacService _rbacService;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RolesController> _logger;

    public RolesController(IRbacService rbacService, IAuditLogger auditLogger, ILogger<RolesController> logger)
    {
        _rbacService = rbacService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// The caller's own principal, including the ID to use when changing its role
    /// </summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(RbacPrincipalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCurrentPrincipal(CancellationToken cancellationToken = default)
    {
        var principal = await PrincipalResolver.ResolveAsync(User, _rbacService, cancellationToken);
        return principal == null
            ? Problem(detail: "This identity has no capabilities assigned", statusCode: 403)
            : Ok(MapToDto(principal));
    }

    /// <summary>
    /// List all RBAC principals (users/roles)
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(List<RbacPrincipalDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPrincipals(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var principals = await _rbacService.ListPrincipalsAsync(cancellationToken);

        var dtos = principals
            .Skip(offset)
            .Take(count)
            .Select(MapToDto)
            .ToList();

        Response.Headers["X-Total-Count"] = principals.Count.ToString();
        return Ok(dtos);
    }

    /// <summary>
    /// Get a specific principal by API key hash
    /// </summary>
    [HttpGet("{apiKeyHash}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(RbacPrincipalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPrincipal(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var principal = await _rbacService.GetPrincipalAsync(apiKeyHash, cancellationToken);
        if (principal == null)
            return Problem(detail: "Principal not found", statusCode: 404);

        return Ok(MapToDto(principal));
    }

    /// <summary>
    /// Create or update a principal
    /// </summary>
    [HttpPut("{apiKeyHash}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(typeof(RbacPrincipalDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SavePrincipal(
        string apiKeyHash,
        [FromBody] SavePrincipalRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKeyHash) || apiKeyHash.Length > 256)
            return Problem(detail: "Invalid API key hash", statusCode: 400);

        if (!Enum.TryParse<SystemRole>(request.Role, true, out var role))
            return Problem(detail: $"Invalid role. Valid roles: {string.Join(", ", Enum.GetNames<SystemRole>())}", statusCode: 400);

        var principal = new RbacPrincipal
        {
            Id = apiKeyHash,
            Role = role,
            AdditionalCapabilities = ParseCapabilities(request.AdditionalCapabilities),
            IndexPermissions = (request.IndexPermissions ?? new List<IndexPermissionDto>())
                .Select(ip => new IndexPermission
                {
                    IndexPattern = ip.IndexPattern,
                    Capabilities = ParseCapabilities(ip.Capabilities)
                }).ToList()
        };

        var saved = await _rbacService.SavePrincipalAsync(principal, cancellationToken);
        if (!saved)
            return Problem(detail: "RBAC service does not support principal management. Use database-backed RBAC.", statusCode: 501);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "RbacPrincipal",
            ResourceId = apiKeyHash,
            Description = $"Saved principal with role '{role}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(MapToDto(principal));
    }

    /// <summary>
    /// Delete a principal
    /// </summary>
    [HttpDelete("{apiKeyHash}")]
    [RequireCapability(Capability.UserManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePrincipal(string apiKeyHash, CancellationToken cancellationToken = default)
    {
        var deleted = await _rbacService.DeletePrincipalAsync(apiKeyHash, cancellationToken);
        if (!deleted)
            return Problem(detail: "Principal not found or RBAC service does not support deletion", statusCode: 404);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "RbacPrincipal",
            ResourceId = apiKeyHash,
            Description = "Deleted RBAC principal",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    private static Capability ParseCapabilities(List<string>? capabilities)
    {
        if (capabilities == null || capabilities.Count == 0)
            return Capability.None;

        var result = Capability.None;
        foreach (var cap in capabilities)
        {
            if (Enum.TryParse<Capability>(cap, true, out var parsed))
                result |= parsed;
        }
        return result;
    }

    private static RbacPrincipalDto MapToDto(RbacPrincipal principal) => new()
    {
        ClientId = principal.Id,
        Role = principal.Role.ToString(),
        AdditionalCapabilities = GetCapabilityNames(principal.AdditionalCapabilities),
        IndexPermissions = principal.IndexPermissions.Select(ip => new IndexPermissionDto
        {
            IndexPattern = ip.IndexPattern,
            Capabilities = GetCapabilityNames(ip.Capabilities)
        }).ToList()
    };

    private static List<string> GetCapabilityNames(Capability caps)
    {
        return Enum.GetValues<Capability>()
            .Where(c => c != Capability.None && c != Capability.All && caps.HasFlag(c))
            .Select(c => c.ToString())
            .ToList();
    }
}

// DTOs
public class RbacPrincipalDto
{
    public string ClientId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> AdditionalCapabilities { get; set; } = new();
    public List<IndexPermissionDto> IndexPermissions { get; set; } = new();
}

public class IndexPermissionDto
{
    public string IndexPattern { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = new();
}

public class SavePrincipalRequest
{
    public string Role { get; set; } = "Viewer";
    public List<string>? AdditionalCapabilities { get; set; }
    public List<IndexPermissionDto>? IndexPermissions { get; set; }
}
