using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Middleware;

/// <summary>
/// Lightweight Prometheus-compatible metrics endpoint.
/// Tracks HTTP request counts, latencies, and storage statistics.
/// Exposes /metrics in Prometheus text exposition format without external dependencies.
/// </summary>
public sealed class MetricsMiddleware
{
    // Prometheus scrapes every few seconds, and these stats scan the whole table
    private readonly TimeSpan StatsCacheDuration;
    private readonly SemaphoreSlim _statsLock = new(1, 1);
    private StorageStats? _cachedStats;
    private DateTime _cachedStatsAt;

    private async Task<StorageStats> GetCachedStatsAsync(ILogStorage storage, CancellationToken cancellationToken)
    {
        await _statsLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedStats == null || DateTime.UtcNow - _cachedStatsAt >= StatsCacheDuration)
            {
                _cachedStats = await storage.GetStatsAsync(cancellationToken);
                _cachedStatsAt = DateTime.UtcNow;
            }

            return _cachedStats;
        }
        finally
        {
            _statsLock.Release();
        }
    }

    private readonly RequestDelegate _next;

    public MetricsMiddleware(RequestDelegate next, Cabinet.API.Infrastructure.ServerLimits? limits = null)
    {
        _next = next;
        StatsCacheDuration = TimeSpan.FromSeconds((limits ?? new Cabinet.API.Infrastructure.ServerLimits()).MetricsCacheSeconds);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Exactly, so that a proxy rule blocking /metrics also blocks every way of asking for it
        if (context.Request.Path.Equals("/metrics", StringComparison.Ordinal))
        {
            // Design decision: /metrics is intentionally unauthenticated.
            // Prometheus scrapers typically do not support API key authentication, and requiring
            // auth here would break standard monitoring setups. Access should be restricted at
            // the network level instead — e.g., Kubernetes NetworkPolicy, firewall rules, or
            // ingress annotations that limit /metrics to the monitoring namespace/CIDR only.
            // See also: IpRateLimitingMiddleware bypasses /metrics to avoid false positives.
            await WriteMetricsAsync(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            var method = LabelForMethod(context.Request.Method);
            var path = LabelForPath(context);
            var status = context.Response.StatusCode.ToString();

            MetricsCollector.RecordRequest(method, path, status, sw.Elapsed.TotalSeconds);
        }
    }

    private async Task WriteMetricsAsync(HttpContext context)
    {
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        context.Response.StatusCode = 200;

        var sb = new StringBuilder(4096);

        // HTTP request metrics
        sb.AppendLine("# HELP cabinet_http_requests_total Total number of HTTP requests.");
        sb.AppendLine("# TYPE cabinet_http_requests_total counter");
        foreach (var entry in MetricsCollector.GetRequestCounts())
        {
            sb.AppendLine($"cabinet_http_requests_total{{method=\"{entry.Method}\",path=\"{entry.Path}\",status=\"{entry.Status}\"}} {entry.Count}");
        }

        sb.AppendLine("# HELP cabinet_http_request_duration_seconds HTTP request duration in seconds.");
        sb.AppendLine("# TYPE cabinet_http_request_duration_seconds histogram");
        foreach (var entry in MetricsCollector.GetLatencyBuckets())
        {
            sb.AppendLine($"cabinet_http_request_duration_seconds_bucket{{method=\"{entry.Method}\",path=\"{entry.Path}\",le=\"{entry.Boundary}\"}} {entry.Count}");
            if (entry.IsInf)
            {
                sb.AppendLine($"cabinet_http_request_duration_seconds_sum{{method=\"{entry.Method}\",path=\"{entry.Path}\"}} {entry.Sum:F6}");
                sb.AppendLine($"cabinet_http_request_duration_seconds_count{{method=\"{entry.Method}\",path=\"{entry.Path}\"}} {entry.TotalCount}");
            }
        }

        // Storage metrics
        var storage = context.RequestServices.GetService<ILogStorage>();
        if (storage != null)
        {
            try
            {
                // Timeout storage stats to prevent hanging the metrics endpoint
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var stats = await GetCachedStatsAsync(storage, cts.Token);
                sb.AppendLine("# HELP cabinet_events_total Total number of stored log events.");
                sb.AppendLine("# TYPE cabinet_events_total gauge");
                sb.AppendLine($"cabinet_events_total {stats.TotalEvents}");

                sb.AppendLine("# HELP cabinet_storage_size_bytes Total storage size in bytes.");
                sb.AppendLine("# TYPE cabinet_storage_size_bytes gauge");
                sb.AppendLine($"cabinet_storage_size_bytes {stats.StorageSizeBytes}");
            }
            catch (Exception ex)
            {
                // Log but don't fail metrics if storage is unavailable
                var logger = context.RequestServices.GetService<ILogger<MetricsMiddleware>>();
                logger?.LogWarning(ex, "Failed to collect storage metrics");

                sb.AppendLine("# HELP cabinet_storage_errors_total Storage stats collection errors.");
                sb.AppendLine("# TYPE cabinet_storage_errors_total counter");
                sb.AppendLine("cabinet_storage_errors_total 1");
            }
        }

        // Process metrics
        var process = Process.GetCurrentProcess();
        sb.AppendLine("# HELP process_cpu_seconds_total Total user and system CPU time spent in seconds.");
        sb.AppendLine("# TYPE process_cpu_seconds_total counter");
        sb.AppendLine($"process_cpu_seconds_total {process.TotalProcessorTime.TotalSeconds:F2}");

        sb.AppendLine("# HELP process_working_set_bytes Process working set in bytes.");
        sb.AppendLine("# TYPE process_working_set_bytes gauge");
        sb.AppendLine($"process_working_set_bytes {process.WorkingSet64}");

        sb.AppendLine("# HELP process_private_memory_bytes Process private memory in bytes.");
        sb.AppendLine("# TYPE process_private_memory_bytes gauge");
        sb.AppendLine($"process_private_memory_bytes {process.PrivateMemorySize64}");

        sb.AppendLine("# HELP process_num_threads Number of process threads.");
        sb.AppendLine("# TYPE process_num_threads gauge");
        sb.AppendLine($"process_num_threads {process.Threads.Count}");

        sb.AppendLine("# HELP process_start_time_seconds Start time of the process since unix epoch in seconds.");
        sb.AppendLine("# TYPE process_start_time_seconds gauge");
        sb.AppendLine($"process_start_time_seconds {new DateTimeOffset(process.StartTime).ToUnixTimeSeconds()}");

        // GC metrics
        sb.AppendLine("# HELP dotnet_gc_collections_total Total GC collections by generation.");
        sb.AppendLine("# TYPE dotnet_gc_collections_total counter");
        for (var gen = 0; gen <= 2; gen++)
        {
            sb.AppendLine($"dotnet_gc_collections_total{{generation=\"{gen}\"}} {GC.CollectionCount(gen)}");
        }

        sb.AppendLine("# HELP dotnet_gc_memory_total_available_bytes Total available GC memory.");
        sb.AppendLine("# TYPE dotnet_gc_memory_total_available_bytes gauge");
        sb.AppendLine($"dotnet_gc_memory_total_available_bytes {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}");

        // Query cache metrics
        var queryCache = context.RequestServices.GetService<Cabinet.API.Services.QueryCacheService>();
        if (queryCache != null)
        {
            var cacheStats = queryCache.GetStats();
            sb.AppendLine("# HELP cabinet_query_cache_entries Current query cache entry count.");
            sb.AppendLine("# TYPE cabinet_query_cache_entries gauge");
            sb.AppendLine($"cabinet_query_cache_entries {cacheStats.EntryCount}");

            sb.AppendLine("# HELP cabinet_query_cache_hits_total Total query cache hits.");
            sb.AppendLine("# TYPE cabinet_query_cache_hits_total counter");
            sb.AppendLine($"cabinet_query_cache_hits_total {cacheStats.HitCount}");

            sb.AppendLine("# HELP cabinet_query_cache_misses_total Total query cache misses.");
            sb.AppendLine("# TYPE cabinet_query_cache_misses_total counter");
            sb.AppendLine($"cabinet_query_cache_misses_total {cacheStats.MissCount}");
        }

        await context.Response.WriteAsync(sb.ToString());
    }

    /// <summary>
    /// Uses the matched route template. Requests are attacker-controlled and these labels are
    /// kept forever, so raw paths would be both a memory leak and a label injection.
    /// </summary>
    private static string LabelForPath(HttpContext context) =>
        context.GetEndpoint() is RouteEndpoint endpoint && endpoint.RoutePattern.RawText is { } pattern
            ? "/" + pattern.TrimStart('/')
            : "unmatched";

    private static string LabelForMethod(string method) =>
        method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? method
            : "OTHER";
}

/// <summary>
/// In-memory metrics collector with thread-safe counters.
/// </summary>
public static class MetricsCollector
{
    private static readonly ConcurrentDictionary<string, long> RequestCounts = new();
    private static readonly ConcurrentDictionary<string, LatencyHistogram> Latencies = new();

    private static readonly double[] Buckets = { 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10 };

    public static void RecordRequest(string method, string path, string status, double durationSeconds)
    {
        var countKey = $"{method}|{path}|{status}";
        RequestCounts.AddOrUpdate(countKey, 1, (_, count) => count + 1);

        var latencyKey = $"{method}|{path}";
        var histogram = Latencies.GetOrAdd(latencyKey, _ => new LatencyHistogram());
        histogram.Record(durationSeconds);
    }

    public static IEnumerable<RequestCountEntry> GetRequestCounts()
    {
        foreach (var kvp in RequestCounts)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length == 3)
            {
                yield return new RequestCountEntry(parts[0], parts[1], parts[2], kvp.Value);
            }
        }
    }

    public static IEnumerable<LatencyBucketEntry> GetLatencyBuckets()
    {
        foreach (var kvp in Latencies)
        {
            var parts = kvp.Key.Split('|');
            if (parts.Length != 2) continue;

            var method = parts[0];
            var path = parts[1];
            var histogram = kvp.Value;
            var bucketCounts = histogram.GetBucketCounts();

            for (var i = 0; i < Buckets.Length; i++)
            {
                yield return new LatencyBucketEntry(method, path, Buckets[i].ToString("F3"), bucketCounts[i], false, histogram.Sum, histogram.Count);
            }
            yield return new LatencyBucketEntry(method, path, "+Inf", histogram.Count, true, histogram.Sum, histogram.Count);
        }
    }

    public record RequestCountEntry(string Method, string Path, string Status, long Count);
    public record LatencyBucketEntry(string Method, string Path, string Boundary, long Count, bool IsInf, double Sum, long TotalCount);

    private sealed class LatencyHistogram
    {
        private readonly long[] _buckets = new long[Buckets.Length];
        private long _count;
        private double _sum;
        private readonly object _lock = new();

        public long Count => Interlocked.Read(ref _count);
        public double Sum { get { lock (_lock) return _sum; } }

        public void Record(double value)
        {
            Interlocked.Increment(ref _count);
            lock (_lock) { _sum += value; }

            for (var i = 0; i < Buckets.Length; i++)
            {
                if (value <= Buckets[i])
                {
                    Interlocked.Increment(ref _buckets[i]);
                }
            }
        }

        public long[] GetBucketCounts()
        {
            var result = new long[Buckets.Length];
            for (var i = 0; i < Buckets.Length; i++)
            {
                result[i] = Interlocked.Read(ref _buckets[i]);
            }
            return result;
        }
    }
}
