using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Storage;

/// <summary>
/// Integration tests for OpenSearchLogStorage.
/// Requires a running OpenSearch or Elasticsearch instance.
/// Set CABINET_TEST_OPENSEARCH_URL environment variable to enable.
/// Example: https://localhost:9200
/// Optionally set CABINET_TEST_OPENSEARCH_USER and CABINET_TEST_OPENSEARCH_PASS.
/// </summary>
[Trait("Category", "Integration")]
public class OpenSearchLogStorageIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly OpenSearchLogStorage? _storage;
    private readonly bool _isAvailable;
    private const string TestIndexPrefix = "cabinet-test";

    public OpenSearchLogStorageIntegrationTests()
    {
        var url = Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_URL");
        _isAvailable = !string.IsNullOrEmpty(url);

        if (_isAvailable)
        {
            var username = Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_USER");
            var password = Environment.GetEnvironmentVariable("CABINET_TEST_OPENSEARCH_PASS");

            _storage = new OpenSearchLogStorage(
                url!,
                NullLogger<OpenSearchLogStorage>.Instance,
                indexPrefix: TestIndexPrefix,
                username: username,
                password: password,
                allowInsecureSsl: true);
        }
    }

    public async Task InitializeAsync()
    {
        if (_isAvailable && _storage != null)
        {
            await _storage.InitializeAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _storage?.Dispose();
    }

    private void SkipIfNotAvailable()
    {
        Skip.If(!_isAvailable, "OpenSearch not available. Set CABINET_TEST_OPENSEARCH_URL to enable.");
    }

    [SkippableFact]
    public async Task StoreAndRetrieve_RoundTrip()
    {
        SkipIfNotAvailable();

        var evt = CreateEvent("OpenSearch roundtrip test");
        var stored = await _storage!.StoreAsync(evt);
        stored.Should().BeTrue();

        // Wait for refresh
        await Task.Delay(1500);

        var retrieved = await _storage.GetByIdAsync(evt.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(evt.Id);
        retrieved.RawLine.Should().Be("OpenSearch roundtrip test");
    }

    [SkippableFact]
    public async Task StoreBatch_InsertsMultipleEvents()
    {
        SkipIfNotAvailable();

        var events = Enumerable.Range(1, 20)
            .Select(i => CreateEvent($"OS Batch event {i}"))
            .ToList();

        var count = await _storage!.StoreBatchAsync(events);
        count.Should().Be(20);
    }

    [SkippableFact]
    public async Task QueryAsync_SearchText_FiltersResults()
    {
        SkipIfNotAvailable();

        var evt1 = CreateEvent("Login successful for admin");
        var evt2 = CreateEvent("System shutdown initiated");
        await _storage!.StoreBatchAsync(new[] { evt1, evt2 });
        await Task.Delay(1500);

        var context = new QueryContext { SearchText = "Login", Limit = 100 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.RawLine.Contains("Login"));
    }

    [SkippableFact]
    public async Task QueryAsync_TimeRange_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var now = DateTime.UtcNow;
        var old = CreateEvent("Old OS event", timestamp: now.AddHours(-48));
        var recent = CreateEvent("Recent OS event", timestamp: now.AddMinutes(-1));
        await _storage!.StoreBatchAsync(new[] { old, recent });
        await Task.Delay(1500);

        var context = new QueryContext
        {
            StartTime = now.AddHours(-1),
            EndTime = now.AddMinutes(5),
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.Id == recent.Id);
    }

    [SkippableFact]
    public async Task QueryAsync_SeverityFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var info = CreateEvent("OS Info", severity: LogSeverity.Info);
        var error = CreateEvent("OS Error", severity: LogSeverity.Error);
        await _storage!.StoreBatchAsync(new[] { info, error });
        await Task.Delay(1500);

        var context = new QueryContext
        {
            MinSeverity = LogSeverity.Error,
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().OnlyContain(e => e.Severity >= LogSeverity.Error);
    }

    [SkippableFact]
    public async Task QueryAsync_TagFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var tagged = CreateEvent("OS tagged event");
        tagged.Tags.Add("urgent");
        tagged.Tags.Add("security");
        var untagged = CreateEvent("OS untagged event");
        await _storage!.StoreBatchAsync(new[] { tagged, untagged });
        await Task.Delay(1500);

        var context = new QueryContext
        {
            Tags = new HashSet<string> { "urgent" },
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.Id == tagged.Id);
    }

    [SkippableFact]
    public async Task GetCountAsync_ReturnsCorrectCount()
    {
        SkipIfNotAvailable();

        var events = Enumerable.Range(1, 5)
            .Select(i => CreateEvent($"OS count test {i}"))
            .ToList();
        await _storage!.StoreBatchAsync(events);
        await Task.Delay(1500);

        var count = await _storage.GetCountAsync();
        count.Should().BeGreaterOrEqualTo(5);
    }

    [SkippableFact]
    public async Task GetStatsAsync_ReturnsStats()
    {
        SkipIfNotAvailable();

        await _storage!.StoreAsync(CreateEvent("OS stats test"));
        await Task.Delay(1500);

        var stats = await _storage.GetStatsAsync();
        stats.TotalEvents.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    public async Task QueryAsync_IndexFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var mainEvt = CreateEvent("OS main event");
        mainEvt.Index = "main";
        var testEvt = CreateEvent("OS test event");
        testEvt.Index = "test";
        await _storage!.StoreBatchAsync(new[] { mainEvt, testEvt });
        await Task.Delay(1500);

        var context = new QueryContext { Index = "test", Limit = 100 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().OnlyContain(e => e.Index == "test");
    }

    private static LogEvent CreateEvent(
        string rawLine,
        LogSeverity severity = LogSeverity.Info,
        DateTime? timestamp = null)
    {
        return new LogEvent
        {
            RawLine = rawLine,
            Severity = severity,
            SourceType = SourceTypes.Generic,
            Timestamp = timestamp ?? DateTime.UtcNow,
            Host = "test-host"
        };
    }
}
