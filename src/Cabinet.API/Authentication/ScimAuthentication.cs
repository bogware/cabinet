using System.Security.Claims;
using System.Text.Encodings.Web;
using Cabinet.API.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Cabinet.API.Authentication;

/// <summary>
/// Authenticates SCIM calls by their bearer token. Used only on /scim, where it's the only scheme;
/// the identity it makes has no role, so it can't pass any capability check elsewhere.
/// </summary>
public sealed class ScimAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme's name.</summary>
    public const string SchemeName = "Scim";

    /// <summary>The path SCIM is served under.</summary>
    public const string PathPrefix = "/scim";

    /// <summary>The claim naming the token used.</summary>
    public const string TokenClaim = "cabinet.scimToken";

    private readonly ScimTokenService _tokens;

    public ScimAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ScimTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = await _tokens.ValidateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (token == null)
        {
            return AuthenticateResult.Fail("Invalid SCIM token");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim("ClientId", $"scim-{token.Id}"),
            new Claim(ClaimTypes.Name, token.Name),
            new Claim(TokenClaim, token.Id),

            // For the rate limiter: a sync sends bursts
            new Claim("Tier", "Professional"),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Bearer realm=\"Cabinet SCIM\"");
        Response.ContentType = "application/scim+json";
        await Response.WriteAsync("{\"schemas\":[\"urn:ietf:params:scim:api:messages:2.0:Error\"],\"status\":\"401\",\"detail\":\"A SCIM token is required\"}");
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/scim+json";
        await Response.WriteAsync("{\"schemas\":[\"urn:ietf:params:scim:api:messages:2.0:Error\"],\"status\":\"403\",\"detail\":\"Only a SCIM token can call SCIM\"}");
    }
}

/// <summary>
/// For SCIM endpoints: the caller must have authenticated with a SCIM token. Nothing else, API keys
/// and sessions included, is accepted.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireScimTokenAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var identity = context.HttpContext.User.Identity;
        if (identity is not { IsAuthenticated: true, AuthenticationType: ScimAuthenticationHandler.SchemeName }
            || context.HttpContext.User.FindFirst(ScimAuthenticationHandler.TokenClaim) == null)
        {
            context.Result = new ContentResult
            {
                StatusCode = StatusCodes.Status401Unauthorized,
                ContentType = "application/scim+json",
                Content = "{\"schemas\":[\"urn:ietf:params:scim:api:messages:2.0:Error\"],\"status\":\"401\",\"detail\":\"A SCIM token is required\"}",
            };
        }
    }
}
