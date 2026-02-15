using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class CefParserTests
{
    private readonly CefParser _parser = new();

    [Fact]
    public void CanParse_CefLine_ReturnsTrue()
    {
        _parser.CanParse("CEF:0|Vendor|Product|1.0|100|Event|5|src=10.0.0.1").Should().BeTrue();
    }

    [Fact]
    public void CanParse_CefWithSyslogPrefix_ReturnsTrue()
    {
        _parser.CanParse("Jan 18 11:07:53 host CEF:0|Vendor|Product|1.0|100|Event|5|src=10.0.0.1").Should().BeTrue();
    }

    [Fact]
    public void CanParse_NonCef_ReturnsFalse()
    {
        _parser.CanParse("plain text log line").Should().BeFalse();
        _parser.CanParse("{\"message\":\"json\"}").Should().BeFalse();
        _parser.CanParse("").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_BasicCef_ExtractsHeaderFields()
    {
        var line = "CEF:0|Security|IDS|4.0|100|Intrusion Detected|7|src=10.0.0.1 dst=192.168.1.1";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceTypes.Cef);
        result.GetFieldString("CEFVersion").Should().Be("0");
        result.GetFieldString("DeviceVendor").Should().Be("Security");
        result.GetFieldString("DeviceProduct").Should().Be("IDS");
        result.GetFieldString("DeviceVersion").Should().Be("4.0");
        result.GetFieldString("SignatureId").Should().Be("100");
        result.GetFieldString("EventName").Should().Be("Intrusion Detected");
        result.GetFieldString("CEFSeverity").Should().Be("7");
        result.Tags.Should().Contain("cef");
    }

    [Fact]
    public async Task ParseAsync_CefExtensions_ExtractsKeyValues()
    {
        var line = "CEF:0|Vendor|Product|1.0|100|Event|5|src=10.0.0.1 dst=192.168.1.1 suser=alice duser=bob";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.GetFieldString("src").Should().Be("10.0.0.1");
        result.GetFieldString("dst").Should().Be("192.168.1.1");
        result.GetFieldString("SourceAddress").Should().Be("10.0.0.1");
        result.GetFieldString("DestinationAddress").Should().Be("192.168.1.1");
        result.GetFieldString("SourceUser").Should().Be("alice");
        result.GetFieldString("DestinationUser").Should().Be("bob");
    }

    [Fact]
    public async Task ParseAsync_SeverityMapping_MapsCorrectly()
    {
        // Low severity (0-3)
        var low = await _parser.ParseAsync("CEF:0|V|P|1|1|E|2|src=1.2.3.4");
        low!.Severity.Should().Be(LogSeverity.Info);

        // Medium severity (4-6)
        var medium = await _parser.ParseAsync("CEF:0|V|P|1|1|E|5|src=1.2.3.4");
        medium!.Severity.Should().Be(LogSeverity.Warning);

        // High severity (7-8)
        var high = await _parser.ParseAsync("CEF:0|V|P|1|1|E|8|src=1.2.3.4");
        high!.Severity.Should().Be(LogSeverity.Error);

        // Very-high severity (9-10)
        var critical = await _parser.ParseAsync("CEF:0|V|P|1|1|E|10|src=1.2.3.4");
        critical!.Severity.Should().Be(LogSeverity.Critical);
    }

    [Fact]
    public async Task ParseAsync_SyslogPrefix_ExtractsSyslogHeader()
    {
        var line = "Jan 18 11:07:53 host CEF:0|Vendor|Product|1.0|100|Event|5|src=10.0.0.1";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.GetFieldString("SyslogHeader").Should().Be("Jan 18 11:07:53 host");
    }

    [Fact]
    public async Task ParseAsync_EscapedPipes_HandlesCorrectly()
    {
        var line = "CEF:0|Vendor\\|Name|Product|1.0|100|Event|5|src=10.0.0.1";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.GetFieldString("DeviceVendor").Should().Be("Vendor|Name");
    }

    [Fact]
    public async Task ParseAsync_HostExtension_SetsHost()
    {
        var line = "CEF:0|V|P|1|1|E|5|shost=webserver-01";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.Host.Should().Be("webserver-01");
    }

    [Fact]
    public async Task ParseAsync_MessageExtension_SetsMessage()
    {
        var line = "CEF:0|V|P|1|1|E|5|msg=Unauthorized access attempt";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.GetFieldString("Message").Should().Be("Unauthorized access attempt");
    }

    [Fact]
    public async Task ParseAsync_NoExtensions_HandlesGracefully()
    {
        var line = "CEF:0|Vendor|Product|1.0|100|Event|5|";
        var result = await _parser.ParseAsync(line);

        result.Should().NotBeNull();
        result!.GetFieldString("EventName").Should().Be("Event");
    }

    [Fact]
    public void Name_ReturnsCef()
    {
        _parser.Name.Should().Be("CEF");
    }

    [Fact]
    public void SourceType_ReturnsCef()
    {
        _parser.SourceType.Should().Be(SourceTypes.Cef);
    }
}
