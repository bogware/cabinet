using Cabinet.Core.Models;
using Cabinet.Extras.Gaming;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cabinet.Tests.Gaming;

/// <summary>
/// Lines in the shapes a live Left 4 Dead 2 server with SourceMod writes, with names, Steam IDs and
/// addresses replaced.
/// </summary>
public sealed class RealServerLogTests
{
    private static readonly LogLineParser Parser = new ServiceCollection()
        .AddLogging()
        .AddCabinetParsers(s => s.AddGamingParsers())
        .BuildServiceProvider()
        .GetRequiredService<LogLineParser>();

    private static Task<LogEvent> L4D2(string body) => Parser.ParseAsync($"L 11/05/2026 - 12:07:12: {body}", GamingSourceTypes.L4D2);

    private static Task<LogEvent> SourceMod(string body) => Parser.ParseAsync($"L 11/08/2026 - 07:10:05: {body}", GamingSourceTypes.SourceMod);

    [Fact]
    public async Task Chat_WithTheLongPlayerFormat_NamesThePlayer()
    {
        var e = await L4D2("\"Alpha<8><STEAM_1:0:111><Infected><SPITTER><DEAD><1+0><setpos -4513.59 7293.46 89.90; setang -29.03 -82.76 0.00><Area 15833>\" say_team \"witch\"");

        e.Fields.Should().Contain("PlayerName", "Alpha").And.Contain("SteamId", "STEAM_1:0:111")
            .And.Contain("Team", "Infected").And.Contain("Character", "SPITTER").And.Contain("ChatMessage", "witch");
        e.Tags.Should().Contain("team_chat");
    }

    [Theory]
    [InlineData("this server has so much lag")]
    [InlineData("is that a hack")]
    [InlineData("admin pls, error on my end")]
    public async Task WhatPlayersType_DoesNotTagOrRaiseSeverity(string message)
    {
        var e = await L4D2($"\"Alpha<8><STEAM_1:0:111><Survivor><Coach><ALIVE><100+0><setpos 1 2 3; setang 0 0 0><Area 1>\" say \"{message}\"");

        e.Severity.Should().Be(LogSeverity.Info);
        e.Tags.Should().BeEquivalentTo("all_chat");
    }

    [Fact]
    public async Task InfectedBotKick_IsNotAnAdminAction()
    {
        var e = await L4D2("\"Jockey<6><BOT><Infected>\" disconnected (reason \"Kicked by Console : death anim finished\")");

        e.Tags.Should().BeEquivalentTo("player_disconnect");
        e.Fields.Should().Contain("IsBot", true).And.Contain("DisconnectReason", "Kicked by Console : death anim finished");
    }

    [Fact]
    public async Task Disconnect_KeepsJustTheReason()
    {
        var e = await L4D2("\"Bravo<22><STEAM_1:1:222><Spectator>\" disconnected (reason \"Disconnect by user.\")");

        e.Fields.Should().Contain("DisconnectReason", "Disconnect by user.").And.Contain("Team", "Spectator");
    }

    [Fact]
    public async Task PlayerKill_RecordsBothSides()
    {
        var e = await L4D2("(DEATH)\"Charlie Jewel<4><STEAM_1:1:333><Survivor><Mechanic><ALIVE><100+0><setpos_exact -5768.22 7317.45 354.03; setang 2.19 164.92 0.00><Area 389665>\" killed \"ツ \"G\"<6><STEAM_1:0:444><Infected><SPITTER><DEAD><1+0><setpos -6064.85 7393.36 292.03; setang 337.95 345.65 0.00><Area 16873>\" with \"smg\"");

        e.Fields.Should().Contain("Killer", "Charlie Jewel").And.Contain("KillerSteamId", "STEAM_1:1:333")
            .And.Contain("Victim", "ツ \"G\"").And.Contain("VictimSteamId", "STEAM_1:0:444")
            .And.Contain("VictimCharacter", "SPITTER").And.Contain("Weapon", "smg");
        e.Tags.Should().Contain("kill").And.Contain("player_kill");
    }

    [Fact]
    public async Task BotKillingCommonInfected_IsAKillButNotAPlayerKill()
    {
        var e = await L4D2("(DEATH)\"Rochelle<4><BOT><Survivor><Producer><ALIVE><100+0><setpos_exact 1 2 3; setang 0 0 0><Area 1>\" killed \"infected<457><><Infected><infected><DEAD><-9><setpos_exact 1 2 3; setang 0 0 0><Area 1>\" with \"pistol\"");

        e.Fields.Should().Contain("KillerIsBot", true).And.Contain("Victim", "infected");
        e.Tags.Should().Contain("kill").And.NotContain("player_kill");
    }

    [Fact]
    public async Task Ban_RecordsLengthAndWhoBanned()
    {
        var e = await L4D2("Banid: \"<><STEAM_1:0:555><>\" was banned \"for 5.00 minutes\" by \"Console\"");

        e.Fields.Should().Contain("SteamId", "STEAM_1:0:555").And.Contain("BanDuration", "5.00 minutes").And.Contain("BannedBy", "Console");
        e.Tags.Should().Contain("ban");
        e.Severity.Should().Be(LogSeverity.Warning);
    }

    [Fact]
    public async Task UpdateNotice_IsAWarning()
    {
        var e = await L4D2("Your server needs to be restarted in order to receive the latest update.");

        e.Tags.Should().Contain("update_required");
        e.Severity.Should().Be(LogSeverity.Warning);
    }

    [Theory]
    [InlineData("World triggered \"Round_Start\"", "world_event")]
    [InlineData("\"Bravo<2><STEAM_1:0:222><>\" entered the game", "player_enter")]
    [InlineData("\"Bravo<2><STEAM_1:0:222><>\" STEAM USERID validated", "player_validated")]
    [InlineData("Started map \"c4m1_milltown_a\" (CRC \"-687439715\")", "map_start")]
    [InlineData("\"Bravo<2><STEAM_1:0:222><>\" connected, address \"203.0.113.5:27005\"", "player_connect")]
    public async Task EventsAreTagged(string body, string tag)
    {
        (await L4D2(body)).Tags.Should().Contain(tag);
    }

    [Fact]
    public async Task SourceModConsole_HasNoSteamId()
    {
        var e = await SourceMod("[basecommands.smx] \"Console<0><Console><Console>\" changed cvar (cvar \"sv_maxunlag\") (value \"1\")");

        e.Fields.Should().Contain("Plugin", "basecommands.smx").And.NotContainKey("SteamId");
    }

    [Fact]
    public async Task SourceModBlaming_NamesThePlugin()
    {
        var e = await SourceMod("[SM] Blaming: bizzymod/l4d_boss_percent.smx");

        e.Fields.Should().Contain("Plugin", "bizzymod/l4d_boss_percent.smx");
        e.Severity.Should().Be(LogSeverity.Error);
    }
}
