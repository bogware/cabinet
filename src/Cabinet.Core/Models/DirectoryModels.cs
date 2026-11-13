namespace Cabinet.Core.Models;

/// <summary>Where a directory user came from, which decides how they sign in.</summary>
public enum UserSource
{
    /// <summary>Made in Cabinet, with a password for signing in here.</summary>
    Local,

    /// <summary>Provisioned by an identity provider over SCIM; signs in through single sign-on.</summary>
    Scim,

    /// <summary>Made in Cabinet without a password, to give a single sign-on user a role or to deactivate them.</summary>
    Sso,
}

/// <summary>One of a user's email addresses, as SCIM describes them.</summary>
public sealed class DirectoryEmail
{
    /// <summary>The address.</summary>
    public required string Value { get; set; }

    /// <summary>Its kind, such as work or home.</summary>
    public string? Type { get; set; }

    /// <summary>Whether it's the user's main address.</summary>
    public bool Primary { get; set; }
}

/// <summary>
/// A person in Cabinet's user directory: a local account with a password, a user an identity
/// provider provisioned over SCIM, or a single sign-on user an administrator added. Kept in the
/// shared document store, so every instance and every backend sees the same directory.
/// </summary>
public sealed class DirectoryUser
{
    /// <summary>A ULID.</summary>
    public required string Id { get; set; }

    /// <summary>Unique, compared without regard to case.</summary>
    public required string UserName { get; set; }

    /// <summary>The name shown for the user.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Given name, from SCIM's name.givenName.</summary>
    public string? GivenName { get; set; }

    /// <summary>Family name, from SCIM's name.familyName.</summary>
    public string? FamilyName { get; set; }

    /// <summary>Job title.</summary>
    public string? Title { get; set; }

    /// <summary>Department, from SCIM's enterprise extension.</summary>
    public string? Department { get; set; }

    /// <summary>Email addresses. The primary one, or else the first, is the user's email.</summary>
    public List<DirectoryEmail> Emails { get; set; } = new();

    /// <summary>A deactivated user can't sign in, and their sessions and tokens stop working.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Where the user came from.</summary>
    public UserSource Source { get; set; } = UserSource.Local;

    /// <summary>The identity provider's ID for the user, such as Entra's object ID or Okta's user ID.</summary>
    public string? ExternalId { get; set; }

    /// <summary>A role given to the user directly, beside any their groups give.</summary>
    public SystemRole? Role { get; set; }

    /// <summary>Index permissions given to the user directly.</summary>
    public List<IndexPermission> IndexPermissions { get; set; } = new();

    /// <summary>The password's PBKDF2 hash, or null for users who can't sign in with a password.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>When the password last changed.</summary>
    public DateTime? PasswordChangedAt { get; set; }

    /// <summary>The user must choose a new password before doing anything else.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Whether sign-in asks for an authenticator code.</summary>
    public bool MfaEnabled { get; set; }

    /// <summary>The TOTP secret, encrypted.</summary>
    public string? MfaSecret { get; set; }

    /// <summary>A TOTP secret being enrolled and not yet confirmed, encrypted.</summary>
    public string? PendingMfaSecret { get; set; }

    /// <summary>The last time step a code was accepted for, so the same code can't be used twice.</summary>
    public long LastMfaStep { get; set; }

    /// <summary>SHA-256 hashes of the recovery codes not yet used.</summary>
    public List<string> RecoveryCodeHashes { get; set; } = new();

    /// <summary>Wrong passwords or codes since the last success or lockout.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Until when sign-in is refused after too many failures.</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>Sessions carry this; raising it ends every session the user has.</summary>
    public int SessionVersion { get; set; } = 1;

    /// <summary>When the user was added.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user last changed.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the user last signed in to the dashboard.</summary>
    public DateTime? LastSignInAt { get; set; }

    /// <summary>The primary email, or else the first.</summary>
    public string? PrimaryEmail() =>
        (Emails.FirstOrDefault(email => email.Primary) ?? Emails.FirstOrDefault())?.Value;

    /// <summary>Makes this the user's only, primary email; null or empty removes them all.</summary>
    public void SetEmail(string? email)
    {
        Emails = string.IsNullOrWhiteSpace(email)
            ? new List<DirectoryEmail>()
            : new List<DirectoryEmail> { new() { Value = email.Trim(), Type = "work", Primary = true } };
    }
}

/// <summary>
/// A group in the user directory. Its members are directory users, and it can grant them a role
/// and index permissions: the group-to-role mapping, which is managed in Cabinet even when an
/// identity provider provisions the group.
/// </summary>
public sealed class DirectoryGroup
{
    /// <summary>A ULID.</summary>
    public required string Id { get; set; }

    /// <summary>The group's name.</summary>
    public required string DisplayName { get; set; }

    /// <summary>The identity provider's ID for the group.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Where the group came from.</summary>
    public UserSource Source { get; set; } = UserSource.Local;

    /// <summary>IDs of the users in the group.</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>The role the group gives its members, if any.</summary>
    public SystemRole? Role { get; set; }

    /// <summary>Index permissions the group gives its members.</summary>
    public List<IndexPermission> IndexPermissions { get; set; } = new();

    /// <summary>When the group was added.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the group last changed.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>What a directory user may do, from their own role and their groups'.</summary>
public sealed class DirectoryGrant
{
    /// <summary>The highest role given.</summary>
    public required SystemRole Role { get; init; }

    /// <summary>
    /// The union of the index permissions given. Empty means the role applies to every index.
    /// </summary>
    public required List<IndexPermission> IndexPermissions { get; init; }

    /// <summary>
    /// Combines grants: the highest role wins and index permissions are unioned. A grant with no
    /// index permissions gives its role's data capabilities on every index, so it's kept as
    /// <c>*</c> when another grant does limit indexes.
    /// </summary>
    public static DirectoryGrant? Combine(IEnumerable<(SystemRole Role, IReadOnlyList<IndexPermission> Permissions)> grants)
    {
        var list = grants.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var role = list.Max(grant => grant.Role);
        if (list.All(grant => grant.Permissions.Count == 0))
        {
            return new DirectoryGrant { Role = role, IndexPermissions = new List<IndexPermission>() };
        }

        var permissions = new List<IndexPermission>();
        foreach (var (grantRole, grantPermissions) in list)
        {
            if (grantPermissions.Count == 0)
            {
                var everywhere = RbacPrincipal.GetRoleCapabilities(grantRole) & RbacPrincipal.IndexScopedCapabilities;
                if (everywhere != Capability.None)
                {
                    permissions.Add(new IndexPermission { IndexPattern = "*", Capabilities = everywhere });
                }

                continue;
            }

            permissions.AddRange(grantPermissions.Select(permission => new IndexPermission { IndexPattern = permission.IndexPattern, Capabilities = permission.Capabilities }));
        }

        // One entry per pattern, with every capability any grant gave it
        var merged = permissions
            .GroupBy(permission => permission.IndexPattern, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IndexPermission { IndexPattern = group.First().IndexPattern, Capabilities = group.Aggregate(Capability.None, (all, permission) => all | permission.Capabilities) })
            .ToList();
        return new DirectoryGrant { Role = role, IndexPermissions = merged };
    }
}
