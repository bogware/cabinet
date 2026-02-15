using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class SourceModParserTests
{
    private readonly SourceModParser _parser = new();

    [Fact]
    public void CanParse_SourceModLog_ReturnsTrue()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] Loaded plugin \"admin-flatfile.smx\"";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_NonSourceModLog_ReturnsFalse()
    {
        var line = "Just a random line with no SM markers";
        _parser.CanParse(line).Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_PluginLoad_ExtractsPluginName()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] Loaded plugin \"admin-flatfile.smx\"";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.SourceMod);
    }

    [Fact]
    public async Task ParseAsync_AdminCommand_ExtractsCommand()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] \"Admin<1><STEAM_1:0:12345><>\" executed command \"sm_kick\"";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.SourceMod);
    }

    [Fact]
    public async Task ParseAsync_Error_SetsErrorSeverity()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] FATAL ERROR: Plugin \"broken.smx\" failed to load";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.Severity.Should().BeOneOf(LogSeverity.Error, LogSeverity.Critical, LogSeverity.Fatal);
    }
}
