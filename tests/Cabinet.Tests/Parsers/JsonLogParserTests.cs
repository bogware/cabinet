using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class JsonLogParserTests
{
    private readonly JsonLogParser _parser = new();

    [Fact]
    public void CanParse_ValidJson_ReturnsTrue()
    {
        _parser.CanParse("{\"message\":\"hello\"}").Should().BeTrue();
    }

    [Fact]
    public void CanParse_NonJson_ReturnsFalse()
    {
        _parser.CanParse("plain text log line").Should().BeFalse();
        _parser.CanParse("[2024-01-01] ERROR").Should().BeFalse();
        _parser.CanParse("").Should().BeFalse();
    }

    [Fact]
    public void CanParse_JsonWithWhitespace_ReturnsTrue()
    {
        _parser.CanParse("  { \"key\": \"value\" }  ").Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_BasicJson_ExtractsFields()
    {
        var json = "{\"message\":\"test event\",\"level\":\"error\",\"host\":\"web-01\"}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.Json);
        result.GetFieldString("Message").Should().Be("test event");
        result.Severity.Should().Be(LogSeverity.Error);
        result.Host.Should().Be("web-01");
        result.Tags.Should().Contain("json");
    }

    [Fact]
    public async Task ParseAsync_TimestampExtraction_SetsTimestamp()
    {
        var json = "{\"timestamp\":\"2024-06-15T10:30:00Z\",\"message\":\"test\"}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ParseAsync_AtTimestamp_SetsTimestamp()
    {
        var json = "{\"@timestamp\":\"2024-06-15T10:30:00Z\",\"msg\":\"hello\"}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.Timestamp.Should().Be(new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ParseAsync_AllSeverityLevels_MapsCorrectly()
    {
        var levels = new Dictionary<string, LogSeverity>
        {
            ["trace"] = LogSeverity.Trace,
            ["debug"] = LogSeverity.Debug,
            ["info"] = LogSeverity.Info,
            ["warn"] = LogSeverity.Warning,
            ["error"] = LogSeverity.Error,
            ["fatal"] = LogSeverity.Fatal,
            ["critical"] = LogSeverity.Fatal,
            ["information"] = LogSeverity.Info,
            ["warning"] = LogSeverity.Warning
        };

        foreach (var (level, expected) in levels)
        {
            var json = $"{{\"level\":\"{level}\",\"msg\":\"test\"}}";
            var result = await _parser.ParseAsync(json);
            result!.Severity.Should().Be(expected, $"level '{level}' should map to {expected}");
        }
    }

    [Fact]
    public async Task ParseAsync_NestedJson_ExtractsAsRawText()
    {
        var json = "{\"message\":\"test\",\"context\":{\"user\":\"alice\",\"id\":42}}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        var context = result!.GetFieldString("context");
        context.Should().Contain("alice");
        context.Should().Contain("42");
    }

    [Fact]
    public async Task ParseAsync_NumericValues_ExtractsCorrectly()
    {
        var json = "{\"message\":\"test\",\"duration\":123,\"ratio\":0.95}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.GetField<long>("duration").Should().Be(123);
        result.GetField<double>("ratio").Should().BeApproximately(0.95, 0.001);
    }

    [Fact]
    public async Task ParseAsync_BooleanValues_ExtractsCorrectly()
    {
        var json = "{\"message\":\"test\",\"success\":true,\"cached\":false}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.GetField<bool>("success").Should().BeTrue();
        result!.GetField<bool>("cached").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_NullFields_Ignored()
    {
        var json = "{\"message\":\"test\",\"optional\":null}";
        var result = await _parser.ParseAsync(json);

        result.Should().NotBeNull();
        result!.GetFieldString("optional").Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_HostVariants_ExtractsHost()
    {
        var hostFields = new[] { "host", "hostname", "server", "node" };

        foreach (var field in hostFields)
        {
            var json = $"{{\"{field}\":\"prod-01\",\"msg\":\"test\"}}";
            var result = await _parser.ParseAsync(json);
            result!.Host.Should().Be("prod-01", $"field '{field}' should set Host");
        }
    }

    [Fact]
    public void Name_ReturnsJson()
    {
        _parser.Name.Should().Be("JSON");
    }

    [Fact]
    public void SourceType_ReturnsJson()
    {
        _parser.SourceType.Should().Be(SourceTypes.Json);
    }
}
