using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class L4D2ParserTests
{
    private readonly L4D2Parser _parser = new();

    [Fact]
    public void CanParse_WithValidL4D2Log_ReturnsTrue()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:23:45: Loading map \"c1m1_hotel\"";

        // Act
        var result = _parser.CanParse(line);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void CanParse_WithNonL4D2Log_ReturnsFalse()
    {
        // Arrange
        var line = "[2024-12-07 10:00:00] Some other log format";

        // Act
        var result = _parser.CanParse(line);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_ExtractsPlayerInfo()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:24:01: \"Survivor1<2><STEAM_1:0:12345678><>\" connected, address \"192.168.1.100:27005\"";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.L4D2);
        result.GetFieldString("PlayerName").Should().Be("Survivor1");
        result.GetFieldString("PlayerId").Should().Be("2");
        result.GetFieldString("SteamId").Should().Be("STEAM_1:0:12345678");
        result.GetFieldString("IpAddress").Should().Be("192.168.1.100");
        result.Tags.Should().Contain("player_connect");
    }

    [Fact]
    public async Task ParseAsync_ExtractsChatMessages()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:25:30: \"Survivor1<2><STEAM_1:0:12345678><Survivor>\" say \"let's go team!\"";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.GetFieldString("ChatMessage").Should().Be("let's go team!");
        result.Tags.Should().Contain("all_chat");
    }

    [Fact]
    public async Task ParseAsync_ExtractsKillEvents()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:26:45: \"Survivor2<3><STEAM_1:1:87654321><Survivor>\" killed \"Infected<5><BOT><Infected>\" with \"smg\"";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.GetFieldString("Killer").Should().Be("Survivor2");
        result.GetFieldString("Victim").Should().Be("Infected");
        result.GetFieldString("Weapon").Should().Be("smg");
        result.Tags.Should().Contain("kill");
    }

    [Fact]
    public async Task ParseAsync_ExtractsMapInfo()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:23:45: Loading map \"c1m1_hotel\"";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.GetFieldString("Map").Should().Be("c1m1_hotel");
        result.Tags.Should().Contain("map_load");
    }

    [Fact]
    public async Task ParseAsync_DetectsSeverityLevels()
    {
        // Arrange
        var errorLine = "L 12/07/2024 - 10:30:45: ERROR: Failed to load model \"models/infected/special.mdl\"";
        var warningLine = "L 12/07/2024 - 10:31:00: WARNING: Client \"Survivor1\" exceeded command rate limit";

        // Act
        var errorResult = await _parser.ParseAsync(errorLine);
        var warningResult = await _parser.ParseAsync(warningLine);

        // Assert
        errorResult!.Severity.Should().Be(LogSeverity.Error);
        errorResult.Tags.Should().Contain("error");

        warningResult!.Severity.Should().Be(LogSeverity.Warning);
    }

    [Fact]
    public async Task ParseAsync_ExtractsVoteEvents()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:28:15: Vote passed - \"RestartGame\"";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.Tags.Should().Contain("vote_passed");
    }

    [Fact]
    public async Task ParseAsync_ExtractsTimestamp()
    {
        // Arrange
        var line = "L 12/07/2024 - 10:23:45: Test message";

        // Act
        var result = await _parser.ParseAsync(line);

        // Assert
        result.Should().NotBeNull();
        result!.Timestamp.Month.Should().Be(12);
        result.Timestamp.Day.Should().Be(7);
        result.Timestamp.Year.Should().Be(2024);
        result.Timestamp.Hour.Should().Be(10);
        result.Timestamp.Minute.Should().Be(23);
        result.Timestamp.Second.Should().Be(45);
    }

    [Fact]
    public async Task ParseBatchAsync_ProcessesMultipleLines()
    {
        // Arrange
        var lines = new[]
        {
            "L 12/07/2024 - 10:23:45: Loading map \"c1m1_hotel\"",
            "L 12/07/2024 - 10:24:01: \"Survivor1<2><STEAM_1:0:12345678><>\" connected",
            "L 12/07/2024 - 10:25:30: \"Survivor1<2><STEAM_1:0:12345678><Survivor>\" say \"hello\""
        };

        // Act
        var results = new List<LogEvent>();
        await foreach (var evt in _parser.ParseBatchAsync(lines))
        {
            results.Add(evt);
        }

        // Assert
        results.Should().HaveCount(3);
        results[0].Tags.Should().Contain("map_load");
        results[1].Tags.Should().Contain("player_connect");
        results[2].Tags.Should().Contain("all_chat");
    }
}