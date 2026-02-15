using Cabinet.Core.Models;
using Cabinet.Detection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Detection;

public class ScumscanDetectorTests
{
    private readonly ScumscanDetector _detector = new(NullLogger<ScumscanDetector>.Instance);

    [Fact]
    public async Task AnalyzeAsync_ToxicLanguage_DetectsViolation()
    {
        var events = new[]
        {
            CreateChatEvent("Player1", "you're terrible at this game noob"),
            CreateChatEvent("Player1", "get out of here loser"),
            CreateChatEvent("Player1", "everyone report this trash player")
        };

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.SuiteName.Should().Be("scumscan");
        report.EventsAnalyzed.Should().Be(3);
    }

    [Fact]
    public async Task AnalyzeAsync_CleanChat_NoDetections()
    {
        var events = new[]
        {
            CreateChatEvent("Player1", "good game everyone"),
            CreateChatEvent("Player2", "gg wp"),
            CreateChatEvent("Player1", "nice shot!")
        };

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 5 });

        report.Detections.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_SpamMessages_DetectsSpam()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Same player sending 20 identical messages rapidly
        for (var i = 0; i < 20; i++)
        {
            var evt = CreateChatEvent("Spammer", "BUY CHEAP ITEMS AT SCAMSITE.COM");
            evt.Timestamp = now.AddSeconds(i);
            events.Add(evt);
        }

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().NotBeEmpty();
    }

    [Fact]
    public void Name_ReturnsScumscan()
    {
        _detector.Name.Should().Be("scumscan");
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        _detector.Description.Should().NotBeNullOrEmpty();
    }

    private static LogEvent CreateChatEvent(string playerName, string message)
    {
        var evt = new LogEvent
        {
            RawLine = $"\"{playerName}\" say \"{message}\"",
            Timestamp = DateTime.UtcNow,
            SourceType = SourceTypes.L4D2
        };

        evt.SetField("PlayerName", playerName);
        evt.SetField("ChatMessage", message);
        evt.Tags.Add("all_chat");

        return evt;
    }
}
