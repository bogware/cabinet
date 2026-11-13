using System.Security.Claims;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>A clock tests can move.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

public sealed class UserDirectoryTests
{
    private readonly InMemoryDocumentStore _store = new();

    private UserDirectory Directory(int cacheSeconds = 5, TimeProvider? time = null) =>
        new(_store, new AccountOptions { DirectoryCacheSeconds = cacheSeconds }, NullLogger<UserDirectory>.Instance, time);

    private static DirectoryUser User(string name, string? externalId = null) =>
        new() { Id = string.Empty, UserName = name, ExternalId = externalId };

    [Fact]
    public async Task UserNames_AreUnique_WithoutRegardToCase()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(User("Alice"));

        alice.Id.Should().HaveLength(26, "IDs are ULIDs");
        var duplicate = () => directory.CreateAsync(User("ALICE"));
        await duplicate.Should().ThrowAsync<DirectoryConflictException>();
        (await directory.FindByUserNameAsync("aLiCe"))!.Id.Should().Be(alice.Id);
    }

    [Fact]
    public async Task Renaming_FreesTheOldName_AndTakesTheNew()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(User("alice"));
        await directory.CreateAsync(User("bob"));

        var clash = () => directory.UpdateAsync(alice.Id, user => user.UserName = "BOB");
        await clash.Should().ThrowAsync<DirectoryConflictException>();

        await directory.UpdateAsync(alice.Id, user => user.UserName = "alice.smith");
        (await directory.FindByUserNameAsync("alice")).Should().BeNull();
        (await directory.FindByUserNameAsync("Alice.Smith"))!.Id.Should().Be(alice.Id);
        (await directory.CreateAsync(User("alice"))).Id.Should().NotBe(alice.Id, "the old name is free again");
    }

    [Fact]
    public async Task ExternalIds_AreUnique_AndFindTheirUser()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(User("alice", "00u-alice"));

        var duplicate = () => directory.CreateAsync(User("other", "00u-alice"));
        await duplicate.Should().ThrowAsync<DirectoryConflictException>();
        (await directory.FindByExternalIdAsync("00u-alice"))!.Id.Should().Be(alice.Id);

        await directory.UpdateAsync(alice.Id, user => user.ExternalId = null);
        (await directory.FindByExternalIdAsync("00u-alice")).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_AUser_TakesThemOutOfTheirGroups()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(User("alice"));
        var group = await directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "ops", Members = [alice.Id, "no-such-user"] });
        group.Members.Should().Equal(alice.Id);

        (await directory.DeleteAsync(alice.Id)).Should().BeTrue();

        (await directory.GetGroupAsync(group.Id))!.Members.Should().BeEmpty();
        (await directory.GetAsync(alice.Id)).Should().BeNull();
        (await directory.FindByUserNameAsync("alice")).Should().BeNull();
    }

    [Fact]
    public async Task GroupNames_AreUnique()
    {
        var directory = Directory();
        await directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "Ops" });

        var duplicate = () => directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "ops" });
        await duplicate.Should().ThrowAsync<DirectoryConflictException>();
    }

    [Fact]
    public async Task Grant_HighestRoleWins_AndPermissionsUnion()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(new DirectoryUser
        {
            Id = string.Empty,
            UserName = "alice",
            Role = SystemRole.Viewer,
            IndexPermissions = [new IndexPermission { IndexPattern = "team-a", Capabilities = Capability.Search }],
        });
        await directory.CreateGroupAsync(new DirectoryGroup
        {
            Id = string.Empty,
            DisplayName = "analysts",
            Members = [alice.Id],
            Role = SystemRole.Analyst,
            IndexPermissions = [new IndexPermission { IndexPattern = "team-b", Capabilities = Capability.Search | Capability.Ingest }],
        });
        await directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "no role", Members = [alice.Id] });

        var grant = await directory.GrantForAsync(alice);

        grant!.Role.Should().Be(SystemRole.Analyst);
        var principal = new RbacPrincipal { Id = "x", Role = grant.Role, IndexPermissions = grant.IndexPermissions };
        principal.HasCapability(Capability.Search, "team-a").Should().BeTrue();
        principal.HasCapability(Capability.Ingest, "team-b").Should().BeTrue();
        principal.HasCapability(Capability.Search, "other").Should().BeFalse();
        principal.HasCapability(Capability.Detection).Should().BeTrue("the highest role's other capabilities apply");
    }

    [Fact]
    public void Grant_WithoutIndexLimits_StillAppliesEverywhere_WhenAnotherIsLimited()
    {
        var grant = DirectoryGrant.Combine(
        [
            (SystemRole.Admin, Array.Empty<IndexPermission>()),
            (SystemRole.Viewer, [new IndexPermission { IndexPattern = "team-*", Capabilities = Capability.Search }]),
        ]);

        var principal = new RbacPrincipal { Id = "x", Role = grant!.Role, IndexPermissions = grant.IndexPermissions };
        principal.Role.Should().Be(SystemRole.Admin);
        principal.HasCapability(Capability.IndexManagement, "anything").Should().BeTrue("the admin grant wasn't limited to any index");
        DirectoryGrant.Combine([]).Should().BeNull();
        DirectoryGrant.Combine([(SystemRole.Viewer, Array.Empty<IndexPermission>())])!.IndexPermissions.Should().BeEmpty();
    }

    [Fact]
    public async Task SetUserGroups_PutsThemInExactlyThoseGroups()
    {
        var directory = Directory();
        var alice = await directory.CreateAsync(User("alice"));
        var a = await directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "a", Members = [alice.Id] });
        var b = await directory.CreateGroupAsync(new DirectoryGroup { Id = string.Empty, DisplayName = "b" });

        await directory.SetUserGroupsAsync(alice.Id, [b.Id]);

        (await directory.GroupsOfAsync(alice.Id)).Select(group => group.Id).Should().Equal(b.Id);
        var unknown = () => directory.SetUserGroupsAsync(alice.Id, ["nope"]);
        await unknown.Should().ThrowAsync<ArgumentException>();
        a.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AnotherInstance_SeesChanges_OnceItsCacheExpires()
    {
        var clock = new TestClock();
        var here = Directory(5, clock);
        var there = Directory(5, clock);
        var alice = await here.CreateAsync(User("alice"));
        (await there.GetAsync(alice.Id))!.Active.Should().BeTrue();

        await here.UpdateAsync(alice.Id, user => user.Active = false);

        (await here.GetAsync(alice.Id))!.Active.Should().BeFalse("the instance that changed it knows at once");
        (await there.GetAsync(alice.Id))!.Active.Should().BeTrue("the other reuses what it read");
        clock.Advance(TimeSpan.FromSeconds(6));
        (await there.GetAsync(alice.Id))!.Active.Should().BeFalse();
    }

    [Fact]
    public async Task Match_PrefersTheExternalId_ThenTheSignInName_ThenEmail()
    {
        var directory = Directory();
        var byId = await directory.CreateAsync(User("someone", "oid-123"));
        var byName = await directory.CreateAsync(User("bob@example.com"));
        var byEmail = await directory.CreateAsync(new DirectoryUser { Id = string.Empty, UserName = "carol", Emails = [new DirectoryEmail { Value = "Carol@Example.com", Primary = true }] });

        (await directory.MatchAsync(new ClaimsIdentity([new Claim("oid", "oid-123"), new Claim("preferred_username", "bob@example.com")])))!.Id.Should().Be(byId.Id);
        (await directory.MatchAsync(new ClaimsIdentity([new Claim("sub", "x"), new Claim("preferred_username", "BOB@example.com")])))!.Id.Should().Be(byName.Id);
        (await directory.MatchAsync(new ClaimsIdentity([new Claim("sub", "y"), new Claim("email", "carol@example.com")])))!.Id.Should().Be(byEmail.Id);
        (await directory.MatchAsync(new ClaimsIdentity([new Claim("sub", "z")]))).Should().BeNull();
    }

    [Fact]
    public async Task Match_IgnoresAnEmailTheProviderHasntVerified()
    {
        var directory = Directory();
        await directory.CreateAsync(User("admin@example.com"));

        var identity = new ClaimsIdentity([new Claim("sub", "attacker"), new Claim("email", "admin@example.com"), new Claim("email_verified", "false")]);

        (await directory.MatchAsync(identity)).Should().BeNull();
    }
}
