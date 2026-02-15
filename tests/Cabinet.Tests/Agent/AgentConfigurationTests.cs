using Cabinet.Agent.Configuration;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Agent;

public class AgentConfigurationTests
{
    [Fact]
    public void AgentConfiguration_DefaultValues()
    {
        var config = new AgentConfiguration();

        config.Inputs.Should().BeEmpty();
        config.Output.Should().NotBeNull();
        config.Registry.Should().NotBeNull();
        config.ScanIntervalSeconds.Should().Be(10);
        config.Hostname.Should().BeEmpty();
    }

    [Fact]
    public void InputConfiguration_DefaultValues()
    {
        var input = new InputConfiguration();

        input.Paths.Should().BeEmpty();
        input.Recursive.Should().BeFalse();
        input.SourceType.Should().Be("generic");
        input.Index.Should().Be("main");
        input.ExcludePatterns.Should().BeEmpty();
        input.MultiLine.Should().BeNull();
    }

    [Fact]
    public void OutputConfiguration_DefaultValues()
    {
        var output = new OutputConfiguration();

        output.ServerUrl.Should().Be("http://localhost:5000");
        output.ApiKey.Should().BeEmpty();
        output.BatchSize.Should().Be(100);
        output.FlushIntervalSeconds.Should().Be(5);
        output.MaxRetries.Should().Be(5);
        output.TimeoutSeconds.Should().Be(30);
        output.VerifySsl.Should().BeTrue();
        output.MaxBufferSize.Should().Be(10000);
        output.SpillDirectory.Should().Be("spool");
    }

    [Fact]
    public void RegistryConfiguration_DefaultValues()
    {
        var registry = new RegistryConfiguration();

        registry.Path.Should().Be("registry.json");
        registry.SaveIntervalSeconds.Should().Be(10);
    }

    [Fact]
    public void MultiLineConfiguration_DefaultValues()
    {
        var multiLine = new MultiLineConfiguration();

        multiLine.Pattern.Should().BeEmpty();
        multiLine.Negate.Should().BeTrue();
        multiLine.MaxLines.Should().Be(500);
        multiLine.TimeoutSeconds.Should().Be(5);
    }

    [Fact]
    public void InputConfiguration_CanConfigureMultipleInputs()
    {
        var config = new AgentConfiguration
        {
            Inputs =
            [
                new InputConfiguration
                {
                    Paths = ["/var/log/syslog", "/var/log/auth.log"],
                    SourceType = "syslog",
                    Index = "security"
                },
                new InputConfiguration
                {
                    Paths = ["/var/log/nginx/*.log"],
                    SourceType = "nginx",
                    Index = "web",
                    Recursive = true,
                    ExcludePatterns = ["*.gz"]
                }
            ]
        };

        config.Inputs.Should().HaveCount(2);
        config.Inputs[0].Paths.Should().HaveCount(2);
        config.Inputs[0].SourceType.Should().Be("syslog");
        config.Inputs[1].Recursive.Should().BeTrue();
        config.Inputs[1].ExcludePatterns.Should().Contain("*.gz");
    }

    [Fact]
    public void InputConfiguration_MultiLineCanBeConfigured()
    {
        var input = new InputConfiguration
        {
            Paths = ["/var/log/app.log"],
            MultiLine = new MultiLineConfiguration
            {
                Pattern = @"^\d{4}-\d{2}-\d{2}",
                Negate = true,
                MaxLines = 100,
                TimeoutSeconds = 3
            }
        };

        input.MultiLine.Should().NotBeNull();
        input.MultiLine!.Pattern.Should().Contain("\\d{4}");
        input.MultiLine.MaxLines.Should().Be(100);
    }
}
