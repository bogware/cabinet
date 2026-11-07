using System.Diagnostics;
using Cabinet.API.Controllers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class ApiStartupTests : IDisposable
{
    private readonly string _workingDirectory =
        Directory.CreateTempSubdirectory("cabinet-startup-").FullName;

    [Fact]
    public async Task Api_FatalStartupError_ExitsWithNonZeroCode()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(LogsController).Assembly.Location);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
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

    public void Dispose()
    {
        Directory.Delete(_workingDirectory, recursive: true);
    }
}
