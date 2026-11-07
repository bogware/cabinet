using System.Runtime.CompilerServices;
using System.Security.Claims;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Authentication;

/// <summary>
/// The indexes the current request or background job may read. Set when the request is authorized,
/// and enforced by <see cref="IndexRestrictedLogStorage"/>, so that every route to storage
/// honors it, including subsearches, macros and joins.
/// </summary>
public sealed class IndexAccessScope
{
    private FieldMaskingService? _masking;

    /// <summary>Patterns the caller may read, or null for every index.</summary>
    public IReadOnlyList<string>? ReadableIndexes { get; set; }

    /// <summary>The caller's principal, once authorized.</summary>
    public RbacPrincipal? Principal { get; set; }

    /// <summary>Whether reading isn't limited to some indexes.</summary>
    public bool CanReadEveryIndex => ReadableIndexes == null;

    /// <summary>Whether the caller may read this index.</summary>
    public bool CanRead(string index) =>
        ReadableIndexes == null || ReadableIndexes.Any(pattern => IndexPermission.Matches(pattern, index));

    /// <summary>Whether the caller may do this to this index.</summary>
    public bool Can(Capability capability, string index) => Principal?.HasCapability(capability, index) ?? false;

    /// <summary>Applies this scope to storage.</summary>
    public ILogStorage Restrict(ILogStorage storage) => new IndexRestrictedLogStorage(storage, this, _masking);

    /// <summary>Masks what this scope reads.</summary>
    public FieldMaskingService? Masking
    {
        set => _masking = value;
    }

    /// <summary>A key that keeps cached query results apart for callers who see different data.</summary>
    public string CacheKey =>
        $"{Principal?.Role}|{(ReadableIndexes == null ? string.Empty : string.Join(',', ReadableIndexes.Order(StringComparer.OrdinalIgnoreCase)))}";
}

/// <summary>
/// Storage that only shows the indexes in a scope. Reads the scope on each call, so it can be
/// created before the request is authorized.
/// </summary>
public sealed class IndexRestrictedLogStorage : ILogStorage
{
    private readonly ILogStorage _inner;
    private readonly IndexAccessScope _scope;
    private readonly FieldMaskingService? _masking;

    public IndexRestrictedLogStorage(ILogStorage inner, IndexAccessScope scope, FieldMaskingService? masking = null)
    {
        _inner = inner;
        _scope = scope;
        _masking = masking;
    }

    /// <summary>Masked for the caller. A caller nobody identified gets the strictest view.</summary>
    private LogEvent Mask(LogEvent logEvent) =>
        _masking?.MaskEvent(logEvent, _scope.Principal?.Role ?? SystemRole.Viewer) ?? logEvent;

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Restrict(context) is not { } restricted)
        {
            yield break;
        }

        await foreach (var logEvent in _inner.QueryAsync(restricted, cancellationToken))
        {
            yield return Mask(logEvent);
        }
    }

    /// <inheritdoc />
    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var logEvent = await _inner.GetByIdAsync(id, cancellationToken);
        return logEvent != null && _scope.CanRead(logEvent.Index) ? Mask(logEvent) : null;
    }

    /// <inheritdoc />
    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        var restricted = Restrict(context ?? new QueryContext());
        return restricted == null ? 0 : await _inner.GetCountAsync(restricted, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = await _inner.GetStatsAsync(cancellationToken);
        if (_scope.ReadableIndexes == null)
        {
            return stats;
        }

        // Totals would reveal how much the caller can't see
        var visible = stats.EventsByIndex.Where(index => _scope.CanRead(index.Key)).ToDictionary(index => index.Key, index => index.Value);
        return new StorageStats
        {
            EventsByIndex = visible,
            TotalEvents = visible.Values.Sum(),
        };
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) => _inner.StoreAsync(logEvent, cancellationToken);

    /// <inheritdoc />
    public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default) => _inner.StoreBatchAsync(events, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeOldEventsAsync(before, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeIndexEventsAsync(indexName, before, cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default) => _inner.DeleteByIndexAsync(indexName, cancellationToken);

    /// <summary>The context limited to the scope, or null if it can't match anything.</summary>
    private QueryContext? Restrict(QueryContext context)
    {
        if (_scope.ReadableIndexes is not { } readable)
        {
            return context;
        }

        if (readable.Count == 0 || (context.Index != null && !_scope.CanRead(context.Index)))
        {
            return null;
        }

        var restricted = context.Clone();
        restricted.AllowedIndexes = readable;
        return restricted;
    }
}

/// <summary>Finds the RBAC principal for an authenticated user.</summary>
public static class PrincipalResolver
{
    /// <summary>Name of the claim holding the role from an OIDC token.</summary>
    public const string RoleClaim = "CabinetRole";

    /// <summary>Name of the claims holding index permissions from an OIDC token.</summary>
    public const string IndexPermissionClaim = "IndexPermission";

    /// <summary>
    /// A stored principal wins, so an administrator can override anyone. API keys otherwise get
    /// their tier's role, and OIDC users the role their token's claims mapped to. An identity with
    /// neither gets nothing.
    /// </summary>
    public static async Task<RbacPrincipal?> ResolveAsync(ClaimsPrincipal user, IRbacService rbac, CancellationToken cancellationToken = default)
    {
        var id = user.FindFirst("ClientId")?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var principal = await rbac.GetPrincipalAsync(id, cancellationToken);
        if (principal != null)
        {
            return principal;
        }

        if (!Enum.TryParse<SystemRole>(user.FindFirst(RoleClaim)?.Value, out var role))
        {
            return null;
        }

        return new RbacPrincipal
        {
            Id = id,
            DisplayName = user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("preferred_username")?.Value,
            Role = role,
            IndexPermissions = user.FindAll(IndexPermissionClaim).Select(ParsePermission).OfType<IndexPermission>().ToList(),
        };
    }

    private static IndexPermission? ParsePermission(Claim claim)
    {
        var separator = claim.Value.LastIndexOf(':');
        if (separator <= 0)
        {
            return null;
        }

        return Enum.TryParse<Capability>(claim.Value[(separator + 1)..], out var capabilities)
            ? new IndexPermission { IndexPattern = claim.Value[..separator], Capabilities = capabilities }
            : null;
    }
}
