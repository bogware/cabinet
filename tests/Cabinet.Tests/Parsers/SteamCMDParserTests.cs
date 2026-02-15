using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class SteamCMDParserTests
{
    private readonly SteamCMDParser _parser = new();

    [Fact]
    public void CanParse_SteamCMDUpdate_ReturnsTrue()
    {
        var line = "Update state (0x61) downloading, progress: 42.37 (1234567890 / 2913847204)";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_NonSteamCMD_ReturnsFalse()
    {
        var line = "L 12/07/2024 - 10:00:00: Regular game log";
        _parser.CanParse(line).Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_UpdateProgress_ExtractsFields()
    {
        var line = "Update state (0x61) downloading, progress: 42.37 (1234567890 / 2913847204)";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.SteamCMD);
    }

    [Fact]
    public async Task ParseAsync_AppUpdate_ExtractsAppId()
    {
        var line = " Update state (0x5) verifying install, progress: 100.00 (0 / 0)";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.SteamCMD);
    }

    [Fact]
    public async Task ParseAsync_ErrorLine_SetsErrorSeverity()
    {
        var line = "ERROR! App '222860' state is 0x202 after update job.";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.Severity.Should().BeOneOf(LogSeverity.Error, LogSeverity.Critical);
    }
}
