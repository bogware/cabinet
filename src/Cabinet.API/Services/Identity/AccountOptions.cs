using Cabinet.Core.Models;

namespace Cabinet.API.Services.Identity;

/// <summary>Who has to use an authenticator code to sign in with a local account.</summary>
public enum MfaRequirement
{
    /// <summary>Nobody has to, though anyone can turn it on.</summary>
    Nobody,

    /// <summary>Every local account.</summary>
    Everyone,

    /// <summary>Analysts and above.</summary>
    Analyst,

    /// <summary>Admins and above.</summary>
    Admin,

    /// <summary>Super admins.</summary>
    SuperAdmin,
}

/// <summary>
/// Local accounts, their passwords and authenticator codes, the user directory and SCIM
/// (<c>Accounts:*</c>). Each is described in the settings catalog.
/// </summary>
public sealed class AccountOptions
{
    /// <summary>Whether local accounts can sign in to the dashboard.</summary>
    public bool LocalSignIn { get; set; } = true;

    /// <summary>Shortest password allowed.</summary>
    public int PasswordMinLength { get; set; } = 12;

    /// <summary>PBKDF2 iterations for new and changed passwords. Never fewer than 600,000.</summary>
    public int PasswordIterations { get; set; } = PasswordHasher.MinimumIterations;

    /// <summary>Wrong passwords or codes in a row before an account is locked.</summary>
    public int LockoutThreshold { get; set; } = 5;

    /// <summary>How long a locked account stays locked, in minutes.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Who must use an authenticator code.</summary>
    public MfaRequirement RequireMfa { get; set; } = MfaRequirement.Nobody;

    /// <summary>The name authenticator apps show beside the account.</summary>
    public string MfaIssuer { get; set; } = "Cabinet";

    /// <summary>How long each instance reuses what it read about a user, in seconds.</summary>
    public int DirectoryCacheSeconds { get; set; } = 5;

    /// <summary>Most users or groups one SCIM list returns.</summary>
    public int ScimMaxResults { get; set; } = 200;

    /// <summary>Whether a local account with this role has to use an authenticator code.</summary>
    public bool MfaRequiredFor(SystemRole? role) => RequireMfa switch
    {
        MfaRequirement.Nobody => false,
        MfaRequirement.Everyone => true,
        MfaRequirement.Analyst => role >= SystemRole.Analyst,
        MfaRequirement.Admin => role >= SystemRole.Admin,
        MfaRequirement.SuperAdmin => role >= SystemRole.SuperAdmin,
        _ => false,
    };

    /// <summary>The settings with values out of range brought into it.</summary>
    public AccountOptions Normalized() => new()
    {
        LocalSignIn = LocalSignIn,
        PasswordMinLength = Math.Clamp(PasswordMinLength, 8, 256),
        PasswordIterations = Math.Clamp(PasswordIterations, PasswordHasher.MinimumIterations, 10_000_000),
        LockoutThreshold = Math.Clamp(LockoutThreshold, 1, 100),
        LockoutMinutes = Math.Clamp(LockoutMinutes, 1, 24 * 60),
        RequireMfa = RequireMfa,
        MfaIssuer = string.IsNullOrWhiteSpace(MfaIssuer) ? "Cabinet" : MfaIssuer.Trim(),
        DirectoryCacheSeconds = Math.Clamp(DirectoryCacheSeconds, 0, 300),
        ScimMaxResults = Math.Clamp(ScimMaxResults, 10, 1000),
    };
}
