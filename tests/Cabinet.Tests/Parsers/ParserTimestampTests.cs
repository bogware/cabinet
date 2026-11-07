using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Parsers;

/// <summary>
/// Log lines without an offset are read as UTC, so the same line produces the
/// same instant on every host. CI runs these under a non-UTC time zone.
/// </summary>
public class ParserTimestampTests
{
    private static readonly DateTime Expected = new(2024, 12, 7, 10, 23, 45, DateTimeKind.Utc);

    [Fact]
    public async Task L4D2Parser_TimestampWithoutOffset_IsReadAsUtc()
    {
        var parser = new L4D2Parser(NullLogger<L4D2Parser>.Instance);

        var result = await parser.ParseAsync("L 12/07/2024 - 10:23:45: Test message");

        result!.Timestamp.Should().Be(Expected);
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task SourceModParser_TimestampWithoutOffset_IsReadAsUtc()
    {
        var parser = new SourceModParser(NullLogger<SourceModParser>.Instance);

        var result = await parser.ParseAsync("L 12/07/2024 - 10:23:45: [SM] Test message");

        result!.Timestamp.Should().Be(Expected);
        result.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
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
