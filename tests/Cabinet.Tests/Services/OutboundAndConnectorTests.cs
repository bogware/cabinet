using System.Net;
using System.Text.Json;
using Cabinet.Alerts;
using Cabinet.API.Services;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class OutboundUrlGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1/x")]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.1.27/hook")]
    [InlineData("http://172.20.1.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    [InlineData("http://100.64.1.1/hook")]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/latest/meta-data")]
    [InlineData("http://[2002:c0a8:011b::1]/hook")]
    public void PrivateTargets_AreRefused(string url)
    {
        new OutboundUrlGuard(new OutboundUrlOptions()).Check(url).Should().NotBeNull();
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T/B/X")]
    [InlineData("https://93.184.216.34/hook")]
    public void PublicTargets_AreAllowed(string url)
    {
        new OutboundUrlGuard(new OutboundUrlOptions()).Check(url).Should().BeNull();
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("not a url")]
    [InlineData("https://user:pass@example.com/x")]
    public void BadUrls_AreRefused(string url)
    {
        new OutboundUrlGuard(new OutboundUrlOptions()).Check(url).Should().NotBeNull();
    }

    [Fact]
    public void AllowedHostsAndPrivateSetting_LetInternalTargetsThrough()
    {
        new OutboundUrlGuard(new OutboundUrlOptions { AllowedHosts = ["10.0.0.5"] }).Check("http://10.0.0.5/jira").Should().BeNull();
        new OutboundUrlGuard(new OutboundUrlOptions { AllowPrivateTargets = true }).Check("http://192.168.1.10/x").Should().BeNull();
    }

    [Fact]
    public void Handler_NeverGoesThroughAProxy()
    {
        // Through a proxy the connect check would only see the proxy's address
        new OutboundUrlGuard(new OutboundUrlOptions()).CreateHandler().UseProxy.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_RefusesToConnectToPrivateAddresses_EvenIfTheNameLooksPublic()
    {
        using var client = new HttpClient(new OutboundUrlGuard(new OutboundUrlOptions()).CreateHandler());
        var act = () => client.GetAsync("http://127.0.0.1:9/");
        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("isn't allowed");
    }
}

public sealed class ConnectorPayloadTests
{
    private static readonly ConnectorNotification Note = new()
    {
        Title = "Detection: Brute force",
        Message = "5 failed logins",
        Severity = "Critical",
        Facts = new Dictionary<string, string> { ["host"] = "web-1" },
        DedupKey = "rule:10.0.0.9",
    };

    private static async Task<JsonElement> BodyAsync(HttpRequestMessage request) =>
        JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task PagerDuty_UsesTheRoutingKeyAndDedupKey()
    {
        using var request = ConnectorRunner.BuildRequest(new ActionConnector { Name = "pd", Type = "pagerduty", Secret = "rk" }, Note);
        request.RequestUri!.ToString().Should().Be("https://events.pagerduty.com/v2/enqueue");
        var body = await BodyAsync(request);
        body.GetProperty("routing_key").GetString().Should().Be("rk");
        body.GetProperty("dedup_key").GetString().Should().Be("rule:10.0.0.9");
        body.GetProperty("payload").GetProperty("severity").GetString().Should().Be("critical");
    }

    [Fact]
    public async Task Jira_CreatesAnIssueWithBasicAuth()
    {
        using var request = ConnectorRunner.BuildRequest(new ActionConnector
        {
            Name = "jira", Type = "jira", Url = "https://acme.atlassian.net/", Secret = "token",
            Settings = new() { ["user"] = "me@acme.test", ["project"] = "SEC", ["issueType"] = "Bug" },
        }, Note);
        request.RequestUri!.ToString().Should().Be("https://acme.atlassian.net/rest/api/2/issue");
        request.Headers.Authorization!.Scheme.Should().Be("Basic");
        var fields = (await BodyAsync(request)).GetProperty("fields");
        fields.GetProperty("project").GetProperty("key").GetString().Should().Be("SEC");
        fields.GetProperty("issuetype").GetProperty("name").GetString().Should().Be("Bug");
    }

    [Fact]
    public async Task Opsgenie_SendsTheGenieKeyAndPriority()
    {
        using var request = ConnectorRunner.BuildRequest(new ActionConnector { Name = "og", Type = "opsgenie", Secret = "k" }, Note);
        request.Headers.GetValues("Authorization").Single().Should().Be("GenieKey k");
        (await BodyAsync(request)).GetProperty("priority").GetString().Should().Be("P1");
    }

    [Theory]
    [InlineData("slack")]
    [InlineData("teams")]
    [InlineData("discord")]
    [InlineData("servicenow")]
    [InlineData("webhook")]
    public async Task EveryHttpType_BuildsJson(string type)
    {
        using var request = ConnectorRunner.BuildRequest(new ActionConnector { Name = "x", Type = type, Url = "https://example.com/hook", Secret = "s" }, Note);
        request.Method.Should().Be(HttpMethod.Post);
        (await request.Content!.ReadAsStringAsync()).Should().Contain("Brute force");
    }
}

public sealed class DetectionActionSafetyTests
{
    private readonly InMemoryDocumentStore _documents = new();
    private readonly InMemoryAlertHistoryStorage _history = new();
    private readonly Mock<IAlertService> _alerts = new();
    private readonly InMemoryStorage _emitted = new();

    private static DetectionFiring Firing(DetectionRule rule, string line)
    {
        var logEvent = new LogEvent { RawLine = line, Timestamp = DateTime.UtcNow, Index = "main" };
        logEvent.SetField("card", "4111111111111111");
        return new DetectionFiring(rule, string.Empty, 1, logEvent);
    }

    [Fact]
    public async Task SlowActions_DoNotHoldUpTheCaller_AndHistoryIsRecordedFirst()
    {
        _alerts.Setup(a => a.SendAlertAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>()))
            .Returns<Alert, CancellationToken>(async (_, ct) => await Task.Delay(TimeSpan.FromSeconds(30), ct));
        var executor = new DetectionActionExecutor(_history, _alerts.Object, _emitted, new HttpClient(), NullLogger<DetectionActionExecutor>.Instance,
            background: true, actionTimeout: TimeSpan.FromMilliseconds(200));
        await executor.StartAsync(CancellationToken.None);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await executor.ExecuteAsync(Firing(new DetectionRule { Name = "Slow" }, "x"));
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "actions run on their own queue");
            (await _history.GetAlertsAsync()).Should().ContainSingle();
        }
        finally
        {
            await executor.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ActionsSeeMaskedEvents()
    {
        var masking = new FieldMaskingService();
        masking.AddRule(new FieldMaskingRule { FieldPattern = "card", Strategy = MaskingStrategy.Redact, Replacement = "[REDACTED]", Enabled = true });
        var executor = new DetectionActionExecutor(_history, _alerts.Object, _emitted, new HttpClient(), NullLogger<DetectionActionExecutor>.Instance, masking: masking);

        var rule = new DetectionRule { Name = "Card", Actions = [new DetectionAction { Type = "event", Message = "{line}", Fields = new() { ["seen"] = "{line}" } }] };
        await executor.ExecuteAsync(Firing(rule, "paid card=4111111111111111"));

        var events = new List<LogEvent>();
        await foreach (var e in _emitted.QueryAsync(new Cabinet.Core.Interfaces.QueryContext()))
        {
            events.Add(e);
        }

        events.Should().ContainSingle().Which.RawLine.Should().NotContain("4111111111111111");
        events[0].Fields["seen"]!.ToString().Should().NotContain("4111111111111111");
    }

    [Fact]
    public async Task FullQueue_DropsActionsButKeepsHistory()
    {
        var executor = new DetectionActionExecutor(_history, _alerts.Object, _emitted, new HttpClient(), NullLogger<DetectionActionExecutor>.Instance,
            background: true, queueCapacity: 1);

        // Not started, so nothing drains the queue
        await executor.ExecuteAsync(Firing(new DetectionRule { Name = "A" }, "x"));
        await executor.ExecuteAsync(Firing(new DetectionRule { Name = "B" }, "x"));

        executor.DroppedActions.Should().Be(1);
        (await _history.GetAlertsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task ConnectorAction_GoesToTheNamedConnector()
    {
        var runner = new Mock<IActionConnectorRunner>();
        var executor = new DetectionActionExecutor(_history, _alerts.Object, _emitted, new HttpClient(), NullLogger<DetectionActionExecutor>.Instance, connectors: runner.Object);

        await executor.ExecuteAsync(Firing(new DetectionRule { Name = "R", Actions = [new DetectionAction { Type = "connector", Target = "oncall" }] }, "x"));

        runner.Verify(r => r.SendAsync("oncall", It.Is<ConnectorNotification>(n => n.Title == "Detection: R"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
