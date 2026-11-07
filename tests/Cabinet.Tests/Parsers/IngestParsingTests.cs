using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cabinet.Tests.Parsers;

/// <summary>What a line becomes, whichever ingest route it took.</summary>
public sealed class IngestParsingTests
{
    private static readonly LogLineParser Parser = new ServiceCollection()
        .AddLogging()
        .AddCabinetParsers()
        .AddSingleton<LogLineParser>()
        .BuildServiceProvider()
        .GetRequiredService<LogLineParser>();

    [Theory]
    [InlineData("2026-03-04T05:06:07Z started", "2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04T05:06:07.123Z started", "2026-03-04T05:06:07.123Z")]
    [InlineData("2026-03-04T05:06:07+02:00 started", "2026-03-04T03:06:07Z")]
    [InlineData("2026-03-04T05:06:07-0500 started", "2026-03-04T10:06:07Z")]
    [InlineData("2026-03-04 05:06:07 started", "2026-03-04T05:06:07Z")]
    [InlineData("2026-03-04 05:06:07,250 INFO [main] started", "2026-03-04T05:06:07.250Z")]
    [InlineData("[2026-03-04 05:06:07] started", "2026-03-04T05:06:07Z")]
    [InlineData("2026/03/04 05:06:07 [error] 12#0: upstream failed", "2026-03-04T05:06:07Z")]
    [InlineData("level=info ts=2026-03-04T05:06:07Z msg=started", "2026-03-04T05:06:07Z")]
    public async Task Line_TimestampIsRead(string line, string expected)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.Timestamp.Should().Be(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
        logEvent.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void SyslogTime_FromDecemberReadInJanuary_IsLastYear()
    {
        var now = new DateTime(2027, 1, 1, 0, 5, 0, DateTimeKind.Utc);

        LogTimestamps.TryFind("Dec 31 23:59:58 host app: message", now, out var timestamp).Should().BeTrue();

        timestamp.Should().Be(new DateTime(2026, 12, 31, 23, 59, 58, DateTimeKind.Utc));
    }

    [Fact]
    public void SyslogTime_SlightlyAheadOfTheServerClock_StaysThisYear()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        LogTimestamps.TryFind("Jun  1 12:05:00 host app: message", now, out var timestamp).Should().BeTrue();

        timestamp.Should().Be(new DateTime(2026, 6, 1, 12, 5, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("Job finished with 0 errors")]
    [InlineData("completed: no errors found")]
    [InlineData("errors=0 warnings=0")]
    [InlineData("failed=0 passed=12")]
    [InlineData("error_count=0")]
    [InlineData("Build succeeded without errors")]
    [InlineData("errors: none")]
    [InlineData("loaded /usr/lib/error_handler.so")]
    [InlineData("GET /api/errors 200")]
    [InlineData("information about the terror attack")]
    public async Task Line_ThatOnlyMentionsAnError_IsNotOne(string line)
    {
        var logEvent = await Parser.ParseAsync(line);

        ((int)logEvent.Severity).Should().BeLessThan((int)LogSeverity.Warning, line);
    }

    [Theory]
    [InlineData("connection failed", LogSeverity.Error)]
    [InlineData("Unhandled exception in worker", LogSeverity.Error)]
    [InlineData("disk error: device full", LogSeverity.Error)]
    [InlineData("3 errors in build", LogSeverity.Error)]
    [InlineData("deprecated call, warning only", LogSeverity.Warning)]
    [InlineData("kernel panic - not syncing", LogSeverity.Fatal)]
    [InlineData("2026-03-04 05:06:07 ERROR something happened", LogSeverity.Error)]
    [InlineData("[WARN] slow query", LogSeverity.Warning)]
    [InlineData("level=warn msg=\"slow query\"", LogSeverity.Warning)]
    [InlineData("level=info msg=\"request failed, retrying\"", LogSeverity.Info)]
    [InlineData("INFO retried after failure", LogSeverity.Info)]
    [InlineData("DEBUG cache miss", LogSeverity.Debug)]
    public async Task Line_SeverityIsRead(string line, LogSeverity expected)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.Severity.Should().Be(expected, line);
    }

    [Fact]
    public async Task Logfmt_FieldsAreTypedAndNamedAsWritten()
    {
        var logEvent = await Parser.ParseAsync("level=info status=503 duration=1.5 user=\"a b\"");

        logEvent.GetField<long>("status").Should().Be(503);
        logEvent.GetField<double>("duration").Should().Be(1.5);
        logEvent.GetField<string>("user").Should().Be("a b");
    }

    [Theory]
    [InlineData("""{"time":1772600767,"msg":"x"}""")]
    [InlineData("""{"time":1772600767000,"msg":"x"}""")]
    [InlineData("""{"time":1772600767.0,"msg":"x"}""")]
    [InlineData("""{"time":"1772600767","msg":"x"}""")]
    [InlineData("""{"time":"2026-03-04T05:06:07Z","msg":"x"}""")]
    [InlineData("""{"time":"2026-03-04T05:06:07","msg":"x"}""")]
    [InlineData("""{"event":{"created":"2026-03-04T05:06:07Z"},"msg":"x"}""")]
    public async Task Json_TimesAreRead(string line)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData("""{"level":"warn","msg":"x"}""", LogSeverity.Warning)]
    [InlineData("""{"log":{"level":"error"},"message":"x"}""", LogSeverity.Error)]
    [InlineData("""{"level":50,"msg":"x"}""", LogSeverity.Error)]
    [InlineData("""{"level":30,"msg":"request failed"}""", LogSeverity.Info)]
    [InlineData("""{"msg":"no level here, error in text"}""", LogSeverity.Info)]
    public async Task Json_LevelComesFromTheLevelField(string line, LogSeverity expected)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.Severity.Should().Be(expected);
    }

    [Fact]
    public async Task Json_SourceDoesNotOverwriteHost_AndEcsHostIsFlattened()
    {
        var logEvent = await Parser.ParseAsync("""{"source":"stdout","host":{"name":"web-1"},"http":{"status":500},"msg":"x"}""");

        logEvent.Host.Should().Be("web-1");
        logEvent.GetField<string>("source").Should().Be("stdout");
        logEvent.GetField<long>("http.status").Should().Be(500);
    }

    [Theory]
    [InlineData("We tried, failed, and gave up")]
    [InlineData("user bob, id 7, logged in")]
    [InlineData("2026-03-04 05:06:07 INFO a,b,c")]
    [InlineData("starting worker with options a,b,c")]
    public async Task Prose_WithCommas_IsNotCsv(string line)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.SourceType.Should().NotBe(SourceTypes.Csv, line);
    }

    [Theory]
    [InlineData("a,b,c")]
    [InlineData("web-1,GET,/index.html,200")]
    [InlineData("2026-03-04 05:06:07,web-1,200")]
    [InlineData("\"hello, there\",b,c")]
    public async Task CsvData_IsCsv(string line)
    {
        var logEvent = await Parser.ParseAsync(line);

        logEvent.SourceType.Should().Be(SourceTypes.Csv, line);
    }

    [Fact]
    public async Task Rfc5424_IsParsed()
    {
        var logEvent = await Parser.ParseAsync(
            """<165>1 2026-03-04T05:06:07.003Z mymachine evntslog 1234 ID47 [exampleSDID@32473 iut="3" eventSource="App\"lication"][other@1 a="b"] An application event""");

        logEvent.SourceType.Should().Be(SourceTypes.Syslog);
        logEvent.Timestamp.Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, 3, DateTimeKind.Utc));
        logEvent.Host.Should().Be("mymachine");
        logEvent.Severity.Should().Be(LogSeverity.Info); // 165 % 8 = 5, notice
        logEvent.GetField<string>("app").Should().Be("evntslog");
        logEvent.GetField<string>("pid").Should().Be("1234");
        logEvent.GetField<string>("msgid").Should().Be("ID47");
        logEvent.GetField<string>("facility_name").Should().Be("local4");
        logEvent.GetField<string>("exampleSDID@32473.iut").Should().Be("3");
        logEvent.GetField<string>("exampleSDID@32473.eventSource").Should().Be("App\"lication");
        logEvent.GetField<string>("other@1.a").Should().Be("b");
        logEvent.GetField<string>("Message").Should().Be("An application event");
    }

    [Fact]
    public async Task Rfc5424_NilValuesAreLeftOut()
    {
        var logEvent = await Parser.ParseAsync("<34>1 2026-03-04T05:06:07Z - - - - - hello");

        logEvent.Severity.Should().Be(LogSeverity.Critical);
        logEvent.Host.Should().BeNullOrEmpty();
        logEvent.GetField<string>("app").Should().BeNull();
        logEvent.GetField<string>("Message").Should().Be("hello");
    }

    [Fact]
    public async Task Rfc3164_IsParsed()
    {
        var logEvent = await Parser.ParseAsync("<34>Oct 11 22:14:15 mymachine su[123]: 'su root' failed on /dev/pts/8");

        logEvent.SourceType.Should().Be(SourceTypes.Syslog);
        logEvent.Host.Should().Be("mymachine");
        logEvent.Severity.Should().Be(LogSeverity.Critical);
        logEvent.Timestamp.Month.Should().Be(10);
        logEvent.GetField<string>("app").Should().Be("su");
        logEvent.GetField<string>("pid").Should().Be("123");
        logEvent.GetField<string>("facility_name").Should().Be("auth");
    }

    [Fact]
    public async Task SyslogFileLine_WithoutPriority_TakesSeverityFromTheMessage()
    {
        var logEvent = await Parser.ParseAsync("Mar  4 05:06:07 web-1 sshd[99]: error: connection failed");

        logEvent.SourceType.Should().Be(SourceTypes.Syslog);
        logEvent.Host.Should().Be("web-1");
        logEvent.Severity.Should().Be(LogSeverity.Error);
        logEvent.GetField<string>("app").Should().Be("sshd");
    }

    [Fact]
    public async Task NamedSourceType_SelectsItsParser_AndKeepsTheLabel()
    {
        var logEvent = await Parser.ParseAsync("""{"level":"error","msg":"x"}""", "my-app");

        logEvent.SourceType.Should().Be("my-app");
        logEvent.Severity.Should().Be(LogSeverity.Error);
    }

    [Fact]
    public async Task FallbackTime_IsUsedOnlyWhenTheLineHasNoTime()
    {
        var sent = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        (await Parser.ParseAsync("no time here", fallbackTime: sent)).Timestamp.Should().Be(sent);
        (await Parser.ParseAsync("2026-03-04T05:06:07Z has one", fallbackTime: sent)).Timestamp
            .Should().Be(new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }

    [Fact]
    public void HugeLine_DoesNotStall()
    {
        var line = new string('a', 1_000_000) + " error";

        var started = DateTime.UtcNow;
        LogSeverities.Detect(line);
        LogTimestamps.TryFind(line, out _);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
