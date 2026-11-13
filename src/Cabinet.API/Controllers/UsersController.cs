using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using Cabinet.API.Services.Identity;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// The user directory: local accounts, users provisioned over SCIM, and single sign-on users given
/// a role here. Every change is audited.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
[RequireCapability(Capability.UserManagement)]
public sealed class UsersController : ControllerBase
{
    private readonly UserDirectory _directory;
    private readonly LocalAccountService _accounts;
    private readonly IAuditLogger _audit;

    public UsersController(UserDirectory directory, LocalAccountService accounts, IAuditLogger audit)
    {
        _directory = directory;
        _accounts = accounts;
        _audit = audit;
    }

    private string Caller => User.FindFirst("ClientId")?.Value ?? "unknown";

    private bool IsCaller(string id) => Caller == DirectoryClaims.LocalPrefix + id;

    private Task AuditAsync(AuditAction action, string id, string description, CancellationToken cancellationToken) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = Caller,
            Action = action,
            ResourceType = "User",
            ResourceId = id,
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, cancellationToken);

    /// <summary>
    /// Every user, with their groups and what the directory gives them
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<UserView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default)
    {
        var groups = await _directory.ListGroupsAsync(cancellationToken);
        var views = new List<UserView>();
        foreach (var user in await _directory.ListAsync(cancellationToken))
        {
            views.Add(UserView.From(user, groups, await _directory.GrantForAsync(user, cancellationToken), _accounts.IsLocked(user)));
        }

        return Ok(views);
    }

    /// <summary>
    /// One user
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(UserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(id, cancellationToken);
        return user == null ? Problem(detail: "No such user", statusCode: 404) : Ok(await ViewAsync(user, cancellationToken));
    }

    private async Task<UserView> ViewAsync(DirectoryUser user, CancellationToken cancellationToken) =>
        UserView.From(user, await _directory.ListGroupsAsync(cancellationToken), await _directory.GrantForAsync(user, cancellationToken), _accounts.IsLocked(user));

    /// <summary>
    /// Add a user. A local account without a password gets a temporary one, returned this once,
    /// that must be changed at the first sign-in. A single sign-on user has no password.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreatedUser), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        if (!RoleText.TryParse(request.Role, out var role) || !PermissionText.TryParse(request.IndexPermissions, out var permissions))
        {
            return Problem(detail: RoleText.Help, statusCode: 400);
        }

        var user = new DirectoryUser
        {
            Id = string.Empty,
            UserName = request.UserName?.Trim() ?? string.Empty,
            DisplayName = Clean(request.DisplayName),
            Active = request.Active,
            Source = request.Sso ? UserSource.Sso : UserSource.Local,
            ExternalId = Clean(request.ExternalId),
            Role = role,
            IndexPermissions = permissions,
        };
        user.SetEmail(Clean(request.Email));

        string? temporary = null;
        if (!request.Sso)
        {
            var password = request.Password;
            if (string.IsNullOrEmpty(password))
            {
                temporary = password = _accounts.NewTemporaryPassword();
            }
            else if (_accounts.CheckNewPassword(user, password) is { } problem)
            {
                return Problem(detail: problem, statusCode: 400);
            }

            user.PasswordHash = PasswordHasher.Hash(password, _directory.Options.PasswordIterations);
            user.PasswordChangedAt = DateTime.UtcNow;
            user.MustChangePassword = temporary != null || request.MustChangePassword;
        }

        try
        {
            user = await _directory.CreateAsync(user, cancellationToken);
            if (request.Groups is { Count: > 0 } groups)
            {
                await _directory.SetUserGroupsAsync(user.Id, groups, cancellationToken);
            }
        }
        catch (DirectoryConflictException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }

        await AuditAsync(AuditAction.Create, user.Id, $"Added {user.Source} user '{AuditName.Sanitize(user.UserName)}' with role {role?.ToString() ?? "none"}", cancellationToken);
        var view = await ViewAsync(user, cancellationToken);
        return Created($"/api/v1/users/{user.Id}", new CreatedUser { User = view, TemporaryPassword = temporary });
    }

    /// <summary>
    /// Change a user. Display name, email, active, role and index permissions are replaced;
    /// user name, external ID and groups are kept when left out. Deactivating someone ends their
    /// sessions.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(UserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        if (!RoleText.TryParse(request.Role, out var role) || !PermissionText.TryParse(request.IndexPermissions, out var permissions))
        {
            return Problem(detail: RoleText.Help, statusCode: 400);
        }

        if (IsCaller(id) && (!request.Active || role is not SystemRole.SuperAdmin))
        {
            return Problem(detail: "That's you. Ask another administrator to deactivate you or change your role.", statusCode: 409);
        }

        DirectoryUser? user;
        try
        {
            user = await _directory.UpdateAsync(id, u =>
            {
                if (!string.IsNullOrWhiteSpace(request.UserName))
                {
                    u.UserName = request.UserName;
                }

                if (request.ExternalId != null)
                {
                    u.ExternalId = Clean(request.ExternalId);
                }

                u.DisplayName = Clean(request.DisplayName);
                if (!string.Equals(u.PrimaryEmail(), Clean(request.Email), StringComparison.Ordinal))
                {
                    u.SetEmail(Clean(request.Email));
                }

                u.Active = request.Active;
                u.Role = role;
                u.IndexPermissions = permissions;
            }, cancellationToken);
            if (user != null && request.Groups != null)
            {
                await _directory.SetUserGroupsAsync(id, request.Groups, cancellationToken);
            }
        }
        catch (DirectoryConflictException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }

        if (user == null)
        {
            return Problem(detail: "No such user", statusCode: 404);
        }

        await AuditAsync(AuditAction.Update, id, $"Changed user '{AuditName.Sanitize(user.UserName)}': active {user.Active}, role {role?.ToString() ?? "none"}", cancellationToken);
        return Ok(await ViewAsync(user, cancellationToken));
    }

    /// <summary>
    /// Remove a user. Their sessions end. To keep the record, deactivate them instead.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        if (IsCaller(id))
        {
            return Problem(detail: "That's you. Ask another administrator to remove you.", statusCode: 409);
        }

        var user = await _directory.GetFreshAsync(id, cancellationToken);
        if (user == null || !await _directory.DeleteAsync(id, cancellationToken))
        {
            return Problem(detail: "No such user", statusCode: 404);
        }

        await AuditAsync(AuditAction.Delete, id, $"Removed user '{AuditName.Sanitize(user.UserName)}'", cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Give a local account a temporary password, returned this once, that must be changed at the
    /// next sign-in. Ends the user's sessions and clears a lockout.
    /// </summary>
    [HttpPost("{id}/reset-password")]
    [ProducesResponseType(typeof(TemporaryPassword), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken cancellationToken = default)
    {
        var user = await _directory.GetFreshAsync(id, cancellationToken);
        if (user == null)
        {
            return Problem(detail: "No such user", statusCode: 404);
        }

        if (user.Source != UserSource.Local)
        {
            return Problem(detail: "This user signs in through single sign-on, so has no password here", statusCode: 409);
        }

        var temporary = await _accounts.ResetPasswordAsync(id, cancellationToken);
        await AuditAsync(AuditAction.Update, id, $"Reset the password of '{AuditName.Sanitize(user.UserName)}'; their sessions ended", cancellationToken);
        return Ok(new TemporaryPassword { Password = temporary! });
    }

    /// <summary>
    /// Remove a user's authenticator and recovery codes, for someone who lost their device. Ends
    /// their sessions.
    /// </summary>
    [HttpPost("{id}/reset-mfa")]
    [ProducesResponseType(typeof(UserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ResetMfa(string id, CancellationToken cancellationToken = default) =>
        ActAsync(id, _accounts.ResetMfaAsync, "Reset the authenticator of '{0}'; their sessions ended", cancellationToken);

    /// <summary>
    /// Clear a lockout after too many wrong passwords or codes
    /// </summary>
    [HttpPost("{id}/unlock")]
    [ProducesResponseType(typeof(UserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Unlock(string id, CancellationToken cancellationToken = default) =>
        ActAsync(id, _accounts.UnlockAsync, "Unlocked '{0}'", cancellationToken);

    /// <summary>
    /// End every session a user has, on every instance
    /// </summary>
    [HttpPost("{id}/sign-out")]
    [ProducesResponseType(typeof(UserView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> SignOutEverywhere(string id, CancellationToken cancellationToken = default) =>
        ActAsync(id, _accounts.SignOutEverywhereAsync, "Signed '{0}' out everywhere", cancellationToken);

    private async Task<IActionResult> ActAsync(string id, Func<string, CancellationToken, Task<DirectoryUser?>> action, string description, CancellationToken cancellationToken)
    {
        var user = await action(id, cancellationToken);
        if (user == null)
        {
            return Problem(detail: "No such user", statusCode: 404);
        }

        await AuditAsync(AuditAction.Update, id, string.Format(System.Globalization.CultureInfo.InvariantCulture, description, AuditName.Sanitize(user.UserName)), cancellationToken);
        return Ok(await ViewAsync(user, cancellationToken));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Groups in the user directory, and the role and index permissions each gives its members.
/// Groups an identity provider provisions over SCIM appear here too, so their mapping to a role is
/// managed in Cabinet.
/// </summary>
[ApiController]
[Route("api/v1/groups")]
[Authorize]
[RequireCapability(Capability.UserManagement)]
public sealed class GroupsController : ControllerBase
{
    private readonly UserDirectory _directory;
    private readonly IAuditLogger _audit;

    public GroupsController(UserDirectory directory, IAuditLogger audit)
    {
        _directory = directory;
        _audit = audit;
    }

    private Task AuditAsync(AuditAction action, string id, string description, CancellationToken cancellationToken) =>
        _audit.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = action,
            ResourceType = "Group",
            ResourceId = id,
            Description = description,
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        }, cancellationToken);

    /// <summary>
    /// Every group
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<GroupView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken = default) =>
        Ok((await _directory.ListGroupsAsync(cancellationToken)).Select(GroupView.From).ToList());

    /// <summary>
    /// One group
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(GroupView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken = default)
    {
        var group = await _directory.GetGroupAsync(id, cancellationToken);
        return group == null ? Problem(detail: "No such group", statusCode: 404) : Ok(GroupView.From(group));
    }

    /// <summary>
    /// Add a group, with the role and index permissions it gives its members
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(GroupView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] SaveGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (!RoleText.TryParse(request.Role, out var role) || !PermissionText.TryParse(request.IndexPermissions, out var permissions))
        {
            return Problem(detail: RoleText.Help, statusCode: 400);
        }

        try
        {
            var group = await _directory.CreateGroupAsync(new DirectoryGroup
            {
                Id = string.Empty,
                DisplayName = request.DisplayName ?? string.Empty,
                Role = role,
                IndexPermissions = permissions,
                Members = request.Members ?? new List<string>(),
            }, cancellationToken);
            await AuditAsync(AuditAction.Create, group.Id, $"Added group '{AuditName.Sanitize(group.DisplayName)}' giving role {role?.ToString() ?? "none"}", cancellationToken);
            return Created($"/api/v1/groups/{group.Id}", GroupView.From(group));
        }
        catch (DirectoryConflictException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }
    }

    /// <summary>
    /// Change a group. Name, role and index permissions are replaced; members are kept when left out.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(GroupView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(string id, [FromBody] SaveGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (!RoleText.TryParse(request.Role, out var role) || !PermissionText.TryParse(request.IndexPermissions, out var permissions))
        {
            return Problem(detail: RoleText.Help, statusCode: 400);
        }

        try
        {
            var group = await _directory.UpdateGroupAsync(id, g =>
            {
                if (!string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    g.DisplayName = request.DisplayName;
                }

                g.Role = role;
                g.IndexPermissions = permissions;
                if (request.Members != null)
                {
                    g.Members = request.Members.ToList();
                }
            }, cancellationToken);
            if (group == null)
            {
                return Problem(detail: "No such group", statusCode: 404);
            }

            await AuditAsync(AuditAction.Update, id, $"Changed group '{AuditName.Sanitize(group.DisplayName)}': role {role?.ToString() ?? "none"}, {group.Members.Count} members", cancellationToken);
            return Ok(GroupView.From(group));
        }
        catch (DirectoryConflictException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400);
        }
    }

    /// <summary>
    /// Remove a group. Its members stay, without what it gave them.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken = default)
    {
        var group = await _directory.GetGroupAsync(id, cancellationToken);
        if (group == null || !await _directory.DeleteGroupAsync(id, cancellationToken))
        {
            return Problem(detail: "No such group", statusCode: 404);
        }

        await AuditAsync(AuditAction.Delete, id, $"Removed group '{AuditName.Sanitize(group.DisplayName)}'", cancellationToken);
        return NoContent();
    }
}

/// <summary>Roles as text: a name, or empty for none.</summary>
internal static class RoleText
{
    public static readonly string Help = $"Role must be empty or one of {string.Join(", ", Enum.GetNames<SystemRole>())}, and capabilities from {string.Join(", ", Enum.GetNames<Capability>())}";

    public static bool TryParse(string? text, out SystemRole? role)
    {
        role = null;
        if (string.IsNullOrWhiteSpace(text) || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Enum.TryParse<SystemRole>(text, true, out var parsed) && Enum.IsDefined(parsed))
        {
            role = parsed;
            return true;
        }

        return false;
    }
}

/// <summary>Index permissions as the API takes them.</summary>
internal static class PermissionText
{
    public static bool TryParse(List<IndexPermissionDto>? permissions, out List<IndexPermission> parsed)
    {
        parsed = new List<IndexPermission>();
        foreach (var permission in permissions ?? new List<IndexPermissionDto>())
        {
            if (string.IsNullOrWhiteSpace(permission.IndexPattern) || permission.IndexPattern.Length > 256)
            {
                return false;
            }

            var capabilities = Capability.None;
            foreach (var name in permission.Capabilities)
            {
                if (!Enum.TryParse<Capability>(name, true, out var capability))
                {
                    return false;
                }

                capabilities |= capability;
            }

            parsed.Add(new IndexPermission { IndexPattern = permission.IndexPattern.Trim(), Capabilities = capabilities });
        }

        return true;
    }

    public static List<IndexPermissionDto> Show(IEnumerable<IndexPermission> permissions) =>
        permissions.Select(permission => new IndexPermissionDto
        {
            IndexPattern = permission.IndexPattern,
            Capabilities = Enum.GetValues<Capability>()
                .Where(c => c != Capability.None && c != Capability.All && permission.Capabilities.HasFlag(c))
                .Select(c => c.ToString())
                .ToList(),
        }).ToList();
}

/// <summary>A user as the API shows them. Never their password hash or secrets.</summary>
public sealed class UserView
{
    public required string Id { get; init; }
    public required string UserName { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public bool Active { get; init; }

    /// <summary>Local, Scim or Sso.</summary>
    public required string Source { get; init; }
    public string? ExternalId { get; init; }

    /// <summary>The role given to the user directly.</summary>
    public string? Role { get; init; }
    public List<IndexPermissionDto> IndexPermissions { get; init; } = new();

    /// <summary>The groups the user is in.</summary>
    public List<GroupReference> Groups { get; init; } = new();

    /// <summary>The role the user has, from themselves and their groups.</summary>
    public string? EffectiveRole { get; init; }
    public bool HasPassword { get; init; }
    public bool MustChangePassword { get; init; }
    public bool MfaEnabled { get; init; }
    public int RecoveryCodesLeft { get; init; }
    public bool Locked { get; init; }
    public DateTime? LockedUntil { get; init; }
    public DateTime? PasswordChangedAt { get; init; }
    public DateTime? LastSignInAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static UserView From(DirectoryUser user, IReadOnlyList<DirectoryGroup> groups, DirectoryGrant? grant, bool locked) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        Email = user.PrimaryEmail(),
        Active = user.Active,
        Source = user.Source.ToString(),
        ExternalId = user.ExternalId,
        Role = user.Role?.ToString(),
        IndexPermissions = PermissionText.Show(user.IndexPermissions),
        Groups = groups.Where(group => group.Members.Contains(user.Id)).Select(group => new GroupReference { Id = group.Id, DisplayName = group.DisplayName }).ToList(),
        EffectiveRole = grant?.Role.ToString(),
        HasPassword = user.PasswordHash != null,
        MustChangePassword = user.MustChangePassword,
        MfaEnabled = user.MfaEnabled,
        RecoveryCodesLeft = user.RecoveryCodeHashes.Count,
        Locked = locked,
        LockedUntil = locked ? user.LockedUntil : null,
        PasswordChangedAt = user.PasswordChangedAt,
        LastSignInAt = user.LastSignInAt,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt,
    };
}

/// <summary>A group a user is in.</summary>
public sealed class GroupReference
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
}

/// <summary>A user just added, with their temporary password when one was made.</summary>
public sealed class CreatedUser
{
    public required UserView User { get; init; }

    /// <summary>Shown this once. Must be changed at the first sign-in.</summary>
    public string? TemporaryPassword { get; init; }
}

/// <summary>A temporary password, shown once.</summary>
public sealed class TemporaryPassword
{
    public required string Password { get; init; }
}

/// <summary>A user to add.</summary>
public sealed class CreateUserRequest
{
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>For a local account. Left out, a temporary one is made.</summary>
    public string? Password { get; set; }

    /// <summary>Whether the user must choose a new password at their first sign-in.</summary>
    public bool MustChangePassword { get; set; } = true;

    /// <summary>A single sign-on user, with no password here.</summary>
    public bool Sso { get; set; }

    /// <summary>For a single sign-on user, the identity provider's ID for them.</summary>
    public string? ExternalId { get; set; }

    /// <summary>A role, or empty for none.</summary>
    public string? Role { get; set; }
    public List<IndexPermissionDto>? IndexPermissions { get; set; }

    /// <summary>IDs of groups to put the user in.</summary>
    public List<string>? Groups { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A change to a user.</summary>
public sealed class UpdateUserRequest
{
    /// <summary>Kept when left out.</summary>
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>Kept when left out; empty clears it.</summary>
    public string? ExternalId { get; set; }

    /// <summary>A role, or empty for none.</summary>
    public string? Role { get; set; }
    public List<IndexPermissionDto>? IndexPermissions { get; set; }

    /// <summary>IDs of the groups the user is in; kept when left out.</summary>
    public List<string>? Groups { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A group as the API shows it.</summary>
public sealed class GroupView
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string? ExternalId { get; init; }

    /// <summary>Local or Scim.</summary>
    public required string Source { get; init; }

    /// <summary>The role the group gives its members.</summary>
    public string? Role { get; init; }
    public List<IndexPermissionDto> IndexPermissions { get; init; } = new();

    /// <summary>IDs of the members.</summary>
    public List<string> Members { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public static GroupView From(DirectoryGroup group) => new()
    {
        Id = group.Id,
        DisplayName = group.DisplayName,
        ExternalId = group.ExternalId,
        Source = group.Source.ToString(),
        Role = group.Role?.ToString(),
        IndexPermissions = PermissionText.Show(group.IndexPermissions),
        Members = group.Members.ToList(),
        CreatedAt = group.CreatedAt,
        UpdatedAt = group.UpdatedAt,
    };
}

/// <summary>A group to add or a change to one.</summary>
public sealed class SaveGroupRequest
{
    public string? DisplayName { get; set; }

    /// <summary>A role, or empty for none.</summary>
    public string? Role { get; set; }
    public List<IndexPermissionDto>? IndexPermissions { get; set; }

    /// <summary>IDs of the members; kept when left out of a change.</summary>
    public List<string>? Members { get; set; }
}
