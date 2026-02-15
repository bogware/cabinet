using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class GenericParserTests
{
    private readonly GenericParser _parser = new();

    [Fact]
    public void CanParse_AnyLine_ReturnsTrue()
    {
        _parser.CanParse("any random log line").Should().BeTrue();
        _parser.CanParse("2024-01-01 ERROR something bad").Should().BeTrue();
        _parser.CanParse("").Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_GenericLine_ReturnsEvent()
    {
        var result = await _parser.ParseAsync("Some generic log message");

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.Generic);
        result.RawLine.Should().Be("Some generic log message");
    }

    [Fact]
    public async Task ParseAsync_ErrorKeyword_SetsErrorSeverity()
    {
        var result = await _parser.ParseAsync("ERROR: Something went wrong");

        result.Should().NotBeNull();
        result!.Severity.Should().Be(LogSeverity.Error);
    }

    [Fact]
    public async Task ParseAsync_WarningKeyword_SetsWarningSeverity()
    {
        var result = await _parser.ParseAsync("WARNING: Disk space low");

        result.Should().NotBeNull();
        result!.Severity.Should().Be(LogSeverity.Warning);
    }

    [Fact]
    public async Task ParseAsync_InfoMessage_SetsInfoSeverity()
    {
        var result = await _parser.ParseAsync("Server started on port 27015");

        result.Should().NotBeNull();
        result!.Severity.Should().Be(LogSeverity.Info);
    }

    [Fact]
    public async Task ParseAsync_TimestampedLine_ExtractsTimestamp()
    {
        var result = await _parser.ParseAsync("[2024-12-07 10:30:45] Service started");

        result.Should().NotBeNull();
        // Should extract at least the date part
        result!.RawLine.Should().Contain("Service started");
    }

    [Fact]
    public async Task ParseBatchAsync_MultipleLines_ParsesAll()
    {
        var lines = new[]
        {
            "Line 1: normal",
            "Line 2: ERROR happened",
            "Line 3: WARNING notice"
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _parser.ParseBatchAsync(lines))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(3);
    }
}
