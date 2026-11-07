using Cabinet.Core.Models;
using Cabinet.Extras.Gaming;
using Cabinet.Extras.Gaming.Detection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Gaming;

public class HackwatchDetectorTests
{
    private readonly HackwatchDetector _detector = new(NullLogger<HackwatchDetector>.Instance);

    [Fact]
    public async Task AnalyzeAsync_VacBanKeyword_DetectsCheat()
    {
        var events = new[]
        {
            CreateEvent("Player was VAC banned from secure server", playerName: "BannedPlayer")
        };

        var report = await _detector.AnalyzeAsync(events, CreateOptions());

        report.Detections.Should().NotBeEmpty();
        report.Detections.Should().Contain(d => d.Type == "VAC Ban");
    }

    [Fact]
    public async Task AnalyzeAsync_AimbotKeyword_DetectsCheat()
    {
        var events = new[]
        {
            CreateEvent("Suspected aimbot usage detected for player", playerName: "Cheater1")
        };

        var report = await _detector.AnalyzeAsync(events, CreateOptions());

        report.Detections.Should().NotBeEmpty();
        report.Detections.Should().Contain(d => d.Type == "Aimbot");
    }

    [Fact]
    public async Task AnalyzeAsync_CleanLogs_NoDetections()
    {
        var events = new[]
        {
            CreateEvent("Player1 connected to server"),
            CreateEvent("Map changed to de_dust2"),
            CreateEvent("Player1 scored 5 points")
        };

        var report = await _detector.AnalyzeAsync(events, new DetectionOptions { MinSeverity = 5 });

        report.Detections.Should().BeEmpty();
    }

    [Fact]
    public async Task AnalyzeAsync_HighKDRatio_DetectsAnomaly()
    {
        var events = new List<LogEvent>();
        // Create 50 kills and 1 death for a single player
        for (var i = 0; i < 50; i++)
        {
            var evt = CreateEvent($"Player killed Victim{i}", playerName: "SuspiciousPlayer", steamId: "STEAM_0:1:99999");
            evt.Tags.Add("kill");
            events.Add(evt);
        }

        // Add one death
        var deathEvt = CreateEvent("EnemyPlayer killed SuspiciousPlayer", playerName: "SuspiciousPlayer", steamId: "STEAM_0:1:99999");
        deathEvt.SetField("Victim", "SuspiciousPlayer");
        events.Add(deathEvt);

        var report = await _detector.AnalyzeAsync(events, CreateOptions(minSeverity: 1));

        report.Detections.Should().Contain(d => d.Type == "Impossible K/D Ratio");
    }

    [Fact]
    public async Task AnalyzeAsync_HeadshotDetection_UsesTagsNotWeapon()
    {
        var events = new List<LogEvent>();
        // Create kills with headshot tag (not weapon name containing "headshot")
        for (var i = 0; i < 15; i++)
        {
            var evt = CreateEvent($"Player killed Victim{i} with ak47", playerName: "Sniper", steamId: "STEAM_0:1:88888");
            evt.Tags.Add("kill");
            evt.Tags.Add("headshot");
            evt.SetField("Weapon", "ak47");
            events.Add(evt);
        }

        var report = await _detector.AnalyzeAsync(events, CreateOptions(minSeverity: 1));

        // Should detect headshot anomaly since all 15 kills are headshots (100%)
        report.Detections.Should().Contain(d => d.Type == "Impossible Headshot Rate");
    }

    [Fact]
    public async Task AnalyzeAsync_RapidReconnections_DetectsAnomaly()
    {
        var events = new List<LogEvent>();
        var now = DateTime.UtcNow;

        // Create 15 rapid reconnections in 10 minutes
        for (var i = 0; i < 15; i++)
        {
            var connectEvt = CreateEvent("Player connected", playerName: "BanEvader", steamId: "STEAM_0:1:77777");
            connectEvt.Tags.Add("player_connect");
            connectEvt.Timestamp = now.AddMinutes(i);
            events.Add(connectEvt);

            var disconnectEvt = CreateEvent("Player disconnected", playerName: "BanEvader", steamId: "STEAM_0:1:77777");
            disconnectEvt.Tags.Add("player_disconnect");
            disconnectEvt.Timestamp = now.AddMinutes(i).AddSeconds(30);
            events.Add(disconnectEvt);
        }

        var report = await _detector.AnalyzeAsync(events, CreateOptions(minSeverity: 1));

        report.Detections.Should().Contain(d => d.Type == "Rapid Reconnections");
    }

    [Fact]
    public async Task AnalyzeAsync_CommandSpam_DetectsAnomaly()
    {
        var events = new List<LogEvent>();

        for (var i = 0; i < 150; i++)
        {
            var evt = CreateEvent($"Player executed command: sv_cheats {i}", playerName: "Spammer", steamId: "STEAM_0:1:66666");
            evt.SetField("Command", $"sv_cheats {i}");
            events.Add(evt);
        }

        var report = await _detector.AnalyzeAsync(events, CreateOptions(minSeverity: 1));

        report.Detections.Should().Contain(d => d.Type == "Command Flooding");
    }

    [Fact]
    public async Task AnalyzeAsync_ReturnsCorrectEventCount()
    {
        var events = new[]
        {
            CreateEvent("Event 1"),
            CreateEvent("Event 2"),
            CreateEvent("Event 3")
        };

        var report = await _detector.AnalyzeAsync(events, CreateOptions());

        report.EventsAnalyzed.Should().Be(3);
        report.SuiteName.Should().Be("hackwatch");
    }

    [Fact]
    public void Name_ReturnsHackwatch()
    {
        _detector.Name.Should().Be("hackwatch");
    }

    private static LogEvent CreateEvent(string rawLine, string? playerName = null, string? steamId = null)
    {
        var evt = new LogEvent
        {
            RawLine = rawLine,
            Timestamp = DateTime.UtcNow,
            SourceType = GamingSourceTypes.L4D2
        };

        if (playerName != null)
            evt.SetField("PlayerName", playerName);
        if (steamId != null)
            evt.SetField("SteamId", steamId);

        return evt;
    }

    private static DetectionOptions CreateOptions(int minSeverity = 3)
    {
        return new DetectionOptions
        {
            MinSeverity = minSeverity,
            Threshold = 0.8
        };
    }
}
