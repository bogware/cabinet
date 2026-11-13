using System.Net;
using Cabinet.API.Authentication;
using Cabinet.API.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Cabinet.Tests.Middleware;

public sealed class MetricsAccessTests
{
    private static HttpContext Request(string address, bool requireKey, string? key = null, string? bearer = null, params string[] allowFrom)
    {
        var settings = new Dictionary<string, string?> { ["Metrics:RequireKey"] = requireKey.ToString() };
        for (var i = 0; i < allowFrom.Length; i++)
        {
            settings[$"Metrics:AllowFrom:{i}"] = allowFrom[i];
        }

        var keys = new Mock<IApiKeyService>();
        keys.Setup(k => k.ValidateKeyAsync(It.IsAny<string>())).ReturnsAsync((string k) => new ApiKeyValidationResult
        {
            IsValid = k is "read-key" or "ingest-key",
            Tier = k == "ingest-key" ? "Ingest" : "Professional",
        });
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
            .AddSingleton(keys.Object)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (key != null) context.Request.Headers["X-API-Key"] = key;
        if (bearer != null) context.Request.Headers.Authorization = $"Bearer {bearer}";
        return context;
    }

    [Theory]
    [InlineData("203.0.113.9", false, null, null, true)]
    [InlineData("203.0.113.9", true, null, null, false)]
    [InlineData("127.0.0.1", true, null, null, true)]
    [InlineData("::1", true, null, null, true)]
    [InlineData("203.0.113.9", true, "read-key", null, true)]
    [InlineData("203.0.113.9", true, null, "read-key", true)]
    [InlineData("203.0.113.9", true, "ingest-key", null, false)]
    [InlineData("203.0.113.9", true, "wrong", null, false)]
    public async Task Metrics_AreOpen_OrNeedAKeyAboveIngest(string address, bool requireKey, string? key, string? bearer, bool allowed)
    {
        (await MetricsMiddleware.MayReadMetricsAsync(Request(address, requireKey, key, bearer))).Should().Be(allowed);
    }

    [Fact]
    public async Task ALocalProxy_DoesntMakeEveryoneLocal()
    {
        // Behind a reverse proxy on the same machine, requests arrive from loopback with a forwarding header
        var proxied = Request("127.0.0.1", true);
        proxied.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        (await MetricsMiddleware.MayReadMetricsAsync(proxied)).Should().BeFalse();
    }

    [Fact]
    public async Task GuessingKeys_IsCutOff_AfterTwenty()
    {
        // /metrics answers before the pre-auth rate limit, so it limits wrong keys itself
        MetricsMiddleware.KeyGuesses.Reset();
        var middleware = new MetricsMiddleware(_ => Task.CompletedTask);
        for (var i = 0; i < MetricsMiddleware.KeyGuesses.MaxGuesses; i++)
        {
            var guess = Request("198.51.100.7", true, key: $"guess-{i}");
            guess.Request.Path = "/metrics";
            await middleware.InvokeAsync(guess);
            guess.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        }

        var right = Request("198.51.100.7", true, key: "read-key");
        right.Request.Path = "/metrics";
        await middleware.InvokeAsync(right);
        right.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests, "even a right key isn't checked once guessing has used up the allowance");

        var other = Request("198.51.100.8", true, key: "read-key");
        other.Request.Path = "/metrics";
        other.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(other);
        other.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        MetricsMiddleware.KeyGuesses.Reset();
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("::ffff:10.1.2.3", true)]
    [InlineData("192.0.2.10", true)]
    [InlineData("192.0.2.11", false)]
    [InlineData("11.0.0.1", false)]
    public async Task AllowedAddresses_ReadWithoutAKey(string address, bool allowed)
    {
        (await MetricsMiddleware.MayReadMetricsAsync(Request(address, true, null, null, "10.0.0.0/8", "192.0.2.10"))).Should().Be(allowed);
    }
}
