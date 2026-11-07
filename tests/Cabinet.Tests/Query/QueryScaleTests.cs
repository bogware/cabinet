using System.Runtime.CompilerServices;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;
using Cabinet.Query.Bql;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Query;

/// <summary>Queries over far more events than fit in memory, from a generated event source.</summary>
public sealed class QueryScaleTests
{
    private const int ManyEvents = 350_000;

    private static BqlQueryEngine Engine(ILogStorage storage, QueryLimits? limits = null) =>
        new(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance, limits: limits);

    private static Task<QueryResult> RunAsync(ILogStorage storage, string query, QueryLimits? limits = null, int limit = 100_000, TimeSpan? timeout = null) =>
        Engine(storage, limits).ExecuteAsync(new QueryExecutionContext { Query = query, Limit = limit, Timeout = timeout ?? TimeSpan.FromMinutes(2) });

    private static List<IDictionary<string, object?>> Rows(QueryResult result) =>
        result.Events.Select(row => (IDictionary<string, object?>)row).ToList();

    [Fact]
    public async Task Stats_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats count by host");

        result.Statistics!.Truncated.Should().BeFalse();
        result.Statistics.EventsScanned.Should().Be(ManyEvents);
        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
        result.Events.Should().HaveCount(GeneratedStorage.HostCount);
    }

    [Fact]
    public async Task Aggregates_MatchTheirDefinition_OverEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats sum(n) as total, avg(n) as mean, min(n) as low, max(n) as high, dc(host) as hosts, stdevp(n) as sd");

        var row = Rows(result).Single();
        var n = (double)ManyEvents;
        Convert.ToDouble(row["total"]).Should().Be(n * (n - 1) / 2);
        Convert.ToDouble(row["mean"]).Should().BeApproximately((n - 1) / 2, 1e-6);
        Convert.ToDouble(row["low"]).Should().Be(0);
        Convert.ToDouble(row["high"]).Should().Be(n - 1);
        Convert.ToInt32(row["hosts"]).Should().Be(GeneratedStorage.HostCount);
        Convert.ToDouble(row["sd"]).Should().BeApproximately(Math.Sqrt(((n * n) - 1) / 12), 1e-3);
    }

    [Fact]
    public async Task Median_OverTooManyToKeep_IsCloseAndMarkedApproximate()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats median(n) as mid, p99(n) as tail");

        result.Statistics!.Approximate.Should().BeTrue();
        var row = Rows(result).Single();
        Convert.ToDouble(row["mid"]).Should().BeApproximately(ManyEvents * 0.5, ManyEvents * 0.01);
        Convert.ToDouble(row["tail"]).Should().BeApproximately(ManyEvents * 0.99, ManyEvents * 0.01);
    }

    [Fact]
    public async Task Median_OfAFewValues_IsExact()
    {
        var storage = new GeneratedStorage(4);

        var result = await RunAsync(storage, "find * | stats median(n) as mid");

        result.Statistics!.Approximate.Should().BeFalse();
        Convert.ToDouble(Rows(result).Single()["mid"]).Should().Be(1.5);
    }

    [Fact]
    public async Task Timechart_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | timechart span=1h count");

        result.Statistics!.Truncated.Should().BeFalse();
        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
    }

    [Fact]
    public async Task Top_BeyondTheOldReadLimit_CountsEveryEvent()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | top 100 host");

        Rows(result).Sum(row => Convert.ToInt64(row["count"])).Should().Be(ManyEvents);
    }

    [Fact]
    public async Task Filter_MatchingOnlyPastTheOldReadLimit_FindsIt()
    {
        var storage = new GeneratedStorage(ManyEvents);

        // Newest first, so n=0 is the very last event read
        var result = await RunAsync(storage, "find * | where n = 0");

        result.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task Head_ReadsOnlyWhatItNeeds()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | head 5");

        result.Events.Should().HaveCount(5);
        result.Statistics!.EventsScanned.Should().Be(5);
        storage.PagesRead.Should().Be(1);
    }

    [Fact]
    public async Task FilterThenHead_StopsReadingOnceItHasEnough()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | where n < 340000 | head 5");

        result.Events.Should().HaveCount(5);
        result.Statistics!.EventsScanned.Should().BeLessThan(ManyEvents / 10);
    }

    [Fact]
    public async Task SortThenHead_OverEveryEvent_GivesTheSameAsAFullSort()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | sort n | head 3");

        result.Statistics!.Truncated.Should().BeFalse();
        Rows(result).Select(row => Convert.ToInt64(row["n"])).Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task Sort_OfMoreRowsThanItCanHold_IsMarkedTruncated()
    {
        var storage = new GeneratedStorage(2_000);

        var result = await RunAsync(storage, "find * | sort n | eval x = 1", new QueryLimits { MaxRowsInMemory = 500 });

        result.Statistics!.Truncated.Should().BeTrue();
        result.Events.Should().HaveCount(500);
    }

    [Fact]
    public async Task Sort_OfMixedTypes_DoesNotThrow()
    {
        var storage = new Mock<ILogStorage>();
        var events = new[] { "200", "-", "404" }.Select((status, i) =>
        {
            var logEvent = new LogEvent { RawLine = status, Timestamp = DateTime.UtcNow.AddSeconds(i) };
            logEvent.SetField("status", long.TryParse(status, out var number) ? number : status);
            return logEvent;
        }).ToList();
        storage
            .Setup(s => s.QueryAsync(It.IsAny<QueryContext>(), It.IsAny<CancellationToken>()))
            .Returns(events.ToAsyncEnumerable());

        var result = await RunAsync(storage.Object, "find * | sort status");

        Rows(result).Select(row => row["status"]!.ToString()).Should().Equal("200", "404", "-");
    }

    [Fact]
    public async Task TooManyGroups_FailsTheQuery_RatherThanRunningOutOfMemory()
    {
        var storage = new GeneratedStorage(1_000);

        var run = () => RunAsync(storage, "find * | stats count by n", new QueryLimits { MaxGroups = 100 });

        await run.Should().ThrowAsync<QueryLimitExceededException>().WithMessage("*100 groups*");
    }

    [Fact]
    public async Task TooManyDistinctValues_FailsTheQuery()
    {
        var storage = new GeneratedStorage(1_000);

        var run = () => RunAsync(storage, "find * | stats dc(n) as values", new QueryLimits { MaxRetainedValues = 100 });

        await run.Should().ThrowAsync<QueryLimitExceededException>();
    }

    [Fact]
    public async Task MaxEventsScanned_StopsTheQuery_AndMarksItTruncated()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var result = await RunAsync(storage, "find * | stats count by host", new QueryLimits { MaxEventsScanned = 20_000 });

        result.Statistics!.Truncated.Should().BeTrue();
        result.Statistics.EventsScanned.Should().Be(20_000);
    }

    [Fact]
    public async Task SlowRegex_FailsQuickly_InsteadOfUsingTheWholeTimeLimit()
    {
        var storage = new GeneratedStorage(1_000, line: _ => new string('a', 40) + "!");
        var started = DateTime.UtcNow;

        var run = () => RunAsync(storage, "find * | rex field=RawLine \"(a+)+$\"");

        await run.Should().ThrowAsync<QueryLimitExceededException>().WithMessage("*too slow*");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Timeout_StopsAQueryThatIsStillReading()
    {
        var storage = new GeneratedStorage(int.MaxValue);
        var started = DateTime.UtcNow;

        var run = () => RunAsync(storage, "find * | stats count by host", timeout: TimeSpan.FromMilliseconds(300));

        await run.Should().ThrowAsync<OperationCanceledException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void LongChainOfNot_DoesNotOverflowTheStack()
    {
        var query = "find * | where " + string.Concat(Enumerable.Repeat("not ", 9_900)) + "a = 1";

        var parse = () => new BqlParser().ParseBql(query);

        parse.Should().NotThrow();
    }

    [Fact]
    public async Task Multisearch_CantHoldMoreThanTheRowLimit()
    {
        var storage = new GeneratedStorage(1_000);

        var result = await RunAsync(
            storage,
            "| multisearch [search \"*\"] [search \"*\"] [search \"*\"]",
            new QueryLimits { MaxRowsInMemory = 1_500 });

        result.Events.Should().HaveCount(1_500);
        result.Statistics!.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task Stream_BeyondTheOldReadLimit_ReadsEveryMatch()
    {
        var storage = new GeneratedStorage(ManyEvents);

        var count = 0L;
        await foreach (var _ in Engine(storage).ExecuteStreamAsync(new QueryExecutionContext { Query = "find * | where n < 50000", Limit = int.MaxValue }))
        {
            count++;
        }

        count.Should().Be(50_000);
    }

    /// <summary>
    /// Generates events on demand, honoring the paging cursor, so a test can read millions of them
    /// without storing any.
    /// </summary>
    private sealed class GeneratedStorage : ILogStorage
    {
        public const int HostCount = 7;

        private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly int _count;
        private readonly Func<int, string> _line;

        public GeneratedStorage(int count, Func<int, string>? line = null)
        {
            _count = count;
            _line = line ?? (n => $"event {n}");
        }

        public int PagesRead { get; private set; }

        public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PagesRead++;

            // Newest first: the highest n comes first
            var next = context.After == null ? _count - 1 : int.Parse(context.After.Id) - 1;
            for (var i = 0; i < context.Limit && next >= 0; i++, next--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var logEvent = new LogEvent
                {
                    Id = next.ToString(),
                    RawLine = _line(next),
                    Host = $"host-{next % HostCount}",
                    Timestamp = Start.AddSeconds(next % 86_400),
                };
                logEvent.SetField("n", (long)next);
                yield return logEvent;
            }

            await Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default) => Task.FromResult((long)_count);

        public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
