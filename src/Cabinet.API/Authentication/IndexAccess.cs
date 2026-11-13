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
    private FieldExtractor? _extractor;

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
    public ILogStorage Restrict(ILogStorage storage) =>
        new IndexRestrictedLogStorage(_extractor == null ? storage : new ExtractingLogStorage(storage, _extractor), this, _masking);

    /// <summary>Adds saved field extractions to what this scope reads.</summary>
    public FieldExtractor? Extractor
    {
        set => _extractor = value;
    }

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
public sealed class IndexRestrictedLogStorage : ILogStorage, IAggregatingLogStorage
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

        if (SearchesMaskedText(restricted))
        {
            await foreach (var logEvent in SearchMaskedAsync(restricted, cancellationToken))
            {
                yield return logEvent;
            }

            yield break;
        }

        await foreach (var logEvent in _inner.QueryAsync(restricted, cancellationToken))
        {
            yield return Mask(logEvent);
        }
    }

    /// <summary>
    /// Whether a search would run against text the caller sees masked. Storage would match the
    /// original, so searching for a hidden value would show whether it's there.
    /// </summary>
    private bool SearchesMaskedText(QueryContext context) =>
        !string.IsNullOrWhiteSpace(context.SearchText)
        && (_masking?.AppliesTo(_scope.Principal?.Role ?? SystemRole.Viewer) ?? false);

    /// <summary>Reads without the search text, and matches it against what the caller sees.</summary>
    private async IAsyncEnumerable<LogEvent> SearchMaskedAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var terms = SearchTerms.Parse(context.SearchText);
        var skip = context.Skip;
        var remaining = context.Limit;
        var page = context.Clone();
        page.SearchText = null;
        page.Skip = 0;
        page.Limit = 1000;

        // Keyset paging needs a time order. Others read one large page, as storage would.
        var keyset = page.SortField.Equals("Timestamp", StringComparison.OrdinalIgnoreCase);
        if (!keyset)
        {
            page.Limit = QueryContext.MaxLimit;
        }

        while (remaining > 0)
        {
            var read = 0;
            LogEvent? last = null;
            await foreach (var logEvent in _inner.QueryAsync(page, cancellationToken))
            {
                read++;
                last = logEvent;
                var masked = Mask(logEvent);
                if (!SearchTerms.Matches(masked.RawLine, terms))
                {
                    continue;
                }

                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                yield return masked;
                if (--remaining == 0)
                {
                    yield break;
                }
            }

            if (!keyset || last == null || read < page.Limit)
            {
                yield break;
            }

            page = page.Clone();
            page.After = new EventCursor(last.Timestamp, last.Id);
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
        if (restricted == null)
        {
            return 0;
        }

        if (SearchesMaskedText(restricted))
        {
            var all = restricted.Clone();
            all.Skip = 0;
            all.Limit = QueryContext.MaxLimit;
            long count = 0;
            await foreach (var _ in SearchMaskedAsync(all, cancellationToken))
            {
                count++;
            }

            return count;
        }

        return await _inner.GetCountAsync(restricted, cancellationToken);
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

    /// <inheritdoc />
    public async Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        // Storage would aggregate values the caller is only allowed to see masked
        if (_inner is not IAggregatingLogStorage aggregating
            || (_masking?.AppliesTo(_scope.Principal?.Role ?? SystemRole.Viewer) ?? false))
        {
            return null;
        }

        if (Restrict(request.Filter) is not { } restricted)
        {
            return new AggregateResult { Rows = request.GroupBy.Count == 0 && request.Bucket == null ? [EmptyRow(request)] : [] };
        }

        return await aggregating.AggregateAsync(
            new AggregateRequest
            {
                Filter = restricted,
                GroupBy = request.GroupBy,
                Bucket = request.Bucket,
                Aggregates = request.Aggregates,
                MaxGroups = request.MaxGroups,
            },
            cancellationToken);
    }

    /// <summary>What aggregating no events gives: counts of zero, and nothing for the rest.</summary>
    private static Dictionary<string, object?> EmptyRow(AggregateRequest request) =>
        request.Aggregates.ToDictionary(
            spec => spec.OutputName,
            spec => spec.Kind is AggregateKind.Count or AggregateKind.DistinctCount ? (object?)0 : null,
            StringComparer.OrdinalIgnoreCase);

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

        // What the query narrowed itself to, within what this caller may read. Patterns from
        // elsewhere can't be intersected exactly, so the caller's indexes are used alone.
        var restricted = context.Clone();
        restricted.AllowedIndexes = context.AllowedIndexes is { } requested && requested.All(name => !name.Contains('*', StringComparison.Ordinal))
            ? requested.Where(name => readable.Any(pattern => IndexPermission.Matches(pattern, name))).ToList()
            : readable;
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
    /// A stored principal wins, so an administrator can override anyone. Then, for someone in the
    /// user directory, what the directory gives them now: their own role and their groups'. A
    /// deactivated directory user gets nothing. API keys otherwise get their tier's role, and OIDC
    /// users the role their token's claims mapped to. An identity with none of these gets nothing.
    /// Local accounts only ever get what the directory gives them.
    /// </summary>
    public static async Task<RbacPrincipal?> ResolveAsync(ClaimsPrincipal user, IRbacService rbac, CancellationToken cancellationToken = default, Services.Identity.UserDirectory? directory = null)
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

        var local = id.StartsWith(DirectoryClaims.LocalPrefix, StringComparison.Ordinal);
        if (user.FindFirst(DirectoryClaims.UserId)?.Value is { } userId)
        {
            var account = directory == null ? null : await directory.GetAsync(userId, cancellationToken);
            if (account is not { Active: true })
            {
                return null;
            }

            if (await directory!.GrantForAsync(account, cancellationToken) is { } grant)
            {
                return new RbacPrincipal
                {
                    Id = id,
                    DisplayName = account.DisplayName ?? account.UserName,
                    Role = grant.Role,
                    IndexPermissions = grant.IndexPermissions,
                };
            }
        }

        if (local)
        {
            return null;
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
