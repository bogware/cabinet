using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Query.Natural;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>
/// Plain-words queries, each pinned to the BQL it must read as. A failure here is a change in
/// what someone's question means, so update an expectation only on purpose.
/// </summary>
public sealed class NaturalQueryTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 0, 0, DateTimeKind.Utc);

    private static readonly FieldCatalog Catalog = new(
        ["status", "path", "method", "duration_ms", "user", "src_ip", "bytes", "action"],
        new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["host"] = ["web-01", "web-02", "db-01", "api-1"],
            ["sourcetype"] = ["nginx", "auth", "firewall"],
            ["index"] = ["main", "security"],
            ["method"] = ["GET", "POST"],
            ["user"] = ["alice", "bob"],
        });

    private readonly QueryUnderstander _understander = new();

    private QueryUnderstanding Understand(string text) => _understander.Understand(text, Catalog, Now);

    public static TheoryData<string, string> Readings => new()
    {
        // Times, levels, hosts
        { "errors on web servers since yesterday by host", "find * earliest=-1d@d | where severity >= error and host like \"web%\" | stats count by host | sort by count desc" },
        { "show me errors from the last hour", "find * from last 1h | where severity >= error" },
        { "any errors on db-01 today?", "find * earliest=@d | where severity >= error and host = \"db-01\"" },
        { "what happened on web-02 in the last 15 minutes", "find * from last 15m | where host = \"web-02\"" },
        { "show errors from nginx between monday and wednesday", "find * earliest=2026-11-09T00:00:00Z latest=2026-11-11T00:00:00Z | where severity >= error and sourcetype = \"nginx\"" },
        { "show me warnings and errors between 2pm and 4pm", "find * earliest=2026-11-12T14:00:00Z latest=2026-11-12T16:00:00Z | where severity >= warning" },
        { "how many events per sourcetype this week", "find * earliest=@w | stats count by sourcetype | sort by count desc" },
        { "errors excluding web-01 and web-02", "find * | where severity >= error and NOT host in (\"web-01\", \"web-02\")" },
        { "timeout errors not on web-01", "find timeout | where severity >= error and host != \"web-01\"" },
        { "errors where host is not web-01", "find * | where severity >= error and host != \"web-01\"" },
        { "errors in the security index", "find * | where severity >= error and index = \"security\"" },
        { "web-01", "find * | where host = \"web-01\"" },

        // Counting and grouping, in any order
        { "how many errors per host in the last 24 hours", "find * from last 24h | where severity >= error | stats count by host | sort by count desc" },
        { "count of events by sourcetype", "find * | stats count by sourcetype | sort by count desc" },
        { "compare errors by host", "find * | where severity >= error | stats count by host | sort by count desc" },
        { "erors by hots", "find * | where severity >= error | stats count by host | sort by count desc" },
        { "how many hosts", "find * | stats dc(host) as distinct_host" },
        { "how many 404s did we get yesterday", "find * earliest=-1d@d latest=@d | where status = 404 | stats count" },
        { "count of 5xx errors per endpoint", "find * | where status >= 500 and status <= 599 | stats count by path | sort by count desc" },
        { "failed logins in the last hour grouped by src ip", "find failed login* from last 1h | stats count by src_ip | sort by count desc" },

        // Measures
        { "average response time by endpoint over the last 7 days", "find * from last 7d | stats avg(duration_ms) as avg_duration_ms by path | sort by avg_duration_ms desc" },
        { "95th percentile of duration by path", "find * | stats perc95(duration_ms) as p95_duration_ms by path | sort by p95_duration_ms desc" },
        { "p99 latency per host", "find * | stats perc99(duration_ms) as p99_duration_ms by host | sort by p99_duration_ms desc" },
        { "max duration per method", "find * | stats max(duration_ms) as max_duration_ms by method | sort by max_duration_ms desc" },
        { "sum of bytes by host sorted by bytes", "find * | stats sum(bytes) as total_bytes by host | sort by total_bytes desc" },
        { "what is the average duration for POST requests", "find * | where method = \"POST\" | stats avg(duration_ms) as avg_duration_ms" },
        { "top 5 slowest endpoints", "find * | stats avg(duration_ms) as avg_duration_ms by path | sort by avg_duration_ms desc | head 5" },

        // Over time
        { "errors over time", "find * | where severity >= error | timechart span=1h count" },
        { "error rate by hour for the last 2 days", "find * from last 2d | where severity >= error | timechart span=1h count" },
        { "hourly errors by host", "find * | where severity >= error | timechart span=1h count by host" },
        { "how many unique users per day", "find * | timechart span=1d dc(user)" },
        { "bytes sent by host per day", "find * | timechart span=1d sum(bytes) by host" },
        { "requests per minute last 30 minutes", "find * from last 30m | timechart span=1m count" },
        { "events per minute over the last hour by host", "find * from last 1h | timechart span=1m count by host" },

        // Most and least common, values, columns
        { "which host has the most errors", "find * | where severity >= error | top 1 host" },
        { "most common paths", "find * | top 10 path" },
        { "5 most common users", "find * | top 5 user" },
        { "least common status codes", "find * | rare 10 status" },
        { "busiest hosts in the past 6 hours", "find * from last 6h | top 10 host" },
        { "top 10 ips by number of requests", "find * | top 10 src_ip" },
        { "list hosts", "find * | stats count by host | sort by count desc" },
        { "unique ips", "find * | stats count by src_ip | sort by count desc" },
        { "list all distinct paths", "find * | stats count by path | sort by count desc" },
        { "status codes for /api/v1/orders", "find * | where path = \"/api/v1/orders\" | stats count by status | sort by count desc" },
        { "show host, path and status for errors", "find * | where severity >= error | table host, path, status" },
        { "show me only host and message", "find * | table host, message" },
        { "latest event per host", "find * | dedup host" },

        // Comparisons, written every which way
        { "requests where status is greater than or equal to 500", "find * | where status >= 500" },
        { "status 500 errors on web-01", "find * | where severity >= error and status = 500 and host = \"web-01\"" },
        { "nginx logs with status 404", "find * | where status = 404 and sourcetype = \"nginx\"" },
        { "events where user is alice or bob", "find * | where user in (\"alice\", \"bob\")" },
        { "user alice", "find * | where user = \"alice\"" },
        { "logins from alice yesterday", "find login* earliest=-1d@d latest=@d | where user = \"alice\"" },
        { "POST requests over 1000 ms", "find * | where duration_ms > 1000 and method = \"POST\"" },
        { "duration greater than 2 seconds", "find * | where duration_ms > 2000" },
        { "duration over 500 by path", "find * | where duration_ms > 500 | stats count by path | sort by count desc" },
        { "path starts with /api", "find * | where path like \"/api%\"" },
        { "GET /health", "find * | where path = \"/health\" and method = \"GET\"" },

        // Limits and order
        { "latest 20 errors", "find * | where severity >= error | head 20" },
        { "oldest 5 events", "find * | sort by _time asc | head 5" },
        { "last 50 events from db-01", "find * | where host = \"db-01\" | head 50" },
        { "give me the latest 10 warnings from api-1 sorted by time", "find * | where severity >= warning and host = \"api-1\" | head 10" },

        // Plain text, with the asking words dropped
        { "events containing \"connection refused\"", "find \"connection refused\"" },
        { "show me everything", "find *" },
    };

    [Theory]
    [MemberData(nameof(Readings))]
    public void Phrasing_ReadsAs(string text, string expected)
    {
        var understanding = Understand(text);

        understanding.WillRun.Should().BeTrue($"\"{text}\" should be understood");
        understanding.Query.Should().Be(expected, text);
        understanding.Explanation.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("error")]
    [InlineData("connection refused timeout")]
    [InlineData("find error")]
    [InlineData("search status=500 host=web-01")]
    [InlineData("index=security failed")]
    [InlineData("host=\"web-01\" error")]
    [InlineData("\"connection refused\" OR timeout")]
    [InlineData("search error | top host")]
    [InlineData("find * | where status >= 500 | stats count by path")]
    [InlineData("find errors from last 24h")]
    public void StrictQueries_RunExactlyAsTyped(string text)
    {
        var understanding = Understand(text);

        understanding.Kind.Should().Be(UnderstandingKind.Exact);
        understanding.Query.Should().Be(text);
    }

    [Fact]
    public void ANaturalReading_OffersTheExactWordsBack()
    {
        var understanding = Understand("how many errors per host in the last 24 hours");

        understanding.Kind.Should().Be(UnderstandingKind.Natural);
        understanding.Alternatives.Should().Contain(a => a.Query == "how many errors per host in the last 24 hours");
        understanding.Alternatives.Should().Contain(a => a.Query == "find error from last 24h | stats count by host | sort by count desc", "errors can also be the plain word");
        understanding.Explanation.Should().Be("The count of error or worse events for each host, in the last 24 hours, highest first");
    }

    [Fact]
    public void Typos_AreFixed_AndShown()
    {
        var understanding = Understand("erors by hots");

        understanding.Corrections.Select(c => (c.From, c.To)).Should().Equal(("erors", "errors"), ("hots", "host"));
        understanding.Confidence.Should().BeLessThan(1);
    }

    [Theory]
    [InlineData("find error | sotr by count", "find error | sort by count", "sotr", "sort")]
    [InlineData("stats count by hostt", "find * | stats count by host", "hostt", "host")]
    [InlineData("find * | stats count by hots", "find * | stats count by host", "hots", "host")]
    public void MistypedCommandsAndFields_AreCorrected(string text, string expected, string from, string to)
    {
        var understanding = Understand(text);

        understanding.Kind.Should().Be(UnderstandingKind.Corrected);
        understanding.Query.Should().Be(expected);
        understanding.Corrections.Should().ContainSingle(c => c.From == from && c.To == to);
    }

    [Theory]
    [InlineData("find * | stats cuont by host", "find * | stats count by host")]
    [InlineData("find error | count by host", "find error | stats count by host")]
    public void StagesWrittenInWords_AreRead(string text, string expected)
    {
        var understanding = Understand(text);

        understanding.Kind.Should().Be(UnderstandingKind.Natural);
        understanding.Query.Should().Be(expected);
    }

    [Fact]
    public void AQuestionItCantRead_IsntRun_AndComesBackWithChoices()
    {
        var understanding = Understand("which users logged in from more than one ip");

        understanding.Kind.Should().Be(UnderstandingKind.Unclear);
        understanding.WillRun.Should().BeFalse();
        understanding.SuggestAssistant.Should().BeTrue();
        understanding.Alternatives.Should().NotBeEmpty();
        understanding.Problems.Should().ContainSingle().Which.Message.Should().Contain("logged more than one");
    }

    [Fact]
    public void AnUnknownCommand_SuggestsTheNearestOne()
    {
        var understanding = Understand("find error | zzzz by host");

        understanding.WillRun.Should().BeFalse();
        understanding.Problems.Should().ContainSingle().Which.Message.Should().Contain("isn't a command");
    }

    [Fact]
    public void WithoutACatalog_FieldsAreReadFromThePhrasingAlone()
    {
        var understanding = _understander.Understand("average latency by endpoint", null, Now);

        understanding.Query.Should().Be("find * | stats avg(latency_ms) as avg_latency_ms by endpoint | sort by avg_latency_ms desc");
    }

    [Fact]
    public async Task EveryReading_RunsOnRealEvents()
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(Enumerable.Range(0, 200).Select(i =>
        {
            var e = new LogEvent
            {
                RawLine = $"request {i} {(i % 7 == 0 ? "timeout failed login" : "ok")} connection refused",
                Host = new[] { "web-01", "web-02", "db-01", "api-1" }[i % 4],
                SourceType = new[] { "nginx", "auth", "firewall" }[i % 3],
                Severity = (LogSeverity)(i % 6),
                Timestamp = Now.AddMinutes(-i * 13),
                Index = i % 5 == 0 ? "security" : "main",
            };
            e.SetField("status", new long[] { 200, 404, 500, 503 }[i % 4]);
            e.SetField("path", new[] { "/", "/health", "/api/v1/orders", "/login" }[i % 4]);
            e.SetField("method", i % 2 == 0 ? "GET" : "POST");
            e.SetField("duration_ms", (long)(i * 37 % 3000));
            e.SetField("user", i % 3 == 0 ? "alice" : "bob");
            e.SetField("src_ip", $"10.0.0.{i % 9}");
            e.SetField("bytes", (long)(i * 101));
            return e;
        }));
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);

        foreach (var text in Readings.Select(row => (string)row[0]))
        {
            var query = Understand(text).Query!;
            var run = async () => await engine.ExecuteAsync(new QueryExecutionContext { Query = query, Limit = 1000 });
            await run.Should().NotThrowAsync($"\"{text}\" reads as {query}");
        }
    }
}
