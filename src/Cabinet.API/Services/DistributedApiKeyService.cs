using Cabinet.API.Authentication;
using Cabinet.API.Infrastructure;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Text.Json;

namespace Cabinet.API.Services;

/// <summary>
/// Distributed API key service that wraps the base ApiKeyService with Redis caching.
/// Ensures API key state is shared across multiple application instances.
/// Falls back to the inner service if Redis is unavailable.
/// </summary>
public sealed class DistributedApiKeyService : IApiKeyService
{
    private readonly IApiKeyService _inner;
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<DistributedApiKeyService> _logger;
    private readonly TimeSpan _cacheTtl;

    private const string CachePrefix = "apikey:";
    private const string InvalidationChannel = "apikey:invalidate";

    public DistributedApiKeyService(
        IApiKeyService inner,
        RedisConnectionManager redis,
        ILogger<DistributedApiKeyService> logger,
        TimeSpan? cacheTtl = null)
    {
        _inner = inner;
        _redis = redis;
        _logger = logger;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(60);
    }

    public async Task<ApiKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        // Use hashed key for Redis cache — never store raw keys in cache
        var cacheKey = $"{CachePrefix}{ApiKeyService.HashKey(apiKey)}";

        // Try Redis cache first
        try
        {
            var db = await _redis.GetDatabaseAsync();
            var cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                var result = JsonSerializer.Deserialize<ApiKeyValidationResult>(cached.ToString());
                if (result != null) return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis cache miss for API key validation, falling back to inner service");
        }

        // Fall back to inner service
        var validationResult = await _inner.ValidateKeyAsync(apiKey);

        // Only cache valid results — prevents cache poisoning from invalid key spray
        if (validationResult.IsValid)
        {
            try
            {
                var db = await _redis.GetDatabaseAsync();
                var json = JsonSerializer.Serialize(validationResult);
                await db.StringSetAsync(cacheKey, json, _cacheTtl);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to cache API key validation result");
            }
        }

        return validationResult;
    }

    public async Task<string> GenerateKeyAsync(string clientName, string tier = "Basic", List<string>? permissions = null)
    {
        var key = await _inner.GenerateKeyAsync(clientName, tier, permissions);

        // Invalidate cache across all instances
        await PublishInvalidationAsync(key);

        return key;
    }

    public async Task<bool> RevokeKeyAsync(string apiKey)
    {
        var result = await _inner.RevokeKeyAsync(apiKey);

        if (result)
        {
            // Remove from cache and notify other instances
            await InvalidateCacheAsync(apiKey);
            await PublishInvalidationAsync(apiKey);
        }

        return result;
    }

    public Task<List<ApiKeyInfo>> GetActiveKeysAsync()
    {
        // Always go to the inner service for the full list
        return _inner.GetActiveKeysAsync();
    }

    /// <summary>
    /// Subscribe to cache invalidation events from other instances.
    /// Call this during application startup.
    /// </summary>
    public async Task SubscribeToInvalidationsAsync()
    {
        try
        {
            var subscriber = await _redis.GetSubscriberAsync();
            await subscriber.SubscribeAsync(RedisChannel.Literal(InvalidationChannel), async (_, message) =>
            {
                if (message.HasValue)
                {
                    // Message is already a hashed key — delete directly from cache
                    try
                    {
                        var db = await _redis.GetDatabaseAsync();
                        await db.KeyDeleteAsync($"{CachePrefix}{message}");
                        _logger.LogDebug("Invalidated cached API key from remote event");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogDebug(ex2, "Failed to invalidate cached API key from remote event");
                    }
                }
            });
            _logger.LogInformation("Subscribed to API key invalidation events");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to API key invalidation events");
        }
    }

    private async Task InvalidateCacheAsync(string apiKey)
    {
        try
        {
            var hashedKey = ApiKeyService.HashKey(apiKey);
            var db = await _redis.GetDatabaseAsync();
            await db.KeyDeleteAsync($"{CachePrefix}{hashedKey}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to invalidate cached API key");
        }
    }

    private async Task PublishInvalidationAsync(string apiKey)
    {
        try
        {
            // Publish the hash, never the raw key
            var hashedKey = ApiKeyService.HashKey(apiKey);
            var subscriber = await _redis.GetSubscriberAsync();
            await subscriber.PublishAsync(RedisChannel.Literal(InvalidationChannel), hashedKey);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish API key invalidation event");
        }
    }
}
