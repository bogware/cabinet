using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Redis-backed distributed rate limiter using sliding window algorithm.
/// Ensures consistent rate limiting across multiple application instances.
/// Falls back to local in-memory rate limiting if Redis is unavailable (never fails fully open).
/// </summary>
/// <remarks>
/// This class is not currently wired into the request pipeline. Rate limiting is handled
/// by the ASP.NET Core built-in rate limiter configured in AddCabinetRateLimiting().
/// Retained for potential future use in distributed deployments.
/// </remarks>
[Obsolete("Not wired into the request pipeline. Rate limiting uses ASP.NET Core built-in rate limiter. Will be removed or integrated in a future version.")]
public sealed class DistributedRateLimiter
{
    private readonly RedisConnectionManager _redis;
    private readonly ILogger<DistributedRateLimiter> _logger;

    // Local fallback: per-key sliding window counters (used when Redis is unavailable)
    private readonly ConcurrentDictionary<string, LocalRateLimitBucket> _localBuckets = new();

    public DistributedRateLimiter(RedisConnectionManager redis, ILogger<DistributedRateLimiter> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Try to acquire a rate limit permit using Redis sorted set sliding window.
    /// Falls back to local in-memory sliding window if Redis is unavailable.
    /// </summary>
    public async Task<RateLimitResult> TryAcquireAsync(string key, int maxRequests, TimeSpan window)
    {
        try
        {
            var db = await _redis.GetDatabaseAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var windowStart = now - (long)window.TotalMilliseconds;
            var redisKey = $"ratelimit:{key}";

            // Lua script for atomic sliding window rate limiting
            var script = @"
                local key = KEYS[1]
                local now = tonumber(ARGV[1])
                local window_start = tonumber(ARGV[2])
                local max_requests = tonumber(ARGV[3])
                local window_ms = tonumber(ARGV[4])

                -- Remove expired entries
                redis.call('ZREMRANGEBYSCORE', key, 0, window_start)

                -- Count current entries
                local current = redis.call('ZCARD', key)

                if current < max_requests then
                    -- Add new entry with current timestamp as score
                    redis.call('ZADD', key, now, now .. ':' .. math.random(1000000))
                    redis.call('PEXPIRE', key, window_ms)
                    return {1, max_requests - current - 1, 0}
                else
                    -- Rate limited — calculate retry-after from oldest entry
                    local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
                    local retry_after = 0
                    if #oldest >= 2 then
                        retry_after = tonumber(oldest[2]) + window_ms - now
                    end
                    return {0, 0, retry_after}
                end
            ";

            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { redisKey },
                new RedisValue[] { now, windowStart, maxRequests, (long)window.TotalMilliseconds });

            if (result == null || result.Length < 3)
            {
                _logger.LogWarning("Unexpected Redis rate limit response, falling back to local limiter");
                return TryAcquireLocal(key, maxRequests, window);
            }

            var allowed = (int)result[0] == 1;
            var remaining = (int)result[1];
            var retryAfterMs = (long)result[2];

            return allowed
                ? RateLimitResult.Allowed(remaining)
                : RateLimitResult.Limited(TimeSpan.FromMilliseconds(retryAfterMs));
        }
        catch (Exception ex)
        {
            // Redis unavailable: fall back to local per-instance rate limiting (NOT fail-open)
            _logger.LogWarning(ex, "Redis rate limiter unavailable, using local fallback for key: {Key}", key);
            return TryAcquireLocal(key, maxRequests, window);
        }
    }

    /// <summary>
    /// Local in-memory rate limiter fallback. Per-instance only (not distributed),
    /// but still provides protection against abuse when Redis is down.
    /// </summary>
    private RateLimitResult TryAcquireLocal(string key, int maxRequests, TimeSpan window)
    {
        var bucket = _localBuckets.GetOrAdd(key, _ => new LocalRateLimitBucket());
        return bucket.TryAcquire(maxRequests, window);
    }

    private sealed class LocalRateLimitBucket
    {
        private readonly object _lock = new();
        private readonly Queue<long> _timestamps = new();

        public RateLimitResult TryAcquire(int maxRequests, TimeSpan window)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var windowStart = now - (long)window.TotalMilliseconds;

            lock (_lock)
            {
                // Evict expired entries
                while (_timestamps.Count > 0 && _timestamps.Peek() < windowStart)
                    _timestamps.Dequeue();

                if (_timestamps.Count < maxRequests)
                {
                    _timestamps.Enqueue(now);
                    return RateLimitResult.Allowed(maxRequests - _timestamps.Count);
                }

                var oldest = _timestamps.Peek();
                var retryAfterMs = oldest + (long)window.TotalMilliseconds - now;
                return RateLimitResult.Limited(TimeSpan.FromMilliseconds(Math.Max(0, retryAfterMs)));
            }
        }
    }
}

/// <summary>
/// Result of a rate limit check
/// </summary>
public sealed class RateLimitResult
{
    public bool IsAllowed { get; private init; }
    public int Remaining { get; private init; }
    public TimeSpan RetryAfter { get; private init; }

    public static RateLimitResult Allowed(int remaining) => new()
    {
        IsAllowed = true,
        Remaining = remaining
    };

    public static RateLimitResult Limited(TimeSpan retryAfter) => new()
    {
        IsAllowed = false,
        RetryAfter = retryAfter
    };
}
