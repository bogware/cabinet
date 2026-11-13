using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cabinet.API.Authentication;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cabinet.API.Services;

/// <summary>
/// Service for managing API keys loaded from configuration (environment variables, secrets, etc.)
/// Keys are hashed with SHA-256 for storage; validation uses constant-time comparison.
/// Thread-safe for concurrent access from multiple request threads.
/// </summary>
public class ApiKeyService : IApiKeyService
{
    /// <summary>Where keys made in the dashboard, and revocations, are kept.</summary>
    public const string Collection = "api-keys";

    /// <summary>The tiers a key can have.</summary>
    public static readonly IReadOnlyList<string> Tiers = ["Ingest", "Basic", "Professional", "Enterprise", "Admin"];

    private readonly ConcurrentDictionary<string, ApiKeyInfo> _keysByHash = new();
    private readonly ILogger<ApiKeyService> _logger;
    private readonly IDocumentStore? _documents;
    private readonly TimeSpan _refreshInterval;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    // Ticks of the last load, read outside the lock
    private long _loadedAtTicks;

    public ApiKeyService(IConfiguration configuration, ILogger<ApiKeyService> logger)
        : this(configuration, logger, null)
    {
    }

    /// <summary>
    /// With a document store, keys made and revoked in the dashboard are kept there, so they
    /// survive restarts and every instance sees them. Each instance rereads it every
    /// <c>ApiKeys:RefreshSeconds</c> (default 30).
    /// </summary>
    public ApiKeyService(IConfiguration configuration, ILogger<ApiKeyService> logger, IDocumentStore? documents)
    {
        _logger = logger;
        _documents = documents;
        _refreshInterval = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("ApiKeys:RefreshSeconds", 30)));

        // Load keys from configuration (appsettings, env vars, secrets manager — never hardcode)
        var keysSection = configuration.GetSection("ApiKeys:Keys");
        var keyConfigs = keysSection.GetChildren();

        foreach (var keyConfig in keyConfigs)
        {
            var key = keyConfig["Key"];
            var tier = keyConfig["Tier"] ?? "Basic";
            var name = keyConfig["Name"] ?? "Unknown Client";
            DateTime? expiresAt = null;
            if (keyConfig["ExpiresAt"] is { Length: > 0 } expiry)
            {
                if (!DateTime.TryParse(expiry, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    throw new InvalidOperationException($"ApiKeys:Keys: ExpiresAt '{expiry}' for '{name}' isn't a date");
                }

                expiresAt = parsed;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                _logger.LogWarning("Skipping API key config entry with empty key");
                continue;
            }

            var keyHash = HashKey(key);
            _keysByHash[keyHash] = new ApiKeyInfo
            {
                // From the key, so roles and ownership survive a restart and match on all instances
                Id = PrincipalIdFor(keyHash),
                Key = keyHash, // Store hash, not raw key
                ClientName = name,
                Tier = tier,
                Permissions = GetDefaultPermissions(tier),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                Source = "settings",
                Prefix = key.Length > 6 ? key[..6] : null,
            };
        }

        if (_keysByHash.IsEmpty)
        {
            _logger.LogWarning("No API keys configured. Set ApiKeys__Keys__0__Key, ApiKeys__Keys__0__Tier, ApiKeys__Keys__0__Name environment variables.");
        }
        else
        {
            _logger.LogInformation("Loaded {Count} API key(s) from configuration", _keysByHash.Count);
        }
    }

    public async Task<ApiKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        await EnsureLoadedAsync();
        var keyHash = HashKey(apiKey);

        if (FindConstantTime(keyHash) is { IsActive: true } keyInfo)
        {
            // Check expiration
            if (keyInfo.ExpiresAt.HasValue && keyInfo.ExpiresAt.Value < DateTime.UtcNow)
            {
                return new ApiKeyValidationResult
                {
                    IsValid = false,
                    Reason = "API key has expired"
                };
            }

            keyInfo.LastUsedAt = DateTime.UtcNow;
            Interlocked.Increment(ref keyInfo._requestCount);

            return new ApiKeyValidationResult
            {
                IsValid = true,
                ClientId = keyInfo.Id,
                ClientName = keyInfo.ClientName,
                Tier = keyInfo.Tier,
                Permissions = keyInfo.Permissions
            };
        }

        return new ApiKeyValidationResult
        {
            IsValid = false,
            Reason = "Invalid API key"
        };
    }

    /// <summary>
    /// Reads keys made in the dashboard, and revocations, from the document store: once, then
    /// every refresh interval, so changes on other instances apply.
    /// </summary>
    private async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_documents == null || DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) < _refreshInterval.Ticks)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) < _refreshInterval.Ticks)
            {
                return;
            }

            try
            {
                await _documents.InitializeAsync(cancellationToken);
                foreach (var stored in await _documents.ListAsync<StoredApiKey>(Collection, cancellationToken))
                {
                    Apply(stored);
                }

            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keys from settings, and those already read, keep working while the database is away
                _logger.LogWarning(ex, "Couldn't read API keys from the database; trying again in {Seconds} s", _refreshInterval.TotalSeconds);
            }

            Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private void Apply(StoredApiKey stored)
    {
        if (stored.Source == "dashboard" && !string.IsNullOrEmpty(stored.KeyHash))
        {
            var info = _keysByHash.GetOrAdd(stored.KeyHash, hash => new ApiKeyInfo { Key = hash });
            info.Id = stored.Id;
            info.ClientName = stored.Name;
            info.Tier = stored.Tier;
            info.Permissions = GetDefaultPermissions(stored.Tier);
            info.CreatedAt = stored.CreatedAt;
            info.ExpiresAt = stored.ExpiresAt;
            info.Source = "dashboard";
            info.Prefix = stored.Prefix;
            info.IsActive = !stored.Revoked;
            info.RevokedAt = stored.RevokedAt;
            return;
        }

        // A revocation of a key from settings
        if (stored.Revoked && _keysByHash.Values.FirstOrDefault(key => key.Id == stored.Id) is { } fromSettings)
        {
            fromSettings.IsActive = false;
            fromSettings.RevokedAt = stored.RevokedAt;
        }
    }

    /// <summary>Every key, including revoked ones. Never the keys themselves.</summary>
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _keysByHash.Values.OrderBy(key => key.CreatedAt).ThenBy(key => key.ClientName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a key and keeps its hash. The key itself is returned here and never again.
    /// </summary>
    public async Task<(string Key, ApiKeyInfo Info)> CreateAsync(string name, string tier, DateTime? expiresAt, CancellationToken cancellationToken = default)
    {
        if (_documents == null)
        {
            throw new InvalidOperationException("Keys can only be made where there's a database to keep them in");
        }

        var canonicalTier = Tiers.FirstOrDefault(known => known.Equals(tier, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Tier must be one of: {string.Join(", ", Tiers)}", nameof(tier));
        await EnsureLoadedAsync(cancellationToken);

        var key = $"cab_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var hash = HashKey(key);
        var stored = new StoredApiKey
        {
            Id = PrincipalIdFor(hash),
            KeyHash = hash,
            Name = name,
            Tier = canonicalTier,
            Prefix = key[..6],
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt?.ToUniversalTime(),
            Source = "dashboard",
        };
        await _documents.PutAsync(Collection, stored.Id, stored, cancellationToken);
        Apply(stored);
        _logger.LogInformation("Created API key {Id} ({Name}, {Tier})", stored.Id, name, canonicalTier);
        return (key, _keysByHash[hash]);
    }

    /// <summary>Revokes a key by ID, whether made in the dashboard or in settings.</summary>
    public async Task<ApiKeyInfo?> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        var info = _keysByHash.Values.FirstOrDefault(key => key.Id == id);
        if (info == null)
        {
            return null;
        }

        info.IsActive = false;
        info.RevokedAt = DateTime.UtcNow;
        if (_documents != null)
        {
            var stored = await _documents.GetAsync<StoredApiKey>(Collection, id, cancellationToken)
                ?? new StoredApiKey { Id = id, Name = info.ClientName, Tier = info.Tier, Source = info.Source, CreatedAt = info.CreatedAt };
            stored.Revoked = true;
            stored.RevokedAt = info.RevokedAt;
            await _documents.PutAsync(Collection, id, stored, cancellationToken);
        }

        _logger.LogInformation("Revoked API key {Id} ({Name})", id, info.ClientName);
        return info;
    }

    public Task<string> GenerateKeyAsync(string clientName, string tier = "Basic", List<string>? permissions = null)
    {
        // Generate cryptographically random key (no tier embedded — prevents privilege leakage)
        var keyBytes = RandomNumberGenerator.GetBytes(32);
        var apiKey = $"cab_{Convert.ToHexString(keyBytes).ToLowerInvariant()}";

        var keyHash = HashKey(apiKey);
        _keysByHash[keyHash] = new ApiKeyInfo
        {
            Id = PrincipalIdFor(keyHash),
            Key = keyHash, // Store hash only
            ClientName = clientName,
            Tier = tier,
            Permissions = permissions ?? GetDefaultPermissions(tier),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = null
        };

        _logger.LogInformation("Generated new API key for client: {ClientName}, tier: {Tier}", clientName, tier);
        return Task.FromResult(apiKey);
    }

    public Task<bool> RevokeKeyAsync(string apiKey)
    {
        var keyHash = HashKey(apiKey);
        if (_keysByHash.TryGetValue(keyHash, out var keyInfo))
        {
            keyInfo.IsActive = false;
            keyInfo.RevokedAt = DateTime.UtcNow;
            _logger.LogInformation("Revoked API key for client: {ClientName}", keyInfo.ClientName);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public async Task<List<ApiKeyInfo>> GetActiveKeysAsync()
    {
        await EnsureLoadedAsync();
        return _keysByHash.Values
            .Where(k => k.IsActive)
            .ToList();
    }

    /// <summary>
    /// The key with this hash, compared against every stored hash in constant time, so how long a
    /// check takes says nothing about how close a guess came to any key.
    /// </summary>
    private ApiKeyInfo? FindConstantTime(string keyHash)
    {
        var wanted = Encoding.ASCII.GetBytes(keyHash);
        ApiKeyInfo? found = null;
        foreach (var (hash, info) in _keysByHash)
        {
            if (CryptographicOperations.FixedTimeEquals(wanted, Encoding.ASCII.GetBytes(hash)))
            {
                found = info;
            }
        }

        return found;
    }

    /// <summary>
    /// Hash an API key with SHA-256 for storage and lookup.
    /// </summary>
    internal static string HashKey(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// A key's public ID. Part of its hash, which identifies it without letting anyone test guesses
    /// of the key against the full hash.
    /// </summary>
    internal static string PrincipalIdFor(string keyHash) => $"key-{keyHash[..16]}";

    private static List<string> GetDefaultPermissions(string tier)
    {
        return tier.ToUpper() switch
        {
            "ADMIN" or "ENTERPRISE" => new List<string> { "Read", "Write", "Delete", "Admin" },
            "PROFESSIONAL" => new List<string> { "Read", "Write" },
            "INGEST" => new List<string> { "Write" },
            "BASIC" => new List<string> { "Read" },
            _ => new List<string> { "Read" }
        };
    }
}

/// <summary>A key made in the dashboard, or a revocation, as kept in the document store.</summary>
public sealed class StoredApiKey
{
    public required string Id { get; set; }

    /// <summary>SHA-256 of the key. The key itself is never kept.</summary>
    public string? KeyHash { get; set; }
    public required string Name { get; set; }
    public required string Tier { get; set; }
    public string? Prefix { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>dashboard for keys made there; settings for a revocation of a key from settings.</summary>
    public required string Source { get; set; }
    public bool Revoked { get; set; }
    public DateTime? RevokedAt { get; set; }
}
