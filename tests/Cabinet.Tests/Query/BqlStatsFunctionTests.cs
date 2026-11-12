using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

public sealed class BqlStatsFunctionTests
{
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query, InMemoryStorage? storage = null)
    {
        if (storage == null)
        {
            storage = new InMemoryStorage();
            var events = new (string Host, int Status, int Minute)[]
            {
                ("web", 200, 3), ("web", 500, 1), ("db", 503, 2), ("web", 200, 4), ("db", 200, 5),
            };
            await storage.StoreBatchAsync(events.Select(e =>
            {
                var logEvent = new LogEvent { RawLine = $"status={e.Status}", Host = e.Host, Timestamp = Start.AddMinutes(e.Minute) };
                logEvent.SetField("status", (long)e.Status);
                return logEvent;
            }));
        }

        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    private static async Task<IDictionary<string, object?>> OneAsync(string query) => (await RunAsync(query)).Single();

    [Fact]
    public async Task Values_AreDistinctAndSorted()
    {
        var row = await OneAsync("find * | stats values(status) as codes");

        ((IEnumerable<object?>)row["codes"]!).Should().Equal("200", "500", "503");
    }

    [Fact]
    public async Task List_KeepsEveryValue()
    {
        var row = await OneAsync("find * | stats list(status) as all");

        ((IEnumerable<object?>)row["all"]!).Should().HaveCount(5);
    }

    [Fact]
    public async Task EarliestAndLatest_FollowTime_NotOrder()
    {
        var row = await OneAsync("find * | stats earliest(status) as first_seen, latest(status) as last_seen");

        row["first_seen"].Should().Be(500L);
        row["last_seen"].Should().Be(200L);
    }

    [Fact]
    public async Task RangeAndMode()
    {
        var row = await OneAsync("find * | stats range(status) as spread, mode(status) as usual");

        Convert.ToDouble(row["spread"]).Should().Be(303);
        row["usual"].Should().Be(200L);
    }

    [Fact]
    public async Task CountOfEval_CountsWhereItsTrue()
    {
        var rows = await RunAsync("find * | stats count(eval(status >= 500)) as errors, count as total by host");

        var web = rows.Single(r => Equals(r["host"], "web"));
        Convert.ToInt64(web["errors"]).Should().Be(1);
        Convert.ToInt64(web["total"]).Should().Be(3);
        Convert.ToInt64(rows.Single(r => Equals(r["host"], "db"))["errors"]).Should().Be(1);
    }

    [Theory]
    [InlineData("in(host, \"web\", \"api\")", true)]
    [InlineData("in(host, \"db\")", false)]
    [InlineData("cidrmatch(\"10.0.0.0/8\", \"10.1.2.3\")", true)]
    [InlineData("cidrmatch(\"10.0.0.0/8\", \"192.168.1.1\")", false)]
    [InlineData("mvjoin(mvzip(split(\"a,b\", \",\"), split(\"1,2\", \",\"), \"=\"), \";\")", "a=1;b=2")]
    [InlineData("mvjoin(mvrange(1, 4), \",\")", "1,2,3")]
    [InlineData("mvjoin(json_keys(\"{\\\"a\\\":1,\\\"b\\\":2}\"), \",\")", "a,b")]
    public async Task EvalFunctions(string expression, object expected)
    {
        var row = (await RunAsync($"find * | head 1 | eval host=\"web\" | eval x={expression}")).Single();

        row["x"].Should().Be(expected);
    }

    [Fact]
    public async Task EarliestAndLatest_InTheSearch_SetTheTimeRange()
    {
        var storage = new InMemoryStorage();
        var now = DateTime.UtcNow;
        await storage.StoreBatchAsync(
        [
            new LogEvent { RawLine = "old", Timestamp = now.AddDays(-3) },
            new LogEvent { RawLine = "recent", Timestamp = now.AddHours(-1) },
        ]);

        var rows = await RunAsync("find earliest=-1d latest=now", storage);

        rows.Should().ContainSingle().Which["RawLine"].Should().Be("recent");
    }

    [Fact]
    public void EarliestSnapsToTheDay()
    {
        var query = new BqlParser().ParseBql("find error earliest=-1d@d");

        query.TimeRange!.Start.Should().Be(DateTime.UtcNow.Date.AddDays(-1));
        query.Stages[0].Should().BeOfType<SearchStage>().Which.SearchText.Should().Be("error");
    }
}
