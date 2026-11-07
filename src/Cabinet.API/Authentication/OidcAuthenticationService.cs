using System.Security.Claims;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Authentication;

/// <summary>
/// Maps OIDC/SAML claims to Cabinet RBAC roles and capabilities.
/// Uses ClaimRoleMapping from SsoConfiguration to translate external
/// identity claims to internal SystemRole assignments.
/// </summary>
public sealed class ClaimRoleMapper
{
    private readonly ILogger<ClaimRoleMapper> _logger;

    public ClaimRoleMapper(ILogger<ClaimRoleMapper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Add Cabinet-specific claims based on OIDC claims and configured role mappings.
    /// </summary>
    public void MapClaims(ClaimsIdentity identity, SsoConfiguration ssoConfig)
    {
        SystemRole? mappedRole = ssoConfig.AutoProvision ? ssoConfig.DefaultRole : null;

        foreach (var mapping in ssoConfig.RoleMappings)
        {
            // Group claims often appear several times, one per group
            if (!identity.FindAll(mapping.ClaimType).Any(claim => claim.Value.Equals(mapping.ClaimValue, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            mappedRole = mapping.Role;
            foreach (var indexPerm in mapping.IndexPermissions)
            {
                identity.AddClaim(new Claim(PrincipalResolver.IndexPermissionClaim, $"{indexPerm.IndexPattern}:{indexPerm.Capabilities}"));
            }

            _logger.LogDebug("Mapped claim {Type}={Value} to role {Role}", mapping.ClaimType, mapping.ClaimValue, mapping.Role);
            break;
        }

        // The subject identifies the user whether or not they're allowed in, for audit logging
        var sub = identity.FindFirst(ClaimTypes.NameIdentifier) ?? identity.FindFirst("sub");
        if (sub != null)
        {
            identity.AddClaim(new Claim("ClientId", $"oidc-{sub.Value}"));
        }

        if (mappedRole is not { } role)
        {
            _logger.LogInformation("No role mapping matched for {Subject}, and Sso:AutoProvision is off", sub?.Value);
            return;
        }

        identity.AddClaim(new Claim(PrincipalResolver.RoleClaim, role.ToString()));
        identity.AddClaim(new Claim("Tier", MapRoleToTier(role)));
        identity.AddClaim(new Claim("Permission", role >= SystemRole.Analyst ? "Write" : "Read"));
    }

    private static string MapRoleToTier(SystemRole role) => role switch
    {
        SystemRole.SuperAdmin => "Enterprise",
        SystemRole.Admin => "Enterprise",
        SystemRole.Analyst => "Professional",
        _ => "Basic"
    };

}
