using Cabinet.API.Middleware;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public class MetricsTests
{
    [Fact]
    public void RecordRequest_IncrementsCounter()
    {
        // Use unique path to avoid interference from other tests
        MetricsCollector.RecordRequest("GET", "/test/metrics-1", "200", 0.05);
        MetricsCollector.RecordRequest("GET", "/test/metrics-1", "200", 0.10);

        var counts = MetricsCollector.GetRequestCounts()
            .Where(c => c.Path == "/test/metrics-1" && c.Status == "200")
            .ToList();

        counts.Should().HaveCount(1);
        counts[0].Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void RecordRequest_TracksDifferentStatuses()
    {
        MetricsCollector.RecordRequest("POST", "/test/metrics-2", "200", 0.05);
        MetricsCollector.RecordRequest("POST", "/test/metrics-2", "400", 0.02);
        MetricsCollector.RecordRequest("POST", "/test/metrics-2", "500", 0.03);

        var counts = MetricsCollector.GetRequestCounts()
            .Where(c => c.Path == "/test/metrics-2")
            .ToList();

        counts.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void GetLatencyBuckets_ReturnsHistogram()
    {
        MetricsCollector.RecordRequest("GET", "/test/metrics-3", "200", 0.001);
        MetricsCollector.RecordRequest("GET", "/test/metrics-3", "200", 0.5);
        MetricsCollector.RecordRequest("GET", "/test/metrics-3", "200", 2.0);

        var buckets = MetricsCollector.GetLatencyBuckets()
            .Where(b => b.Path == "/test/metrics-3")
            .ToList();

        buckets.Should().NotBeEmpty();
        // Should have standard histogram buckets + +Inf
        var infBucket = buckets.FirstOrDefault(b => b.IsInf);
        infBucket.Should().NotBeNull();
        infBucket!.TotalCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public void GetLatencyBuckets_SumTracksTotal()
    {
        MetricsCollector.RecordRequest("GET", "/test/metrics-4", "200", 1.0);
        MetricsCollector.RecordRequest("GET", "/test/metrics-4", "200", 2.0);

        var infBucket = MetricsCollector.GetLatencyBuckets()
            .FirstOrDefault(b => b.Path == "/test/metrics-4" && b.IsInf);

        infBucket.Should().NotBeNull();
        infBucket!.Sum.Should().BeGreaterThanOrEqualTo(3.0);
    }
}
