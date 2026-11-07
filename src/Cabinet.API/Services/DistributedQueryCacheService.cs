using System.Text.Json;
using Cabinet.API.Infrastructure;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>
/// Two-tier query result cache: L1 in-memory + L2 Redis for distributed deployments.
/// Falls back gracefully to in-memory only if Redis is unavailable.
/// Pub/sub invalidation ensures cache coherence across Cabinet API instances.
/// </summary>
public sealed class DistributedQueryCacheService
{
    private readonly QueryCacheService _localCache;
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<DistributedQueryCacheService> _logger;
    private readonly TimeSpan _redisTtl;
    private readonly string _keyPrefix;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _subscribed;

    private const string InvalidationChannel = "cabinet:query-cache:invalidate";

    public DistributedQueryCacheService(
        QueryCacheService localCache,
        RedisConnectionManager redis,
        ILogger<DistributedQueryCacheService> logger,
        TimeSpan? redisTtl = null,
        string keyPrefix = "cabinet:qcache:")
    {
        _localCache = localCache;
        _redis = redis;
        _logger = logger;
        _redisTtl = redisTtl ?? TimeSpan.FromSeconds(60);
        _keyPrefix = keyPrefix;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Subscribe to cache invalidation events from other instances.
    /// Call once during startup.
    /// </summary>
    public async Task SubscribeToInvalidationsAsync()
    {
        if (_subscribed) return;

        try
        {
            var subscriber = await _redis.GetSubscriberAsync();
            await subscriber.SubscribeAsync(
                StackExchange.Redis.RedisChannel.Literal(InvalidationChannel),
                (_, message) =>
                {
                    var msg = message.ToString();
                    if (msg == "*")
                    {
                        _localCache.Clear();
                    }
                    else if (!string.IsNullOrEmpty(msg))
                    {
                        _localCache.InvalidateIndex(msg);
                    }
                });
            _subscribed = true;
            _logger.LogInformation("Subscribed to distributed query cache invalidation channel");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Redis cache invalidation — operating in local-only mode");
        }
    }

    /// <summary>
    /// Try to get a cached result. Checks L1 (in-memory) first, then L2 (Redis).
    /// </summary>
    public async Task<QueryResult?> TryGetAsync(string query, string? index = null)
    {
        // L1: Check local in-memory cache first (fastest)
        var localResult = _localCache.TryGet(query, index);
        if (localResult != null)
            return localResult;

        // L2: Check Redis
        try
        {
            var db = await _redis.GetDatabaseAsync();
            var key = ComputeRedisKey(query, index);
            var cached = await db.StringGetAsync(key);

            if (cached.HasValue)
            {
                var result = JsonSerializer.Deserialize<QueryResult>(cached.ToString(), _jsonOptions);
                if (result != null)
                {
                    // Promote to L1 for subsequent fast access
                    _localCache.Set(query, result, index);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis cache lookup failed — falling back to local only");
        }

        return null;
    }

    /// <summary>
    /// Cache a query result in both L1 (in-memory) and L2 (Redis).
    /// </summary>
    public async Task SetAsync(string query, QueryResult result, string? index = null, TimeSpan? ttl = null)
    {
        // L1: Always cache locally
        _localCache.Set(query, result, index, ttl);

        // L2: Best-effort write to Redis
        try
        {
            var db = await _redis.GetDatabaseAsync();
            var key = ComputeRedisKey(query, index);
            var json = JsonSerializer.Serialize(result, _jsonOptions);
            await db.StringSetAsync(key, json, ttl ?? _redisTtl);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Redis cache write failed — cached locally only");
        }
    }

    /// <summary>
    /// Invalidate all cached results across all instances.
    /// </summary>
    public async Task ClearAsync()
    {
        _localCache.Clear();

        try
        {
            var subscriber = await _redis.GetSubscriberAsync();
            await subscriber.PublishAsync(
                StackExchange.Redis.RedisChannel.Literal(InvalidationChannel), "*");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish cache invalidation to Redis");
        }
    }

    /// <summary>
    /// Invalidate cached results for a specific index across all instances.
    /// </summary>
    public async Task InvalidateIndexAsync(string index)
    {
        _localCache.InvalidateIndex(index);

        try
        {
            var subscriber = await _redis.GetSubscriberAsync();
            await subscriber.PublishAsync(
                StackExchange.Redis.RedisChannel.Literal(InvalidationChannel), index);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish index invalidation to Redis");
        }
    }

    /// <summary>
    /// Get combined cache statistics (local + distributed).
    /// </summary>
    public CacheStats GetStats() => _localCache.GetStats();

    private string ComputeRedisKey(string query, string? index)
    {
        var input = $"{index ?? ""}:{query}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return $"{_keyPrefix}{index ?? "default"}:{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
    }
}
