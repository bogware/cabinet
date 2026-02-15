namespace Cabinet.Core.Models;

public sealed class SsoConfiguration
{
    public bool Enabled { get; set; }

    public SsoProviderType Provider { get; set; } = SsoProviderType.Oidc;

    public OidcSettings? Oidc { get; set; }

    public SamlSettings? Saml { get; set; }

    public LdapSettings? Ldap { get; set; }

    public List<ClaimRoleMapping> RoleMappings { get; set; } = new();

    public bool AutoProvision { get; set; } = true;

    public SystemRole DefaultRole { get; set; } = SystemRole.Viewer;
}

public enum SsoProviderType
{
    Oidc,
    Saml,
    Ldap
}

public sealed class OidcSettings
{
    public required string Authority { get; set; }

    public required string ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string[] Scopes { get; set; } = new[] { "openid", "profile", "email" };

    public string CallbackPath { get; set; } = "/auth/callback";

    public string GroupsClaim { get; set; } = "groups";
}

public sealed class SamlSettings
{
    public required string EntityId { get; set; }

    public required string MetadataUrl { get; set; }

    public string? AssertionConsumerServiceUrl { get; set; }

    public string? Certificate { get; set; }

    public bool RequireSignedAssertions { get; set; } = true;
}

public sealed class LdapSettings
{
    public required string Server { get; set; }

    public int Port { get; set; } = 636;

    public bool UseSsl { get; set; } = true;

    public required string BaseDn { get; set; }

    public string? BindDn { get; set; }

    public string? BindPassword { get; set; }

    public string UserSearchFilter { get; set; } = "(sAMAccountName={0})";

    public string? GroupSearchFilter { get; set; }
}

public sealed class ClaimRoleMapping
{
    public required string ClaimType { get; set; }

    public required string ClaimValue { get; set; }

    public SystemRole Role { get; set; } = SystemRole.Viewer;

    public List<IndexPermission> IndexPermissions { get; set; } = new();
}
