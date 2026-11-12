using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage.Engine;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public sealed class EngineLogStorageTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);
    private readonly string _path = Directory.CreateTempSubdirectory("cabinet-engine-").FullName;
    private readonly List<EngineLogStorage> _open = [];

    public void Dispose()
    {
        _open.ForEach(engine => engine.Dispose());
        Directory.Delete(_path, recursive: true);
    }

    private EngineLogStorage Open(EngineOptions? options = null)
    {
        var engine = new EngineLogStorage(_path, options);
        _open.Add(engine);
        return engine;
    }

    private void Close(EngineLogStorage engine)
    {
        engine.Dispose();
        _open.Remove(engine);
    }

    private static LogEvent Event(int i, string index = "main", int minutesOffset = 0) => new()
    {
        Id = $"evt-{i:D8}",
        RawLine = $"request {i} from host-{i % 3} took {i % 50}ms {(i % 10 == 0 ? "error" : "ok")}",
        Host = $"host-{i % 3}",
        Index = index,
        Severity = i % 10 == 0 ? LogSeverity.Error : LogSeverity.Info,
        Timestamp = Start.AddSeconds(i).AddMinutes(minutesOffset),
        Fields = { ["status"] = i % 10 == 0 ? 500L : 200L, ["path"] = $"/p{i % 4}" },
    };

    [Fact]
    public async Task EventsSurviveAReopen_FromTheLog_AndFromSegments()
    {
        var engine = Open();
        await engine.StoreBatchAsync(Enumerable.Range(0, 100).Select(i => Event(i)));
        await engine.FlushAsync();
        await engine.StoreBatchAsync(Enumerable.Range(100, 50).Select(i => Event(i)));
        Close(engine);

        var reopened = Open();
        (await reopened.GetCountAsync()).Should().Be(150);
        var newest = await reopened.QueryAsync(new QueryContext { Limit = 1 }).SingleAsync();
        newest.Id.Should().Be("evt-00000149");
        newest.GetField<long>("status").Should().Be(200);
    }

    [Fact]
    public async Task ATornLogRecord_IsDropped_AndEverythingBeforeItKept()
    {
        var engine = Open();
        await engine.StoreBatchAsync(Enumerable.Range(0, 10).Select(i => Event(i)));
        Close(engine);

        // A crash cut the last write short
        var log = Directory.EnumerateFiles(_path, "wal-*.log", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(log, "\u0010\0\0\0garbage");

        var reopened = Open();
        (await reopened.GetCountAsync()).Should().Be(10);
    }

    [Fact]
    public async Task ResentEvents_AreStoredOnce_WhetherInMemoryOrInSegments()
    {
        var engine = Open(new EngineOptions { FlushEvents = 1000 });
        var events = Enumerable.Range(0, 5000).Select(i => Event(i)).ToList();

        (await engine.StoreBatchAsync(events)).Should().Be(5000);
        (await engine.StoreBatchAsync(events)).Should().Be(0, "every one is already in a segment");
        (await engine.StoreBatchAsync([Event(1), Event(1), Event(9999)])).Should().Be(1);
        (await engine.GetCountAsync()).Should().Be(5001);
    }

    [Fact]
    public async Task AResentEvent_WithADifferentTime_IsStillStoredOnce()
    {
        // A collector that stamps events on arrival gives a retry a later time than the original
        var engine = Open();
        await engine.StoreBatchAsync(Enumerable.Range(0, 10).Select(i => Event(i)));
        await engine.FlushAsync();

        var retried = Event(3, minutesOffset: 90);
        (await engine.StoreBatchAsync([retried])).Should().Be(0);
        Close(engine);

        (await Open().StoreBatchAsync([Event(4, minutesOffset: -90)])).Should().Be(0, "the check holds after a reopen too");
    }

    [Fact]
    public async Task LateEvents_InOverlappingSegments_StillComeOutInOrder_AndPageByCursor()
    {
        var engine = Open(new EngineOptions { FlushEvents = 100 });
        await engine.StoreBatchAsync(Enumerable.Range(0, 300).Where(i => i % 2 == 0).Select(i => Event(i)));
        await engine.StoreBatchAsync(Enumerable.Range(0, 300).Where(i => i % 2 == 1).Select(i => Event(i)));
        await engine.StoreBatchAsync([Event(1000, minutesOffset: -60)]);

        var seen = new List<LogEvent>();
        var page = new QueryContext { Limit = 37 };
        while (true)
        {
            var batch = await engine.QueryAsync(page).ToListAsync();
            seen.AddRange(batch);
            if (batch.Count < page.Limit)
            {
                break;
            }

            page = page.Clone();
            page.After = new EventCursor(batch[^1].Timestamp, batch[^1].Id);
        }

        seen.Should().HaveCount(301);
        seen.Select(e => e.Timestamp).Should().BeInDescendingOrder();
        seen[^1].Id.Should().Be("evt-00001000");
    }

    [Fact]
    public async Task Filters_SkipBlocks_AndStillFindEveryMatch()
    {
        var engine = Open(new EngineOptions { FlushEvents = 2000 });
        await engine.StoreBatchAsync(Enumerable.Range(0, 20_000).Select(i => Event(i)));
        await engine.FlushAsync();

        (await engine.GetCountAsync(new QueryContext { SearchText = "error" })).Should().Be(2000);
        (await engine.GetCountAsync(new QueryContext { Host = "HOST-1" })).Should().Be(6667);
        (await engine.GetCountAsync(new QueryContext { MinSeverity = LogSeverity.Error, Host = "host-0" })).Should().Be(667);
        (await engine.GetCountAsync(new QueryContext { FieldFilters = new() { ["status"] = "500" } })).Should().Be(2000);
        (await engine.GetCountAsync(new QueryContext { StartTime = Start.AddSeconds(100), EndTime = Start.AddSeconds(199) })).Should().Be(100);
        (await engine.GetCountAsync(new QueryContext { SearchText = "nosuchword" })).Should().Be(0);
    }

    [Fact]
    public async Task Purge_DropsWholeSegments_AndRewritesOneThatStraddles()
    {
        var engine = Open(new EngineOptions { FlushEvents = 100 });
        await engine.StoreBatchAsync(Enumerable.Range(0, 1000).Select(i => Event(i)));
        await engine.StoreBatchAsync(Enumerable.Range(0, 10).Select(i => Event(5000 + i, "other")));

        var deleted = await engine.PurgeIndexEventsAsync("main", Start.AddSeconds(450));

        deleted.Should().Be(450);
        (await engine.GetCountAsync(new QueryContext { Index = "main" })).Should().Be(550);
        (await engine.GetCountAsync(new QueryContext { Index = "other" })).Should().Be(10);
        (await engine.QueryAsync(new QueryContext { Index = "main", SortDescending = false, Limit = 1 }).SingleAsync()).Id.Should().Be("evt-00000450");

        Close(engine);
        (await Open().GetCountAsync(new QueryContext { Index = "main" })).Should().Be(550, "the purge holds after a reopen");
    }

    [Fact]
    public async Task DeletingAnIndex_RemovesItsFolder_AndOthersStay()
    {
        var engine = Open();
        await engine.StoreBatchAsync(Enumerable.Range(0, 10).Select(i => Event(i, "a/b c")));
        await engine.StoreBatchAsync(Enumerable.Range(10, 5).Select(i => Event(i, "keep")));

        (await engine.DeleteByIndexAsync("a/b c")).Should().Be(10);

        (await engine.GetStatsAsync()).EventsByIndex.Should().ContainSingle().Which.Key.Should().Be("keep");
        Directory.GetDirectories(_path).Should().ContainSingle();
    }

    [Fact]
    public async Task GetById_AndStats_CoverMemoryAndSegments()
    {
        var engine = Open(new EngineOptions { FlushEvents = 100 });
        await engine.StoreBatchAsync(Enumerable.Range(0, 150).Select(i => Event(i)));

        (await engine.GetByIdAsync("evt-00000007"))!.Host.Should().Be("host-1");
        (await engine.GetByIdAsync("evt-00000140"))!.Host.Should().Be("host-2");
        (await engine.GetByIdAsync("missing")).Should().BeNull();

        var stats = await engine.GetStatsAsync();
        stats.TotalEvents.Should().Be(150);
        stats.EventsBySeverity[LogSeverity.Error].Should().Be(15);
        stats.OldestEvent.Should().Be(Start);
    }

    [Fact]
    public async Task Aggregates_AcrossManyBlocks_CountWholeBlocksUnread_AndSplitTheRest()
    {
        var engine = Open(new EngineOptions { FlushEvents = 5000 });
        await engine.StoreBatchAsync(Enumerable.Range(0, 20_000).Select(i => Event(i)));
        await engine.StoreBatchAsync(Enumerable.Range(20_000, 700).Select(i => Event(i)));

        var all = await engine.AggregateAsync(new AggregateRequest
        {
            Filter = new QueryContext(),
            Aggregates = [new AggregateSpec(AggregateKind.Count, null, 0, "count")],
        });
        all!.Rows.Single()["count"].Should().Be(20_700);

        var hourly = await engine.AggregateAsync(new AggregateRequest
        {
            Filter = new QueryContext { StartTime = Start.AddSeconds(1000) },
            Bucket = TimeSpan.FromHours(1),
            Aggregates = [new AggregateSpec(AggregateKind.Count, null, 0, "count")],
        });
        hourly!.Rows.Select(r => (int)r["count"]!).Should().Equal(3600 - 1000, 3600, 3600, 3600, 3600, 20_700 - 5 * 3600);
        hourly.Rows.Select(r => (DateTime)r["_time"]!).Should().BeInAscendingOrder();

        var byHost = await engine.AggregateAsync(new AggregateRequest
        {
            Filter = new QueryContext { MinSeverity = LogSeverity.Error },
            GroupBy = ["host"],
            Aggregates = [new AggregateSpec(AggregateKind.Count, null, 0, "count"), new AggregateSpec(AggregateKind.Max, "status", 0, "max")],
        });
        byHost!.Rows.Should().HaveCount(3);
        byHost.Rows.Sum(r => (int)r["count"]!).Should().Be(2070);
        byHost.Rows.Should().OnlyContain(r => (double)r["max"]! == 500);
        byHost.Rows[0]["host"].Should().Be("host-2", "the newest error, 20690, is on host-2");

        // Spread over blocks read by different workers, then merged
        var spread = await engine.AggregateAsync(new AggregateRequest
        {
            Filter = new QueryContext(),
            GroupBy = ["path"],
            Aggregates =
            [
                new AggregateSpec(AggregateKind.SampleDeviation, "status", 0, "sd"),
                new AggregateSpec(AggregateKind.Percentile, "status", 95, "p95"),
                new AggregateSpec(AggregateKind.Average, "status", 0, "avg"),
                new AggregateSpec(AggregateKind.DistinctCount, "host", 0, "hosts"),
            ],
        });
        foreach (var row in spread!.Rows)
        {
            var statuses = Enumerable.Range(0, 20_700).Where(i => $"/p{i % 4}" == (string)row["path"]!).Select(i => i % 10 == 0 ? 500.0 : 200.0).OrderBy(v => v).ToList();
            var mean = statuses.Average();
            ((double)row["sd"]!).Should().BeApproximately(Math.Sqrt(statuses.Sum(v => (v - mean) * (v - mean)) / (statuses.Count - 1)), 1e-9);
            ((double)row["avg"]!).Should().BeApproximately(mean, 1e-9);
            var index = 0.95 * (statuses.Count - 1);
            var (lower, upper) = ((int)Math.Floor(index), (int)Math.Ceiling(index));
            ((double)row["p95"]!).Should().Be(statuses[lower] + ((index - lower) * (statuses[upper] - statuses[lower])));
            row["hosts"].Should().Be(3);
        }
    }

    [Theory]
    [InlineData("needle")]
    [InlineData("NEEDLE")]
    [InlineData("café")]
    [InlineData("x")]
    public async Task WordSearch_InStoredBytes_MatchesTheWordRule(string term)
    {
        string[] lines =
        [
            "a needle here", "NEEDLE at start", "the needles", "xneedle", "needle—dash", "naïveneedle", "needleé", "é needle", "needle.",
            "(needle)", "café open", "Café", "cafés", "x marks", "x", "box", "日本 needle 日本", "needle space", "",
        ];
        var engine = Open();
        var reference = new Cabinet.Storage.InMemoryStorage();
        var events = lines.Select((line, i) => new LogEvent { Id = $"w{i:D3}", RawLine = line, Timestamp = Start.AddSeconds(i) }).ToList();
        await engine.StoreBatchAsync(events);
        await engine.FlushAsync();
        await reference.StoreBatchAsync(events);

        var found = await engine.QueryAsync(new QueryContext { SearchText = term, Limit = 100 }).Select(e => e.RawLine).ToListAsync();
        var expected = await reference.QueryAsync(new QueryContext { SearchText = term, Limit = 100 }).Select(e => e.RawLine).ToListAsync();

        found.Should().BeEquivalentTo(expected);
        (await engine.GetCountAsync(new QueryContext { SearchText = term })).Should().Be(expected.Count);
    }

    private int SegmentFiles() => Directory.EnumerateFiles(_path, "*.seg", SearchOption.AllDirectories).Count();

    [Fact]
    public async Task Compaction_MergesSmallSegments_KeepingEveryEventOnceAndInOrder()
    {
        var engine = Open(new EngineOptions { FlushEvents = 100 });
        for (var b = 0; b < 25; b++)
        {
            // Interleaved times, so merged segments overlap and must be re-sorted
            await engine.StoreBatchAsync(Enumerable.Range(0, 100).Select(i => Event((i * 25) + b)));
            await engine.FlushAsync();
        }

        SegmentFiles().Should().Be(25);
        (await engine.CompactAsync()).Should().BeGreaterThan(0);
        SegmentFiles().Should().BeLessThan(10);

        (await engine.GetCountAsync()).Should().Be(2500);
        (await engine.StoreBatchAsync(Enumerable.Range(0, 2500).Select(i => Event(i)))).Should().Be(0, "every ID is still known");
        var all = await engine.QueryAsync(new QueryContext { Limit = 10_000, SortDescending = false }).ToListAsync();
        all.Select(e => e.Id).Should().Equal(Enumerable.Range(0, 2500).Select(i => $"evt-{i:D8}"));
        all[123].GetField<long>("status").Should().Be(200);
        (await engine.GetByIdAsync("evt-00001234"))!.Host.Should().Be("host-1");

        Close(engine);
        (await Open().GetCountAsync()).Should().Be(2500);
    }

    [Fact]
    public async Task AMergeCutShort_AfterWritingItsSegment_LeavesNoDuplicates()
    {
        var engine = Open(new EngineOptions { FlushEvents = 10 });
        for (var b = 0; b < 10; b++)
        {
            await engine.StoreBatchAsync(Enumerable.Range(b * 10, 10).Select(i => Event(i)));
            await engine.FlushAsync();
        }

        var originals = Directory.EnumerateFiles(_path, "*.seg", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        (await engine.CompactAsync()).Should().Be(1);
        Close(engine);

        // As if the process died after the merged segment was written, before the others were deleted
        foreach (var (file, bytes) in originals)
        {
            await File.WriteAllBytesAsync(file, bytes);
        }

        var reopened = Open();
        (await reopened.GetCountAsync()).Should().Be(100);
        SegmentFiles().Should().Be(1);
    }

    [Fact]
    public async Task WritesDuringMerges_AreAllKept()
    {
        var engine = Open(new EngineOptions { FlushEvents = 50, SyncWrites = false });
        await engine.StoreBatchAsync(Enumerable.Range(0, 2000).Select(i => Event(i)));

        var writer = Task.Run(async () =>
        {
            for (var b = 0; b < 40; b++)
            {
                await engine.StoreBatchAsync(Enumerable.Range(0, 50).Select(i => Event(100_000 + (b * 50) + i)));
            }
        });
        var merger = Task.Run(async () =>
        {
            for (var round = 0; round < 10; round++)
            {
                await engine.CompactAsync();
            }
        });
        await Task.WhenAll(writer, merger);
        await engine.CompactAsync();

        (await engine.GetCountAsync()).Should().Be(4000);
        (await engine.StoreBatchAsync([Event(5), Event(100_005)])).Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentWritersAndReaders_LoseNothing()
    {
        var engine = Open(new EngineOptions { FlushEvents = 500, SyncWrites = false });
        var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var b = 0; b < 25; b++)
            {
                await engine.StoreBatchAsync(Enumerable.Range(0, 50).Select(i => Event(w * 100_000 + b * 50 + i)));
            }
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var q = 0; q < 20; q++)
            {
                await engine.QueryAsync(new QueryContext { SearchText = "error", Limit = 50 }).ToListAsync();
            }
        }));

        await Task.WhenAll(writers.Concat(readers));

        (await engine.GetCountAsync()).Should().Be(8 * 25 * 50);
    }
}
