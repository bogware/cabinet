using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Agent;

public class LogForwarderTests : IAsyncDisposable
{
    private readonly Mock<ILogger<LogForwarder>> _loggerMock = new();
    private LogForwarder? _forwarder;

    [Fact]
    public void LogEventDto_SerializesTimeAsEpoch()
    {
        var dto = new LogEventDto
        {
            Timestamp = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            Event = "test event",
            Host = "web-01",
            SourceType = "syslog",
            Index = "main"
        };

        // 2024-01-15T12:00:00Z = 1705320000 epoch
        dto.Time.Should().BeApproximately(1705320000.0, 1.0);
    }

    [Fact]
    public void LogEventDto_PropertiesSetCorrectly()
    {
        var dto = new LogEventDto
        {
            Event = "error in app",
            Host = "app-server",
            Source = "/var/log/app.log",
            SourceType = "application",
            Index = "apps",
            LineNumber = 42
        };

        dto.Event.Should().Be("error in app");
        dto.Host.Should().Be("app-server");
        dto.Source.Should().Be("/var/log/app.log");
        dto.SourceType.Should().Be("application");
        dto.Index.Should().Be("apps");
        dto.LineNumber.Should().Be(42);
    }

    [Fact]
    public void GetStats_InitialState_AllZeros()
    {
        var config = new OutputConfiguration
        {
            ServerUrl = "http://localhost:9999",
            ApiKey = "test-key",
            BatchSize = 10,
            FlushIntervalSeconds = 60
        };

        using var httpClient = new HttpClient();
        _forwarder = new LogForwarder(config, httpClient, _loggerMock.Object);

        var stats = _forwarder.GetStats();

        stats.EventsSent.Should().Be(0);
        stats.EventsDropped.Should().Be(0);
        stats.BatchesSent.Should().Be(0);
        stats.BatchesFailed.Should().Be(0);
        stats.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_AcceptsEvents()
    {
        var config = new OutputConfiguration
        {
            ServerUrl = "http://localhost:9999",
            ApiKey = "test-key",
            BatchSize = 1000,
            FlushIntervalSeconds = 60,
            MaxBufferSize = 100
        };

        using var httpClient = new HttpClient();
        _forwarder = new LogForwarder(config, httpClient, _loggerMock.Object);

        var dto = new LogEventDto
        {
            Event = "test event",
            Host = "test-host",
            SourceType = "test"
        };

        // Should not throw
        await _forwarder.EnqueueAsync(dto);

        // Note: Background processor may drain the queue before we check,
        // so verify the event was accepted by checking that no events were dropped
        var stats = _forwarder.GetStats();
        stats.EventsDropped.Should().Be(0);
    }

    [Fact]
    public void ForwarderStats_Properties()
    {
        var stats = new ForwarderStats
        {
            EventsSent = 100,
            EventsDropped = 5,
            BatchesSent = 10,
            BatchesFailed = 2,
            QueueDepth = 15
        };

        stats.EventsSent.Should().Be(100);
        stats.EventsDropped.Should().Be(5);
        stats.BatchesSent.Should().Be(10);
        stats.BatchesFailed.Should().Be(2);
        stats.QueueDepth.Should().Be(15);
    }

    public async ValueTask DisposeAsync()
    {
        if (_forwarder != null)
        {
            await _forwarder.DisposeAsync();
        }
    }
}
