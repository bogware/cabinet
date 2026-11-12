using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class BrowserSessionTests
{
    private static async Task<bool> AcceptedAsync(string method, bool withHeader)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddCabinetBrowserSession(null);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(BrowserSession.Scheme);

        var http = new DefaultHttpContext { RequestServices = provider };
        http.Request.Method = method;
        if (withHeader)
        {
            http.Request.Headers[BrowserSession.RequestHeader] = "1";
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "oidc-alice")], BrowserSession.Scheme));
        var context = new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(BrowserSession.Scheme, null, typeof(CookieAuthenticationHandler)),
            options,
            new AuthenticationTicket(principal, BrowserSession.Scheme));
        await options.Events.ValidatePrincipal(context);
        return context.Principal != null;
    }

    [Theory]
    [InlineData("GET", false, true)]
    [InlineData("POST", true, true)]
    [InlineData("POST", false, false)]
    [InlineData("DELETE", false, false)]
    [InlineData("PUT", true, true)]
    public async Task ACookieRequestThatChangesSomething_NeedsThePagesHeader(string method, bool withHeader, bool accepted)
    {
        // Another site's page can make the browser send the cookie, but can't add the header
        (await AcceptedAsync(method, withHeader)).Should().Be(accepted);
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("*)(uid=*", @"\2a\29\28uid=\2a")]
    [InlineData(@"a\b", @"a\5cb")]
    public void LdapFilterValues_AreEscaped(string input, string expected) =>
        LdapAuthenticator.EscapeFilter(input).Should().Be(expected);

    [Theory]
    [InlineData("alice", "")]
    [InlineData("", "secret")]
    [InlineData("   ", "secret")]
    public async Task Ldap_RefusesAnEmptyNameOrPassword_WithoutAskingTheDirectory(string user, string password)
    {
        // An empty password is an anonymous bind, which directories often accept for anyone
        var ldap = new LdapAuthenticator(new LdapSettings { Server = "ldap.invalid", BaseDn = "dc=example,dc=com" }, NullLogger<LdapAuthenticator>.Instance);

        (await ldap.AuthenticateAsync(user, password)).Should().BeNull();
    }

    [Fact]
    public void Session_IsOnlyUsed_WhenNothingElseIdentifiesTheRequest()
    {
        var withCookie = new DefaultHttpContext();
        withCookie.Request.Headers.Cookie = $"{BrowserSession.CookieName}=x";
        BrowserSession.UsesSession(withCookie.Request).Should().BeTrue();

        withCookie.Request.Headers["X-API-Key"] = "k";
        BrowserSession.UsesSession(withCookie.Request).Should().BeFalse();
    }
}
