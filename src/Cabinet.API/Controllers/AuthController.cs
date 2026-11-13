using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Signing in to the dashboard with the organization's accounts: OIDC, SAML or LDAP. Each ends
/// in the same session cookie. API clients keep using API keys or bearer tokens.
/// </summary>
[ApiController]
[Route("auth")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AuthController : ControllerBase
{
    private readonly SsoConfiguration? _sso;
    private readonly ClaimRoleMapper? _mapper;
    private readonly LdapAuthenticator? _ldap;
    private readonly SamlSignIn? _saml;
    private readonly IAuditLogger _audit;
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly ILogger<AuthController> _logger;
    private readonly UserDirectory? _directory;
    private readonly SignInThrottle? _throttle;

    public AuthController(
        IAuditLogger audit,
        IAuthenticationSchemeProvider schemes,
        ILogger<AuthController> logger,
        SsoConfiguration? sso = null,
        ClaimRoleMapper? mapper = null,
        LdapAuthenticator? ldap = null,
        SamlSignIn? saml = null,
        UserDirectory? directory = null,
        SignInThrottle? throttle = null)
    {
        _audit = audit;
        _schemes = schemes;
        _logger = logger;
        _sso = sso;
        _mapper = mapper;
        _ldap = ldap;
        _saml = saml;
        _directory = directory;
        _throttle = throttle;
    }

    /// <summary>Which ways of signing in are on, and who's signed in.</summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> Providers()
    {
        var session = await HttpContext.AuthenticateAsync(BrowserSession.Scheme);
        return Ok(new
        {
            oidc = await _schemes.GetSchemeAsync(BrowserSession.OidcScheme) != null,
            saml = _saml != null,
            ldap = _ldap != null,
            local = _directory != null && _directory.Options.LocalSignIn && await _directory.HasLocalUsersAsync(HttpContext.RequestAborted),
            localAccount = session.Principal?.FindFirst("ClientId")?.Value?.StartsWith(DirectoryClaims.LocalPrefix, StringComparison.Ordinal) == true,
            signedIn = session.Succeeded,
            name = session.Principal?.FindFirst(ClaimTypes.Name)?.Value ?? session.Principal?.FindFirst("name")?.Value
                ?? session.Principal?.FindFirst("preferred_username")?.Value ?? session.Principal?.FindFirst("ClientId")?.Value,
            role = session.Principal?.FindFirst(PrincipalResolver.RoleClaim)?.Value,
        });
    }

    /// <summary>Starts OIDC sign-in, redirecting to the identity provider.</summary>
    [HttpGet("login/oidc")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginOidc([FromQuery] string? returnUrl = null)
    {
        if (await _schemes.GetSchemeAsync(BrowserSession.OidcScheme) == null)
        {
            return NotFound();
        }

        return Challenge(new AuthenticationProperties { RedirectUri = LocalOnly(returnUrl) }, BrowserSession.OidcScheme);
    }

    /// <summary>Signs in with a directory user name and password.</summary>
    [HttpPost("login/ldap")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginLdap([FromBody] LdapLoginRequest request, CancellationToken cancellationToken)
    {
        if (_ldap == null || _sso == null || _mapper == null)
        {
            return NotFound();
        }

        // The same slowing as local accounts get, since the directory may not lock accounts itself
        var userName = request.Username ?? string.Empty;
        var address = HttpContext.Connection.RemoteIpAddress?.ToString();
        var attempted = AuditName.Sanitize(userName);
        if (_throttle?.RetryAfter($"ldap:{userName}", address) is { } wait && wait > TimeSpan.Zero)
        {
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await AuditAsync($"ldap-{attempted}", "LDAP sign-in slowed after repeated failures", false);
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Too many wrong tries. Wait a moment and try again.", retryAfter = Math.Ceiling(wait.TotalSeconds) });
        }

        var identity = await _ldap.AuthenticateAsync(userName, request.Password ?? string.Empty, cancellationToken);
        if (identity == null)
        {
            _throttle?.Failed($"ldap:{userName}", address);
            await AuditAsync($"ldap-{attempted}", "LDAP sign-in failed", false);
            return Unauthorized(new { message = "Wrong user name or password" });
        }

        _throttle?.Succeeded($"ldap:{userName}");
        return await CompleteAsync(identity, "ldap");
    }

    /// <summary>Starts SAML sign-in, redirecting to the identity provider.</summary>
    [HttpGet("login/saml")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginSaml([FromQuery] string? returnUrl, CancellationToken cancellationToken)
    {
        if (_saml == null)
        {
            return NotFound();
        }

        var configuration = await _saml.ConfigurationAsync(cancellationToken);
        var binding = new Saml2RedirectBinding();
        binding.SetRelayStateQuery(new Dictionary<string, string> { ["ReturnUrl"] = LocalOnly(returnUrl) });
        return binding.Bind(new Saml2AuthnRequest(configuration)
        {
            AssertionConsumerServiceUrl = new Uri($"{Request.Scheme}://{Request.Host}{Request.PathBase}/auth/saml/acs"),
        }).ToActionResult();
    }

    /// <summary>Where the SAML identity provider posts its signed response.</summary>
    [HttpPost("saml/acs")]
    [AllowAnonymous]
    public async Task<IActionResult> SamlAssertion(CancellationToken cancellationToken)
    {
        if (_saml == null || _sso == null || _mapper == null)
        {
            return NotFound();
        }

        var configuration = await _saml.ConfigurationAsync(cancellationToken);
        var binding = new Saml2PostBinding();
        var response = new Saml2AuthnResponse(configuration);
        try
        {
            binding.ReadSamlResponse(Request.ToGenericHttpRequest(validate: true), response);
            if (response.Status != ITfoxtec.Identity.Saml2.Schemas.Saml2StatusCodes.Success)
            {
                await AuditAsync("saml-unknown", $"SAML sign-in refused by the identity provider: {response.Status}", false);
                return Unauthorized(new { message = "The identity provider didn't sign you in" });
            }

            binding.Unbind(Request.ToGenericHttpRequest(validate: true), response);
        }
        catch (Exception ex) when (ex is Saml2RequestException or System.Security.Cryptography.CryptographicException or InvalidOperationException or System.Xml.XmlException)
        {
            _logger.LogWarning(ex, "Rejected a SAML response");
            await AuditAsync("saml-unknown", "SAML sign-in failed: the response didn't validate", false);
            return Unauthorized(new { message = "The sign-in response didn't validate" });
        }

        var result = await CompleteAsync(SamlSignIn.IdentityFrom(response), "saml");
        var relay = binding.GetRelayStateQuery();
        return result is OkObjectResult ? Redirect(LocalOnly(relay.TryGetValue("ReturnUrl", out var returnUrl) ? returnUrl : null)) : result;
    }

    /// <summary>Ends the session.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(BrowserSession.Scheme);
        return NoContent();
    }

    private async Task<IActionResult> CompleteAsync(ClaimsIdentity identity, string provider)
    {
        _mapper!.MapClaims(identity, _sso!, provider);
        var who = AuditName.Sanitize(identity.FindFirst("ClientId")?.Value ?? $"{provider}-unknown");

        // A user deactivated in the directory can't sign in, whatever the provider says
        if (!await DirectoryClaims.LinkSsoAsync(identity, _directory, session: true, HttpContext.RequestAborted))
        {
            await AuditAsync(who, $"{provider.ToUpperInvariant()} sign-in refused: the user is deactivated in Cabinet", false);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Your account is deactivated in Cabinet. Ask an administrator." });
        }

        var directoryGrant = identity.FindFirst(DirectoryClaims.UserId)?.Value is { } userId && _directory != null
            && await _directory.GetAsync(userId, HttpContext.RequestAborted) is { } directoryUser
            ? await _directory.GrantForAsync(directoryUser, HttpContext.RequestAborted)
            : null;
        if (identity.FindFirst(PrincipalResolver.RoleClaim) == null && directoryGrant == null)
        {
            // Signed in at the provider, but no mapping gives them a role here
            await AuditAsync(who, $"{provider.ToUpperInvariant()} sign-in has no role in Cabinet", false);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "You're signed in, but your account has no access to Cabinet. Ask an administrator to map one of your groups to a role." });
        }

        await BrowserSession.SignInAsync(HttpContext, identity);
        await AuditAsync(who, $"Signed in with {provider.ToUpperInvariant()}", true);
        return Ok(new { name = identity.FindFirst(ClaimTypes.Name)?.Value ?? who });
    }

    private Task AuditAsync(string principal, string description, bool success) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = principal,
            Action = success ? AuditAction.AuthSuccess : AuditAction.AuthFailure,
            ResourceType = "Session",
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });

    /// <summary>Only a path on this site, so sign-in can't be used to send someone elsewhere.</summary>
    /// Browsers drop tabs and line breaks from a Location, so "/\t/evil.example" would become
    /// "//evil.example": any control character is refused.
    internal static string LocalOnly(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
        && !url.Any(char.IsControl) ? url : "/";
}

/// <summary>An LDAP sign-in.</summary>
public sealed class LdapLoginRequest
{
    /// <summary>The directory user name.</summary>
    public string? Username { get; set; }

    /// <summary>The password, checked by binding to the directory as the user.</summary>
    public string? Password { get; set; }
}
