using System.Diagnostics;
using System.Text;
using Cabinet.API.Authentication;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_IsVersioned_WithIterationsAndSalt_AndVerifies()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        hash.Should().StartWith("$pbkdf2-sha256$v1$600000$");
        hash.Split('$').Should().HaveCount(6);
        Convert.FromBase64String(hash.Split('$')[4]).Should().HaveCount(16, "the salt is 16 random bytes");
        PasswordHasher.Verify("correct horse battery staple", hash).Should().BeTrue();
        PasswordHasher.Verify("correct horse battery stapler", hash).Should().BeFalse();
        PasswordHasher.Hash("correct horse battery staple").Should().NotBe(hash, "each hash has its own salt");
    }

    [Fact]
    public void FewerIterations_ThanAsked_AreNeverUsed()
    {
        var hash = PasswordHasher.Hash("long enough password", 1000);

        hash.Should().Contain("$600000$");
    }

    [Fact]
    public void OlderHashes_StillVerify_AndAskToBeRehashed()
    {
        var hash = PasswordHasher.Hash("long enough password", 600_000);

        PasswordHasher.NeedsRehash(hash, 600_000).Should().BeFalse();
        PasswordHasher.NeedsRehash(hash, 900_000).Should().BeTrue();
        PasswordHasher.Verify("long enough password", hash).Should().BeTrue("the hash names the iterations it used");
        PasswordHasher.NeedsRehash("some-other-format", 600_000).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("$pbkdf2-sha256$v1$600000$notbase64$x")]
    [InlineData("$pbkdf2-sha256$v1$abc$AAAAAAAAAAAAAAAAAAAAAA==$AAAA")]
    [InlineData("plaintext")]
    public void MalformedHashes_NeverVerify(string? stored)
    {
        PasswordHasher.Verify("anything at all", stored).Should().BeFalse();
    }

    [Fact]
    public void ACheckWithNoHash_TakesAboutAsLong_AsARealOne()
    {
        var hash = PasswordHasher.Hash("long enough password");
        PasswordHasher.Verify("warm up", hash);
        PasswordHasher.VerifyDecoy("warm up");

        var real = Stopwatch.StartNew();
        PasswordHasher.Verify("wrong password!!", hash);
        real.Stop();
        var decoy = Stopwatch.StartNew();
        PasswordHasher.Verify("wrong password!!", null);
        decoy.Stop();

        decoy.Elapsed.Should().BeGreaterThan(real.Elapsed / 3, "a missing user mustn't answer faster than a wrong password");
    }
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("short", "Use at least 12")]
    [InlineData("aaaaaaaaaaaaaaaa", "repeats")]
    [InlineData("password1234", "attackers try first")]
    [InlineData("Password1234", "attackers try first")]
    [InlineData("xx-alice-secret-xx", "user name")]
    [InlineData("my alice.smith pass!", "user name")]
    public void WeakPasswords_AreRefused(string password, string reason)
    {
        PasswordPolicy.Check(password, 12, "alice", "alice.smith@example.com").Should().Contain(reason);
    }

    [Fact]
    public void AGoodPassword_IsAccepted()
    {
        PasswordPolicy.Check("tangerine-orbit-quietly-42", 12, "alice", "alice@example.com").Should().BeNull();
    }
}

public sealed class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 appendix B, SHA1
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Codes_MatchTheRfcTestVectors(long unixTime, string expected)
    {
        Totp.Code(RfcSecret, unixTime / 30, digits: 8).Should().Be(expected);
        Totp.Code(RfcSecret, unixTime / 30).Should().Be(expected[2..], "six digits are the last six of eight");
    }

    [Fact]
    public void Base32_FollowsRfc4648()
    {
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).Should().Be("MZXW6YTBOI");
        Base32.TryDecode("mzxw6ytboi", out var decoded).Should().BeTrue();
        Encoding.ASCII.GetString(decoded).Should().Be("foobar");
        Base32.TryDecode("not base32!", out _).Should().BeFalse();
        Base32.TryDecode(Totp.NewSecret(), out var secret).Should().BeTrue();
        secret.Should().HaveCount(20);
    }

    [Fact]
    public void Match_AcceptsOneStepEitherSide_AndNoFurther()
    {
        var secret = Base32.Encode(RfcSecret);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);

        Totp.Match(secret, Totp.Code(RfcSecret, step), now, 0).Should().Be(step);
        Totp.Match(secret, Totp.Code(RfcSecret, step - 1), now, 0).Should().Be(step - 1);
        Totp.Match(secret, Totp.Code(RfcSecret, step + 1), now, 0).Should().Be(step + 1);
        Totp.Match(secret, Totp.Code(RfcSecret, step - 2), now, 0).Should().BeNull();
        Totp.Match(secret, Totp.Code(RfcSecret, step + 2), now, 0).Should().BeNull();
        Totp.Match(secret, "12345", now, 0).Should().BeNull();
        Totp.Match(secret, "abcdef", now, 0).Should().BeNull();
    }

    [Fact]
    public void Match_RefusesAStepAlreadyUsed()
    {
        var secret = Base32.Encode(RfcSecret);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);

        Totp.Match(secret, Totp.Code(RfcSecret, step), now, lastUsedStep: step).Should().BeNull();
        Totp.Match(secret, Totp.Code(RfcSecret, step - 1), now, lastUsedStep: step).Should().BeNull("an older code can't follow a newer one");
    }

    [Fact]
    public void Uri_IsWhatAuthenticatorAppsRead()
    {
        Totp.Uri("Cabinet", "alice@example.com", "ABC").Should()
            .Be("otpauth://totp/Cabinet:alice%40example.com?secret=ABC&issuer=Cabinet&algorithm=SHA1&digits=6&period=30");
    }
}

public sealed class LocalAccountTests
{
    private const string Password = "tangerine-orbit-quietly-42";

    private readonly InMemoryDocumentStore _store = new();
    private readonly TestClock _clock = new();
    private readonly UserDirectory _directory;
    private readonly LocalAccountService _accounts;
    private readonly SecretProtector _secrets = new(new EphemeralDataProtectionProvider());

    public LocalAccountTests() : this(new AccountOptions())
    {
    }

    private LocalAccountTests(AccountOptions options)
    {
        _directory = new UserDirectory(_store, options, NullLogger<UserDirectory>.Instance, _clock);
        _accounts = new LocalAccountService(_directory, _store, _secrets, new SignInThrottle(_clock), NullLogger<LocalAccountService>.Instance, _clock);
    }

    private static LocalAccountTests With(AccountOptions options) => new(options);

    private async Task<DirectoryUser> AddUserAsync(string name = "alice", SystemRole role = SystemRole.Analyst, bool mustChange = false) =>
        await _directory.CreateAsync(new DirectoryUser
        {
            Id = string.Empty,
            UserName = name,
            Role = role,
            PasswordHash = PasswordHasher.Hash(Password),
            MustChangePassword = mustChange,
        });

    private async Task<string> CurrentCodeAsync(string userId, long stepOffset = 0)
    {
        var user = await _directory.GetFreshAsync(userId);
        var secret = await _secrets.UnprotectAsync(user!.MfaSecret ?? user.PendingMfaSecret!);
        Base32.TryDecode(secret, out var bytes);
        return Totp.Code(bytes, Totp.StepAt(_clock.Now) + stepOffset);
    }

    private async Task<IReadOnlyList<string>> EnrollAsync(string userId)
    {
        await _accounts.BeginEnrollmentAsync(userId);
        var codes = await _accounts.ConfirmEnrollmentAsync(userId, await CurrentCodeAsync(userId));
        codes.Should().HaveCount(10);
        _clock.Advance(TimeSpan.FromSeconds(30));
        return codes!;
    }

    [Fact]
    public async Task RightPassword_SignsIn_WithTheDirectoryGrant()
    {
        var alice = await AddUserAsync();

        var step = await _accounts.SignInAsync("ALICE", Password, "10.0.0.1");

        step.Status.Should().Be(SignInStatus.SignedIn);
        step.User!.Id.Should().Be(alice.Id);
        step.Grant!.Role.Should().Be(SystemRole.Analyst);
        var identity = DirectoryClaims.ForLocalUser(step.User, step.Grant);
        identity.FindFirst("ClientId")!.Value.Should().Be($"local-{alice.Id}");
        identity.FindFirst(DirectoryClaims.SessionVersion)!.Value.Should().Be("1");
        identity.FindFirst(PrincipalResolver.RoleClaim)!.Value.Should().Be("Analyst");
    }

    [Fact]
    public async Task WrongPasswords_LockTheAccount_EvenAgainstTheRightOne_UntilItExpires()
    {
        var alice = await AddUserAsync();

        for (var i = 0; i < 5; i++)
        {
            // From different addresses, so only the account's lockout is at work
            (await _accounts.SignInAsync("alice", "wrong password " + i, $"10.0.0.{i}")).Status.Should().BeOneOf(SignInStatus.Failed, SignInStatus.Throttled);
            _clock.Advance(TimeSpan.FromSeconds(70));
        }

        (await _directory.GetFreshAsync(alice.Id))!.LockedUntil.Should().NotBeNull();
        (await _accounts.SignInAsync("alice", Password, "10.0.1.1")).Status.Should().Be(SignInStatus.Failed, "a locked account refuses even the right password");

        _clock.Advance(TimeSpan.FromMinutes(16));
        (await _accounts.SignInAsync("alice", Password, "10.0.1.1")).Status.Should().Be(SignInStatus.SignedIn);
    }

    [Fact]
    public async Task Unlock_ClearsALockout()
    {
        var alice = await AddUserAsync();
        await _directory.UpdateAsync(alice.Id, u => u.LockedUntil = _clock.Now.UtcDateTime.AddMinutes(15));

        await _accounts.UnlockAsync(alice.Id);

        (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);
    }

    [Fact]
    public async Task RepeatedFailures_AreSlowed_PerAccountAndPerAddress()
    {
        var throttle = new SignInThrottle(_clock);
        for (var i = 0; i < SignInThrottle.AccountFreeFailures; i++)
        {
            throttle.Failed("alice", $"10.0.0.{i}");
        }

        throttle.RetryAfter("alice", "10.9.9.9").Should().Be(TimeSpan.Zero);
        throttle.Failed("alice", "10.0.0.99");
        throttle.RetryAfter("ALICE", "10.9.9.9").Should().BeGreaterThan(TimeSpan.Zero, "the account is slowed from any address");

        for (var i = 0; i < SignInThrottle.AddressFreeFailures + 1; i++)
        {
            throttle.Failed($"user{i}", "10.1.1.1");
        }

        throttle.RetryAfter("someone-new", "10.1.1.1").Should().BeGreaterThan(TimeSpan.Zero, "the address is slowed for any account");
        _clock.Advance(TimeSpan.FromMinutes(16));
        throttle.RetryAfter("someone-new", "10.1.1.1").Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task UnknownUsers_AndUsersWithoutPasswords_FailLikeAWrongPassword()
    {
        await _directory.CreateAsync(new DirectoryUser { Id = string.Empty, UserName = "scim-user", Source = UserSource.Scim });

        (await _accounts.SignInAsync("nobody", Password, "10.0.0.1")).Message.Should().Be((await _accounts.SignInAsync("scim-user", Password, "10.0.0.2")).Message);
    }

    [Fact]
    public async Task DeactivatedUsers_CantSignIn()
    {
        var alice = await AddUserAsync();
        await _directory.UpdateAsync(alice.Id, u => u.Active = false);

        (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.Disabled);
    }

    [Fact]
    public async Task WithMfa_ThePasswordOnlyGivesAChallenge_AndTheCodeFinishes()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);

        var first = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        first.Status.Should().Be(SignInStatus.MfaRequired);
        first.User.Should().BeNull("the challenge isn't a session");

        var done = await _accounts.CompleteMfaAsync(first.Challenge, await CurrentCodeAsync(alice.Id), "10.0.0.1");
        done.Status.Should().Be(SignInStatus.SignedIn);

        var replay = await _accounts.CompleteMfaAsync(first.Challenge, await CurrentCodeAsync(alice.Id, 1), "10.0.0.1");
        replay.Status.Should().Be(SignInStatus.ChallengeInvalid, "a challenge is used once");
    }

    [Fact]
    public async Task ACode_CantBeUsedTwice()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);
        var code = await CurrentCodeAsync(alice.Id);

        var first = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        (await _accounts.CompleteMfaAsync(first.Challenge, code, "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);

        var second = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        (await _accounts.CompleteMfaAsync(second.Challenge, code, "10.0.0.1")).Status.Should().Be(SignInStatus.Rejected, "that time step was used");
    }

    [Fact]
    public async Task AChallenge_IsBoundToItsUser()
    {
        var alice = await AddUserAsync("alice");
        var bob = await AddUserAsync("bob");
        await EnrollAsync(alice.Id);
        await EnrollAsync(bob.Id);

        var aliceChallenge = (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Challenge;
        var withBobsCode = await _accounts.CompleteMfaAsync(aliceChallenge, await CurrentCodeAsync(bob.Id), "10.0.0.1");

        withBobsCode.Status.Should().NotBe(SignInStatus.SignedIn);
        withBobsCode.User.Should().BeNull();
    }

    [Fact]
    public async Task AChallenge_ExpiresAfterFiveMinutes()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);
        var challenge = (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Challenge;

        _clock.Advance(TimeSpan.FromMinutes(6));

        (await _accounts.CompleteMfaAsync(challenge, await CurrentCodeAsync(alice.Id), "10.0.0.1")).Status.Should().Be(SignInStatus.ChallengeInvalid);
    }

    [Fact]
    public async Task AChallenge_CantBeUsedForAnotherStep()
    {
        var alice = await AddUserAsync(mustChange: true);
        var challenge = (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Challenge;

        (await _accounts.CompleteMfaAsync(challenge, "123456", "10.0.0.1")).Status.Should().Be(SignInStatus.ChallengeInvalid);
        (await _accounts.CompletePasswordChangeAsync(challenge, "a brand new long passphrase")).Status.Should().Be(SignInStatus.SignedIn, "it's still good for its own step");
        alice.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task RecoveryCodes_WorkOnce()
    {
        var alice = await AddUserAsync();
        var codes = await EnrollAsync(alice.Id);

        var first = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        (await _accounts.CompleteMfaAsync(first.Challenge, codes[0].ToUpperInvariant(), "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);

        var second = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        (await _accounts.CompleteMfaAsync(second.Challenge, codes[0], "10.0.0.1")).Status.Should().Be(SignInStatus.Rejected);
        (await _directory.GetFreshAsync(alice.Id))!.RecoveryCodeHashes.Should().HaveCount(9);
    }

    [Fact]
    public async Task WrongCodes_CountTowardsLockout()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);

        var challenge = (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Challenge;
        SignInStep step = null!;
        for (var i = 0; i < 5; i++)
        {
            step = await _accounts.CompleteMfaAsync(challenge, "000000", $"10.0.0.{i + 10}");
            challenge = step.Challenge ?? challenge;
            _clock.Advance(TimeSpan.FromSeconds(10));
        }

        step.Status.Should().Be(SignInStatus.Failed);
        (await _directory.GetFreshAsync(alice.Id))!.LockedUntil.Should().NotBeNull();
    }

    [Fact]
    public async Task MustChangePassword_ComesBeforeAnythingElse_AndEndsOtherSessions()
    {
        var alice = await AddUserAsync(mustChange: true);

        var step = await _accounts.SignInAsync("alice", Password, "10.0.0.1");
        step.Status.Should().Be(SignInStatus.PasswordChangeRequired);

        (await _accounts.CompletePasswordChangeAsync(step.Challenge, "short")).Status.Should().Be(SignInStatus.Rejected);
        (await _accounts.CompletePasswordChangeAsync(step.Challenge, Password)).Message.Should().Contain("haven't used");
        var done = await _accounts.CompletePasswordChangeAsync(step.Challenge, "a brand new long passphrase");

        done.Status.Should().Be(SignInStatus.SignedIn);
        done.User!.MustChangePassword.Should().BeFalse();
        done.User.SessionVersion.Should().Be(alice.SessionVersion + 1);
    }

    [Fact]
    public async Task RequiredMfa_MakesSomeoneWithoutItSetItUp_BeforeSigningIn()
    {
        var test = With(new AccountOptions { RequireMfa = MfaRequirement.Admin });
        await test.AddUserAsync("viewer", SystemRole.Viewer);
        var admin = await test.AddUserAsync("admin", SystemRole.Admin);

        (await test._accounts.SignInAsync("viewer", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);

        var step = await test._accounts.SignInAsync("admin", Password, "10.0.0.1");
        step.Status.Should().Be(SignInStatus.MfaEnrollmentRequired);
        var enrollment = await test._accounts.StartEnrollmentAsync(step.Challenge);
        enrollment.Secret.Should().NotBeNullOrEmpty();
        enrollment.OtpAuthUri.Should().StartWith("otpauth://totp/Cabinet:admin?secret=");
        Base32.TryDecode(enrollment.Secret, out var secret);

        var done = await test._accounts.CompleteEnrollmentAsync(enrollment.Challenge, Totp.Code(secret, Totp.StepAt(test._clock.Now)));

        done.Status.Should().Be(SignInStatus.SignedIn);
        done.RecoveryCodes.Should().HaveCount(10);
        (await test._directory.GetFreshAsync(admin.Id))!.MfaEnabled.Should().BeTrue();
        (await test._accounts.DisableMfaAsync(admin.Id, "000000")).Should().Contain("has to use");
    }

    [Fact]
    public async Task TheSecret_IsStoredEncrypted()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);

        var stored = (await _directory.GetFreshAsync(alice.Id))!;
        stored.MfaSecret.Should().StartWith("dp:");
        Base32.TryDecode(stored.MfaSecret, out _).Should().BeFalse();
        stored.RecoveryCodeHashes.Should().AllSatisfy(hash => hash.Should().HaveLength(64));
    }

    [Fact]
    public async Task DisablingMfa_NeedsACode()
    {
        var alice = await AddUserAsync();
        await EnrollAsync(alice.Id);

        (await _accounts.DisableMfaAsync(alice.Id, "000000")).Should().NotBeNull();
        (await _accounts.DisableMfaAsync(alice.Id, await CurrentCodeAsync(alice.Id))).Should().BeNull();
        (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);
    }

    [Fact]
    public async Task ChangingOnesPassword_NeedsTheCurrentOne_AndRaisesTheSessionVersion()
    {
        var alice = await AddUserAsync();

        (await _accounts.ChangePasswordAsync(alice.Id, "not it at all", "a brand new long passphrase", "10.0.0.1")).Problem.Should().Contain("current password");
        var (problem, user) = await _accounts.ChangePasswordAsync(alice.Id, Password, "a brand new long passphrase", "10.0.0.1");

        problem.Should().BeNull();
        user!.SessionVersion.Should().Be(2);
        (await _accounts.SignInAsync("alice", "a brand new long passphrase", "10.0.0.1")).Status.Should().Be(SignInStatus.SignedIn);
    }

    [Fact]
    public async Task AnAdministratorsReset_GivesATemporaryPassword_ThatMustBeChanged()
    {
        var alice = await AddUserAsync();

        var temporary = await _accounts.ResetPasswordAsync(alice.Id);

        temporary.Should().HaveLength(16);
        var user = (await _directory.GetFreshAsync(alice.Id))!;
        user.MustChangePassword.Should().BeTrue();
        user.SessionVersion.Should().Be(2, "the old sessions end");
        (await _accounts.SignInAsync("alice", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.Failed);
        (await _accounts.SignInAsync("alice", temporary, "10.0.0.1")).Status.Should().Be(SignInStatus.PasswordChangeRequired);
    }

    [Fact]
    public async Task AUserNobodyGaveARole_HasNoAccess()
    {
        await _directory.CreateAsync(new DirectoryUser { Id = string.Empty, UserName = "norole", PasswordHash = PasswordHasher.Hash(Password) });

        (await _accounts.SignInAsync("norole", Password, "10.0.0.1")).Status.Should().Be(SignInStatus.NoAccess);
    }
}
