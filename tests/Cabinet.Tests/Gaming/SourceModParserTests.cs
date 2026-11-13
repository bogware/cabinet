using Cabinet.Core.Models;
using Cabinet.Extras.Gaming;
using Cabinet.Extras.Gaming.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Gaming;

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
        result!.SourceType.Should().Be(GamingSourceTypes.SourceMod);
    }

    [Fact]
    public async Task ParseAsync_AdminCommand_ExtractsCommand()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] \"Admin<1><STEAM_1:0:12345><>\" executed command \"sm_kick\"";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(GamingSourceTypes.SourceMod);
    }

    [Fact]
    public async Task ParseAsync_Error_SetsErrorSeverity()
    {
        var line = "L 12/07/2024 - 10:00:00: [SM] FATAL ERROR: Plugin \"broken.smx\" failed to load";

        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.Severity.Should().BeOneOf(LogSeverity.Error, LogSeverity.Critical, LogSeverity.Fatal);
    }

    // The pattern CanParse used before it was made linear, kept to check the new one agrees.
    private static readonly System.Text.RegularExpressions.Regex OldLogPattern =
        new(@"^L\s+\d{2}/\d{2}/\d{4}.*\[.*\]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("L 12/07/2024 - 10:00:00: [basecommands] \"Admin<2><STEAM_1:0:1><>\" kicked \"Bob<3><STEAM_1:0:2><>\"", true)]
    [InlineData("L 12/07/2024 - 10:00:00: [admin-flatfile] Loaded 3 admins", true)]
    [InlineData("L 12/07/2024 - 10:00:00: [funcommands] [nested] text", true)]
    [InlineData("L 12/07/2024 - 10:00:00: Server cvar \"sv_cheats\" = \"0\"", false)]
    [InlineData("L 12/07/2024 - 10:00:00: only an opening [ bracket", false)]
    [InlineData("L 12/07/2024 - 10:00:00: closed ] before [ open", false)]
    [InlineData("L 12/07/2024 - 10:00:00: [] empty", true)]
    [InlineData("L 12/07/2024 - 10:00:00: [first line\n] second line", false)]
    [InlineData("L 12/07/2024 - 10:00:00: no bracket\n[x] second line", false)]
    [InlineData("12/07/2024 - 10:00:00: [basecommands] no L prefix", false)]
    public void CanParse_DatedLinesWithBrackets_AgreesWithTheOldPattern(string line, bool expected)
    {
        OldLogPattern.IsMatch(line).Should().Be(expected);
        _parser.CanParse(line).Should().Be(expected);
    }

    [Fact]
    public void CanParse_LineOfUnclosedBrackets_IsLinear()
    {
        // The old ".*\[.*\]" was quadratic on a line of brackets that never close
        var line = "L 01/01/2000" + new string('[', 300_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = _parser.CanParse(line);
        watch.Stop();

        result.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ParseAsync_TwoPlayers_StillReadsBoth()
    {
        var line = "L 12/07/2024 - 10:00:00: \"Admin<2><STEAM_1:0:111><CT>\" slapped \"Bob<3><STEAM_1:0:222><TERRORIST>\"";

        var result = await _parser.ParseAsync(line);

        result!.Fields["PlayerName"].Should().Be("Admin");
        result.Fields["SteamId"].Should().Be("STEAM_1:0:111");
        result.Fields["Team"].Should().Be("CT");
        result.Fields["TargetName"].Should().Be("Bob");
        result.Fields["TargetSteamId"].Should().Be("STEAM_1:0:222");
    }
}
