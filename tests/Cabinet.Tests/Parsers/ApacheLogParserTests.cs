using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class ApacheLogParserTests
{
    private readonly ApacheLogParser _parser = new();

    [Fact]
    public void CanParse_CombinedLogFormat_ReturnsTrue()
    {
        var line = @"192.168.1.1 - frank [10/Oct/2000:13:55:36 -0700] ""GET /apache_pb.gif HTTP/1.0"" 200 2326 ""http://www.example.com/start.html"" ""Mozilla/4.08""";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_CommonLogFormat_ReturnsTrue()
    {
        var line = @"127.0.0.1 - frank [10/Oct/2000:13:55:36 -0700] ""GET /index.html HTTP/1.0"" 200 2326";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_RandomText_ReturnsFalse()
    {
        _parser.CanParse("just some random text").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_CombinedFormat_ExtractsAllFields()
    {
        var line = @"192.168.1.100 - admin [15/Jan/2024:09:30:45 +0000] ""POST /api/login HTTP/1.1"" 401 512 ""https://example.com/"" ""curl/8.0""";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.SourceType.Should().Be("Apache");
        evt.GetFieldString("ClientIP").Should().Be("192.168.1.100");
        evt.GetFieldString("User").Should().Be("admin");
        evt.GetFieldString("Method").Should().Be("POST");
        evt.GetFieldString("Uri").Should().Be("/api/login");
        evt.GetField<int>("Status").Should().Be(401);
        evt.GetField<long>("Bytes").Should().Be(512);
        evt.GetFieldString("Referer").Should().Be("https://example.com/");
        evt.GetFieldString("UserAgent").Should().Be("curl/8.0");
        evt.Severity.Should().Be(Cabinet.Core.Models.LogSeverity.Warning); // 4xx
        evt.Tags.Should().Contain("access_log");
        evt.Tags.Should().Contain("http");
    }

    [Fact]
    public async Task ParseAsync_500Error_SeverityError()
    {
        var line = @"10.0.0.1 - - [01/Feb/2024:12:00:00 +0000] ""GET /fail HTTP/1.1"" 503 0 ""-"" ""bot""";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.GetField<int>("Status").Should().Be(503);
        evt.Severity.Should().Be(Cabinet.Core.Models.LogSeverity.Error);
    }

    [Fact]
    public async Task ParseAsync_CommonFormat_NoRefererOrUserAgent()
    {
        var line = @"127.0.0.1 - - [10/Oct/2000:13:55:36 -0700] ""GET / HTTP/1.0"" 200 1024";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.GetFieldString("ClientIP").Should().Be("127.0.0.1");
        evt.GetFieldString("Method").Should().Be("GET");
    }
}
