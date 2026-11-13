using System.Security.Claims;
using System.Security.Cryptography;
using Cabinet.API.Authentication;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Tokens shaped exactly as Entra ID and Cognito issue them, validated with the settings the
/// server uses. The signing key stands in for the one discovered from the provider's metadata.
/// </summary>
public sealed class OidcProvidersTests
{
    private const string Tenant = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string OtherTenant = "11111111-2222-3333-4444-555555555555";
    private const string EntraApp = "3f1d6a2e-7c49-4b8e-9a0f-2d5c8e1b4a77";
    private const string Pool = "us-east-1_AbCdEf123";
    private const string CognitoApp = "4h2k8m1n0p9q7r6s5t3u2v1w0x";

    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "test" };

    private static string Token(Dictionary<string, object> claims, RsaSecurityKey? key = null) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Claims = claims,
        Expires = DateTime.UtcNow.AddMinutes(30),
        NotBefore = DateTime.UtcNow.AddMinutes(-1),
        SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
    });

    /// <summary>Validates as the bearer handler would, with the metadata's issuer and key.</summary>
    private static async Task<TokenValidationResult> ValidateAsync(OidcSettings oidc, string token, string metadataIssuer)
    {
        var options = new JwtBearerOptions();
        OidcProviders.ConfigureBearer(options, oidc);
        var parameters = options.TokenValidationParameters.Clone();
        parameters.ValidIssuer = metadataIssuer;
        parameters.IssuerSigningKey = Key;
        return await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
    }

    private static OidcSettings Entra() => new() { Provider = OidcProvider.Entra, TenantId = Tenant, ClientId = EntraApp };

    private static OidcSettings Cognito() => new() { Provider = OidcProvider.Cognito, Region = "us-east-1", UserPoolId = Pool, ClientId = CognitoApp };

    private static string EntraV2Issuer => $"https://login.microsoftonline.com/{Tenant}/v2.0";

    [Theory]
    [InlineData("https://login.microsoftonline.com/{0}/v2.0", "{1}")]
    [InlineData("https://sts.windows.net/{0}/", "api://{1}")]
    public async Task Entra_AcceptsV1AndV2Tokens_FromItsTenant(string issuer, string audience)
    {
        var token = Token(new()
        {
            ["iss"] = string.Format(issuer, Tenant, EntraApp),
            ["aud"] = string.Format(audience, Tenant, EntraApp),
            ["tid"] = Tenant,
            ["oid"] = "c0ffee00-1111-2222-3333-444455556666",
            ["sub"] = "pairwise-subject-for-this-app",
        });

        var result = await ValidateAsync(Entra(), token, EntraV2Issuer);

        result.IsValid.Should().BeTrue(result.Exception?.Message);
    }

    [Theory]
    [InlineData(OtherTenant, OtherTenant)]
    [InlineData(OtherTenant, Tenant)]
    [InlineData(Tenant, OtherTenant)]
    public async Task Entra_RefusesTokens_FromAnotherTenant(string issuerTenant, string tid)
    {
        // A token another organization's Entra issued for an app with the same client ID
        var token = Token(new() { ["iss"] = $"https://login.microsoftonline.com/{issuerTenant}/v2.0", ["aud"] = EntraApp, ["tid"] = tid });

        var result = await ValidateAsync(Entra(), token, EntraV2Issuer);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Entra_ConfiguredByDomain_LearnsTheTenantFromItsMetadata()
    {
        var settings = Entra();
        settings.TenantId = "contoso.onmicrosoft.com";
        var mine = Token(new() { ["iss"] = EntraV2Issuer, ["aud"] = EntraApp, ["tid"] = Tenant });
        var theirs = Token(new() { ["iss"] = $"https://login.microsoftonline.com/{OtherTenant}/v2.0", ["aud"] = EntraApp, ["tid"] = OtherTenant });

        (await ValidateAsync(settings, mine, EntraV2Issuer)).IsValid.Should().BeTrue();
        (await ValidateAsync(settings, theirs, EntraV2Issuer)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Entra_RefusesATokenForAnotherApplication()
    {
        var token = Token(new() { ["iss"] = EntraV2Issuer, ["aud"] = "00000003-0000-0000-c000-000000000000", ["tid"] = Tenant });

        (await ValidateAsync(Entra(), token, EntraV2Issuer)).IsValid.Should().BeFalse("a Microsoft Graph token isn't one for Cabinet");
    }

    [Fact]
    public async Task Entra_RefusesATokenSignedWithAnotherKey()
    {
        var token = Token(new() { ["iss"] = EntraV2Issuer, ["aud"] = EntraApp, ["tid"] = Tenant }, new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test" });

        (await ValidateAsync(Entra(), token, EntraV2Issuer)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Cognito_AcceptsAccessTokens_ByTheirClientId_AndIdTokens_ByAudience()
    {
        var issuer = $"https://cognito-idp.us-east-1.amazonaws.com/{Pool}";
        var access = Token(new() { ["iss"] = issuer, ["token_use"] = "access", ["client_id"] = CognitoApp, ["sub"] = "u1" });
        var id = Token(new() { ["iss"] = issuer, ["token_use"] = "id", ["aud"] = CognitoApp, ["sub"] = "u1" });

        (await ValidateAsync(Cognito(), access, issuer)).IsValid.Should().BeTrue();
        (await ValidateAsync(Cognito(), id, issuer)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("access", "another-app-client")]
    [InlineData("id", CognitoApp)]
    [InlineData(null, CognitoApp)]
    public async Task Cognito_RefusesTokensNotForThisApp(string? use, string client)
    {
        // An access token for another app client, or a client_id without token_use=access and no audience
        var issuer = $"https://cognito-idp.us-east-1.amazonaws.com/{Pool}";
        var claims = new Dictionary<string, object> { ["iss"] = issuer, ["client_id"] = client, ["sub"] = "u1" };
        if (use != null)
        {
            claims["token_use"] = use;
        }

        (await ValidateAsync(Cognito(), Token(claims), issuer)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Cognito_AcceptsOtherAppClients_OnlyWhenListed()
    {
        var issuer = $"https://cognito-idp.us-east-1.amazonaws.com/{Pool}";
        var token = Token(new() { ["iss"] = issuer, ["token_use"] = "access", ["client_id"] = "cli-client", ["sub"] = "u1" });
        var settings = Cognito();
        settings.ClientIds = ["cli-client"];

        (await ValidateAsync(settings, token, issuer)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Cognito_GroupsMapToRoles_ThroughTheGroupsClaim()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "u1"), new Claim("cognito:groups", "cabinet-admins"), new Claim("cognito:groups", "everyone"), new Claim("cognito:username", "alice")], "Bearer");
        var sso = new SsoConfiguration { Enabled = true, Oidc = Cognito(), RoleMappings = [new ClaimRoleMapping { ClaimType = "groups", ClaimValue = "cabinet-admins", Role = SystemRole.Admin }] };

        OidcProviders.Normalize(identity, sso.Oidc);
        new ClaimRoleMapper(NullLogger<ClaimRoleMapper>.Instance).MapClaims(identity, sso);

        identity.FindFirst(PrincipalResolver.RoleClaim)!.Value.Should().Be("Admin");
        identity.FindFirst("preferred_username")!.Value.Should().Be("alice");
    }

    [Fact]
    public void Groups_GivenAsAJsonArray_AreSplit()
    {
        var identity = new ClaimsIdentity([new Claim("cognito:groups", "[\"a\",\"b\"]")], "Bearer");

        OidcProviders.Normalize(identity, Cognito());

        identity.FindAll("groups").Select(c => c.Value).Should().Equal("a", "b");
    }

    [Fact]
    public void Entra_UsersAreKnownByTheirObjectId_NotThePerAppSubject()
    {
        // SCIM provisioning and every other app see the oid; sub differs per application
        var identity = new ClaimsIdentity([new Claim("sub", "pairwise"), new Claim("oid", "c0ffee00-1111-2222-3333-444455556666"), new Claim("roles", "Cabinet.Admin")], "Bearer");
        var sso = new SsoConfiguration { Enabled = true, Oidc = Entra(), RoleMappings = [new ClaimRoleMapping { ClaimType = "roles", ClaimValue = "Cabinet.Admin", Role = SystemRole.Admin }] };

        OidcProviders.Normalize(identity, sso.Oidc);
        new ClaimRoleMapper(NullLogger<ClaimRoleMapper>.Instance).MapClaims(identity, sso);

        identity.FindFirst("ClientId")!.Value.Should().Be("oidc-c0ffee00-1111-2222-3333-444455556666");
        identity.FindFirst(PrincipalResolver.RoleClaim)!.Value.Should().Be("Admin");
    }

    [Theory]
    [InlineData(OidcProvider.Entra, null, null, null, "TenantId")]
    [InlineData(OidcProvider.Entra, "common", null, null, "any Microsoft account")]
    [InlineData(OidcProvider.Entra, "organizations", null, null, "any Microsoft account")]
    [InlineData(OidcProvider.Cognito, null, "us-east-1", null, "UserPoolId")]
    [InlineData(OidcProvider.Generic, null, null, null, "Authority")]
    public void Settings_ThatCantWork_StopTheServerWithAReason(OidcProvider provider, string? tenant, string? region, string? pool, string expected)
    {
        var problem = OidcProviders.Problem(new OidcSettings { Provider = provider, TenantId = tenant, Region = region, UserPoolId = pool, ClientId = "app" });

        problem.Should().Contain(expected);
    }

    [Fact]
    public void Authorities_AreDerived_FromTheTenantOrPool()
    {
        OidcProviders.Authority(Entra()).Should().Be(EntraV2Issuer);
        OidcProviders.Authority(Cognito()).Should().Be($"https://cognito-idp.us-east-1.amazonaws.com/{Pool}");
        OidcProviders.Problem(Entra()).Should().BeNull();
        OidcProviders.Problem(Cognito()).Should().BeNull();
    }
}
