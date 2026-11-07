using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinet.API.Controllers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Starts the real API process, because startup failures happen outside anything a
/// controller or DI test can reach.
/// </summary>
public sealed class ApiStartupTests : IAsyncLifetime
{
    private readonly string _workingDirectory =
        Directory.CreateTempSubdirectory("cabinet-startup-").FullName;

    private ProcessStartInfo CreateStartInfo(string urls)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(LogsController).Assembly.Location);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = urls;
        return startInfo;
    }

    [Fact]
    public async Task Api_FatalStartupError_ExitsWithNonZeroCode()
    {
        var startInfo = CreateStartInfo("http://127.0.0.1:0");
        startInfo.Environment["Storage__Type"] = "NotARealBackend";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr);

        process.ExitCode.Should().NotBe(0, "service managers restart on a failure exit code");
        (await stdout).Should().Contain("NotARealBackend");
    }

    [Fact]
    public async Task Api_UnderAnonymousFlood_KeepsHealthProbesAndMetricsBounded()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(client, process);

            // More than the anonymous allowance, each to a unique path with a junk method.
            var marker = $"flood{Guid.NewGuid():N}"[..16];
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 150; i++)
            {
                using var request = new HttpRequestMessage(new HttpMethod("FLOOD"), $"/{marker}{i}");
                using var response = await client.SendAsync(request);
                statuses.Add(response.StatusCode);
            }

            statuses.Should().Contain(HttpStatusCode.TooManyRequests, "the flood should exceed the anonymous limit");

            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

            var metrics = await client.GetStringAsync("/metrics");
            metrics.Should().NotContain(marker, "request paths must not become metric labels");
            metrics.Should().NotContain("FLOOD", "request methods must not become metric labels");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task Api_DefaultProductionConfiguration_Starts()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(Path.GetDirectoryName(typeof(LogsController).Assembly.Location)!);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(client, process);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task Api_SplunkAuthorizationHeader_WorksOnCollectorRoutesOnly()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";
        startInfo.Environment["ApiKeys__Keys__0__Key"] = "hec-test-key";
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Enterprise";
        startInfo.Environment["ApiKeys__Keys__0__Name"] = "HEC test";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(client, process);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Splunk hec-test-key");

            var collector = await client.PostAsJsonAsync(
                "/services/collector/event",
                new { @event = "Server started", sourcetype = "syslog", host = "web-01" });
            collector.StatusCode.Should().Be(HttpStatusCode.OK);

            var other = await client.GetAsync("/api/v1/logs/stats");
            other.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Api_GamingExtension_IsOffUnlessEnabled(bool enabled)
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";
        startInfo.Environment["ApiKeys__Keys__0__Key"] = "gaming-test-key";
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Enterprise";
        startInfo.Environment["ApiKeys__Keys__0__Name"] = "Gaming test";
        if (enabled)
        {
            startInfo.Environment["Gaming__Enabled"] = "true";
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add("X-API-Key", "gaming-test-key");
            await WaitUntilReadyAsync(client, process);

            var detection = await client.PostAsJsonAsync("/api/v1/detection/scumscan", new { events = Array.Empty<object>() });
            detection.StatusCode.Should().Be(enabled ? HttpStatusCode.OK : HttpStatusCode.NotFound);

            var ingest = await client.PostAsJsonAsync(
                "/api/v1/logs/ingest",
                new { Logs = new[] { "L 12/07/2024 - 10:23:45: World triggered \"Round_Start\"" } });
            ingest.StatusCode.Should().Be(HttpStatusCode.OK);

            var query = await client.PostAsJsonAsync("/api/v1/query/execute", new { query = "find Round_Start | fields SourceType" });
            using var body = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("results")[0].GetProperty("SourceType").GetString()
                .Should().Be(enabled ? "L4D2" : "Generic");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task Hec_SameEventIdTwice_StoresOnce()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";
        startInfo.Environment["ApiKeys__Keys__0__Key"] = "hec-id-key";
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Enterprise";
        startInfo.Environment["ApiKeys__Keys__0__Name"] = "HEC ID test";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add("X-API-Key", "hec-id-key");
            await WaitUntilReadyAsync(client, process);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var sent = await client.PostAsJsonAsync(
                    "/services/collector/event",
                    new { id = "0123456789abcdef0123456789abcdef", @event = "retried event" });
                sent.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // An unusable ID is ignored rather than failing the event.
            await client.PostAsJsonAsync("/services/collector/event", new { id = "../bad id", @event = "retried event" });

            var query = await client.PostAsJsonAsync("/api/v1/query/execute", new { query = "find retried | stats count" });
            using var body = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("results")[0].GetProperty("count").GetInt32().Should().Be(2);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [Fact]
    public async Task ApiSpec_MatchesTheRunningApi()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(client, process);

            var generated = await client.GetStringAsync("/swagger/v1/swagger.json");
            var specPath = RepositoryFile("api-spec.json");

            if (Environment.GetEnvironmentVariable("CABINET_UPDATE_API_SPEC") == "1")
            {
                await File.WriteAllTextAsync(specPath, generated.ReplaceLineEndings("\n") + "\n");
            }

            var matches = JsonNode.DeepEquals(
                JsonNode.Parse(generated),
                JsonNode.Parse(await File.ReadAllTextAsync(specPath)));
            matches.Should().BeTrue(
                "api-spec.json must describe the API; run the tests with CABINET_UPDATE_API_SPEC=1 to regenerate it");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static string RepositoryFile(string name, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", name));

    [Fact]
    public async Task Api_SpoofedForwardedFor_DoesNotEvadePreAuthLimit()
    {
        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "Memory";
        startInfo.Environment["RateLimiting__PreAuth__MaxRequestsPerMinute"] = "20";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            await WaitUntilReadyAsync(client, process);

            var limited = false;
            for (var i = 0; i < 40 && !limited; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/logs/stats");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{i}");
                using var response = await client.SendAsync(request);
                limited = response.StatusCode == HttpStatusCode.TooManyRequests;
            }

            limited.Should().BeTrue("rotating X-Forwarded-For must not create fresh rate-limit buckets");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Api_WithPostgres_StartsAndServesIngestAndQuery()
    {
        var connectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");
        Skip.If(string.IsNullOrEmpty(connectionString), "CABINET_TEST_POSTGRES_CONNSTR is not set");

        var baseAddress = new Uri($"http://127.0.0.1:{GetFreePort()}");
        var startInfo = CreateStartInfo(baseAddress.ToString().TrimEnd('/'));
        startInfo.Environment["Storage__Type"] = "PostgreSQL";
        startInfo.Environment["Storage__ConnectionString"] = connectionString;
        startInfo.Environment["Storage__MetadataConnectionString"] =
            $"Data Source={Path.Combine(_workingDirectory, "metadata.db")}";

        startInfo.Environment["ApiKeys__Keys__0__Key"] = "startup-test-key";
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Enterprise";
        startInfo.Environment["ApiKeys__Keys__0__Name"] = "Startup test";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add("X-API-Key", "startup-test-key");
            await WaitUntilReadyAsync(client, process);

            var marker = $"startup{Guid.NewGuid():N}";
            var ingest = await client.PostAsJsonAsync("/api/v1/logs/ingest", new { Logs = new[] { marker } });
            ingest.StatusCode.Should().Be(HttpStatusCode.OK);

            var query = await client.PostAsJsonAsync(
                "/api/v1/query/execute",
                new { query = $"find {marker} | stats count" });
            query.StatusCode.Should().Be(HttpStatusCode.OK);

            using var body = JsonDocument.Parse(await query.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("results")[0].GetProperty("count").GetInt32().Should().Be(1);

            var dashboards = await client.GetAsync("/api/v1/dashboards");
            dashboards.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task WaitUntilReadyAsync(HttpClient client, Process process)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            process.HasExited.Should().BeFalse("the API should keep running after startup");
            try
            {
                var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet; keep polling until the deadline.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException("The API did not become ready within 60 seconds.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Windows holds files briefly after a killed process exits.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_workingDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }
}
