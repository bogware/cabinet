using System.Security.Claims;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cabinet.API.Authentication;

/// <summary>
/// What Entra ID and Cognito do differently from a plain OpenID Connect provider, applied to both
/// bearer tokens for API clients and the dashboard's sign-in.
/// </summary>
/// <remarks>
/// Entra issues v1 tokens (issuer https://sts.windows.net/{tid}/) or v2 tokens
/// (https://login.microsoftonline.com/{tid}/v2.0) depending on the API's manifest, gives a
/// different <c>sub</c> to each application but the same <c>oid</c> everywhere (the ID SCIM
/// provisioning uses), and puts app roles in <c>roles</c> and group object IDs in <c>groups</c>,
/// leaving groups out when a user has too many ("overage"). Cognito access tokens have no
/// audience, only <c>client_id</c> and <c>token_use=access</c>, and groups are in
/// <c>cognito:groups</c>.
/// </remarks>
public static class OidcProviders
{
    /// <summary>The claim Entra and Cognito access tokens name their app client in.</summary>
    private const string ClientIdClaim = "client_id";

    private static readonly Regex Guid = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The authority to discover the provider's metadata from.</summary>
    public static string Authority(OidcSettings oidc) => oidc.Provider switch
    {
        _ when !string.IsNullOrWhiteSpace(oidc.Authority) => oidc.Authority.TrimEnd('/'),
        OidcProvider.Entra => $"https://login.microsoftonline.com/{oidc.TenantId}/v2.0",
        OidcProvider.Cognito => $"https://cognito-idp.{oidc.Region}.amazonaws.com/{oidc.UserPoolId}",
        _ => oidc.Authority,
    };

    /// <summary>The claim holding group memberships.</summary>
    public static string GroupsClaim(OidcSettings oidc) =>
        oidc.Provider == OidcProvider.Cognito && oidc.GroupsClaim == "groups" ? "cognito:groups" : oidc.GroupsClaim;

    /// <summary>Why these settings can't work, or null. Checked at startup so a mistake stops the server rather than every sign-in.</summary>
    public static string? Problem(OidcSettings oidc)
    {
        if (string.IsNullOrWhiteSpace(oidc.ClientId))
        {
            return "Sso:Oidc:ClientId is required.";
        }

        switch (oidc.Provider)
        {
            case OidcProvider.Entra when string.IsNullOrWhiteSpace(oidc.Authority):
                if (string.IsNullOrWhiteSpace(oidc.TenantId))
                {
                    return "Entra needs Sso:Oidc:TenantId: the directory (tenant) ID or a verified domain.";
                }

                if (oidc.TenantId.Trim().ToLowerInvariant() is "common" or "organizations" or "consumers")
                {
                    return $"Sso:Oidc:TenantId '{oidc.TenantId}' would let any Microsoft account sign in. Use your organization's tenant ID.";
                }

                break;
            case OidcProvider.Entra when Regex.IsMatch(oidc.Authority, "/(common|organizations|consumers)(/|$)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)):
                return "Sso:Oidc:Authority names a multi-tenant Entra endpoint, which would let any Microsoft account sign in. Use your organization's tenant.";
            case OidcProvider.Cognito when string.IsNullOrWhiteSpace(oidc.Authority) && (string.IsNullOrWhiteSpace(oidc.Region) || string.IsNullOrWhiteSpace(oidc.UserPoolId)):
                return "Cognito needs Sso:Oidc:Region and Sso:Oidc:UserPoolId, or Sso:Oidc:Authority.";
            case OidcProvider.Generic when string.IsNullOrWhiteSpace(oidc.Authority):
                return "Sso:Oidc:Authority is required: the provider's issuer URL.";
        }

        return null;
    }

    /// <summary>The audiences a bearer token may be issued for.</summary>
    public static IReadOnlyList<string> Audiences(OidcSettings oidc)
    {
        var audiences = new List<string> { oidc.ClientId };
        if (oidc.Provider == OidcProvider.Entra && !oidc.ClientId.StartsWith("api://", StringComparison.OrdinalIgnoreCase))
        {
            audiences.Add($"api://{oidc.ClientId}");
        }

        audiences.AddRange(oidc.Audiences.Where(audience => !string.IsNullOrWhiteSpace(audience)));
        return audiences.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Sets up bearer-token validation for the provider.</summary>
    public static void ConfigureBearer(JwtBearerOptions options, OidcSettings oidc)
    {
        var authority = Authority(oidc);
        options.Authority = authority;
        options.RequireHttpsMetadata = !authority.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
            && !authority.StartsWith("http://127.0.0.1", StringComparison.Ordinal);

        // Claims keep the provider's names (sub, groups, roles), as the dashboard's sign-in does
        options.MapInboundClaims = false;
        var audiences = Audiences(oidc);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            ValidAudiences = audiences,
            NameClaimType = "name",
            RoleClaimType = PrincipalResolver.RoleClaim,
        };

        if (oidc.Provider == OidcProvider.Entra)
        {
            // The handler gives the discovered metadata to the second; tests and older paths use the first
            var configured = Guid.IsMatch(oidc.TenantId ?? string.Empty) ? oidc.TenantId : TenantOf(oidc.Authority);
            parameters.IssuerValidator = (issuer, token, p) =>
                EntraIssuer(issuer, token, (p.ValidIssuers ?? []).Append(p.ValidIssuer), configured);
            parameters.IssuerValidatorUsingConfiguration = (issuer, token, p, configuration) =>
                EntraIssuer(issuer, token, (p.ValidIssuers ?? []).Append(p.ValidIssuer).Append(configuration?.Issuer), configured);
        }

        if (oidc.Provider == OidcProvider.Cognito)
        {
            var clients = new HashSet<string>(oidc.ClientIds.Append(oidc.ClientId), StringComparer.Ordinal);
            parameters.AudienceValidator = (tokenAudiences, token, _) => CognitoAudience(tokenAudiences, token, audiences, clients);
        }

        options.TokenValidationParameters = parameters;
    }

    /// <summary>
    /// Entra's v1 and v2 issuers for the configured tenant, and no other: the tenant comes from
    /// the discovered metadata, and the token's tid must name it too.
    /// </summary>
    internal static string EntraIssuer(string issuer, SecurityToken token, IEnumerable<string?> metadataIssuers, string? configuredTenant)
    {
        var tenant = configuredTenant?.ToLowerInvariant() ?? metadataIssuers.Select(TenantOf).FirstOrDefault(t => t != null);
        var tid = (token as JsonWebToken)?.TryGetPayloadValue<string>("tid", out var value) == true ? value : null;
        var accepted = tenant != null
            && string.Equals(tid, tenant, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(issuer, $"https://login.microsoftonline.com/{tenant}/v2.0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(issuer, $"https://sts.windows.net/{tenant}/", StringComparison.OrdinalIgnoreCase));

        return accepted ? issuer : throw new SecurityTokenInvalidIssuerException($"Issuer '{issuer}' isn't this Entra tenant's");
    }

    /// <summary>The tenant ID in an Entra issuer URL.</summary>
    internal static string? TenantOf(string? issuer)
    {
        if (issuer == null || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var first = uri.AbsolutePath.Trim('/').Split('/')[0];
        return Guid.IsMatch(first) ? first.ToLowerInvariant() : null;
    }

    /// <summary>An ID token names the app client as its audience; an access token only in client_id.</summary>
    internal static bool CognitoAudience(IEnumerable<string> tokenAudiences, SecurityToken token, IReadOnlyCollection<string> audiences, IReadOnlySet<string> clients)
    {
        if (tokenAudiences.Any(audiences.Contains))
        {
            return true;
        }

        return token is JsonWebToken jwt
            && jwt.TryGetPayloadValue<string>("token_use", out var use) && use == "access"
            && jwt.TryGetPayloadValue<string>(ClientIdClaim, out var client) && clients.Contains(client);
    }

    /// <summary>
    /// Gives a provider's claims the shapes role mapping and the user directory expect: groups in
    /// <c>groups</c>, Entra's stable <c>oid</c> as the user's ID, and a user name.
    /// </summary>
    public static void Normalize(ClaimsIdentity identity, OidcSettings oidc, ILogger? logger = null)
    {
        var groupsClaim = GroupsClaim(oidc);
        if (groupsClaim != "groups")
        {
            foreach (var group in identity.FindAll(groupsClaim).ToList())
            {
                foreach (var value in Values(group.Value))
                {
                    identity.AddClaim(new Claim("groups", value));
                }
            }
        }

        if (oidc.Provider == OidcProvider.Entra)
        {
            // sub differs per application; oid is the user's object ID in the directory, the same
            // everywhere, and what SCIM provisioning sends
            if (identity.FindFirst("oid")?.Value is { Length: > 0 } oid && identity.FindFirst(ClaimTypes.NameIdentifier) == null)
            {
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, oid));
            }

            if (identity.FindFirst("preferred_username") == null && identity.FindFirst("upn")?.Value is { } upn)
            {
                identity.AddClaim(new Claim("preferred_username", upn));
            }

            // Over 200 groups (150 in SAML, 6 in implicit flow), Entra leaves them out and points at Graph
            if (identity.HasClaim(claim => claim.Type == "hasgroups") || identity.FindFirst("_claim_names")?.Value.Contains("\"groups\"", StringComparison.Ordinal) == true)
            {
                logger?.LogWarning(
                    "Entra left groups out of the token for {User}: they're in too many. Map app roles (the roles claim) instead, or set the app registration's group claims to 'Groups assigned to the application'",
                    identity.FindFirst("preferred_username")?.Value ?? identity.FindFirst("oid")?.Value);
            }
        }

        if (oidc.Provider == OidcProvider.Cognito && identity.FindFirst("preferred_username") == null
            && (identity.FindFirst("cognito:username") ?? identity.FindFirst("username"))?.Value is { } username)
        {
            identity.AddClaim(new Claim("preferred_username", username));
        }
    }

    /// <summary>A claim's values: one, or a JSON array's items when the handler kept the array whole.</summary>
    private static IEnumerable<string> Values(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('['))
        {
            return [value];
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(trimmed);
            return document.RootElement.EnumerateArray().Select(item => item.ToString()).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [value];
        }
    }
}
