namespace Cabinet.Core.Models;

/// <summary>
/// SSO configuration for Cabinet authentication.
/// Currently only OIDC is implemented end-to-end (see <see cref="OidcSettings"/>).
/// SAML 2.0 and LDAP configuration models exist as foundation for future implementation
/// but are not yet wired to authentication providers. See ADR-008 in ARCHITECTURE.md.
/// </summary>
public sealed class SsoConfiguration
{
    /// <summary>
    /// Enable SSO authentication. When true, the configured <see cref="Provider"/> is activated
    /// alongside API key authentication. Currently only <see cref="SsoProviderType.Oidc"/> is functional.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The main provider. Each configured section is available whatever this says: OIDC bearer
    /// tokens for API clients and OIDC, SAML and LDAP sign-in for the dashboard.
    /// </summary>
    public SsoProviderType Provider { get; set; } = SsoProviderType.Oidc;

    /// <summary>
    /// OpenID Connect settings: bearer tokens for API clients, and, with a client secret, the
    /// dashboard's sign-in (authorization code flow with PKCE).
    /// </summary>
    public OidcSettings? Oidc { get; set; }

    /// <summary>
    /// SAML 2.0 settings for the dashboard's sign-in. Cabinet is the service provider; the
    /// identity provider's metadata gives its sign-in URL and signing certificates.
    /// </summary>
    public SamlSettings? Saml { get; set; }

    /// <summary>
    /// LDAP or Active Directory settings for the dashboard's sign-in with a user name and
    /// password, checked by binding to the directory. Groups come from memberOf.
    /// </summary>
    public LdapSettings? Ldap { get; set; }

    /// <summary>
    /// Maps external identity claims (OIDC tokens, SAML assertions, LDAP groups)
    /// to Cabinet <see cref="SystemRole"/> assignments and index permissions.
    /// </summary>
    public List<ClaimRoleMapping> RoleMappings { get; set; } = new();

    /// <summary>
    /// When true, users authenticating via SSO are automatically provisioned
    /// with <see cref="DefaultRole"/> if no <see cref="RoleMappings"/> match. Off by default:
    /// otherwise every account the identity provider or directory knows could read every index.
    /// </summary>
    public bool AutoProvision { get; set; }

    /// <summary>How long a dashboard sign-in lasts while it's used, in hours.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>
    /// The longest a dashboard sign-in lasts however much it's used, in hours, so a role taken
    /// away at the identity provider or directory takes effect.
    /// </summary>
    public int MaxSessionHours { get; set; } = 24;

    /// <summary>
    /// Default role assigned to SSO-authenticated users when no claim mapping matches.
    /// </summary>
    public SystemRole DefaultRole { get; set; } = SystemRole.Viewer;
}

/// <summary>
/// SSO provider type selector. Only <see cref="Oidc"/> is currently implemented.
/// <see cref="Saml"/> and <see cref="Ldap"/> are reserved for future implementation.
/// </summary>
public enum SsoProviderType
{
    /// <summary>
    /// OpenID Connect — the recommended and only implemented SSO provider.
    /// Supports JWT Bearer tokens via any OIDC-compliant identity provider
    /// (Entra ID, Okta, Keycloak, Auth0, etc.).
    /// </summary>
    Oidc,

    /// <summary>
    /// SAML 2.0 — configuration foundation only, not yet implemented.
    /// Requires the Sustainsys.Saml2 NuGet package. Planned for future release.
    /// </summary>
    Saml,

    /// <summary>
    /// LDAP / Active Directory — configuration foundation only, not yet implemented.
    /// Requires the Novell.Directory.Ldap.NETStandard NuGet package. Planned for future release.
    /// </summary>
    Ldap
}

/// <summary>
/// OpenID Connect authentication settings. This is the only SSO provider currently
/// implemented in Cabinet. Configured via the <c>Sso:Oidc</c> configuration section.
/// Supports any OIDC-compliant identity provider (Entra ID, Okta, Keycloak, Auth0, etc.).
/// </summary>
public sealed class OidcSettings
{
    /// <summary>
    /// The identity provider, for what it does differently: <see cref="OidcProvider.Entra"/> and
    /// <see cref="OidcProvider.Cognito"/> derive the authority, issuers and audiences, and read their
    /// own group claims. <see cref="OidcProvider.Generic"/> (the default) takes everything as configured.
    /// </summary>
    public OidcProvider Provider { get; set; } = OidcProvider.Generic;

    /// <summary>
    /// OIDC authority URL (issuer). Example: <c>https://login.microsoftonline.com/{tenant}/v2.0</c>.
    /// Leave empty for Entra and Cognito to derive it from <see cref="TenantId"/>, or
    /// <see cref="Region"/> and <see cref="UserPoolId"/>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Entra ID: the directory (tenant) ID or a verified domain. Not common or organizations: one organization signs in.</summary>
    public string? TenantId { get; set; }

    /// <summary>Cognito: the user pool's AWS region, such as us-east-1.</summary>
    public string? Region { get; set; }

    /// <summary>Cognito: the user pool ID, such as us-east-1_AbCdEf123.</summary>
    public string? UserPoolId { get; set; }

    /// <summary>
    /// More audiences accepted in bearer tokens besides <see cref="ClientId"/>, such as an Entra
    /// application ID URI (api://... is accepted for Entra already) or a resource server identifier.
    /// </summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>
    /// Cognito: other app clients whose access tokens are accepted. Cognito access tokens have no
    /// audience, only the client_id they were issued to; <see cref="ClientId"/> is always accepted.
    /// </summary>
    public List<string> ClientIds { get; set; } = [];

    /// <summary>
    /// OIDC client (application) ID registered with the identity provider.
    /// </summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// OIDC client secret. Required for confidential clients. Use environment variables
    /// or user-secrets in production — never hardcode.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// OIDC scopes to request. Defaults to openid, profile, email.
    /// </summary>
    public string[] Scopes { get; set; } = new[] { "openid", "profile", "email" };

    /// <summary>
    /// OAuth2 callback path for authorization code flow.
    /// </summary>
    public string CallbackPath { get; set; } = "/auth/callback";

    /// <summary>
    /// Name of the JWT claim containing group memberships, used for role mapping. Its values are
    /// also given as <c>groups</c> claims, so role mappings on <c>groups</c> work with any provider.
    /// Cognito's is <c>cognito:groups</c>, used when this is left as <c>groups</c>.
    /// </summary>
    public string GroupsClaim { get; set; } = "groups";
}

/// <summary>Identity providers whose differences Cabinet knows.</summary>
public enum OidcProvider
{
    /// <summary>Any standard OpenID Connect provider: Okta, Keycloak, Auth0, Google Workspace, Ping and others.</summary>
    Generic,

    /// <summary>Microsoft Entra ID (Azure AD): tenant issuers v1 and v2, oid as the stable user ID, app roles and group object IDs.</summary>
    Entra,

    /// <summary>Amazon Cognito user pools: access tokens identified by client_id, groups in cognito:groups.</summary>
    Cognito,
}

/// <summary>
/// SAML 2.0 authentication settings. Configuration foundation only — not yet implemented.
/// OIDC is the recommended SSO path. SAML 2.0 support requires the Sustainsys.Saml2
/// NuGet package and is planned for a future release. These settings are present so that
/// configuration can be prepared ahead of the implementation.
/// </summary>
public sealed class SamlSettings
{
    /// <summary>
    /// Service Provider entity ID (your application's identifier in the IdP).
    /// </summary>
    public required string EntityId { get; set; }

    /// <summary>
    /// IdP metadata URL for federation metadata discovery.
    /// </summary>
    public required string MetadataUrl { get; set; }

    /// <summary>
    /// Assertion Consumer Service (ACS) URL where the IdP posts SAML responses.
    /// </summary>
    public string? AssertionConsumerServiceUrl { get; set; }

    /// <summary>
    /// X.509 certificate (PEM or Base64) for signature validation.
    /// </summary>
    public string? Certificate { get; set; }

    /// <summary>
    /// Require the IdP to sign SAML assertions. Strongly recommended for production.
    /// </summary>
    public bool RequireSignedAssertions { get; set; } = true;
}

/// <summary>
/// LDAP / Active Directory authentication settings. Configuration foundation only — not yet implemented.
/// OIDC is the recommended SSO path. LDAP support requires the Novell.Directory.Ldap.NETStandard
/// NuGet package and is planned for a future release. These settings are present so that
/// configuration can be prepared ahead of the implementation.
/// </summary>
public sealed class LdapSettings
{
    /// <summary>
    /// LDAP server hostname or IP address.
    /// </summary>
    public required string Server { get; set; }

    /// <summary>
    /// LDAP server port. Defaults to 636 (LDAPS). Use 389 for non-TLS (not recommended).
    /// </summary>
    public int Port { get; set; } = 636;

    /// <summary>
    /// Use SSL/TLS for the LDAP connection. Strongly recommended for production.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Base Distinguished Name for user searches (e.g., <c>DC=example,DC=com</c>).
    /// </summary>
    public required string BaseDn { get; set; }

    /// <summary>
    /// Bind DN for authenticating to the LDAP server (service account).
    /// Use environment variables in production — never hardcode credentials.
    /// </summary>
    public string? BindDn { get; set; }

    /// <summary>
    /// Bind password for the service account.
    /// Use environment variables in production — never hardcode credentials.
    /// </summary>
    public string? BindPassword { get; set; }

    /// <summary>
    /// LDAP filter for user lookup. <c>{0}</c> is replaced with the username.
    /// Default uses sAMAccountName for Active Directory compatibility.
    /// </summary>
    public string UserSearchFilter { get; set; } = "(sAMAccountName={0})";

    /// <summary>
    /// Optional LDAP filter for group membership lookup.
    /// </summary>
    public string? GroupSearchFilter { get; set; }
}

/// <summary>
/// Maps an external identity claim (from OIDC JWT or future SAML assertion)
/// to a Cabinet <see cref="SystemRole"/> and optional per-index permissions.
/// </summary>
public sealed class ClaimRoleMapping
{
    /// <summary>
    /// The claim type to match (e.g., <c>groups</c>, <c>role</c>, <c>department</c>).
    /// </summary>
    public required string ClaimType { get; set; }

    /// <summary>
    /// The claim value to match (case-insensitive comparison).
    /// </summary>
    public required string ClaimValue { get; set; }

    /// <summary>
    /// Cabinet role assigned when this claim matches.
    /// </summary>
    public SystemRole Role { get; set; } = SystemRole.Viewer;

    /// <summary>
    /// Optional per-index permissions granted when this claim matches.
    /// </summary>
    public List<IndexPermission> IndexPermissions { get; set; } = new();
}
