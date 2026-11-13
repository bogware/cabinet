using System.Collections.Concurrent;
using System.Security.Claims;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;

namespace Cabinet.API.Services.Identity;

/// <summary>A user name or external ID that another user or group already has.</summary>
public sealed class DirectoryConflictException : Exception
{
    public DirectoryConflictException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Cabinet's user directory: users, groups and the roles groups give. Kept in the shared document
/// store, so it works on every backend and every instance sees the same people. Each instance
/// reuses what it read for <see cref="AccountOptions.DirectoryCacheSeconds"/>, so a change made on
/// another instance, such as deactivating someone, applies there within that time; changes made
/// on this instance apply at once.
/// </summary>
/// <remarks>
/// User names are unique without regard to case. A lookup document per name and external ID
/// (<see cref="NamesCollection"/>) finds users without reading them all. Writes on one instance
/// are serialized; two instances creating the same name in the same moment could both succeed,
/// as the document store has no transactions.
/// </remarks>
public sealed class UserDirectory
{
    /// <summary>Where users are kept.</summary>
    public const string UsersCollection = "directory-users";

    /// <summary>Where groups are kept.</summary>
    public const string GroupsCollection = "directory-groups";

    /// <summary>User names and external IDs, to the ID of the user who has them.</summary>
    public const string NamesCollection = "directory-names";

    /// <summary>Longest user name, display name or external ID.</summary>
    public const int MaxNameLength = 256;

    // Claims an identity provider uses for its own ID of a user
    private static readonly string[] ExternalIdClaims =
    [
        "oid", "http://schemas.microsoft.com/identity/claims/objectidentifier", "sub", ClaimTypes.NameIdentifier, "uid",
    ];

    // Claims that hold a user's sign-in name or email
    private static readonly string[] NameClaims =
    [
        "preferred_username", "upn", ClaimTypes.Upn, "unique_name", "email", ClaimTypes.Email, "login", ClaimTypes.NameIdentifier,
    ];

    private readonly IDocumentStore _documents;
    private readonly AccountOptions _options;
    private readonly ILogger<UserDirectory> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CachedUser> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedMatch> _matches = new(StringComparer.Ordinal);
    private volatile bool _initialized;
    private volatile CachedGroups? _groups;
    private volatile CachedFlag? _hasLocalUsers;

    public UserDirectory(IDocumentStore documents, AccountOptions options, ILogger<UserDirectory> logger, TimeProvider? time = null)
    {
        _documents = documents;
        _options = options.Normalized();
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settings in force.</summary>
    public AccountOptions Options => _options;

    private TimeSpan CacheFor => TimeSpan.FromSeconds(_options.DirectoryCacheSeconds);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
            {
                await _documents.InitializeAsync(cancellationToken);
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string UserNameKey(string userName) => $"user:{userName.Trim().ToLowerInvariant()}";

    private static string ExternalIdKey(string externalId) => $"ext:{externalId.Trim()}";

    // ---------- Users ----------

    /// <summary>A user, as read within the last few seconds. For checking sessions and permissions.</summary>
    public async Task<DirectoryUser?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        if (_users.TryGetValue(id, out var cached) && Now - cached.ReadAt < CacheFor)
        {
            return cached.User;
        }

        return await GetFreshAsync(id, cancellationToken);
    }

    /// <summary>A user as stored now.</summary>
    public async Task<DirectoryUser?> GetFreshAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxNameLength)
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken);
        var user = await _documents.GetAsync<DirectoryUser>(UsersCollection, id, cancellationToken);
        Remember(id, user);
        return user;
    }

    private void Remember(string id, DirectoryUser? user)
    {
        if (_users.Count > 100_000)
        {
            _users.Clear();
        }

        _users[id] = new CachedUser(user, Now);
    }

    /// <summary>The user with this user name, compared without regard to case.</summary>
    public async Task<DirectoryUser?> FindByUserNameAsync(string? userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > MaxNameLength)
        {
            return null;
        }

        var user = await FindByKeyAsync(UserNameKey(userName), cancellationToken);
        return user != null && user.UserName.Equals(userName.Trim(), StringComparison.OrdinalIgnoreCase) ? user : null;
    }

    /// <summary>The user with this external ID.</summary>
    public async Task<DirectoryUser?> FindByExternalIdAsync(string? externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > MaxNameLength)
        {
            return null;
        }

        var user = await FindByKeyAsync(ExternalIdKey(externalId), cancellationToken);
        return user != null && string.Equals(user.ExternalId, externalId.Trim(), StringComparison.Ordinal) ? user : null;
    }

    private async Task<DirectoryUser?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var entry = await _documents.GetAsync<NameEntry>(NamesCollection, key, cancellationToken);
        return entry == null ? null : await GetFreshAsync(entry.UserId, cancellationToken);
    }

    /// <summary>Every user.</summary>
    public async Task<IReadOnlyList<DirectoryUser>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return (await _documents.ListAsync<DirectoryUser>(UsersCollection, cancellationToken))
            .OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether any user can sign in with a password, for the sign-in dialog.</summary>
    public async Task<bool> HasLocalUsersAsync(CancellationToken cancellationToken = default)
    {
        if (_hasLocalUsers is { } cached && Now - cached.ReadAt < TimeSpan.FromSeconds(30))
        {
            return cached.Value;
        }

        var any = (await ListAsync(cancellationToken)).Any(user => user.PasswordHash != null);
        _hasLocalUsers = new CachedFlag(any, Now);
        return any;
    }

    /// <summary>Adds a user. Throws <see cref="DirectoryConflictException"/> when the name or external ID is taken.</summary>
    public async Task<DirectoryUser> CreateAsync(DirectoryUser user, CancellationToken cancellationToken = default)
    {
        Validate(user);
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            user.Id = string.IsNullOrEmpty(user.Id) ? Ulid.NewUlid() : user.Id;
            user.UserName = user.UserName.Trim();
            user.ExternalId = string.IsNullOrWhiteSpace(user.ExternalId) ? null : user.ExternalId.Trim();
            await EnsureUniqueAsync(user, cancellationToken);
            user.CreatedAt = user.UpdatedAt = Now;

            // The lookups first: a user without them couldn't be found, while a lookup without its user is ignored
            await WriteNamesAsync(user, null, cancellationToken);
            await _documents.PutAsync(UsersCollection, user.Id, user, cancellationToken);
            Remember(user.Id, user);
            _hasLocalUsers = null;
            return user;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Changes a user as stored now, and saves the change. Returns null when there's no such user.
    /// Throws <see cref="DirectoryConflictException"/> when the change takes a name someone has.
    /// </summary>
    public async Task<DirectoryUser?> UpdateAsync(string id, Action<DirectoryUser> change, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _documents.GetAsync<DirectoryUser>(UsersCollection, id, cancellationToken);
            if (user == null)
            {
                return null;
            }

            var before = new NameSnapshot(user.UserName, user.ExternalId);
            change(user);
            user.Id = id;
            user.UserName = user.UserName.Trim();
            user.ExternalId = string.IsNullOrWhiteSpace(user.ExternalId) ? null : user.ExternalId.Trim();
            Validate(user);
            if (!before.UserName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase) || before.ExternalId != user.ExternalId)
            {
                await EnsureUniqueAsync(user, cancellationToken);
                await WriteNamesAsync(user, before, cancellationToken);
            }

            user.UpdatedAt = Now;
            await _documents.PutAsync(UsersCollection, id, user, cancellationToken);
            Remember(id, user);
            _hasLocalUsers = null;
            return user;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Removes a user, and takes them out of every group.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var user = await _documents.GetAsync<DirectoryUser>(UsersCollection, id, cancellationToken);
            if (user == null)
            {
                return false;
            }

            await _documents.DeleteAsync(UsersCollection, id, cancellationToken);
            await RemoveNameAsync(UserNameKey(user.UserName), id, cancellationToken);
            if (user.ExternalId != null)
            {
                await RemoveNameAsync(ExternalIdKey(user.ExternalId), id, cancellationToken);
            }

            foreach (var group in (await ReadGroupsAsync(cancellationToken)).Where(group => group.Members.Contains(id)))
            {
                group.Members.Remove(id);
                group.UpdatedAt = Now;
                await _documents.PutAsync(GroupsCollection, group.Id, group, cancellationToken);
            }

            _groups = null;
            Remember(id, null);
            _hasLocalUsers = null;
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void Validate(DirectoryUser user)
    {
        if (string.IsNullOrWhiteSpace(user.UserName) || user.UserName.Trim().Length > MaxNameLength || user.UserName.Any(char.IsControl))
        {
            throw new ArgumentException($"A user name is needed, of up to {MaxNameLength} characters");
        }

        if (user.ExternalId is { Length: > MaxNameLength } || user.DisplayName is { Length: > MaxNameLength })
        {
            throw new ArgumentException($"Names and external IDs can be up to {MaxNameLength} characters");
        }
    }

    private async Task EnsureUniqueAsync(DirectoryUser user, CancellationToken cancellationToken)
    {
        var sameName = await FindByUserNameAsync(user.UserName, cancellationToken);
        if (sameName != null && sameName.Id != user.Id)
        {
            throw new DirectoryConflictException($"There's already a user named '{user.UserName}'");
        }

        if (user.ExternalId != null && await FindByExternalIdAsync(user.ExternalId, cancellationToken) is { } sameExternal && sameExternal.Id != user.Id)
        {
            throw new DirectoryConflictException("Another user has that external ID");
        }
    }

    private async Task WriteNamesAsync(DirectoryUser user, NameSnapshot? before, CancellationToken cancellationToken)
    {
        await _documents.PutAsync(NamesCollection, UserNameKey(user.UserName), new NameEntry { UserId = user.Id }, cancellationToken);
        if (user.ExternalId != null)
        {
            await _documents.PutAsync(NamesCollection, ExternalIdKey(user.ExternalId), new NameEntry { UserId = user.Id }, cancellationToken);
        }

        if (before != null)
        {
            if (!before.UserName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase))
            {
                await RemoveNameAsync(UserNameKey(before.UserName), user.Id, cancellationToken);
            }

            if (before.ExternalId != null && before.ExternalId != user.ExternalId)
            {
                await RemoveNameAsync(ExternalIdKey(before.ExternalId), user.Id, cancellationToken);
            }
        }
    }

    // Only a lookup that still points at this user: another may have taken the name since
    private async Task RemoveNameAsync(string key, string userId, CancellationToken cancellationToken)
    {
        var entry = await _documents.GetAsync<NameEntry>(NamesCollection, key, cancellationToken);
        if (entry?.UserId == userId)
        {
            await _documents.DeleteAsync(NamesCollection, key, cancellationToken);
        }
    }

    // ---------- Groups ----------

    /// <summary>Every group, as read within the last few seconds.</summary>
    public async Task<IReadOnlyList<DirectoryGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (_groups is { } cached && Now - cached.ReadAt < CacheFor)
        {
            return cached.Groups;
        }

        var groups = await ReadGroupsAsync(cancellationToken);
        _groups = new CachedGroups(groups, Now);
        return groups;
    }

    private async Task<IReadOnlyList<DirectoryGroup>> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return (await _documents.ListAsync<DirectoryGroup>(GroupsCollection, cancellationToken))
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A group as stored now.</summary>
    public async Task<DirectoryGroup?> GetGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxNameLength)
        {
            return null;
        }

        await EnsureInitializedAsync(cancellationToken);
        return await _documents.GetAsync<DirectoryGroup>(GroupsCollection, id, cancellationToken);
    }

    /// <summary>The groups a user is in.</summary>
    public async Task<IReadOnlyList<DirectoryGroup>> GroupsOfAsync(string userId, CancellationToken cancellationToken = default) =>
        (await ListGroupsAsync(cancellationToken)).Where(group => group.Members.Contains(userId)).ToList();

    /// <summary>Adds a group. Members that aren't users are left out.</summary>
    public async Task<DirectoryGroup> CreateGroupAsync(DirectoryGroup group, CancellationToken cancellationToken = default)
    {
        ValidateGroup(group);
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            group.Id = string.IsNullOrEmpty(group.Id) ? Ulid.NewUlid() : group.Id;
            group.DisplayName = group.DisplayName.Trim();
            group.ExternalId = string.IsNullOrWhiteSpace(group.ExternalId) ? null : group.ExternalId.Trim();
            await EnsureUniqueGroupAsync(group, cancellationToken);
            group.Members = await ExistingUsersAsync(group.Members, cancellationToken);
            group.CreatedAt = group.UpdatedAt = Now;
            await _documents.PutAsync(GroupsCollection, group.Id, group, cancellationToken);
            _groups = null;
            return group;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Changes a group as stored now, and saves it. Returns null when there's no such group.</summary>
    public async Task<DirectoryGroup?> UpdateGroupAsync(string id, Action<DirectoryGroup> change, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var group = await _documents.GetAsync<DirectoryGroup>(GroupsCollection, id, cancellationToken);
            if (group == null)
            {
                return null;
            }

            var members = group.Members.ToHashSet(StringComparer.Ordinal);
            var before = (group.DisplayName, group.ExternalId);
            change(group);
            group.Id = id;
            group.DisplayName = group.DisplayName.Trim();
            group.ExternalId = string.IsNullOrWhiteSpace(group.ExternalId) ? null : group.ExternalId.Trim();
            ValidateGroup(group);
            if (!before.DisplayName.Equals(group.DisplayName, StringComparison.OrdinalIgnoreCase) || before.ExternalId != group.ExternalId)
            {
                await EnsureUniqueGroupAsync(group, cancellationToken);
            }

            // Only members added now need checking
            var added = group.Members.Where(member => !members.Contains(member)).ToList();
            var existing = (await ExistingUsersAsync(added, cancellationToken)).ToHashSet(StringComparer.Ordinal);
            group.Members = group.Members.Where(member => members.Contains(member) || existing.Contains(member)).Distinct(StringComparer.Ordinal).ToList();
            group.UpdatedAt = Now;
            await _documents.PutAsync(GroupsCollection, id, group, cancellationToken);
            _groups = null;
            return group;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Removes a group. Its members stay, without what it gave them.</summary>
    public async Task<bool> DeleteGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var deleted = await _documents.DeleteAsync(GroupsCollection, id, cancellationToken);
            _groups = null;
            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Puts a user in exactly these groups. Unknown group IDs are refused.</summary>
    public async Task SetUserGroupsAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken cancellationToken = default)
    {
        var wanted = groupIds.ToHashSet(StringComparer.Ordinal);
        var groups = await ReadGroupsAsync(cancellationToken);
        var unknown = wanted.Where(id => groups.All(group => group.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"No such group: {string.Join(", ", unknown)}");
        }

        foreach (var group in groups)
        {
            var isMember = group.Members.Contains(userId);
            if (isMember == wanted.Contains(group.Id))
            {
                continue;
            }

            await UpdateGroupAsync(group.Id, g =>
            {
                if (isMember)
                {
                    g.Members.Remove(userId);
                }
                else
                {
                    g.Members.Add(userId);
                }
            }, cancellationToken);
        }
    }

    private static void ValidateGroup(DirectoryGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.DisplayName) || group.DisplayName.Trim().Length > MaxNameLength || group.DisplayName.Any(char.IsControl))
        {
            throw new ArgumentException($"A group needs a name of up to {MaxNameLength} characters");
        }

        if (group.ExternalId is { Length: > MaxNameLength })
        {
            throw new ArgumentException($"External IDs can be up to {MaxNameLength} characters");
        }

        if (group.Members.Count > 100_000)
        {
            throw new ArgumentException("A group can have up to 100,000 members");
        }
    }

    private async Task EnsureUniqueGroupAsync(DirectoryGroup group, CancellationToken cancellationToken)
    {
        var groups = await ReadGroupsAsync(cancellationToken);
        if (groups.Any(other => other.Id != group.Id && other.DisplayName.Equals(group.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DirectoryConflictException($"There's already a group named '{group.DisplayName}'");
        }

        if (group.ExternalId != null && groups.Any(other => other.Id != group.Id && other.ExternalId == group.ExternalId))
        {
            throw new DirectoryConflictException("Another group has that external ID");
        }
    }

    private async Task<List<string>> ExistingUsersAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var existing = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (await _documents.GetAsync<DirectoryUser>(UsersCollection, id, cancellationToken) != null)
            {
                existing.Add(id);
            }
            else
            {
                _logger.LogInformation("Left user {Id} out of a group: there's no such user", id);
            }
        }

        return existing;
    }

    // ---------- What users may do ----------

    /// <summary>
    /// What a user may do: their own role and permissions, and those of each group they're in
    /// that gives a role. The highest role wins and index permissions are unioned. Null when
    /// nothing gives them a role.
    /// </summary>
    public async Task<DirectoryGrant?> GrantForAsync(DirectoryUser user, CancellationToken cancellationToken = default)
    {
        var grants = new List<(SystemRole, IReadOnlyList<IndexPermission>)>();
        if (user.Role is { } role)
        {
            grants.Add((role, user.IndexPermissions));
        }

        foreach (var group in await GroupsOfAsync(user.Id, cancellationToken))
        {
            if (group.Role is { } groupRole)
            {
                grants.Add((groupRole, group.IndexPermissions));
            }
        }

        return DirectoryGrant.Combine(grants);
    }

    /// <summary>
    /// The directory user a single sign-on identity belongs to: one whose external ID is the
    /// provider's ID for them (such as Entra's oid, or the subject), or else whose user name or
    /// email is the identity's sign-in name or email, without regard to case. An email the
    /// provider says it hasn't verified isn't used.
    /// </summary>
    public async Task<DirectoryUser?> MatchAsync(ClaimsIdentity identity, CancellationToken cancellationToken = default)
    {
        foreach (var value in ClaimValues(identity, ExternalIdClaims))
        {
            if (await FindByExternalIdAsync(value, cancellationToken) is { } byExternalId)
            {
                return byExternalId;
            }
        }

        var unverifiedEmail = identity.FindFirst("email_verified")?.Value is { } verified
            && verified.Equals("false", StringComparison.OrdinalIgnoreCase);
        var names = NameClaims
            .Where(type => !(unverifiedEmail && (type == "email" || type == ClaimTypes.Email)))
            .ToArray();
        foreach (var value in ClaimValues(identity, names))
        {
            if (await FindByUserNameAsync(value, cancellationToken) is { } byName)
            {
                return byName;
            }
        }

        // An email on the directory user, when the user name is something else
        var emails = ClaimValues(identity, unverifiedEmail ? ["preferred_username", "upn", ClaimTypes.Upn] : ["email", ClaimTypes.Email, "preferred_username", "upn", ClaimTypes.Upn])
            .Where(value => value.Contains('@'))
            .ToList();
        if (emails.Count == 0)
        {
            return null;
        }

        return (await ListAsync(cancellationToken)).FirstOrDefault(user =>
            user.Emails.Any(email => emails.Contains(email.Value, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// <see cref="MatchAsync"/>, reusing the answer for the same caller for a few seconds. For
    /// bearer tokens, which are checked on every request.
    /// </summary>
    public async Task<DirectoryUser?> MatchCachedAsync(string callerKey, ClaimsIdentity identity, CancellationToken cancellationToken = default)
    {
        if (_matches.TryGetValue(callerKey, out var cached) && Now - cached.ReadAt < CacheFor)
        {
            return cached.UserId == null ? null : await GetAsync(cached.UserId, cancellationToken);
        }

        var user = await MatchAsync(identity, cancellationToken);
        if (_matches.Count > 100_000)
        {
            _matches.Clear();
        }

        _matches[callerKey] = new CachedMatch(user?.Id, Now);
        return user;
    }

    private static List<string> ClaimValues(ClaimsIdentity identity, IEnumerable<string> types) =>
        types.SelectMany(type => identity.FindAll(type))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxNameLength)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private sealed record CachedUser(DirectoryUser? User, DateTime ReadAt);

    private sealed record CachedGroups(IReadOnlyList<DirectoryGroup> Groups, DateTime ReadAt);

    private sealed record CachedFlag(bool Value, DateTime ReadAt);

    private sealed record CachedMatch(string? UserId, DateTime ReadAt);

    private sealed record NameSnapshot(string UserName, string? ExternalId);

    /// <summary>A user name or external ID, pointing at its user.</summary>
    public sealed class NameEntry
    {
        public required string UserId { get; set; }
    }
}
