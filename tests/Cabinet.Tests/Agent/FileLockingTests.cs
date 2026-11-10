using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Agent;

/// <summary>
/// Source game servers hold an exclusive advisory lock on the log they're writing. With .NET's
/// default shared lock on open, the agent couldn't read a file until the game closed it, which for
/// a daily log was the next morning. Found live on the L4D2 servers.
/// </summary>
public sealed class FileLockingTests
{
    [Fact]
    public void Agent_DoesNotTakeFileLocks()
    {
        // The setting is read once per process, so it's checked in what the agent runs with
        var path = Path.Combine(AppContext.BaseDirectory, "cabinet-agent.runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var options = document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        options.GetProperty("System.IO.DisableFileLocking").GetBoolean().Should().BeTrue();
    }
}
