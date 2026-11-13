using System.Collections.Concurrent;
using System.Net;

namespace Cabinet.API.Middleware;

/// <summary>
/// Pre-authentication IP-based rate limiting middleware.
/// Runs BEFORE authentication to protect against brute-force attacks
/// on the auth layer (e.g., API key guessing, credential stuffing).
/// Uses a sliding window algorithm with per-IP counters.
/// Only requests that end unauthenticated count: a valid key or session gives its count back, so
/// a busy sender such as a log forwarder isn't throttled, while an address that has used up its
/// allowance on failures is refused before authentication is even tried.
/// </summary>
public sealed class IpRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IpRateLimitingMiddleware> _logger;
    private readonly int _maxRequestsPerWindow;
    private readonly TimeSpan _window;

    // IP -> (requestCount, windowStart)
    private static readonly ConcurrentDictionary<string, IpRateEntry> _entries = new();
    private static DateTime _lastCleanup = DateTime.UtcNow;
    private static readonly object _cleanupLock = new();
    private const int MaxTrackedIps = 100_000;

    public IpRateLimitingMiddleware(
        RequestDelegate next,
        ILogger<IpRateLimitingMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _maxRequestsPerWindow = configuration.GetValue("RateLimiting:PreAuth:MaxRequestsPerMinute", 300);
        _window = TimeSpan.FromSeconds(configuration.GetValue("RateLimiting:PreAuth:WindowSeconds", 60));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip rate limiting for health and metrics endpoints (used by load balancers/k8s/Prometheus)
        var path = context.Request.Path.Value;
        if (path != null && (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/metrics", StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        var ip = GetClientIp(context);
        var now = DateTime.UtcNow;

        // Periodic cleanup of stale entries (every 5 minutes)
        CleanupStaleEntries(now);

        var entry = _entries.GetOrAdd(ip, _ => new IpRateEntry(now));

        // If the window has expired, reset
        if (now - entry.WindowStart > _window)
        {
            entry.Reset(now);
        }

        var requestCount = entry.Increment();

        // Each instance enforces its share, so the limit holds across the cluster
        var instances = context.RequestServices?.GetService<Cabinet.Core.Interfaces.IClusterCoordinator>()?.LiveInstances ?? 1;
        if (requestCount > Math.Max(1, _maxRequestsPerWindow / Math.Max(1, instances)))
        {
            _logger.LogWarning(
                "Pre-auth rate limit exceeded for IP {IP}: {Count} requests in {Window}s window",
                ip, requestCount, _window.TotalSeconds);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";

            var retryAfter = (int)(_window - (now - entry.WindowStart)).TotalSeconds;
            retryAfter = Math.Max(1, retryAfter);
            context.Response.Headers.Append("Retry-After", retryAfter.ToString());

            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://httpstatuses.com/429",
                title = "Too Many Requests",
                status = 429,
                detail = "Rate limit exceeded. Please retry later.",
                retryAfter
            });
            return;
        }

        await _next(context);

        // What authenticated was never a guess; the ASP.NET limiter after authentication covers it
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            entry.Decrement();
        }
    }

    private static string GetClientIp(HttpContext context)
    {
        // Never read X-Forwarded-For here: any client can set it. Behind a configured proxy
        // (Network:TrustedProxies), forwarded-header middleware has already resolved this.
        // If no IP is available, use a sentinel that still gets rate-limited.
        return context.Connection.RemoteIpAddress?.ToString() ?? "0.0.0.0";
    }

    private static void CleanupStaleEntries(DateTime now)
    {
        // Force cleanup if dictionary exceeds memory bounds (regardless of time)
        var forceCleanup = _entries.Count > MaxTrackedIps;

        // Only clean up every 5 minutes to avoid lock contention (unless forced)
        if (!forceCleanup && now - _lastCleanup < TimeSpan.FromMinutes(5))
            return;

        // Thread-safety notes:
        // - The lock prevents concurrent cleanup runs but does NOT block request threads
        //   from calling GetOrAdd/Increment concurrently — ConcurrentDictionary handles that.
        // - ConcurrentDictionary.GetEnumerator() is weakly consistent: iteration won't throw
        //   on concurrent modifications, though it may see or miss in-flight additions.
        // - Keys are materialized to a List via .ToList() BEFORE the TryRemove loop, so we
        //   never modify the dictionary while iterating it.
        lock (_cleanupLock)
        {
            if (!forceCleanup && now - _lastCleanup < TimeSpan.FromMinutes(5))
                return;

            _lastCleanup = now;
            var staleThreshold = now - TimeSpan.FromMinutes(10);
            var staleKeys = _entries
                .Where(kvp => kvp.Value.WindowStart < staleThreshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in staleKeys)
            {
                _entries.TryRemove(key, out _);
            }

            // If still over limit after removing stale entries, evict oldest
            if (_entries.Count > MaxTrackedIps)
            {
                var excess = _entries
                    .OrderBy(kvp => kvp.Value.WindowStart)
                    .Take(_entries.Count - MaxTrackedIps + MaxTrackedIps / 10) // Remove 10% extra headroom
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in excess)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }
    }

    // Exposed for testing
    internal static void Reset()
    {
        _entries.Clear();
        _lastCleanup = DateTime.UtcNow;
    }

    private sealed class IpRateEntry
    {
        private int _count;
        public DateTime WindowStart { get; private set; }

        public IpRateEntry(DateTime windowStart)
        {
            WindowStart = windowStart;
            _count = 0;
        }

        public int Increment() => Interlocked.Increment(ref _count);

        public void Decrement() => Interlocked.Decrement(ref _count);

        /// <summary>
        /// Resets the window. Note: WindowStart and _count are not atomically updated together,
        /// so a concurrent Increment() between the two assignments could be lost. This is acceptable
        /// for rate limiting — at worst one request at a window boundary is undercounted, which does
        /// not compromise the security guarantee (the next window will enforce correctly).
        /// Making this fully atomic would require a lock on every Increment(), which is too costly.
        /// </summary>
        public void Reset(DateTime newWindowStart)
        {
            WindowStart = newWindowStart;
            Interlocked.Exchange(ref _count, 0);
        }
    }
}
