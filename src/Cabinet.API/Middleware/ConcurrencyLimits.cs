using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Cabinet.API.Middleware;

/// <summary>What an endpoint's concurrent requests count against.</summary>
public enum ConcurrencyPool
{
    /// <summary>Queries, exports and other reads that run until they finish.</summary>
    Query,

    /// <summary>Live streams, which stay open.</summary>
    Stream,
}

/// <summary>
/// How many expensive requests can run at once. Rate limits count requests, not the work they
/// start, so one client could otherwise fill the server with long queries.
/// </summary>
public sealed class ConcurrencyOptions
{
    public int MaxConcurrent { get; set; } = 32;
    public int MaxConcurrentPerClient { get; set; } = 8;
    public int MaxStreams { get; set; } = 64;
    public int MaxStreamsPerClient { get; set; } = 4;
}

/// <summary>Counts running requests, overall and for each client.</summary>
public sealed class ConcurrencyLimiter
{
    private readonly ConcurrentDictionary<(ConcurrencyPool Pool, string Client), int> _perClient = new();
    private readonly int[] _totals = new int[Enum.GetValues<ConcurrencyPool>().Length];
    private readonly ConcurrencyOptions _options;

    public ConcurrencyLimiter(ConcurrencyOptions options)
    {
        _options = options;
    }

    /// <summary>Takes a slot, if there's one free. Every success needs a <see cref="Release"/>.</summary>
    public bool TryAcquire(ConcurrencyPool pool, string client)
    {
        var (total, perClient) = pool == ConcurrencyPool.Query
            ? (_options.MaxConcurrent, _options.MaxConcurrentPerClient)
            : (_options.MaxStreams, _options.MaxStreamsPerClient);

        if (Interlocked.Increment(ref _totals[(int)pool]) > total)
        {
            Interlocked.Decrement(ref _totals[(int)pool]);
            return false;
        }

        var key = (pool, client);
        if (_perClient.AddOrUpdate(key, 1, (_, count) => count + 1) > perClient)
        {
            Release(pool, client);
            return false;
        }

        return true;
    }

    public void Release(ConcurrencyPool pool, string client)
    {
        Interlocked.Decrement(ref _totals[(int)pool]);

        var key = (pool, client);
        if (_perClient.AddOrUpdate(key, 0, (_, count) => count - 1) <= 0)
        {
            // Stops the map growing with clients that come and go. Only removes it at zero.
            _perClient.TryRemove(new KeyValuePair<(ConcurrencyPool, string), int>(key, 0));
        }
    }
}

/// <summary>
/// Limits how many of these requests run at once, returning 429 when they're all in use.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LimitConcurrencyAttribute : Attribute, IAsyncResourceFilter
{
    private readonly ConcurrencyPool _pool;

    public LimitConcurrencyAttribute(ConcurrencyPool pool = ConcurrencyPool.Query)
    {
        _pool = pool;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var limiter = http.RequestServices.GetRequiredService<ConcurrencyLimiter>();
        var client = http.User.FindFirst("ClientId")?.Value
            ?? http.User.Identity?.Name
            ?? http.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        if (!limiter.TryAcquire(_pool, client))
        {
            http.Response.Headers.RetryAfter = "5";
            context.Result = new Microsoft.AspNetCore.Mvc.ObjectResult(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many concurrent requests",
                Detail = "Too many of these requests are running. Retry when one has finished.",
            })
            {
                StatusCode = StatusCodes.Status429TooManyRequests,
            };
            return;
        }

        try
        {
            await next();
        }
        finally
        {
            limiter.Release(_pool, client);
        }
    }
}
