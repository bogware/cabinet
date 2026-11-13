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
    private readonly ConcurrentDictionary<string, long> _lastWriteByIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxRows;
    private long _lastWriteAny;
    private long _rows;
    private long _hits;
    private long _misses;

    public QueryCacheService(ILogger<QueryCacheService> logger, TimeSpan? defaultTtl = null, int maxEntries = 1000, long maxRows = 500_000)
    {
        _logger = logger;
        _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
        _maxEntries = maxEntries;
        _maxRows = Math.Max(1, maxRows);
    }

    /// <summary>Rows held across all entries, which is what the cache's memory follows.</summary>
    public long Rows => Interlocked.Read(ref _rows);

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
            if (DateTime.UtcNow < entry.ExpiresAt && !WrittenSince(entry))
            {
                entry.LastAccessedAt = DateTime.UtcNow;
                Interlocked.Increment(ref _hits);
                return entry.Result;
            }

            // Expired, or events arrived that it would include
            Remove(key);
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    /// <summary>
    /// Cache a query result with optional custom TTL.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="result">Its result.</param>
    /// <param name="index">Keeps entries apart, such as for callers who see different data.</param>
    /// <param name="ttl">How long it's kept at most.</param>
    /// <param name="dataIndex">The one index the query reads, or null when it may read any.</param>
    /// <param name="createdAt">When the query started, in <see cref="Stopwatch"/> ticks; events written after it may be missing.</param>
    public void Set(string query, QueryResult result, string? index = null, TimeSpan? ttl = null, string? dataIndex = null, long? createdAt = null)
    {
        var key = ComputeCacheKey(query, index);
        var effectiveTtl = ttl ?? _defaultTtl;
        var rows = result.Events.Count;
        if (rows > _maxRows / 4)
        {
            // One result this large would push out most others
            return;
        }

        // Evict if at capacity
        if (_cache.Count >= _maxEntries || Interlocked.Read(ref _rows) + rows > _maxRows)
            EvictOldest();

        var entry = new CacheEntry
        {
            Result = result,
            Rows = rows,
            DataIndex = dataIndex,
            CreatedAt = DateTime.UtcNow,
            Version = createdAt ?? System.Diagnostics.Stopwatch.GetTimestamp(),
            LastAccessedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + effectiveTtl
        };
        if (_cache.TryRemove(key, out var replaced))
            Interlocked.Add(ref _rows, -replaced.Rows);
        if (_cache.TryAdd(key, entry))
            Interlocked.Add(ref _rows, rows);
    }

    /// <summary>A time to pass to <see cref="Set"/> as when a query started.</summary>
    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

    private bool WrittenSince(CacheEntry entry)
    {
        // A write in the same clock tick the query started in may not be in its result, so it counts
        if (Interlocked.Read(ref _lastWriteAny) < entry.Version)
            return false;

        // A query of every index sees any write; one of a single index only that index's
        return entry.DataIndex == null
            || (_lastWriteByIndex.TryGetValue(entry.DataIndex, out var written) && written >= entry.Version);
    }

    private void Remove(string key)
    {
        if (_cache.TryRemove(key, out var removed))
            Interlocked.Add(ref _rows, -removed.Rows);
    }

    /// <summary>
    /// Invalidate all cached results.
    /// </summary>
    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        Interlocked.Exchange(ref _rows, 0);
        _logger.LogInformation("Query cache cleared ({Count} entries evicted)", count);
    }

    /// <summary>
    /// Invalidate cached results for a specific index.
    /// </summary>
    public void InvalidateIndex(string index)
    {
        // Entries are checked when read, so a busy ingest costs a timestamp, not a scan
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        _lastWriteByIndex[index] = now;
        Interlocked.Exchange(ref _lastWriteAny, now);
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
            Remove(key);
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
        public long Version { get; init; }
        public int Rows { get; init; }
        public string? DataIndex { get; init; }
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
