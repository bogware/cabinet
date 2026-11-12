using System.Net;
using System.Net.Http.Json;
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

public sealed class DetectionActionTests
{
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryDocumentStore _documents = new();
    private readonly InMemoryAlertHistoryStorage _history = new();
    private readonly Mock<IAlertService> _alerts = new();
    private readonly InMemoryStorage _emitted = new();
    private readonly CapturingHandler _webhook = new();
    private readonly DetectionRuleStore _rules;
    private readonly DetectionEngine _engine;

    public DetectionActionTests()
    {
        _rules = new DetectionRuleStore(_documents, new ConfigurationBuilder().Build());
        var executor = new DetectionActionExecutor(_history, _alerts.Object, _emitted, new HttpClient(_webhook), NullLogger<DetectionActionExecutor>.Instance);
        _engine = new DetectionEngine(_rules, executor, NullLogger<DetectionEngine>.Instance);
    }

    private async Task AddRuleAsync(DetectionRule rule)
    {
        await _rules.SaveAsync(rule);
        _engine.InvalidateRules();
    }

    private static LogEvent Event(string line) => new() { RawLine = line, Timestamp = Start, Host = "web-1", Index = "main" };

    [Fact]
    public async Task NoActions_StillNotifies_AsBefore()
    {
        await AddRuleAsync(new DetectionRule { Name = "Disk", Pattern = "disk full", Severity = "Error" });

        await _engine.CheckAsync([Event("disk full")]);

        _alerts.Verify(a => a.SendAlertAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _history.GetAlertsAsync()).Should().ContainSingle();
        (await _emitted.GetCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EventAction_WritesASearchableEvent_WithTemplatedFields()
    {
        await AddRuleAsync(new DetectionRule
        {
            Name = "Offensive chat",
            Condition = "match(ChatMessage, \"badword\")",
            Severity = "Warning",
            GroupBy = "SteamId",
            Actions =
            [
                new DetectionAction
                {
                    Type = "event",
                    Target = "moderation",
                    Message = "{rule}: {group} said something flagged",
                    Fields = new Dictionary<string, string> { ["offender"] = "{group}", ["kind"] = "chat" },
                },
            ],
        });

        var logEvent = new LogEvent { RawLine = "chat", Timestamp = Start, Index = "l4d2" };
        logEvent.SetField("ChatMessage", "that is a badword");
        logEvent.SetField("SteamId", "STEAM_1:0:42");

        await _engine.CheckAsync([logEvent]);

        var events = new List<LogEvent>();
        await foreach (var e in _emitted.QueryAsync(new Cabinet.Core.Interfaces.QueryContext()))
        {
            events.Add(e);
        }

        var emitted = events.Should().ContainSingle().Subject;
        emitted.Index.Should().Be("moderation");
        emitted.SourceType.Should().Be("detection");
        emitted.Severity.Should().Be(LogSeverity.Warning);
        emitted.RawLine.Should().Be("Offensive chat: STEAM_1:0:42 said something flagged");
        emitted.Fields["offender"].Should().Be("STEAM_1:0:42");
        emitted.Fields["kind"].Should().Be("chat");
        emitted.Fields["SteamId"].Should().Be("STEAM_1:0:42");
        emitted.Fields["source_event_id"].Should().Be(logEvent.Id);

        // The emitted event must never be fed back into detection (no loop).
        (await _emitted.GetCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task WebhookAction_PostsTheFiringAsJson()
    {
        await AddRuleAsync(new DetectionRule
        {
            Name = "Brute force",
            Pattern = "failed",
            Severity = "Critical",
            Actions = [new DetectionAction { Type = "webhook", Target = "https://hook.example/incident" }],
        });

        await _engine.CheckAsync([Event("failed login")]);

        _webhook.LastUrl.Should().Be("https://hook.example/incident");
        var body = JsonDocument.Parse(_webhook.LastBody!).RootElement;
        body.GetProperty("rule").GetString().Should().Be("Brute force");
        body.GetProperty("severity").GetString().Should().Be("Critical");
        body.GetProperty("host").GetString().Should().Be("web-1");
    }

    [Fact]
    public async Task SeveralActions_AllRun_AndOneFailingDoesNotStopTheOthers()
    {
        _webhook.FailNext = true;
        await AddRuleAsync(new DetectionRule
        {
            Name = "Multi",
            Pattern = "boom",
            Actions =
            [
                new DetectionAction { Type = "webhook", Target = "https://hook.example/x" },
                new DetectionAction { Type = "event", Target = "incidents" },
                new DetectionAction { Type = "notify" },
            ],
        });

        await _engine.CheckAsync([Event("boom")]);

        // The failing webhook didn't stop the event or the notify, and history still recorded it.
        (await _emitted.GetCountAsync()).Should().Be(1);
        _alerts.Verify(a => a.SendAlertAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>()), Times.Once);
        (await _history.GetAlertsAsync()).Should().ContainSingle();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastBody { get; private set; }
        public bool FailNext { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (FailNext)
            {
                FailNext = false;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
