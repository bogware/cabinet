using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class SessionRevocationTests
{
    private readonly InMemoryDocumentStore _store = new();
    private readonly TestClock _clock = new();
    private readonly UserDirectory _directory;
    private readonly LocalAccountService _accounts;

    public SessionRevocationTests()
    {
        _directory = new UserDirectory(_store, new AccountOptions(), NullLogger<UserDirectory>.Instance, _clock);
        _accounts = new LocalAccountService(_directory, _store, new SecretProtector(new EphemeralDataProtectionProvider()), new SignInThrottle(_clock), NullLogger<LocalAccountService>.Instance, _clock);
    }

    private async Task<(DirectoryUser User, ClaimsPrincipal Session)> SignedInAsync()
    {
        var user = await _directory.CreateAsync(new DirectoryUser { Id = string.Empty, UserName = "alice", Role = SystemRole.Analyst, PasswordHash = PasswordHasher.Hash("tangerine-orbit-quietly-42") });
        var grant = await _directory.GrantForAsync(user);
        return (user, new ClaimsPrincipal(DirectoryClaims.ForLocalUser(user, grant!)));
    }

    [Fact]
    public async Task ASession_IsValid_UntilSomethingEndsIt()
    {
        var (_, session) = await SignedInAsync();

        (await DirectoryClaims.SessionIsValidAsync(session, _directory)).Should().BeTrue();
    }

    [Fact]
    public async Task DisablingTheUser_EndsTheirSessions()
    {
        var (user, session) = await SignedInAsync();

        await _directory.UpdateAsync(user.Id, u => u.Active = false);

        (await DirectoryClaims.SessionIsValidAsync(session, _directory)).Should().BeFalse();
    }

    [Fact]
    public async Task ResettingThePassword_EndsTheirSessions()
    {
        var (user, session) = await SignedInAsync();

        await _accounts.ResetPasswordAsync(user.Id);

        (await DirectoryClaims.SessionIsValidAsync(session, _directory)).Should().BeFalse();
    }

    [Fact]
    public async Task SigningOutEverywhere_EndsTheirSessions()
    {
        var (user, session) = await SignedInAsync();

        await _accounts.SignOutEverywhereAsync(user.Id);

        (await DirectoryClaims.SessionIsValidAsync(session, _directory)).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingTheUser_EndsTheirSessions()
    {
        var (user, session) = await SignedInAsync();

        await _directory.DeleteAsync(user.Id);

        (await DirectoryClaims.SessionIsValidAsync(session, _directory)).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherInstance_EndsTheSession_WithinTheCacheTime()
    {
        var (user, session) = await SignedInAsync();
        var other = new UserDirectory(_store, new AccountOptions { DirectoryCacheSeconds = 5 }, NullLogger<UserDirectory>.Instance, _clock);
        (await DirectoryClaims.SessionIsValidAsync(session, other)).Should().BeTrue();

        await _accounts.SignOutEverywhereAsync(user.Id);
        _clock.Advance(TimeSpan.FromSeconds(6));

        (await DirectoryClaims.SessionIsValidAsync(session, other)).Should().BeFalse();
    }

    [Fact]
    public async Task ADirectorySession_WithoutItsVersion_OrWithoutADirectory_IsRefused()
    {
        var (user, _) = await SignedInAsync();
        var noVersion = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DirectoryClaims.UserId, user.Id)], "Session"));

        (await DirectoryClaims.SessionIsValidAsync(noVersion, _directory)).Should().BeFalse();
        (await DirectoryClaims.SessionIsValidAsync(noVersion, null)).Should().BeFalse();
        (await DirectoryClaims.SessionIsValidAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("ClientId", "oidc-x")], "Session")), null))
            .Should().BeTrue("a session that isn't a directory user's has nothing to check");
    }

    [Fact]
    public async Task ALocalUser_GetsWhatTheDirectoryGivesNow_NotWhatTheirSessionSaid()
    {
        var (user, session) = await SignedInAsync();
        var rbac = Mock.Of<IRbacService>();

        (await PrincipalResolver.ResolveAsync(session, rbac, default, _directory))!.Role.Should().Be(SystemRole.Analyst);

        await _directory.UpdateAsync(user.Id, u => u.Role = SystemRole.Viewer);
        (await PrincipalResolver.ResolveAsync(session, rbac, default, _directory))!.Role.Should().Be(SystemRole.Viewer);

        await _directory.UpdateAsync(user.Id, u => u.Role = null);
        (await PrincipalResolver.ResolveAsync(session, rbac, default, _directory)).Should().BeNull("the session's role claim is never used for a local account");

        await _directory.UpdateAsync(user.Id, u =>
        {
            u.Role = SystemRole.Admin;
            u.Active = false;
        });
        (await PrincipalResolver.ResolveAsync(session, rbac, default, _directory)).Should().BeNull();
    }

    [Fact]
    public async Task AStoredPrincipal_StillWins()
    {
        var (user, session) = await SignedInAsync();
        var rbac = new Mock<IRbacService>();
        rbac.Setup(service => service.GetPrincipalAsync($"local-{user.Id}", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RbacPrincipal { Id = $"local-{user.Id}", Role = SystemRole.SuperAdmin });

        (await PrincipalResolver.ResolveAsync(session, rbac.Object, default, _directory))!.Role.Should().Be(SystemRole.SuperAdmin);
    }
}
