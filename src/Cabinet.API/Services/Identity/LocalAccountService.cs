using System.Security.Cryptography;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services.Identity;

/// <summary>Where a local sign-in has got to.</summary>
public enum SignInStatus
{
    /// <summary>Done: start the session.</summary>
    SignedIn,

    /// <summary>The password was right; an authenticator or recovery code is next.</summary>
    MfaRequired,

    /// <summary>The user has to choose a new password first.</summary>
    PasswordChangeRequired,

    /// <summary>The user has to set up an authenticator app first.</summary>
    MfaEnrollmentRequired,

    /// <summary>Wrong user name, password or code, or the account is locked.</summary>
    Failed,

    /// <summary>Too many recent failures: try again after a while.</summary>
    Throttled,

    /// <summary>The account is deactivated.</summary>
    Disabled,

    /// <summary>Signed in, but nothing gives the account a role.</summary>
    NoAccess,

    /// <summary>The challenge is unknown, used, expired, or for another step.</summary>
    ChallengeInvalid,

    /// <summary>The new password or code wasn't acceptable; the message says why.</summary>
    Rejected,
}

/// <summary>The outcome of a step of local sign-in.</summary>
public sealed class SignInStep
{
    public required SignInStatus Status { get; init; }

    /// <summary>The user, once known to be who they say.</summary>
    public DirectoryUser? User { get; init; }

    /// <summary>What the user may do, when signed in.</summary>
    public DirectoryGrant? Grant { get; init; }

    /// <summary>The single-use token for the next step.</summary>
    public string? Challenge { get; init; }

    /// <summary>For <see cref="SignInStatus.Throttled"/>, how long to wait.</summary>
    public TimeSpan RetryAfter { get; init; }

    /// <summary>A message for the person signing in.</summary>
    public string? Message { get; init; }

    /// <summary>Recovery codes, shown once, when an authenticator app was just set up.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; init; }

    /// <summary>For enrollment: the new secret and its otpauth:// URI.</summary>
    public string? Secret { get; init; }

    public string? OtpAuthUri { get; init; }
}

/// <summary>
/// Local accounts: signing in with a password and an authenticator code, choosing a new password,
/// setting up and removing an authenticator, recovery codes, lockout, and the administrator's
/// resets. Sessions themselves are started by the caller once a step says
/// <see cref="SignInStatus.SignedIn"/>.
/// </summary>
/// <remarks>
/// Each step after the password hands back a challenge: a random token, kept hashed in the
/// document store, bound to one user and one step, that lasts five minutes and is taken (deleted)
/// before it's checked, so it can't be used twice even on two instances at once. A wrong code puts
/// it back with one attempt fewer, up to five.
/// </remarks>
public sealed class LocalAccountService
{
    /// <summary>Where sign-in challenges are kept, by the hash of their token.</summary>
    public const string ChallengeCollection = "auth-challenges";

    /// <summary>Recovery codes made at a time.</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>Wrong codes one challenge accepts before sign-in has to start again.</summary>
    public const int ChallengeAttempts = 5;

    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);
    private const string GenericFailure = "Wrong user name or password. After several wrong tries an account is locked for a while.";
    private const string CodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private readonly UserDirectory _directory;
    private readonly IDocumentStore _documents;
    private readonly SecretProtector _secrets;
    private readonly SignInThrottle _throttle;
    private readonly ILogger<LocalAccountService> _logger;
    private readonly TimeProvider _time;
    private long _lastCleanupTicks;

    public LocalAccountService(
        UserDirectory directory,
        IDocumentStore documents,
        SecretProtector secrets,
        SignInThrottle throttle,
        ILogger<LocalAccountService> logger,
        TimeProvider? time = null)
    {
        _directory = directory;
        _documents = documents;
        _secrets = secrets;
        _throttle = throttle;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private AccountOptions Options => _directory.Options;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ---------- Signing in ----------

    /// <summary>The first step: a user name and password.</summary>
    public async Task<SignInStep> SignInAsync(string? userName, string? password, string? address, CancellationToken cancellationToken = default)
    {
        userName = userName?.Trim() ?? string.Empty;
        if (userName.Length == 0 || userName.Length > UserDirectory.MaxNameLength || string.IsNullOrEmpty(password))
        {
            return Fail(GenericFailure);
        }

        var wait = _throttle.RetryAfter(userName, address);
        if (wait > TimeSpan.Zero)
        {
            return new SignInStep { Status = SignInStatus.Throttled, RetryAfter = wait, Message = "Too many wrong tries. Wait a moment and try again." };
        }

        var user = await _directory.FindByUserNameAsync(userName, cancellationToken);
        if (user?.PasswordHash == null || IsLocked(user))
        {
            // As long as a real check, so the answer's timing doesn't say whether the account exists
            PasswordHasher.VerifyDecoy(password);
            _throttle.Failed(userName, address);
            return Fail(GenericFailure);
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash))
        {
            _throttle.Failed(userName, address);
            await RecordFailureAsync(user.Id, cancellationToken);
            _logger.LogInformation("Wrong password for local account {User}", user.UserName);
            return Fail(GenericFailure);
        }

        _throttle.Succeeded(userName);
        if (!user.Active)
        {
            return new SignInStep { Status = SignInStatus.Disabled, Message = "This account is deactivated. Ask an administrator." };
        }

        var rehash = PasswordHasher.NeedsRehash(user.PasswordHash, Options.PasswordIterations);
        user = await _directory.UpdateAsync(user.Id, u =>
        {
            u.FailedAttempts = 0;
            if (rehash)
            {
                u.PasswordHash = PasswordHasher.Hash(password, Options.PasswordIterations);
            }
        }, cancellationToken) ?? user;
        return await NextStepAsync(user, mfaDone: false, cancellationToken);
    }

    /// <summary>The authenticator or recovery code, after the password.</summary>
    public async Task<SignInStep> CompleteMfaAsync(string? challenge, string? code, string? address, CancellationToken cancellationToken = default)
    {
        var taken = await TakeChallengeAsync(challenge, ChallengePurpose.Mfa, cancellationToken);
        if (taken is not { } held)
        {
            return InvalidChallenge();
        }

        var (stored, user) = held;
        var wait = _throttle.RetryAfter(user.UserName, address);
        if (wait > TimeSpan.Zero)
        {
            await PutBackAsync(challenge!, stored, cancellationToken);
            return new SignInStep { Status = SignInStatus.Throttled, RetryAfter = wait, Message = "Too many wrong tries. Wait a moment and try again." };
        }

        if (!await VerifyCodeAsync(user.Id, code, cancellationToken))
        {
            _throttle.Failed(user.UserName, address);
            var locked = await RecordFailureAsync(user.Id, cancellationToken);
            stored.Attempts++;
            if (!locked && stored.Attempts < ChallengeAttempts)
            {
                await PutBackAsync(challenge!, stored, cancellationToken);
                return new SignInStep { Status = SignInStatus.Rejected, Challenge = challenge, Message = "That code isn't right. Check the time on your device and try the newest code." };
            }

            return Fail("That code isn't right. Sign in again.");
        }

        _throttle.Succeeded(user.UserName);
        user = await _directory.GetFreshAsync(user.Id, cancellationToken) ?? user;
        return await NextStepAsync(user, mfaDone: true, cancellationToken);
    }

    /// <summary>A new password, when the account must change it before signing in.</summary>
    public async Task<SignInStep> CompletePasswordChangeAsync(string? challenge, string? newPassword, CancellationToken cancellationToken = default)
    {
        var taken = await TakeChallengeAsync(challenge, ChallengePurpose.PasswordChange, cancellationToken);
        if (taken is not { } held)
        {
            return InvalidChallenge();
        }

        var (stored, user) = held;
        var problem = CheckNewPassword(user, newPassword);
        if (problem != null)
        {
            await PutBackAsync(challenge!, stored, cancellationToken);
            return new SignInStep { Status = SignInStatus.Rejected, Challenge = challenge, Message = problem };
        }

        user = await SetPasswordAsync(user.Id, newPassword!, mustChange: false, cancellationToken) ?? user;
        return await NextStepAsync(user, stored.MfaDone, cancellationToken);
    }

    /// <summary>Starts setting up an authenticator during sign-in, when the account must have one.</summary>
    public async Task<SignInStep> StartEnrollmentAsync(string? challenge, CancellationToken cancellationToken = default)
    {
        var taken = await TakeChallengeAsync(challenge, ChallengePurpose.Enroll, cancellationToken);
        if (taken is not { } held)
        {
            return InvalidChallenge();
        }

        var (stored, user) = held;
        var secret = Totp.NewSecret();
        stored.PendingSecret = await _secrets.ProtectAsync(secret, cancellationToken);
        await PutBackAsync(challenge!, stored, cancellationToken);
        return new SignInStep
        {
            Status = SignInStatus.MfaEnrollmentRequired,
            Challenge = challenge,
            Secret = secret,
            OtpAuthUri = Totp.Uri(Options.MfaIssuer, user.UserName, secret),
        };
    }

    /// <summary>Confirms the authenticator set up during sign-in with a code from it, and signs in.</summary>
    public async Task<SignInStep> CompleteEnrollmentAsync(string? challenge, string? code, CancellationToken cancellationToken = default)
    {
        var taken = await TakeChallengeAsync(challenge, ChallengePurpose.Enroll, cancellationToken);
        if (taken is not { } held)
        {
            return InvalidChallenge();
        }

        var (stored, user) = held;
        if (stored.PendingSecret == null)
        {
            await PutBackAsync(challenge!, stored, cancellationToken);
            return new SignInStep { Status = SignInStatus.Rejected, Challenge = challenge, Message = "Start setting up the authenticator first." };
        }

        var secret = await _secrets.UnprotectAsync(stored.PendingSecret, cancellationToken);
        var step = Totp.Match(secret, code, _time.GetUtcNow(), 0);
        if (step == null)
        {
            stored.Attempts++;
            if (stored.Attempts < ChallengeAttempts)
            {
                await PutBackAsync(challenge!, stored, cancellationToken);
                return new SignInStep { Status = SignInStatus.Rejected, Challenge = challenge, Message = "That code isn't right. Check the time on your device and try the newest code." };
            }

            return Fail("That code isn't right. Sign in again.");
        }

        var codes = NewRecoveryCodes();
        user = await _directory.UpdateAsync(user.Id, u =>
        {
            u.MfaEnabled = true;
            u.MfaSecret = stored.PendingSecret;
            u.PendingMfaSecret = null;
            u.LastMfaStep = step.Value;
            u.RecoveryCodeHashes = codes.Select(HashRecoveryCode).ToList();
        }, cancellationToken) ?? user;
        var next = await NextStepAsync(user, mfaDone: true, cancellationToken);
        return next.Status == SignInStatus.SignedIn
            ? new SignInStep { Status = next.Status, User = next.User, Grant = next.Grant, RecoveryCodes = codes }
            : next;
    }

    private async Task<SignInStep> NextStepAsync(DirectoryUser user, bool mfaDone, CancellationToken cancellationToken)
    {
        if (user.MfaEnabled && !mfaDone)
        {
            return await ChallengeAsync(user, ChallengePurpose.Mfa, mfaDone, SignInStatus.MfaRequired, null, cancellationToken);
        }

        if (user.MustChangePassword)
        {
            return await ChallengeAsync(user, ChallengePurpose.PasswordChange, mfaDone, SignInStatus.PasswordChangeRequired, "Choose a new password to continue.", cancellationToken);
        }

        var grant = await _directory.GrantForAsync(user, cancellationToken);
        if (!user.MfaEnabled && Options.MfaRequiredFor(grant?.Role))
        {
            return await ChallengeAsync(user, ChallengePurpose.Enroll, mfaDone, SignInStatus.MfaEnrollmentRequired, "Set up an authenticator app to continue.", cancellationToken);
        }

        if (grant == null)
        {
            return new SignInStep { Status = SignInStatus.NoAccess, User = user, Message = "Your account has no access to Cabinet yet. Ask an administrator to give it a role." };
        }

        var signedIn = await _directory.UpdateAsync(user.Id, u =>
        {
            u.LastSignInAt = Now;
            u.FailedAttempts = 0;
        }, cancellationToken) ?? user;
        return new SignInStep { Status = SignInStatus.SignedIn, User = signedIn, Grant = grant };
    }

    // ---------- Challenges ----------

    private async Task<SignInStep> ChallengeAsync(DirectoryUser user, string purpose, bool mfaDone, SignInStatus status, string? message, CancellationToken cancellationToken)
    {
        await CleanUpChallengesAsync(cancellationToken);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = new SignInChallenge
        {
            UserId = user.Id,
            Purpose = purpose,
            SessionVersion = user.SessionVersion,
            MfaDone = mfaDone,
            ExpiresAt = Now + (purpose == ChallengePurpose.Enroll ? EnrollmentLifetime : ChallengeLifetime),
        };
        await _documents.PutAsync(ChallengeCollection, HashToken(token), challenge, cancellationToken);
        return new SignInStep { Status = status, Challenge = token, Message = message };
    }

    /// <summary>
    /// Takes a challenge, so nobody else can: the delete succeeds for one caller only. Null unless
    /// it's for this step, unexpired, and its user is still active with the same session version.
    /// </summary>
    private async Task<(SignInChallenge Challenge, DirectoryUser User)?> TakeChallengeAsync(string? token, string purpose, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return null;
        }

        var id = HashToken(token);
        var challenge = await _documents.GetAsync<SignInChallenge>(ChallengeCollection, id, cancellationToken);
        if (challenge == null || !await _documents.DeleteAsync(ChallengeCollection, id, cancellationToken))
        {
            return null;
        }

        if (challenge.Purpose != purpose || challenge.ExpiresAt <= Now)
        {
            // Back for its own step, unless it has expired
            if (challenge.ExpiresAt > Now)
            {
                await _documents.PutAsync(ChallengeCollection, id, challenge, cancellationToken);
            }

            return null;
        }

        var user = await _directory.GetFreshAsync(challenge.UserId, cancellationToken);
        if (user is not { Active: true } || user.SessionVersion != challenge.SessionVersion || IsLocked(user))
        {
            return null;
        }

        return (challenge, user);
    }

    private Task PutBackAsync(string token, SignInChallenge challenge, CancellationToken cancellationToken) =>
        _documents.PutAsync(ChallengeCollection, HashToken(token), challenge, cancellationToken);

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task CleanUpChallengesAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var last = Interlocked.Read(ref _lastCleanupTicks);
        if (now.Ticks - last < TimeSpan.FromMinutes(10).Ticks || Interlocked.CompareExchange(ref _lastCleanupTicks, now.Ticks, last) != last)
        {
            return;
        }

        try
        {
            foreach (var (id, json) in await _documents.ExportAsync(ChallengeCollection, cancellationToken))
            {
                var challenge = System.Text.Json.JsonSerializer.Deserialize<SignInChallenge>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if (challenge == null || challenge.ExpiresAt <= now)
                {
                    await _documents.DeleteAsync(ChallengeCollection, id, cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Couldn't clear expired sign-in challenges");
        }
    }

    // ---------- Codes ----------

    /// <summary>
    /// Checks an authenticator code, or a recovery code, and records its use: a code's time step
    /// can't be used again, and a recovery code is gone once used.
    /// </summary>
    public async Task<bool> VerifyCodeAsync(string userId, string? code, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(userId, cancellationToken);
        if (user is not { MfaEnabled: true, MfaSecret: { } protectedSecret } || string.IsNullOrWhiteSpace(code) || code.Length > 64)
        {
            return false;
        }

        var digits = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (digits.Length == Totp.Digits && digits.All(char.IsAsciiDigit))
        {
            var secret = await _secrets.UnprotectAsync(protectedSecret, cancellationToken);
            if (Totp.Match(secret, digits, _time.GetUtcNow(), user.LastMfaStep) is not { } step)
            {
                return false;
            }

            var accepted = false;
            await _directory.UpdateAsync(userId, u =>
            {
                // Checked again under the lock, so two requests can't both use one step
                if (step > u.LastMfaStep)
                {
                    u.LastMfaStep = step;
                    accepted = true;
                }
            }, cancellationToken);
            return accepted;
        }

        var hash = HashRecoveryCode(code);
        var used = false;
        await _directory.UpdateAsync(userId, u =>
        {
            var match = FindRecoveryCode(u.RecoveryCodeHashes, hash);
            if (match >= 0)
            {
                u.RecoveryCodeHashes.RemoveAt(match);
                used = true;
            }
        }, cancellationToken);
        if (used)
        {
            _logger.LogInformation("Local account {User} used a recovery code", user.UserName);
        }

        return used;
    }

    private static int FindRecoveryCode(List<string> hashes, string hash)
    {
        var wanted = Encoding.ASCII.GetBytes(hash);
        var found = -1;
        for (var i = 0; i < hashes.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(wanted, Encoding.ASCII.GetBytes(hashes[i])))
            {
                found = i;
            }
        }

        return found;
    }

    /// <summary>New recovery codes, such as <c>k7m2p-x9q4r</c>.</summary>
    public static IReadOnlyList<string> NewRecoveryCodes()
    {
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var text = RandomText(CodeAlphabet, 10);
            codes.Add($"{text[..5]}-{text[5..]}");
        }

        return codes;
    }

    /// <summary>A recovery code's hash: SHA-256, ignoring case, spaces and dashes. Codes are random enough not to need a slow hash.</summary>
    public static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static string RandomText(string alphabet, int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(chars);
    }

    // ---------- Self-service ----------

    /// <summary>Why a new password isn't allowed for this user, or null.</summary>
    public string? CheckNewPassword(DirectoryUser user, string? password)
    {
        var problem = PasswordPolicy.Check(password, Options.PasswordMinLength, user.UserName, user.PrimaryEmail());
        if (problem == null && user.PasswordHash != null && PasswordHasher.Verify(password, user.PasswordHash))
        {
            return "Choose a password you haven't used here.";
        }

        return problem;
    }

    /// <summary>
    /// Changes the user's own password, with their current one. Ends their other sessions. Returns
    /// the problem, or null and the updated user.
    /// </summary>
    public async Task<(string? Problem, DirectoryUser? User)> ChangePasswordAsync(string userId, string? currentPassword, string? newPassword, string? address, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(userId, cancellationToken);
        if (user?.PasswordHash == null)
        {
            return ("This account doesn't sign in with a password here.", null);
        }

        var wait = _throttle.RetryAfter(user.UserName, address);
        if (wait > TimeSpan.Zero || IsLocked(user))
        {
            return ("Too many wrong tries. Wait a while and try again.", null);
        }

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            _throttle.Failed(user.UserName, address);
            await RecordFailureAsync(userId, cancellationToken);
            return ("Your current password isn't right.", null);
        }

        var problem = CheckNewPassword(user, newPassword);
        if (problem != null)
        {
            return (problem, null);
        }

        return (null, await SetPasswordAsync(userId, newPassword!, mustChange: false, cancellationToken));
    }

    /// <summary>
    /// Sets a password and raises the session version, which ends every session the user has.
    /// </summary>
    public Task<DirectoryUser?> SetPasswordAsync(string userId, string password, bool mustChange, CancellationToken cancellationToken = default)
    {
        var hash = PasswordHasher.Hash(password, Options.PasswordIterations);
        return _directory.UpdateAsync(userId, u =>
        {
            u.PasswordHash = hash;
            u.PasswordChangedAt = Now;
            u.MustChangePassword = mustChange;
            u.FailedAttempts = 0;
            u.LockedUntil = null;
            u.SessionVersion++;
        }, cancellationToken);
    }

    /// <summary>Starts setting up an authenticator for a signed-in user. Returns the secret and its URI.</summary>
    public async Task<(string Secret, string Uri)?> BeginEnrollmentAsync(string userId, CancellationToken cancellationToken = default)
    {
        var secret = Totp.NewSecret();
        var protectedSecret = await _secrets.ProtectAsync(secret, cancellationToken);
        var user = await _directory.UpdateAsync(userId, u => u.PendingMfaSecret = protectedSecret, cancellationToken);
        return user == null ? null : (secret, Totp.Uri(Options.MfaIssuer, user.UserName, secret));
    }

    /// <summary>Confirms an authenticator with a code from it. Returns the new recovery codes, or null if the code is wrong.</summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrollmentAsync(string userId, string? code, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(userId, cancellationToken);
        if (user?.PendingMfaSecret == null)
        {
            return null;
        }

        var secret = await _secrets.UnprotectAsync(user.PendingMfaSecret, cancellationToken);
        if (Totp.Match(secret, code, _time.GetUtcNow(), 0) is not { } step)
        {
            return null;
        }

        var codes = NewRecoveryCodes();
        var pending = user.PendingMfaSecret;
        await _directory.UpdateAsync(userId, u =>
        {
            u.MfaEnabled = true;
            u.MfaSecret = pending;
            u.PendingMfaSecret = null;
            u.LastMfaStep = step;
            u.RecoveryCodeHashes = codes.Select(HashRecoveryCode).ToList();
        }, cancellationToken);
        return codes;
    }

    /// <summary>
    /// Turns the authenticator off, with a code from it or a recovery code. Refused when the
    /// account's role must have one.
    /// </summary>
    public async Task<string?> DisableMfaAsync(string userId, string? code, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(userId, cancellationToken);
        if (user is not { MfaEnabled: true })
        {
            return "There's no authenticator to remove.";
        }

        if (Options.MfaRequiredFor((await _directory.GrantForAsync(user, cancellationToken))?.Role))
        {
            return "Your role has to use an authenticator, so it can't be removed.";
        }

        if (!await VerifyCodeAsync(userId, code, cancellationToken))
        {
            await RecordFailureAsync(userId, cancellationToken);
            return "That code isn't right.";
        }

        await _directory.UpdateAsync(userId, ClearMfa, cancellationToken);
        return null;
    }

    /// <summary>New recovery codes, replacing the old, with a code from the authenticator.</summary>
    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(string userId, string? code, CancellationToken cancellationToken = default)
    {
        if (!await VerifyCodeAsync(userId, code, cancellationToken))
        {
            await RecordFailureAsync(userId, cancellationToken);
            return null;
        }

        var codes = NewRecoveryCodes();
        await _directory.UpdateAsync(userId, u => u.RecoveryCodeHashes = codes.Select(HashRecoveryCode).ToList(), cancellationToken);
        return codes;
    }

    // ---------- Administration ----------

    /// <summary>A random password that has to be changed at the next sign-in, for an administrator to pass on.</summary>
    public string NewTemporaryPassword() => RandomText(PasswordAlphabet, Math.Max(16, Options.PasswordMinLength));

    /// <summary>Sets a temporary password, ends the user's sessions and unlocks them.</summary>
    public async Task<string?> ResetPasswordAsync(string userId, CancellationToken cancellationToken = default)
    {
        var temporary = NewTemporaryPassword();
        return await SetPasswordAsync(userId, temporary, mustChange: true, cancellationToken) == null ? null : temporary;
    }

    /// <summary>Removes the user's authenticator and recovery codes, and ends their sessions.</summary>
    public Task<DirectoryUser?> ResetMfaAsync(string userId, CancellationToken cancellationToken = default) =>
        _directory.UpdateAsync(userId, u =>
        {
            ClearMfa(u);
            u.SessionVersion++;
        }, cancellationToken);

    /// <summary>Clears a lockout.</summary>
    public Task<DirectoryUser?> UnlockAsync(string userId, CancellationToken cancellationToken = default) =>
        _directory.UpdateAsync(userId, u =>
        {
            u.LockedUntil = null;
            u.FailedAttempts = 0;
        }, cancellationToken);

    /// <summary>Ends every session the user has, on every instance.</summary>
    public Task<DirectoryUser?> SignOutEverywhereAsync(string userId, CancellationToken cancellationToken = default) =>
        _directory.UpdateAsync(userId, u => u.SessionVersion++, cancellationToken);

    private static void ClearMfa(DirectoryUser user)
    {
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.PendingMfaSecret = null;
        user.RecoveryCodeHashes = new List<string>();
    }

    // ---------- Lockout ----------

    /// <summary>Whether the account is locked now.</summary>
    public bool IsLocked(DirectoryUser user) => user.LockedUntil is { } until && until > Now;

    /// <summary>Counts a wrong password or code, locking the account at the threshold. Returns whether it's now locked.</summary>
    private async Task<bool> RecordFailureAsync(string userId, CancellationToken cancellationToken)
    {
        var locked = false;
        await _directory.UpdateAsync(userId, u =>
        {
            u.FailedAttempts++;
            if (u.FailedAttempts >= Options.LockoutThreshold)
            {
                u.LockedUntil = Now.AddMinutes(Options.LockoutMinutes);
                u.FailedAttempts = 0;
                locked = true;
            }
        }, cancellationToken);
        if (locked)
        {
            _logger.LogWarning("Locked local account {UserId} for {Minutes} minutes after {Count} wrong tries", userId, Options.LockoutMinutes, Options.LockoutThreshold);
        }

        return locked;
    }

    private static SignInStep Fail(string message) => new() { Status = SignInStatus.Failed, Message = message };

    private static SignInStep InvalidChallenge() =>
        new() { Status = SignInStatus.ChallengeInvalid, Message = "That sign-in has expired or was already used. Start again." };

    private static class ChallengePurpose
    {
        public const string Mfa = "mfa";
        public const string PasswordChange = "password";
        public const string Enroll = "enroll";
    }

    /// <summary>A step of sign-in waiting for the user, as stored.</summary>
    public sealed class SignInChallenge
    {
        public required string UserId { get; set; }

        public required string Purpose { get; set; }

        public int SessionVersion { get; set; }

        public bool MfaDone { get; set; }

        public int Attempts { get; set; }

        public string? PendingSecret { get; set; }

        public DateTime ExpiresAt { get; set; }
    }
}
