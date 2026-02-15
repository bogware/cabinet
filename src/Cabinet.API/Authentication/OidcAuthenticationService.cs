using System.Security.Claims;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Authentication;

/// <summary>
/// Service to configure OpenID Connect authentication.
/// Maps OIDC claims to Cabinet roles and capabilities.
/// Integrates with the existing RBAC system via ClaimRoleMapping.
/// </summary>
public static class OidcAuthenticationExtensions
{
    /// <summary>
    /// Add OIDC/SAML authentication alongside the existing ApiKey scheme.
    /// When SSO is enabled, both auth schemes are registered and the default
    /// policy accepts either.
    /// </summary>
    public static IServiceCollection AddCabinetSso(
        this IServiceCollection services,
        IConfiguration configuration,
        Serilog.ILogger logger)
    {
        var ssoConfig = configuration.GetSection("Sso").Get<SsoConfiguration>();
        if (ssoConfig == null || !ssoConfig.Enabled)
        {
            logger.Information("SSO not enabled (set Sso:Enabled=true to enable)");
            return services;
        }

        switch (ssoConfig.Provider)
        {
            case SsoProviderType.Oidc when ssoConfig.Oidc != null:
                ConfigureOidc(services, ssoConfig, logger);
                break;

            case SsoProviderType.Saml when ssoConfig.Saml != null:
                logger.Warning("SAML authentication requires the Sustainsys.Saml2 package. Configure OIDC instead or add the package.");
                break;

            case SsoProviderType.Ldap when ssoConfig.Ldap != null:
                logger.Warning("LDAP authentication requires the Novell.Directory.Ldap.NETStandard package. Configure OIDC instead or add the package.");
                break;

            default:
                logger.Warning("SSO enabled but no provider configured. Set Sso:Provider and configure the corresponding section.");
                break;
        }

        // Register the claim-to-role mapper
        services.AddSingleton(ssoConfig);
        services.AddSingleton<ClaimRoleMapper>();

        return services;
    }

    private static void ConfigureOidc(IServiceCollection services, SsoConfiguration ssoConfig, Serilog.ILogger logger)
    {
        var oidc = ssoConfig.Oidc!;

        services.AddAuthentication(options =>
        {
            // Support both ApiKey and OIDC — ApiKey is the default, OIDC is additional
            options.DefaultScheme = "MultiScheme";
            options.DefaultChallengeScheme = "Bearer";
        })
        .AddPolicyScheme("MultiScheme", "API Key or Bearer", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                // If X-API-Key header is present, use ApiKey scheme
                if (context.Request.Headers.ContainsKey("X-API-Key"))
                    return "ApiKey";

                // Otherwise try Bearer/OIDC
                return "Bearer";
            };
        })
        .AddJwtBearer("Bearer", options =>
        {
            options.Authority = oidc.Authority;
            options.Audience = oidc.ClientId;
            options.RequireHttpsMetadata = !oidc.Authority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);

            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    // Map OIDC claims to Cabinet claims
                    var mapper = context.HttpContext.RequestServices.GetService<ClaimRoleMapper>();
                    if (mapper != null && context.Principal != null)
                    {
                        var identity = context.Principal.Identity as ClaimsIdentity;
                        if (identity != null)
                        {
                            mapper.MapClaims(identity, ssoConfig);
                        }
                    }
                    return Task.CompletedTask;
                }
            };
        });

        logger.Information("OIDC authentication configured (Authority: {Authority}, ClientId: {ClientId})",
            oidc.Authority, oidc.ClientId);
    }
}

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
        var mappedRole = ssoConfig.DefaultRole;
        var matchedMapping = false;

        foreach (var mapping in ssoConfig.RoleMappings)
        {
            var claim = identity.FindFirst(mapping.ClaimType);
            if (claim != null && claim.Value.Equals(mapping.ClaimValue, StringComparison.OrdinalIgnoreCase))
            {
                mappedRole = mapping.Role;
                matchedMapping = true;

                // Add index permissions as claims
                foreach (var indexPerm in mapping.IndexPermissions)
                {
                    identity.AddClaim(new Claim("IndexPermission", $"{indexPerm.IndexPattern}:{indexPerm.Capabilities}"));
                }

                _logger.LogDebug("Mapped claim {Type}={Value} to role {Role}",
                    mapping.ClaimType, mapping.ClaimValue, mapping.Role);
                break;
            }
        }

        if (!matchedMapping && ssoConfig.AutoProvision)
        {
            _logger.LogDebug("No role mapping matched, using default role {Role}", ssoConfig.DefaultRole);
        }

        // Add Cabinet-standard claims that the existing auth pipeline expects
        identity.AddClaim(new Claim("Tier", MapRoleToTier(mappedRole)));
        identity.AddClaim(new Claim("Permission", mappedRole >= SystemRole.Analyst ? "Write" : "Read"));

        // Add capabilities based on role
        var capabilities = GetCapabilitiesForRole(mappedRole);
        foreach (var cap in capabilities)
        {
            identity.AddClaim(new Claim("Capability", cap.ToString()));
        }

        // Use the OIDC subject as the ClientId for audit logging
        var sub = identity.FindFirst(ClaimTypes.NameIdentifier) ?? identity.FindFirst("sub");
        if (sub != null)
        {
            identity.AddClaim(new Claim("ClientId", sub.Value));
        }
    }

    private static string MapRoleToTier(SystemRole role) => role switch
    {
        SystemRole.SuperAdmin => "Enterprise",
        SystemRole.Admin => "Enterprise",
        SystemRole.Analyst => "Professional",
        _ => "Basic"
    };

    private static List<Capability> GetCapabilitiesForRole(SystemRole role) => role switch
    {
        SystemRole.SuperAdmin => new List<Capability>
        {
            Capability.Search, Capability.Ingest, Capability.Export,
            Capability.SavedQueries, Capability.Detection, Capability.UserManagement
        },
        SystemRole.Admin => new List<Capability>
        {
            Capability.Search, Capability.Ingest, Capability.Export,
            Capability.SavedQueries, Capability.Detection, Capability.UserManagement
        },
        SystemRole.Analyst => new List<Capability>
        {
            Capability.Search, Capability.Export, Capability.SavedQueries, Capability.Detection
        },
        _ => new List<Capability> { Capability.Search }
    };
}
