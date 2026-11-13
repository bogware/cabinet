using System.Security.Claims;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Models;

namespace Cabinet.API.Authentication;

/// <summary>
/// The claims that tie a session or token to a user in the directory, so that deactivating the
/// user, resetting their password or signing them out everywhere ends it.
/// </summary>
public static class DirectoryClaims
{
    /// <summary>The directory user's ID.</summary>
    public const string UserId = "cabinet.uid";

    /// <summary>The user's session version when the session began.</summary>
    public const string SessionVersion = "cabinet.sv";

    /// <summary>How local accounts' client IDs begin.</summary>
    public const string LocalPrefix = "local-";

    /// <summary>
    /// Removes directory claims an identity provider sent, so nobody can name a directory user
    /// for themselves through a user attribute.
    /// </summary>
    public static void Strip(ClaimsIdentity identity)
    {
        foreach (var claim in identity.Claims.Where(claim => claim.Type is UserId or SessionVersion).ToList())
        {
            identity.RemoveClaim(claim);
        }
    }

    /// <summary>Ties an identity to a directory user, and for a session, to their session version.</summary>
    public static void Link(ClaimsIdentity identity, DirectoryUser user, bool session)
    {
        Strip(identity);
        identity.AddClaim(new Claim(UserId, user.Id));
        if (session)
        {
            identity.AddClaim(new Claim(SessionVersion, user.SessionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>A local account's session identity.</summary>
    public static ClaimsIdentity ForLocalUser(DirectoryUser user, DirectoryGrant grant)
    {
        var identity = new ClaimsIdentity("local");
        identity.AddClaim(new Claim("ClientId", LocalPrefix + user.Id));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id));
        identity.AddClaim(new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName));
        identity.AddClaim(new Claim("preferred_username", user.UserName));
        if (user.PrimaryEmail() is { } email)
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, email));
        }

        identity.AddClaim(new Claim(PrincipalResolver.RoleClaim, grant.Role.ToString()));
        foreach (var permission in grant.IndexPermissions)
        {
            identity.AddClaim(new Claim(PrincipalResolver.IndexPermissionClaim, $"{permission.IndexPattern}:{permission.Capabilities}"));
        }

        identity.AddClaim(new Claim("Tier", TierFor(grant.Role)));
        identity.AddClaim(new Claim("Permission", grant.Role >= SystemRole.Analyst ? "Write" : "Read"));
        Link(identity, user, session: true);
        return identity;
    }

    /// <summary>The rate-limit tier for a role, as for single sign-on users.</summary>
    public static string TierFor(SystemRole role) => role switch
    {
        SystemRole.SuperAdmin or SystemRole.Admin => "Enterprise",
        SystemRole.Analyst => "Professional",
        _ => "Basic",
    };

    /// <summary>
    /// Whether a session tied to a directory user may continue: the user exists, is active, and
    /// their session version hasn't moved since it began.
    /// </summary>
    public static async Task<bool> SessionIsValidAsync(ClaimsPrincipal principal, UserDirectory? directory, CancellationToken cancellationToken = default)
    {
        var id = principal.FindFirst(UserId)?.Value;
        if (id == null)
        {
            return true;
        }

        if (directory == null || !int.TryParse(principal.FindFirst(SessionVersion)?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        var user = await directory.GetAsync(id, cancellationToken);
        return user is { Active: true } && user.SessionVersion == version;
    }

    /// <summary>
    /// Ties a single sign-on identity to its directory user, if there is one. Returns false when
    /// that user is deactivated, so the sign-in or token must be refused.
    /// </summary>
    public static async Task<bool> LinkSsoAsync(ClaimsIdentity identity, UserDirectory? directory, bool session, CancellationToken cancellationToken = default)
    {
        Strip(identity);
        if (directory == null)
        {
            return true;
        }

        var callerKey = identity.FindFirst("ClientId")?.Value;
        var user = session || callerKey == null
            ? await directory.MatchAsync(identity, cancellationToken)
            : await directory.MatchCachedAsync(callerKey, identity, cancellationToken);
        if (user == null)
        {
            return true;
        }

        if (!user.Active)
        {
            return false;
        }

        Link(identity, user, session);
        if (session)
        {
            await directory.UpdateAsync(user.Id, u => u.LastSignInAt = DateTime.UtcNow, cancellationToken);
        }

        return true;
    }
}
