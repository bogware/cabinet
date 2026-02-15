using Cabinet.API.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Middleware;

public class IpRateLimitingMiddlewareTests : IDisposable
{
    private readonly Mock<ILogger<IpRateLimitingMiddleware>> _logger = new();

    public IpRateLimitingMiddlewareTests()
    {
        // Reset static state between tests
        IpRateLimitingMiddleware.Reset();
    }

    public void Dispose()
    {
        IpRateLimitingMiddleware.Reset();
    }

    [Fact]
    public async Task InvokeAsync_UnderLimit_PassesThrough()
    {
        var middleware = CreateMiddleware(maxRequests: 100);
        var context = CreateHttpContext("/api/v1/logs");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().NotBe(429);
    }

    [Fact]
    public async Task InvokeAsync_OverLimit_Returns429()
    {
        var middleware = CreateMiddleware(maxRequests: 5);

        // Make 5 requests (under limit)
        for (var i = 0; i < 5; i++)
        {
            var ctx = CreateHttpContext("/api/v1/logs");
            await middleware.InvokeAsync(ctx);
            ctx.Response.StatusCode.Should().NotBe(429, $"request {i + 1} should be under limit");
        }

        // 6th request should be rate limited
        var overLimitCtx = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(overLimitCtx);
        overLimitCtx.Response.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task InvokeAsync_HealthEndpoint_SkipsRateLimiting()
    {
        var middleware = CreateMiddleware(maxRequests: 1);

        // Exhaust the limit
        var ctx1 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx1);

        var ctx2 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx2);

        // Health endpoint should still work even when rate limited
        var healthCtx = CreateHttpContext("/health");
        await middleware.InvokeAsync(healthCtx);
        healthCtx.Response.StatusCode.Should().NotBe(429);
    }

    [Fact]
    public async Task InvokeAsync_MetricsEndpoint_SkipsRateLimiting()
    {
        var middleware = CreateMiddleware(maxRequests: 1);

        // Exhaust the limit
        var ctx1 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx1);

        var ctx2 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx2);
        ctx2.Response.StatusCode.Should().Be(429);

        // Metrics endpoint is exempt from pre-auth rate limiting (used by Prometheus)
        var metricsCtx = CreateHttpContext("/metrics");
        await middleware.InvokeAsync(metricsCtx);
        metricsCtx.Response.StatusCode.Should().NotBe(429);
    }

    [Fact]
    public async Task InvokeAsync_DifferentIPs_IndependentLimits()
    {
        var middleware = CreateMiddleware(maxRequests: 2);

        // IP 1: make 2 requests
        for (var i = 0; i < 2; i++)
        {
            var ctx = CreateHttpContext("/api/v1/logs", "10.0.0.1");
            await middleware.InvokeAsync(ctx);
        }

        // IP 1 should be rate limited
        var limitedCtx = CreateHttpContext("/api/v1/logs", "10.0.0.1");
        await middleware.InvokeAsync(limitedCtx);
        limitedCtx.Response.StatusCode.Should().Be(429);

        // IP 2 should still work
        var ip2Ctx = CreateHttpContext("/api/v1/logs", "10.0.0.2");
        await middleware.InvokeAsync(ip2Ctx);
        ip2Ctx.Response.StatusCode.Should().NotBe(429);
    }

    [Fact]
    public async Task InvokeAsync_RateLimited_SetsRetryAfterHeader()
    {
        var middleware = CreateMiddleware(maxRequests: 1);

        // Exhaust limit
        var ctx1 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx1);

        // Next request should have Retry-After header
        var ctx2 = CreateHttpContext("/api/v1/logs");
        await middleware.InvokeAsync(ctx2);

        ctx2.Response.StatusCode.Should().Be(429);
        ctx2.Response.Headers.Should().ContainKey("Retry-After");
    }

    [Fact]
    public async Task InvokeAsync_XForwardedFor_UsesClientIp()
    {
        var middleware = CreateMiddleware(maxRequests: 2);

        // Make requests with X-Forwarded-For header
        for (var i = 0; i < 2; i++)
        {
            var ctx = CreateHttpContext("/api/v1/logs");
            ctx.Request.Headers["X-Forwarded-For"] = "192.168.1.100, 10.0.0.1";
            await middleware.InvokeAsync(ctx);
        }

        // Should be rate limited based on the first IP in X-Forwarded-For
        var limitedCtx = CreateHttpContext("/api/v1/logs");
        limitedCtx.Request.Headers["X-Forwarded-For"] = "192.168.1.100, 10.0.0.1";
        await middleware.InvokeAsync(limitedCtx);
        limitedCtx.Response.StatusCode.Should().Be(429);

        // Different forwarded IP should still work
        var otherCtx = CreateHttpContext("/api/v1/logs");
        otherCtx.Request.Headers["X-Forwarded-For"] = "192.168.1.200";
        await middleware.InvokeAsync(otherCtx);
        otherCtx.Response.StatusCode.Should().NotBe(429);
    }

    private IpRateLimitingMiddleware CreateMiddleware(int maxRequests = 300)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:PreAuth:MaxRequestsPerMinute"] = maxRequests.ToString(),
                ["RateLimiting:PreAuth:WindowSeconds"] = "60"
            })
            .Build();

        return new IpRateLimitingMiddleware(
            _ => Task.CompletedTask,
            _logger.Object,
            config);
    }

    private static DefaultHttpContext CreateHttpContext(string path, string? remoteIp = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remoteIp ?? "127.0.0.1");
        context.Response.Body = new MemoryStream();
        return context;
    }
}
