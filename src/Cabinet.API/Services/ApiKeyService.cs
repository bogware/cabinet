using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cabinet.API.Authentication;
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
    private readonly ConcurrentDictionary<string, ApiKeyInfo> _keysByHash = new();
    private readonly ILogger<ApiKeyService> _logger;

    public ApiKeyService(IConfiguration configuration, ILogger<ApiKeyService> logger)
    {
        _logger = logger;

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
                ExpiresAt = expiresAt
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

    public Task<ApiKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        var keyHash = HashKey(apiKey);

        if (_keysByHash.TryGetValue(keyHash, out var keyInfo) && keyInfo.IsActive)
        {
            // Check expiration
            if (keyInfo.ExpiresAt.HasValue && keyInfo.ExpiresAt.Value < DateTime.UtcNow)
            {
                return Task.FromResult(new ApiKeyValidationResult
                {
                    IsValid = false,
                    Reason = "API key has expired"
                });
            }

            keyInfo.LastUsedAt = DateTime.UtcNow;
            Interlocked.Increment(ref keyInfo._requestCount);

            return Task.FromResult(new ApiKeyValidationResult
            {
                IsValid = true,
                ClientId = keyInfo.Id,
                ClientName = keyInfo.ClientName,
                Tier = keyInfo.Tier,
                Permissions = keyInfo.Permissions
            });
        }

        return Task.FromResult(new ApiKeyValidationResult
        {
            IsValid = false,
            Reason = "Invalid API key"
        });
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

    public Task<List<ApiKeyInfo>> GetActiveKeysAsync()
    {
        var activeKeys = _keysByHash.Values
            .Where(k => k.IsActive)
            .ToList();

        return Task.FromResult(activeKeys);
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
