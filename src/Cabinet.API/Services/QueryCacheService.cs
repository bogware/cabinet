using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>
/// In-memory query result cache for dashboard refresh acceleration.
/// Uses a time-bounded LRU cache with configurable TTL.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class QueryCacheService
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private readonly ILogger<QueryCacheService> _logger;
    private long _hits;
    private long _misses;

    public QueryCacheService(ILogger<QueryCacheService> logger, TimeSpan? defaultTtl = null, int maxEntries = 1000)
    {
        _logger = logger;
        _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
        _maxEntries = maxEntries;
    }

    /// <summary>Cache hit count for metrics</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Cache miss count for metrics</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Current cache entry count</summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Try to get a cached result. Returns null on miss.
    /// </summary>
    public QueryResult? TryGet(string query, string? index = null)
    {
        var key = ComputeCacheKey(query, index);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow < entry.ExpiresAt)
            {
                entry.LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _hits);
                return entry.Result;
            }

            // Expired — remove it
            _cache.TryRemove(key, out _);
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Cache a query result with optional custom TTL.
    /// </summary>
    public void Set(string query, QueryResult result, string? index = null, TimeSpan? ttl = null)
    {
        var key = ComputeCacheKey(query, index);
        var effectiveTtl = ttl ?? _defaultTtl;

        // Evict if at capacity
        if (_cache.Count >= _maxEntries)
            EvictOldest();

        _cache[key] = new CacheEntry
        {
            Result = result,
            CreatedAt = DateTime.UtcNow,
            LastAccessedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + effectiveTtl
        };
    }

    /// <summary>
    /// Invalidate all cached results.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _logger.LogInformation("Query cache cleared ({Count} entries evicted)", count);
    }

    /// <summary>
    /// Invalidate cached results for a specific index.
    /// </summary>
    public void InvalidateIndex(string index)
    {
        var prefix = $"{index}:";
        var keysToRemove = _cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
            _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Get cache statistics for metrics endpoint.
    /// </summary>
    public CacheStats GetStats() => new()
    {
        EntryCount = _cache.Count,
        HitCount = Hits,
        MissCount = Misses,
        HitRate = (Hits + Misses) > 0 ? (double)Hits / (Hits + Misses) : 0
    };

    private void EvictOldest()
    {
        // Evict the 10% oldest entries by last access time
        var toEvict = _cache
            .OrderBy(kvp => kvp.Value.LastAccessedAt)
            .Take(Math.Max(1, _maxEntries / 10))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
            _cache.TryRemove(key, out _);
    }

    private static string ComputeCacheKey(string query, string? index)
    {
        var input = $"{index ?? ""}:{query}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"{index ?? "default"}:{Convert.ToHexString(hash[..16]).ToLowerInvariant()}";
    }

    private sealed class CacheEntry
    {
        public required QueryResult Result { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime LastAccessedAt { get; set; }
        public DateTime ExpiresAt { get; init; }
    }
}

public sealed class CacheStats
{
    public int EntryCount { get; set; }
    public long HitCount { get; set; }
    public long MissCount { get; set; }
    public double HitRate { get; set; }
}
