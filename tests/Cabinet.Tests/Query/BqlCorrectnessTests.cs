using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>Answers that used to be quietly different from SPL's.</summary>
public sealed class BqlCorrectnessTests
{
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);

    private static async Task<List<IDictionary<string, object?>>> RunAsync(string query, int events = 0, IEnumerable<LogEvent>? extra = null)
    {
        var storage = new InMemoryStorage();
        var batch = new List<LogEvent>
        {
            new() { RawLine = "error disk full", Host = "web", Timestamp = Start.AddMinutes(1) },
            new() { RawLine = "error timeout", Host = "db", Timestamp = Start.AddMinutes(2) },
            new() { RawLine = "ok fine", Host = "web", Timestamp = Start.AddMinutes(3) },
        };
        batch.AddRange(Enumerable.Range(0, events).Select(i => new LogEvent { RawLine = $"bulk {i}", Host = "bulk", Timestamp = Start.AddSeconds(-i - 1) }));
        if (extra != null)
        {
            batch.AddRange(extra);
        }

        await storage.StoreBatchAsync(batch);
        var engine = new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
        var result = await engine.ExecuteAsync(new QueryExecutionContext { Query = query, Limit = 100_000 });
        return result.Events.Select(e => (IDictionary<string, object?>)e).ToList();
    }

    [Theory]
    [InlineData("find error host=web")]
    [InlineData("find host=web error")]
    [InlineData("find * | search error host=web")]
    [InlineData("find * | search host=web error")]
    public async Task TextAndFieldSearch_Together_MatchBoth(string query)
    {
        var rows = await RunAsync(query);

        rows.Should().ContainSingle().Which["RawLine"].Should().Be("error disk full");
    }

    [Fact]
    public async Task StatsBy_LeavesOutEventsWithoutTheField()
    {
        var rows = await RunAsync("find * | eval kind=if(host=\"web\", \"w\", nosuchfield) | stats count by kind");

        rows.Should().ContainSingle().Which["kind"].Should().Be("w");
    }

    [Fact]
    public async Task Rare_PercentIsOfAllEvents()
    {
        var rows = await RunAsync("find * | rare host");

        var db = rows.Single(r => Equals(r["host"], "db"));
        Convert.ToDouble(db["percent"]).Should().BeApproximately(100.0 / 3, 0.01);
    }

    [Fact]
    public async Task Tail_GivesTheOldestEvents_EvenPastTheFirstPageRead()
    {
        var rows = await RunAsync("find * | tail 2", events: 5000);

        rows.Select(r => r["RawLine"]).Should().BeEquivalentTo(new[] { "bulk 4999", "bulk 4998" });
    }

    [Theory]
    [InlineData("find * | head 99999999999")]
    [InlineData("find * | head abc")]
    public async Task BadNumbers_AreParseErrors(string query)
    {
        var act = () => RunAsync(query);

        await act.Should().ThrowAsync<QueryParseException>();
    }

    [Fact]
    public async Task Timechart_UsesATimeMadeByEval()
    {
        var rows = await RunAsync("find * | eval _time=relative_time(_time, \"+1d\") | timechart span=1h count");

        rows.Should().ContainSingle().Which["_time"].Should().Be(Start.AddDays(1));
    }

    [Fact]
    public async Task Streamstats_OverManyEvents_IsFastAndRight()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = await RunAsync("find * | streamstats count as n", events: 20_000);
        watch.Stop();

        rows.Select(r => Convert.ToInt64(r["n"])).Should().BeInAscendingOrder().And.HaveCount(20_003);
        rows[^1]["n"].Should().Be(20_003L);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("find * | where _time >= \"2026-03-04T05:02:00Z\"", 2)]
    [InlineData("find * | where _time > \"2026-03-04T05:02:00Z\"", 1)]
    [InlineData("find * | where _time <= \"2026-03-04T05:02:00Z\"", 2)]
    [InlineData("find * | where _time < \"2026-03-04T05:02:00Z\" AND host = \"web\"", 1)]
    public async Task TimeComparisons_AreExact(string query, int expected)
    {
        (await RunAsync(query)).Should().HaveCount(expected);
    }

    [Fact]
    public void TimeComparisons_NarrowWhatStorageReads()
    {
        var query = new BqlParser().ParseBql("find * | where _time >= \"2026-03-04T05:02:00Z\" AND _time < \"2026-03-04T06:00:00Z\"");

        var optimized = new BqlOptimizer().Optimize(query, new QueryExecutionContext { Query = "" });

        optimized.StorageContext.StartTime.Should().Be(Start.AddMinutes(2));
        optimized.StorageContext.EndTime.Should().Be(Start.AddHours(1));
    }

    [Fact]
    public async Task Like_WithTheSamePattern_ManyTimes_Works()
    {
        var rows = await RunAsync("find * | where like(RawLine, \"bulk 1%\")", events: 2000);

        rows.Should().HaveCount(1111);
    }
}
