using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Cabinet.API.Middleware;

/// <summary>
/// Rate limiting configuration
/// </summary>
public static class RateLimitingExtensions
{
    private static IConfiguration? _configuration;

    /// <summary>Requests a minute for a tier, from <c>RateLimiting:Tiers:Name</c>.</summary>
    private static int TierLimit(string tier, int defaultLimit) =>
        _configuration?.GetValue($"RateLimiting:Tiers:{tier}", defaultLimit) ?? defaultLimit;

    public static IServiceCollection AddCabinetRateLimiting(this IServiceCollection services, IConfiguration? configuration = null)
    {
        _configuration = configuration;

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                // Use the hashed API key from claims (set by auth handler) — never the raw key
                var clientId = context.User?.FindFirst("ClientId")?.Value;
                if (clientId == null)
                {
                    return GetAnonymousLimiter($"ip:{context.Connection.RemoteIpAddress}");
                }

                return GetRateLimiter(context.User?.FindFirst("Tier")?.Value ?? "Basic", clientId);
            });

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Rate limit exceeded",
                    message = "Too many requests. Please retry later.",
                    retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                        ? retryAfter.TotalSeconds
                        : 60
                }, cancellationToken);
            };
        });

        return services;
    }

    /// <summary>
    /// Per-address limit for unauthenticated requests. Over-limit requests are rejected
    /// rather than queued, so anonymous callers can't hold connections open while they wait.
    /// </summary>
    private static RateLimitPartition<string> GetAnonymousLimiter(string partitionKey) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });

    private static RateLimitPartition<string> GetRateLimiter(string tier, string apiKey)
    {
        return tier.ToUpper() switch
        {
            "ADMIN" or "ENTERPRISE" => RateLimitPartition.GetTokenBucketLimiter(
                apiKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = TierLimit("Enterprise", 10000),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 100,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    TokensPerPeriod = TierLimit("Enterprise", 10000),
                    AutoReplenishment = true
                }),

            "PROFESSIONAL" => RateLimitPartition.GetTokenBucketLimiter(
                apiKey,
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = TierLimit("Professional", 1000),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 50,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    TokensPerPeriod = TierLimit("Professional", 1000),
                    AutoReplenishment = true
                }),

            _ => RateLimitPartition.GetSlidingWindowLimiter(
                apiKey,
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 10
                })
        };
    }
}
