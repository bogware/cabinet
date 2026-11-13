using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Signing in to the dashboard with a local account (a password, then an authenticator code when
/// the account has one), and the account's own settings: its password and authenticator.
/// </summary>
[ApiController]
[Route("auth")]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AccountController : ControllerBase
{
    private readonly LocalAccountService _accounts;
    private readonly UserDirectory _directory;
    private readonly IAuditLogger _audit;

    public AccountController(LocalAccountService accounts, UserDirectory directory, IAuditLogger audit)
    {
        _accounts = accounts;
        _directory = directory;
        _audit = audit;
    }

    private string? Address => HttpContext.Connection.RemoteIpAddress?.ToString();

    // The sign-in steps have no session yet, so the header that shows a request came from the
    // dashboard is checked here: another site's page can't sign someone in to an account it chose
    private bool FromTheDashboard => Request.Headers.ContainsKey(BrowserSession.RequestHeader);

    /// <summary>Signs in with a local account's user name and password.</summary>
    [HttpPost("login/local")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LocalLoginRequest request, CancellationToken cancellationToken)
    {
        if (!_directory.Options.LocalSignIn)
        {
            return NotFound();
        }

        if (!FromTheDashboard)
        {
            return BadRequest(new { message = $"Send the {BrowserSession.RequestHeader} header" });
        }

        var step = await _accounts.SignInAsync(request.UserName, request.Password, Address, cancellationToken);
        return await RespondAsync(step, AuditName.Sanitize(request.UserName), "password");
    }

    /// <summary>Completes sign-in with an authenticator code or a recovery code.</summary>
    [HttpPost("login/mfa")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginMfa([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!_directory.Options.LocalSignIn)
        {
            return NotFound();
        }

        if (!FromTheDashboard)
        {
            return BadRequest(new { message = $"Send the {BrowserSession.RequestHeader} header" });
        }

        var step = await _accounts.CompleteMfaAsync(request.Challenge, request.Code, Address, cancellationToken);
        return await RespondAsync(step, null, "authenticator code");
    }

    /// <summary>Chooses a new password when the account has to before signing in.</summary>
    [HttpPost("login/password")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginPassword([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!_directory.Options.LocalSignIn)
        {
            return NotFound();
        }

        if (!FromTheDashboard)
        {
            return BadRequest(new { message = $"Send the {BrowserSession.RequestHeader} header" });
        }

        var step = await _accounts.CompletePasswordChangeAsync(request.Challenge, request.NewPassword, cancellationToken);
        if (step.Status != SignInStatus.Rejected && step.Status != SignInStatus.ChallengeInvalid)
        {
            await AuditAsync(step.User?.Id is { } id ? DirectoryClaims.LocalPrefix + id : "local-unknown", AuditAction.Update, "Chose a new password at sign-in", true);
        }

        return await RespondAsync(step, null, "new password");
    }

    /// <summary>Starts setting up an authenticator when the account has to have one before signing in.</summary>
    [HttpPost("login/mfa/enroll")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginEnroll([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!_directory.Options.LocalSignIn)
        {
            return NotFound();
        }

        if (!FromTheDashboard)
        {
            return BadRequest(new { message = $"Send the {BrowserSession.RequestHeader} header" });
        }

        var step = await _accounts.StartEnrollmentAsync(request.Challenge, cancellationToken);
        return step.Status == SignInStatus.MfaEnrollmentRequired
            ? Ok(new { status = "mfa-enrollment", challenge = step.Challenge, secret = step.Secret, otpauthUri = step.OtpAuthUri })
            : await RespondAsync(step, null, "authenticator setup");
    }

    /// <summary>Confirms the new authenticator with a code from it, and signs in.</summary>
    [HttpPost("login/mfa/enroll/confirm")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginEnrollConfirm([FromBody] ChallengeRequest request, CancellationToken cancellationToken)
    {
        if (!_directory.Options.LocalSignIn)
        {
            return NotFound();
        }

        if (!FromTheDashboard)
        {
            return BadRequest(new { message = $"Send the {BrowserSession.RequestHeader} header" });
        }

        var step = await _accounts.CompleteEnrollmentAsync(request.Challenge, request.Code, cancellationToken);
        return await RespondAsync(step, null, "authenticator setup");
    }

    private async Task<IActionResult> RespondAsync(SignInStep step, string? attemptedName, string what)
    {
        var who = step.User?.Id is { } id ? DirectoryClaims.LocalPrefix + id : $"local-{attemptedName ?? "unknown"}";
        switch (step.Status)
        {
            case SignInStatus.SignedIn:
                await BrowserSession.SignInAsync(HttpContext, DirectoryClaims.ForLocalUser(step.User!, step.Grant!));
                await AuditAsync(who, AuditAction.AuthSuccess, "Signed in with a local account", true);
                return Ok(new
                {
                    status = "signed-in",
                    name = step.User!.DisplayName ?? step.User.UserName,
                    recoveryCodes = step.RecoveryCodes,
                });
            case SignInStatus.MfaRequired:
                return Ok(new { status = "mfa-required", challenge = step.Challenge });
            case SignInStatus.PasswordChangeRequired:
                return Ok(new { status = "password-change-required", challenge = step.Challenge, message = step.Message, minLength = _directory.Options.PasswordMinLength });
            case SignInStatus.MfaEnrollmentRequired:
                return Ok(new { status = "mfa-enrollment-required", challenge = step.Challenge, message = step.Message });
            case SignInStatus.Rejected:
                return BadRequest(new { status = "rejected", challenge = step.Challenge, message = step.Message });
            case SignInStatus.Throttled:
                Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(step.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await AuditAsync(who, AuditAction.AuthFailure, $"Local sign-in slowed after repeated failures ({what})", false);
                return StatusCode(StatusCodes.Status429TooManyRequests, new { message = step.Message, retryAfter = Math.Ceiling(step.RetryAfter.TotalSeconds) });
            case SignInStatus.Disabled:
                await AuditAsync(who, AuditAction.AuthFailure, "Local sign-in refused: the account is deactivated", false);
                return StatusCode(StatusCodes.Status403Forbidden, new { message = step.Message });
            case SignInStatus.NoAccess:
                await AuditAsync(who, AuditAction.AuthFailure, "Local sign-in has no role in Cabinet", false);
                return StatusCode(StatusCodes.Status403Forbidden, new { message = step.Message });
            case SignInStatus.ChallengeInvalid:
                return Unauthorized(new { message = step.Message });
            default:
                await AuditAsync(who, AuditAction.AuthFailure, $"Local sign-in failed: wrong {what}", false);
                return Unauthorized(new { message = step.Message });
        }
    }

    // ---------- The signed-in account ----------

    /// <summary>The signed-in local account.</summary>
    [HttpGet("account")]
    [RequireLocalAccount]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await _directory.GetFreshAsync(RequireLocalAccountAttribute.UserIdOf(HttpContext)!, cancellationToken);
        if (user == null)
        {
            return NotFound();
        }

        var grant = await _directory.GrantForAsync(user, cancellationToken);
        return Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            email = user.PrimaryEmail(),
            role = grant?.Role.ToString(),
            mfaEnabled = user.MfaEnabled,
            mfaRequired = _directory.Options.MfaRequiredFor(grant?.Role),
            recoveryCodesLeft = user.RecoveryCodeHashes.Count,
            passwordChangedAt = user.PasswordChangedAt,
            passwordMinLength = _directory.Options.PasswordMinLength,
        });
    }

    /// <summary>Changes the account's password. Other sessions end; this one continues.</summary>
    [HttpPost("account/password")]
    [RequireLocalAccount]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireLocalAccountAttribute.UserIdOf(HttpContext)!;
        var (problem, user) = await _accounts.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, Address, cancellationToken);
        if (problem != null || user == null)
        {
            await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, "Password change refused", false);
            return BadRequest(new { message = problem ?? "The password couldn't be changed" });
        }

        await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, "Changed own password; other sessions ended", true);
        return await ContinueSessionAsync(user, cancellationToken);
    }

    /// <summary>Starts setting up an authenticator app. Returns the secret and an otpauth:// URI.</summary>
    [HttpPost("account/mfa/enroll")]
    [RequireLocalAccount]
    public async Task<IActionResult> Enroll(CancellationToken cancellationToken)
    {
        var started = await _accounts.BeginEnrollmentAsync(RequireLocalAccountAttribute.UserIdOf(HttpContext)!, cancellationToken);
        return started is { } enrollment
            ? Ok(new { secret = enrollment.Secret, otpauthUri = enrollment.Uri })
            : NotFound();
    }

    /// <summary>Confirms the authenticator with a code from it. Returns recovery codes, shown once.</summary>
    [HttpPost("account/mfa/confirm")]
    [RequireLocalAccount]
    public async Task<IActionResult> ConfirmEnroll([FromBody] CodeRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireLocalAccountAttribute.UserIdOf(HttpContext)!;
        var codes = await _accounts.ConfirmEnrollmentAsync(userId, request.Code, cancellationToken);
        if (codes == null)
        {
            return BadRequest(new { message = "That code isn't right. Check the time on your device and try the newest code." });
        }

        await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, "Set up an authenticator app", true);
        return Ok(new { recoveryCodes = codes });
    }

    /// <summary>Replaces the recovery codes, with a code from the authenticator.</summary>
    [HttpPost("account/mfa/recovery-codes")]
    [RequireLocalAccount]
    public async Task<IActionResult> RecoveryCodes([FromBody] CodeRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireLocalAccountAttribute.UserIdOf(HttpContext)!;
        var codes = await _accounts.RegenerateRecoveryCodesAsync(userId, request.Code, cancellationToken);
        if (codes == null)
        {
            return BadRequest(new { message = "That code isn't right." });
        }

        await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, "Made new recovery codes", true);
        return Ok(new { recoveryCodes = codes });
    }

    /// <summary>Removes the authenticator, with a code from it or a recovery code.</summary>
    [HttpPost("account/mfa/disable")]
    [RequireLocalAccount]
    public async Task<IActionResult> DisableMfa([FromBody] CodeRequest request, CancellationToken cancellationToken)
    {
        var userId = RequireLocalAccountAttribute.UserIdOf(HttpContext)!;
        var problem = await _accounts.DisableMfaAsync(userId, request.Code, cancellationToken);
        await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, problem == null ? "Removed their authenticator app" : $"Authenticator removal refused: {problem}", problem == null);
        return problem == null ? NoContent() : BadRequest(new { message = problem });
    }

    /// <summary>Ends every session of the account, this one included.</summary>
    [HttpPost("account/sign-out-everywhere")]
    [RequireLocalAccount]
    public async Task<IActionResult> SignOutEverywhere(CancellationToken cancellationToken)
    {
        var userId = RequireLocalAccountAttribute.UserIdOf(HttpContext)!;
        await _accounts.SignOutEverywhereAsync(userId, cancellationToken);
        await HttpContext.SignOutAsync(BrowserSession.Scheme);
        await AuditAsync(DirectoryClaims.LocalPrefix + userId, AuditAction.Update, "Signed out everywhere", true);
        return NoContent();
    }

    // After a password change raised the session version, this session needs the new one
    private async Task<IActionResult> ContinueSessionAsync(DirectoryUser user, CancellationToken cancellationToken)
    {
        var grant = await _directory.GrantForAsync(user, cancellationToken);
        if (grant == null)
        {
            await HttpContext.SignOutAsync(BrowserSession.Scheme);
            return NoContent();
        }

        await BrowserSession.SignInAsync(HttpContext, DirectoryClaims.ForLocalUser(user, grant));
        return NoContent();
    }

    private Task AuditAsync(string principal, AuditAction action, string description, bool success) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = principal,
            Action = action,
            ResourceType = "Session",
            Description = description,
            SourceIp = Address,
            Success = success,
        });
}

/// <summary>A local sign-in.</summary>
public sealed class LocalLoginRequest
{
    public string? UserName { get; set; }

    public string? Password { get; set; }
}

/// <summary>A step of sign-in after the password.</summary>
public sealed class ChallengeRequest
{
    public string? Challenge { get; set; }

    public string? Code { get; set; }

    public string? NewPassword { get; set; }
}

/// <summary>A change of one's own password.</summary>
public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }

    public string? NewPassword { get; set; }
}

/// <summary>An authenticator or recovery code.</summary>
public sealed class CodeRequest
{
    public string? Code { get; set; }
}
