using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Storage;

/// <summary>
/// Integration tests for PostgresLogStorage.
/// Requires a running PostgreSQL instance.
/// Set CABINET_TEST_POSTGRES_CONNSTR environment variable to enable.
/// Example: Host=localhost;Port=5432;Database=cabinet_test;Username=cabinet;Password=test
/// </summary>
[Trait("Category", "Integration")]
public class PostgresLogStorageIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly PostgresLogStorage? _storage;
    private readonly bool _isAvailable;
    private readonly string? _connectionString;

    public PostgresLogStorageIntegrationTests()
    {
        _connectionString = Environment.GetEnvironmentVariable("CABINET_TEST_POSTGRES_CONNSTR");
        _isAvailable = !string.IsNullOrEmpty(_connectionString);

        if (_isAvailable)
        {
            _storage = new PostgresLogStorage(
                _connectionString!,
                NullLogger<PostgresLogStorage>.Instance);
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
        Skip.If(!_isAvailable, "PostgreSQL not available. Set CABINET_TEST_POSTGRES_CONNSTR to enable.");
    }

    [SkippableFact]
    public async Task StoreAndRetrieve_RoundTrip()
    {
        SkipIfNotAvailable();

        var evt = CreateEvent("PostgreSQL roundtrip test");
        var stored = await _storage!.StoreAsync(evt);
        stored.Should().BeTrue();

        var retrieved = await _storage.GetByIdAsync(evt.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Id.Should().Be(evt.Id);
        retrieved.RawLine.Should().Be("PostgreSQL roundtrip test");
        retrieved.SourceType.Should().Be(evt.SourceType);
        retrieved.Severity.Should().Be(evt.Severity);
    }

    [SkippableFact]
    public async Task StoreBatch_InsertsMultipleEvents()
    {
        SkipIfNotAvailable();

        var events = Enumerable.Range(1, 25)
            .Select(i => CreateEvent($"Batch event {i}"))
            .ToList();

        var count = await _storage!.StoreBatchAsync(events);
        count.Should().Be(25);
    }

    [SkippableFact]
    public async Task QueryAsync_SearchText_FiltersResults()
    {
        SkipIfNotAvailable();

        var evt1 = CreateEvent("Login successful for admin");
        var evt2 = CreateEvent("Login failed for user");
        var evt3 = CreateEvent("User registered successfully");
        await _storage!.StoreBatchAsync(new[] { evt1, evt2, evt3 });

        var context = new QueryContext { SearchText = "Login", Limit = 100 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCountGreaterOrEqualTo(2);
    }

    [SkippableFact]
    public async Task QueryAsync_TimeRange_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        // The database is shared between tests, so use a window no other test writes to.
        var anchor = DateTime.UtcNow.AddDays(-Random.Shared.Next(1_000, 100_000));
        var oldEvt = CreateEvent("Old event", timestamp: anchor.AddHours(-48));
        var recentEvt = CreateEvent("Recent event", timestamp: anchor);
        await _storage!.StoreBatchAsync(new[] { oldEvt, recentEvt });

        var context = new QueryContext
        {
            StartTime = anchor.AddMinutes(-1),
            EndTime = anchor.AddMinutes(1),
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.Id == recentEvt.Id);
        results.Should().NotContain(e => e.Id == oldEvt.Id);
    }

    [SkippableFact]
    public async Task QueryAsync_SeverityFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var info = CreateEvent("Info message", severity: LogSeverity.Info);
        var error = CreateEvent("Error message", severity: LogSeverity.Error);
        await _storage!.StoreBatchAsync(new[] { info, error });

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

        var tagged = CreateEvent("Tagged event");
        tagged.Tags.Add("important");
        tagged.Tags.Add("security");
        var untagged = CreateEvent("Untagged event");
        await _storage!.StoreBatchAsync(new[] { tagged, untagged });

        var context = new QueryContext
        {
            Tags = new HashSet<string> { "important", "security" },
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.Id == tagged.Id);
        results.Should().NotContain(e => e.Id == untagged.Id);
    }

    [SkippableFact]
    public async Task QueryAsync_IndexFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var mainEvt = CreateEvent("Main index event");
        mainEvt.Index = "main";
        var testEvt = CreateEvent("Test index event");
        testEvt.Index = "test";
        await _storage!.StoreBatchAsync(new[] { mainEvt, testEvt });

        var context = new QueryContext { Index = "test", Limit = 100 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().OnlyContain(e => e.Index == "test");
    }

    [SkippableFact]
    public async Task QueryAsync_FieldFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        var evt1 = CreateEvent("Prod event");
        evt1.Fields["environment"] = "production";
        var evt2 = CreateEvent("Dev event");
        evt2.Fields["environment"] = "development";
        await _storage!.StoreBatchAsync(new[] { evt1, evt2 });

        var context = new QueryContext
        {
            FieldFilters = new Dictionary<string, object?> { ["environment"] = "production" },
            Limit = 100
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().Contain(e => e.Id == evt1.Id);
    }

    [SkippableFact]
    public async Task GetCountAsync_ReturnsCorrectCount()
    {
        SkipIfNotAvailable();

        var events = Enumerable.Range(1, 10)
            .Select(i => CreateEvent($"Count test {i}"))
            .ToList();
        await _storage!.StoreBatchAsync(events);

        var count = await _storage.GetCountAsync();
        count.Should().BeGreaterOrEqualTo(10);
    }

    [SkippableFact]
    public async Task GetCountAsync_WithFilter_FiltersCorrectly()
    {
        SkipIfNotAvailable();

        await _storage!.StoreAsync(CreateEvent("Error count", severity: LogSeverity.Error));
        await _storage.StoreAsync(CreateEvent("Info count", severity: LogSeverity.Info));

        var count = await _storage.GetCountAsync(new QueryContext { MinSeverity = LogSeverity.Error });
        count.Should().BeGreaterOrEqualTo(1);
    }

    [SkippableFact]
    public async Task GetStatsAsync_ReturnsStats()
    {
        SkipIfNotAvailable();

        await _storage!.StoreAsync(CreateEvent("Stats test", severity: LogSeverity.Warning));

        var stats = await _storage.GetStatsAsync();
        stats.TotalEvents.Should().BeGreaterThan(0);
        stats.StorageSizeBytes.Should().BeGreaterOrEqualTo(0);
    }

    [SkippableFact]
    public async Task PurgeOldEventsAsync_RemovesOldEvents()
    {
        SkipIfNotAvailable();

        var old = CreateEvent("Old purge test", timestamp: DateTime.UtcNow.AddDays(-30));
        await _storage!.StoreAsync(old);

        var purged = await _storage.PurgeOldEventsAsync(DateTime.UtcNow.AddDays(-20));
        purged.Should().BeGreaterOrEqualTo(1);
    }

    [SkippableFact]
    public async Task PurgeIndexEvents_DeletesMoreThanOneBatch()
    {
        SkipIfNotAvailable();

        var index = $"purge-{Guid.NewGuid():N}";
        var count = PostgresLogStorage.DeleteBatchSize * 2 + 7;
        var events = Enumerable.Range(0, count).Select(i =>
        {
            var evt = CreateEvent($"old {i}", timestamp: DateTime.UtcNow.AddDays(-30));
            evt.Index = index;
            return evt;
        }).ToList();
        await _storage!.StoreBatchAsync(events);

        var purged = await _storage.PurgeIndexEventsAsync(index, DateTime.UtcNow.AddDays(-20));

        purged.Should().Be(count);
        (await _storage.GetCountAsync(new QueryContext { Index = index })).Should().Be(0);
    }

    [SkippableFact]
    public async Task StoreBatch_DuplicateIds_HandlesGracefully()
    {
        SkipIfNotAvailable();

        var evt = CreateEvent("Duplicate test");
        await _storage!.StoreAsync(evt);

        // Store same event again — should handle via ON CONFLICT DO NOTHING
        var result = await _storage.StoreAsync(evt);
        result.Should().BeFalse(); // Already exists
    }

    [SkippableFact]
    public async Task QueryAsync_Pagination_Works()
    {
        SkipIfNotAvailable();

        var events = Enumerable.Range(1, 20)
            .Select(i => CreateEvent($"Page test {i}"))
            .ToList();
        await _storage!.StoreBatchAsync(events);

        var context = new QueryContext { Skip = 5, Limit = 5 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(5);
    }

    [SkippableFact]
    public async Task Fields_RoundTrip_PreservesTypes()
    {
        SkipIfNotAvailable();

        var evt = CreateEvent("Fields test");
        evt.Fields["string_val"] = "hello";
        evt.Fields["int_val"] = 42L;
        evt.Fields["bool_val"] = true;
        await _storage!.StoreAsync(evt);

        var retrieved = await _storage.GetByIdAsync(evt.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Fields.Should().ContainKey("string_val");
        retrieved.Fields["string_val"]?.ToString().Should().Be("hello");
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
