using System.Text.Json;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Agent;

public sealed class AgentInputTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-inputs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Kubernetes_CriLines_GiveTheMessage_TimeAndStream()
    {
        var line = KubernetesLogLine.Parse("2026-03-04T05:06:07.123456789Z stderr F panic: nil map");

        line.Message.Should().Be("panic: nil map");
        line.Stream.Should().Be("stderr");
        line.Time.Should().BeCloseTo(new DateTime(2026, 3, 4, 5, 6, 7, 123, DateTimeKind.Utc), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Kubernetes_DockerJsonLines_AreUnwrapped()
    {
        var line = KubernetesLogLine.Parse("""{"log":"GET /health 200\n","stream":"stdout","time":"2026-03-04T05:06:07.5Z"}""");

        line.Message.Should().Be("GET /health 200");
        line.Time.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, 500, DateTimeKind.Utc));
    }

    [Fact]
    public void Kubernetes_AnAppsOwnJson_IsKeptWhole()
    {
        KubernetesLogLine.Parse("""{"level":"info","msg":"hi"}""").Message.Should().Be("""{"level":"info","msg":"hi"}""");
    }

    [Fact]
    public void Kubernetes_FileNames_GiveThePod()
    {
        var fields = KubernetesLogLine.PodFields($"/var/log/containers/web-7d9f_shop_nginx-{new string('a', 64)}.log");

        fields.Should().Contain("k8s.pod", "web-7d9f").And.Contain("k8s.namespace", "shop").And.Contain("k8s.container", "nginx");
    }

    [Fact]
    public void Journald_ResumesAfterItsCursor_OrStartsAtTheEnd()
    {
        JournaldInput.Arguments("s=abc", ["sshd.service"], false).Should().Contain("--after-cursor=s=abc").And.Contain("--unit=sshd.service").And.NotContain("--lines=0");
        JournaldInput.Arguments(null, [], false).Should().Contain("--lines=0");
        JournaldInput.Arguments(null, [], true).Should().NotContain("--lines=0");
    }

    [Fact]
    public void Journald_Entries_BecomeSyslogStyleEvents_WithAStableId()
    {
        const string line = """{"__CURSOR":"s=1;i=2","__REALTIME_TIMESTAMP":"1772600767000000","_HOSTNAME":"web1","SYSLOG_IDENTIFIER":"sshd","_PID":"812","MESSAGE":"Failed password for root from 203.0.113.9","_SYSTEMD_UNIT":"ssh.service","PRIORITY":"6"}""";
        var input = new InputConfiguration { Type = "journald" };

        var first = JournaldInput.ToEvent(line, input, "agent", "journald:")!;
        var again = JournaldInput.ToEvent(line, input, "agent", "journald:")!;

        first.Event.Should().Be("2026-03-04T05:06:07.000Z web1 sshd[812]: Failed password for root from 203.0.113.9");
        first.Checkpoint.Should().Be("s=1;i=2");
        first.Fields.Should().Contain("unit", "ssh.service");
        first.SourceType.Should().Be("journald");
        again.Id.Should().Be(first.Id, "re-reading after a restart must collapse to the same event");
        JournaldInput.ToEvent("not json", input, "agent", "k").Should().BeNull();
    }

    [Fact]
    public void WindowsEvents_EventData_BecomesFields()
    {
        const string xml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System><EventID>4625</EventID></System>
              <EventData><Data Name="TargetUserName">alice</Data><Data Name="LogonType">3</Data><Data Name="IpAddress">203.0.113.9</Data></EventData>
            </Event>
            """;

        WindowsEventXml.Fields(xml).Should().Contain("TargetUserName", "alice").And.Contain("LogonType", "3").And.Contain("IpAddress", "203.0.113.9");
    }

    [Fact]
    public void Checkpoints_SurviveARestart()
    {
        var path = Path.Combine(_directory, "checkpoints.json");
        new InputCheckpoints(path, NullLogger<InputCheckpoints>.Instance).Set(new Dictionary<string, string> { ["journald:"] = "s=9" });

        new InputCheckpoints(path, NullLogger<InputCheckpoints>.Instance).Get("journald:").Should().Be("s=9");
    }

    [Fact]
    public void Fields_AreSentToTheServer_ButCheckpointsAreNot()
    {
        var json = JsonSerializer.Serialize(new LogEventDto { Event = "x", Fields = new() { ["k8s.pod"] = "web" }, InputKey = "journald:", Checkpoint = "s=1" });

        json.Should().Contain("\"fields\":{\"k8s.pod\":\"web\"}").And.NotContain("s=1").And.NotContain("journald");
    }
}
