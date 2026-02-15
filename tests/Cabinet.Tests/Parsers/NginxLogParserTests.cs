using Cabinet.Parsers;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Parsers;

public class NginxLogParserTests
{
    private readonly NginxLogParser _parser = new();

    [Fact]
    public void CanParse_AccessLog_ReturnsTrue()
    {
        var line = @"93.180.71.3 - - [17/May/2015:08:05:32 +0000] ""GET /downloads/product_1 HTTP/1.1"" 304 0 ""-"" ""Debian APT-HTTP/1.3""";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_ErrorLog_ReturnsTrue()
    {
        var line = @"2024/01/15 13:55:36 [error] 1234#5678: *99 open() ""/var/www/html/missing.html"" failed (2: No such file or directory)";
        _parser.CanParse(line).Should().BeTrue();
    }

    [Fact]
    public void CanParse_RandomText_ReturnsFalse()
    {
        _parser.CanParse("hello world").Should().BeFalse();
    }

    [Fact]
    public async Task ParseAsync_AccessLog_ExtractsAllFields()
    {
        var line = @"10.0.0.5 - user [20/Jan/2024:14:30:00 +0000] ""GET /api/v1/health HTTP/2.0"" 200 45 ""https://app.example.com"" ""Mozilla/5.0""";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.SourceType.Should().Be("Nginx");
        evt.GetFieldString("ClientIP").Should().Be("10.0.0.5");
        evt.GetFieldString("User").Should().Be("user");
        evt.GetFieldString("Method").Should().Be("GET");
        evt.GetFieldString("Uri").Should().Be("/api/v1/health");
        evt.GetFieldString("HttpVersion").Should().Be("HTTP/2.0");
        evt.GetField<int>("Status").Should().Be(200);
        evt.GetField<long>("Bytes").Should().Be(45);
        evt.GetFieldString("Referer").Should().Be("https://app.example.com");
        evt.GetFieldString("UserAgent").Should().Be("Mozilla/5.0");
        evt.Severity.Should().Be(Cabinet.Core.Models.LogSeverity.Info);
        evt.Tags.Should().Contain("access_log");
    }

    [Fact]
    public async Task ParseAsync_ErrorLog_ExtractsFields()
    {
        var line = @"2024/01/15 13:55:36 [error] 1234#5678: *99 upstream timed out";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.SourceType.Should().Be("Nginx");
        evt.GetFieldString("PID").Should().Be("1234");
        evt.GetFieldString("TID").Should().Be("5678");
        evt.GetFieldString("ConnectionID").Should().Be("99");
        evt.GetFieldString("Message").Should().Be("upstream timed out");
        evt.Severity.Should().Be(Cabinet.Core.Models.LogSeverity.Error);
        evt.Tags.Should().Contain("error_log");
    }

    [Fact]
    public async Task ParseAsync_CritErrorLog_SeverityCritical()
    {
        var line = @"2024/06/01 00:00:00 [crit] 1#0: worker_connections are not enough";
        var evt = await _parser.ParseAsync(line);

        evt.Should().NotBeNull();
        evt!.Severity.Should().Be(Cabinet.Core.Models.LogSeverity.Critical);
    }
}
