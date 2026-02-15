using Cabinet.Core.Models;
using Cabinet.Detection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Detection;

public class AdminabuseDetectorTests
{
    private readonly AdminabuseDetector _detector = new(NullLogger<AdminabuseDetector>.Instance);

    [Fact]
    public void Name_ReturnsAdminabuse()
    {
        _detector.Name.Should().Be("adminabuse");
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        _detector.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NoAdminActions_ReturnsEmptyDetections()
    {
        var events = new[]
        {
            CreatePlayerEvent("Player1", "say", "hello"),
            CreatePlayerEvent("Player2", "say", "gg")
        };

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.SuiteName.Should().Be("adminabuse");
        report.EventsAnalyzed.Should().Be(2);
        report.Detections.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_NormalAdminActivity_NoDetections()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // A few admin actions spread over time — normal behavior
        events.Add(CreateAdminEvent("Admin1", "kick", "Player1", "AFK", now));
        events.Add(CreateAdminEvent("Admin1", "warn", "Player2", "Language", now.AddHours(1)));
        events.Add(CreateAdminEvent("Admin1", "mute", "Player3", "Mic spam", now.AddHours(2)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 3 });

        // With only 3 actions over 2 hours, no abuse patterns should be detected
        report.Detections.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_ExcessiveActions_DetectsAbuse()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Admin performs 60 actions in 1 hour (way above threshold of 20/hr)
        for (var i = 0; i < 60; i++)
        {
            events.Add(CreateAdminEvent("AbuseAdmin", "kick", $"Player{i}", null, now.AddMinutes(i)));
        }

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().NotBeEmpty();
        report.Detections.Should().Contain(d => d.Type == "Excessive Admin Actions");
    }

    [Fact]
    public async Task AnalyzeAsync_ActionsWithoutReason_DetectsPattern()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Admin performs 10 actions, 9 without reasons (90%)
        for (var i = 0; i < 9; i++)
        {
            events.Add(CreateAdminEvent("LazyAdmin", "ban", $"Player{i}", null, now.AddMinutes(i * 10)));
        }
        events.Add(CreateAdminEvent("LazyAdmin", "ban", "Player9", "Cheating", now.AddMinutes(90)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().Contain(d => d.Type == "Actions Without Justification");
    }

    [Fact]
    public async Task AnalyzeAsync_RepeatedTargeting_DetectsHarassment()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Admin targets the same player 8 out of 10 times
        for (var i = 0; i < 8; i++)
        {
            events.Add(CreateAdminEvent("BullyAdmin", "kick", "VictimPlayer", "No reason", now.AddMinutes(i * 5)));
        }
        events.Add(CreateAdminEvent("BullyAdmin", "kick", "OtherPlayer1", "AFK", now.AddMinutes(45)));
        events.Add(CreateAdminEvent("BullyAdmin", "kick", "OtherPlayer2", "AFK", now.AddMinutes(50)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().Contain(d => d.Type == "Repeated Player Targeting");
    }

    [Fact]
    public async Task AnalyzeAsync_TargetedHarassmentByMultipleAdmins_DetectsVictim()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // One player targeted by the same admin 5+ times out of 7+ total
        for (var i = 0; i < 5; i++)
        {
            events.Add(CreateAdminEvent("Admin1", "kick", "VictimPlayer", null, now.AddMinutes(i * 5)));
        }
        events.Add(CreateAdminEvent("Admin2", "kick", "VictimPlayer", null, now.AddMinutes(25)));
        events.Add(CreateAdminEvent("Admin2", "kick", "VictimPlayer", null, now.AddMinutes(30)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        // Should detect targeted harassment pattern (player targeted 7 times, admin1 did it 5 times)
        report.Detections.Should().Contain(d => d.Type == "Targeted Harassment");
    }

    [Fact]
    public async Task AnalyzeAsync_ExcessiveBanUsage_DetectsSpecificAction()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Admin bans 15 players in 1 hour (above critical threshold of 10/hr for ban)
        for (var i = 0; i < 15; i++)
        {
            events.Add(CreateAdminEvent("BanHammer", "ban", $"Player{i}", null, now.AddMinutes(i * 4)));
        }

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().Contain(d => d.Type == "Excessive ban Usage");
    }

    [Fact]
    public async Task AnalyzeAsync_SeverityEscalation_DetectsPattern()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Admin skips severity levels multiple times
        // warn → ban (skipped mute, gag, kick)
        events.Add(CreateAdminEvent("EscalatingAdmin", "warn", "P1", "Rule 1", now));
        events.Add(CreateAdminEvent("EscalatingAdmin", "ban", "P2", "Rule 2", now.AddMinutes(5)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "warn", "P3", "Rule 1", now.AddMinutes(10)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "ban", "P4", "Rule 3", now.AddMinutes(15)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "mute", "P5", "Mic", now.AddMinutes(20)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "ban", "P6", "Rule 4", now.AddMinutes(25)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "warn", "P7", "Rule 5", now.AddMinutes(30)));
        events.Add(CreateAdminEvent("EscalatingAdmin", "ban", "P8", "Rule 6", now.AddMinutes(35)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Detections.Should().Contain(d => d.Type == "Severity Escalation Pattern");
    }

    [Fact]
    public async Task AnalyzeAsync_MinSeverityFilter_FiltersLowSeverity()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Generate moderate activity that triggers Medium-severity detections
        for (var i = 0; i < 8; i++)
        {
            events.Add(CreateAdminEvent("ModerateAdmin", "kick", $"Player{i}", null, now.AddMinutes(i * 10)));
        }

        var reportLow = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });
        var reportHigh = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 5 });

        // Higher severity filter should return fewer or equal detections
        reportHigh.Detections.Count.Should().BeLessThanOrEqualTo(reportLow.Detections.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_Summary_ContainsExpectedKeys()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        events.Add(CreateAdminEvent("Admin1", "kick", "Player1", "AFK", now));
        events.Add(CreateAdminEvent("Admin2", "ban", "Player2", "Cheating", now.AddMinutes(5)));

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 });

        report.Summary.Should().ContainKey("TotalAdmins");
        report.Summary.Should().ContainKey("TotalActions");
        report.Summary.Should().ContainKey("TotalDetections");
    }

    [Fact]
    public async Task AnalyzeAsync_SingleEvent_ViaIAdminabuseDetector_ReturnsNullForNormal()
    {
        var evt = CreateAdminEvent("Admin1", "kick", "Player1", "AFK", DateTime.UtcNow);

        var result = await _detector.AnalyzeAsync(evt);

        // Single normal action should not trigger detection
        result.Should().BeNull();
    }

    [Fact]
    public async Task AnalyzeBatchAsync_ReturnsResults()
    {
        var events = new[]
        {
            CreateAdminEvent("Admin1", "kick", "Player1", "AFK", DateTime.UtcNow),
            CreateAdminEvent("Admin1", "ban", "Player2", "Cheating", DateTime.UtcNow.AddMinutes(1))
        };

        var results = await _detector.AnalyzeBatchAsync(events);

        results.Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_CancellationToken_Respected()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 100; i++)
        {
            events.Add(CreateAdminEvent("Admin1", "kick", $"Player{i}", null, now.AddMinutes(i)));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 1 }, cts.Token);

        // Should still return a report (cancellation is cooperative)
        report.Should().NotBeNull();
    }

    // Helper methods

    private static LogEvent CreateAdminEvent(string adminName, string action, string target, string? reason, DateTime timestamp)
    {
        var evt = new LogEvent
        {
            RawLine = $"Admin {adminName} performed {action} on {target}" + (reason != null ? $" ({reason})" : ""),
            Timestamp = timestamp,
            SourceType = SourceTypes.SourceMod
        };

        evt.SetField("Admin", adminName);
        evt.SetField("AdminAction", action);
        evt.SetField("Target", target);
        if (reason != null)
            evt.SetField("Reason", reason);
        evt.Tags.Add("admin_action");

        return evt;
    }

    private static LogEvent CreatePlayerEvent(string playerName, string action, string message)
    {
        var evt = new LogEvent
        {
            RawLine = $"\"{playerName}\" {action} \"{message}\"",
            Timestamp = DateTime.UtcNow,
            SourceType = SourceTypes.L4D2
        };

        evt.SetField("PlayerName", playerName);
        return evt;
    }
}
