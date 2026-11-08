using Cabinet.Extras.Gaming;
using Cabinet.Extras.Gaming.Parsers;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Gaming;

/// <summary>
/// Game servers write local time with no offset. The sender's time zone places it; without one,
/// it's read as UTC, so the same line gives the same instant on every host. CI runs these under
/// a non-UTC time zone.
/// </summary>
public class ParserTimestampTests
{
    private static readonly DateTime Expected = new(2024, 12, 7, 10, 23, 45, DateTimeKind.Utc);

    private static readonly LogLineParser LineParser = new ServiceCollection()
        .AddLogging()
        .AddCabinetParsers(s => s.AddGamingParsers())
        .BuildServiceProvider()
        .GetRequiredService<LogLineParser>();

    [Theory]
    [InlineData(GamingSourceTypes.L4D2, "L 12/07/2024 - 10:23:45: World triggered \"Round_Start\"")]
    [InlineData(GamingSourceTypes.SourceMod, "L 12/07/2024 - 10:23:45: [SM] Test message")]
    public async Task WithoutTheSendersZone_TimeIsReadAsUtc(string sourceType, string line)
    {
        var result = await LineParser.ParseAsync(line, sourceType);

        result.Timestamp.Should().Be(Expected);
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Theory]
    [InlineData(GamingSourceTypes.L4D2, "L 11/08/2026 - 06:00:12: World triggered \"Round_Start\"", 12)]
    [InlineData(GamingSourceTypes.SourceMod, "L 11/08/2026 - 06:00:12: [SM] Test message", 12)]
    [InlineData(GamingSourceTypes.L4D2, "L 07/04/2026 - 06:00:12: World triggered \"Round_Start\"", 11)]
    public async Task WithTheSendersZone_TimeIsPlacedInIt(string sourceType, string line, int utcHour)
    {
        // Central time: six hours behind UTC in winter, five in summer
        var chicago = LogLineParser.FindTimeZone("America/Chicago");

        var result = await LineParser.ParseAsync(line, sourceType, timeZone: chicago);

        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        result.Timestamp.Hour.Should().Be(utcHour);
        result.Timestamp.Minute.Should().Be(0);
    }

    [Theory]
    [InlineData("America/Chicago")]
    [InlineData("Central Standard Time")]
    public void ZoneNames_WorkInEitherForm(string name)
    {
        LogLineParser.FindTimeZone(name).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void UnknownZone_IsNull(string? name)
    {
        LogLineParser.FindTimeZone(name).Should().BeNull();
    }

    [Fact]
    public void TimeSkippedWhenClocksGoForward_StillConverts()
    {
        var chicago = LogLineParser.FindTimeZone("America/Chicago")!;

        var act = () => LogLineParser.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified), chicago);

        act.Should().NotThrow();
    }

    [Fact]
    public async Task SteamCmdParser_TimestampWithoutOffset_IsReadAsUtc()
    {
        var parser = new SteamCMDParser(NullLogger<SteamCMDParser>.Instance);

        var result = await parser.ParseAsync("[2024-12-07 10:23:45] Update state (0x5) verifying install");

        result!.Timestamp.Should().Be(Expected);
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }
}
