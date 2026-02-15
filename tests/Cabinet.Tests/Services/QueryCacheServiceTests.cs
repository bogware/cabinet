using Cabinet.API.Services;
using Cabinet.Query;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public class QueryCacheServiceTests
{
    private readonly QueryCacheService _cache;

    public QueryCacheServiceTests()
    {
        _cache = new QueryCacheService(
            NullLogger<QueryCacheService>.Instance,
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TryGet_Miss_ReturnsNull()
    {
        var result = _cache.TryGet("find errors");
        result.Should().BeNull();
    }

    [Fact]
    public void Set_ThenGet_ReturnsCachedResult()
    {
        var queryResult = new QueryResult
        {
            Events = new List<dynamic> { new { count = 42 } },
            Statistics = new QueryStatistics { EventsScanned = 100, EventsMatched = 42 }
        };

        _cache.Set("find errors", queryResult);
        var cached = _cache.TryGet("find errors");

        cached.Should().NotBeNull();
        cached!.Events.Should().HaveCount(1);
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsNull()
    {
        var shortTtlCache = new QueryCacheService(
            NullLogger<QueryCacheService>.Instance,
            TimeSpan.FromMilliseconds(1));

        shortTtlCache.Set("find errors", new QueryResult());
        Thread.Sleep(10); // Wait for expiry
        var result = shortTtlCache.TryGet("find errors");
        result.Should().BeNull();
    }

    [Fact]
    public void Set_DifferentIndex_CachesMissesCorrectly()
    {
        var result1 = new QueryResult { Events = new List<dynamic> { new { x = 1 } } };
        var result2 = new QueryResult { Events = new List<dynamic> { new { x = 2 } } };

        _cache.Set("find *", result1, index: "index1");
        _cache.Set("find *", result2, index: "index2");

        _cache.TryGet("find *", index: "index1")!.Events.Should().HaveCount(1);
        _cache.TryGet("find *", index: "index2")!.Events.Should().HaveCount(1);
        _cache.TryGet("find *", index: "index3").Should().BeNull();
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        _cache.Set("q1", new QueryResult());
        _cache.Set("q2", new QueryResult());
        _cache.Count.Should().Be(2);

        _cache.Clear();
        _cache.Count.Should().Be(0);
    }

    [Fact]
    public void InvalidateIndex_RemovesOnlyMatchingEntries()
    {
        _cache.Set("q1", new QueryResult(), index: "idx1");
        _cache.Set("q2", new QueryResult(), index: "idx2");
        _cache.Count.Should().Be(2);

        _cache.InvalidateIndex("idx1");
        _cache.Count.Should().Be(1);
        _cache.TryGet("q1", index: "idx1").Should().BeNull();
        _cache.TryGet("q2", index: "idx2").Should().NotBeNull();
    }

    [Fact]
    public void GetStats_ReturnsAccurateCounters()
    {
        _cache.Set("q1", new QueryResult());
        _cache.TryGet("q1"); // hit
        _cache.TryGet("q2"); // miss

        var stats = _cache.GetStats();
        stats.EntryCount.Should().Be(1);
        stats.HitCount.Should().Be(1);
        stats.MissCount.Should().Be(1);
        stats.HitRate.Should().Be(0.5);
    }
}
