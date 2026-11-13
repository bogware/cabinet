using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Claims;
using System.Text;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Cabinet.API.Authentication;

/// <summary>
/// The dashboard's signed-in session: a cookie that single sign-on (OIDC, SAML or LDAP) sets, so
/// people sign in with their organization's account rather than pasting an API key.
/// </summary>
/// <remarks>
/// A cookie is sent with any request to the site, including ones another site's page makes, so a
/// cookie request that changes something must carry <see cref="RequestHeader"/>. Another site's
/// page can't add a custom header to a request here without this site agreeing through CORS.
/// </remarks>
public static class BrowserSession
{
    /// <summary>The session's authentication scheme.</summary>
    public const string Scheme = "Session";

    /// <summary>The OIDC sign-in scheme.</summary>
    public const string OidcScheme = "oidc";

    /// <summary>The session cookie's name.</summary>
    public const string CookieName = "cabinet_session";

    /// <summary>The header the dashboard sends with every request it makes with the cookie.</summary>
    public const string RequestHeader = "X-Cabinet-Request";

    /// <summary>Adds the session cookie and, when configured, OIDC sign-in.</summary>
    public static AuthenticationBuilder AddCabinetBrowserSession(this AuthenticationBuilder builder, SsoConfiguration? sso)
    {
        builder.AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(sso?.SessionHours ?? 8, 1, 24 * 7));
            options.SlidingExpiration = true;
            var longest = TimeSpan.FromHours(Math.Clamp(sso?.MaxSessionHours ?? 24, 1, 24 * 30));

            // An API answers with a status, not a redirect to a login page
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!IsSafe(context.Request.Method) && !context.Request.Headers.ContainsKey(RequestHeader))
                {
                    context.RejectPrincipal();
                }
                else if (Expired(context.Properties, DateTimeOffset.UtcNow, longest))
                {
                    // Sliding renewal moves IssuedUtc, so the sign-in time travels separately
                    context.RejectPrincipal();
                    context.ShouldRenew = false;
                }
                else if (context.Principal != null && !await DirectoryClaims.SessionIsValidAsync(
                    context.Principal,
                    context.HttpContext.RequestServices.GetService<Services.Identity.UserDirectory>(),
                    context.HttpContext.RequestAborted))
                {
                    // Deactivated, deleted, or signed out everywhere since the session began
                    context.RejectPrincipal();
                    context.ShouldRenew = false;
                    await context.HttpContext.SignOutAsync(Scheme);
                }
            };
        });

        if (sso is { Enabled: true, Oidc: { } oidc } && !string.IsNullOrEmpty(oidc.ClientSecret))
        {
            builder.AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = Scheme;
                options.Authority = OidcProviders.Authority(oidc);
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.CallbackPath = oidc.CallbackPath;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !OidcProviders.Authority(oidc).StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase);
                options.Scope.Clear();
                foreach (var scope in oidc.Scopes)
                {
                    options.Scope.Add(scope);
                }

                // The correlation and nonce cookies cross from the identity provider back here
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.Events.OnTokenValidated = async context =>
                {
                    if (context.Principal?.Identity is ClaimsIdentity identity)
                    {
                        OidcProviders.Normalize(identity, oidc, context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Cabinet.Sso"));
                        context.HttpContext.RequestServices.GetRequiredService<ClaimRoleMapper>().MapClaims(identity, sso, "oidc");

                        // A user deactivated in the directory can't sign in, whatever the provider says
                        if (!await DirectoryClaims.LinkSsoAsync(identity, context.HttpContext.RequestServices.GetService<Services.Identity.UserDirectory>(), session: true, context.HttpContext.RequestAborted))
                        {
                            context.Fail("This account is deactivated in Cabinet");
                        }
                    }
                };
                options.Events.OnRemoteFailure = context =>
                {
                    // Back to the dashboard, which offers sign-in again, rather than an error page
                    context.Response.Redirect("/?signin=failed");
                    context.HandleResponse();
                    return Task.CompletedTask;
                };
                options.Events.OnTicketReceived = context =>
                {
                    if (context.Properties != null)
                    {
                        context.Properties.Items[SignedInItem] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    }

                    return Task.CompletedTask;
                };
            });
        }

        return builder;
    }

    /// <summary>Whether a request carries the session cookie and nothing else to identify it.</summary>
    public static bool UsesSession(HttpRequest request) =>
        request.Cookies.ContainsKey(CookieName)
        && !request.Headers.ContainsKey("X-API-Key")
        && request.Headers.Authorization.Count == 0;

    /// <summary>The item that holds when someone signed in, which renewal doesn't move.</summary>
    public const string SignedInItem = "cabinet.signedIn";

    /// <summary>Whether a session is past its absolute limit, or doesn't say when it began.</summary>
    public static bool Expired(AuthenticationProperties properties, DateTimeOffset now, TimeSpan longest) =>
        !properties.Items.TryGetValue(SignedInItem, out var text)
        || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var signedIn)
        || now - signedIn > longest;

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>Signs someone in, once a provider has said who they are.</summary>
    public static async Task SignInAsync(HttpContext context, ClaimsIdentity identity)
    {
        var session = new ClaimsIdentity(identity.Claims, Scheme, ClaimTypes.Name, PrincipalResolver.RoleClaim);
        var properties = new AuthenticationProperties { IsPersistent = true };
        properties.Items[SignedInItem] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await context.SignInAsync(Scheme, new ClaimsPrincipal(session), properties);
    }
}

/// <summary>
/// Checks a user name and password against an LDAP directory, such as Active Directory: finds the
/// user with the service account, then binds as them to check the password.
/// </summary>
public sealed class LdapAuthenticator
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly LdapSettings _settings;
    private readonly ILogger<LdapAuthenticator> _logger;

    public LdapAuthenticator(LdapSettings settings, ILogger<LdapAuthenticator> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The user's identity with their groups, or null when the name or password is wrong.</summary>
    public Task<ClaimsIdentity?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // An empty password is an anonymous bind, which many directories accept for anyone
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || username.Length > 256 || password.Length > 1024)
        {
            return Task.FromResult<ClaimsIdentity?>(null);
        }

        return Task.Run(() => Authenticate(username.Trim(), password), cancellationToken);
    }

    private ClaimsIdentity? Authenticate(string username, string password)
    {
        try
        {
            using var directory = Connect();
            if (!string.IsNullOrEmpty(_settings.BindDn))
            {
                directory.Bind(new NetworkCredential(_settings.BindDn, _settings.BindPassword));
            }
            else
            {
                directory.AuthType = AuthType.Anonymous;
                directory.Bind();
            }

            var filter = string.Format(System.Globalization.CultureInfo.InvariantCulture, _settings.UserSearchFilter, EscapeFilter(username));
            var request = new SearchRequest(_settings.BaseDn, filter, SearchScope.Subtree, "memberOf", "cn", "mail", "displayName", "uid", "sAMAccountName");
            var response = (SearchResponse)directory.SendRequest(request, Timeout);
            if (response.Entries.Count != 1)
            {
                _logger.LogInformation("LDAP sign-in for {User} found {Count} entries", AuditName.Sanitize(username), response.Entries.Count);
                return null;
            }

            var entry = response.Entries[0];
            using (var asUser = Connect())
            {
                asUser.Bind(new NetworkCredential(entry.DistinguishedName, password));
            }

            var identity = new ClaimsIdentity("ldap");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, username.ToLowerInvariant()));
            identity.AddClaim(new Claim(ClaimTypes.Name, First(entry, "displayName") ?? First(entry, "cn") ?? username));
            if (First(entry, "mail") is { } mail)
            {
                identity.AddClaim(new Claim(ClaimTypes.Email, mail));
            }

            foreach (var group in Values(entry, "memberOf"))
            {
                // Role mappings can name the group's full DN or just its CN
                identity.AddClaim(new Claim("groups", group));
                if (CommonName(group) is { } cn)
                {
                    identity.AddClaim(new Claim("groups", cn));
                }
            }

            return identity;
        }
        catch (LdapException ex) when (ex.ErrorCode == 49)
        {
            // Invalid credentials
            _logger.LogInformation("LDAP sign-in for {User} failed: wrong password", AuditName.Sanitize(username));
            return null;
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            _logger.LogWarning(ex, "LDAP sign-in for {User} couldn't reach or query the directory", AuditName.Sanitize(username));
            return null;
        }
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_settings.Server, _settings.Port))
        {
            AuthType = AuthType.Basic,
            Timeout = Timeout,
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = _settings.UseSsl;
        return connection;
    }

    private static string? First(SearchResultEntry entry, string attribute) => Values(entry, attribute).FirstOrDefault();

    private static IEnumerable<string> Values(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute)
            ? entry.Attributes[attribute].GetValues(typeof(string)).OfType<string>()
            : [];

    private static string? CommonName(string dn) =>
        dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? dn[3..].Split(',')[0] : null;

    /// <summary>
    /// Escapes a value for an LDAP search filter (RFC 4515), so a name like <c>*)(uid=*</c>
    /// can't change what the filter finds.
    /// </summary>
    public static string EscapeFilter(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
