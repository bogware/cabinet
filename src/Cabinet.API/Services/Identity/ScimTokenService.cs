using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Utilities;

namespace Cabinet.API.Services.Identity;

/// <summary>A SCIM token as stored: its hash, never the token.</summary>
public sealed class ScimToken
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>SHA-256 of the token.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The token's first characters, to tell tokens apart.</summary>
    public string? Prefix { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public bool Revoked { get; set; }

    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// Bearer tokens identity providers use to call SCIM. Separate from API keys: a SCIM token only
/// works on /scim/v2, and an API key never does. Kept hashed like API keys and compared against
/// every stored hash in constant time. Each instance rereads them every 30 seconds; a token made or
/// revoked on this instance applies here at once.
/// </summary>
public sealed class ScimTokenService
{
    /// <summary>Where tokens are kept.</summary>
    public const string Collection = "scim-tokens";

    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private readonly IDocumentStore _documents;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastUsed = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<ScimToken> _tokens = Array.Empty<ScimToken>();
    private long _loadedAtTicks;

    public ScimTokenService(IDocumentStore documents)
    {
        _documents = documents;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private async Task<IReadOnlyList<ScimToken>> TokensAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) < Refresh.Ticks)
        {
            return _tokens;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (fresh || DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) >= Refresh.Ticks)
            {
                await _documents.InitializeAsync(cancellationToken);
                _tokens = (await _documents.ListAsync<ScimToken>(Collection, cancellationToken)).OrderBy(token => token.CreatedAt).ToList();
                Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
            }

            return _tokens;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Every token, revoked ones included.</summary>
    public async Task<IReadOnlyList<ScimToken>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await TokensAsync(fresh: true, cancellationToken);
        foreach (var token in tokens)
        {
            if (_lastUsed.TryGetValue(token.Id, out var used) && (token.LastUsedAt == null || used > token.LastUsedAt))
            {
                token.LastUsedAt = used;
            }
        }

        return tokens;
    }

    /// <summary>Makes a token. The token itself is returned here and never again.</summary>
    public async Task<(string Token, ScimToken Info)> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var token = $"scim_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var info = new ScimToken { Id = Ulid.NewUlid(), Name = name, TokenHash = Hash(token), Prefix = token[..9] };
        await _documents.InitializeAsync(cancellationToken);
        await _documents.PutAsync(Collection, info.Id, info, cancellationToken);
        await TokensAsync(fresh: true, cancellationToken);
        return (token, info);
    }

    /// <summary>Revokes a token. Returns null when there's no such token.</summary>
    public async Task<ScimToken?> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        await _documents.InitializeAsync(cancellationToken);
        var info = await _documents.GetAsync<ScimToken>(Collection, id, cancellationToken);
        if (info == null)
        {
            return null;
        }

        info.Revoked = true;
        info.RevokedAt = DateTime.UtcNow;
        await _documents.PutAsync(Collection, id, info, cancellationToken);
        await TokensAsync(fresh: true, cancellationToken);
        return info;
    }

    /// <summary>The unrevoked token this is, or null.</summary>
    public async Task<ScimToken?> ValidateAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return null;
        }

        var wanted = Encoding.ASCII.GetBytes(Hash(token));
        ScimToken? found = null;
        foreach (var candidate in await TokensAsync(fresh: false, cancellationToken))
        {
            if (CryptographicOperations.FixedTimeEquals(wanted, Encoding.ASCII.GetBytes(candidate.TokenHash)) && !candidate.Revoked)
            {
                found = candidate;
            }
        }

        if (found != null)
        {
            _lastUsed[found.Id] = DateTime.UtcNow;
        }

        return found;
    }
}
