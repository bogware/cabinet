using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cabinet.API.Controllers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

/// <summary>
/// Starts the real API process, because startup failures happen outside anything a
/// controller or DI test can reach.
/// </summary>
public sealed class ApiStartupTests : IDisposable
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
            body.RootElement.GetProperty("results")[0].GetProperty("count()").GetInt32().Should().Be(1);

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

    public void Dispose()
    {
        Directory.Delete(_workingDirectory, recursive: true);
    }
}
