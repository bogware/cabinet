using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class InMemoryStorageTests
{
    private readonly InMemoryStorage _storage = new();

    [Fact]
    public async Task StoreAsync_StoresEvent_ReturnsTrue()
    {
        var evt = CreateEvent("Test message");

        var result = await _storage.StoreAsync(evt);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task GetByIdAsync_ExistingEvent_ReturnsEvent()
    {
        var evt = CreateEvent("Test message");
        await _storage.StoreAsync(evt);

        var result = await _storage.GetByIdAsync(evt.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(evt.Id);
        result.RawLine.Should().Be("Test message");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistent_ReturnsNull()
    {
        var result = await _storage.GetByIdAsync("nonexistent-id");

        result.Should().BeNull();
    }

    [Fact]
    public async Task StoreBatchAsync_StoresMultipleEvents()
    {
        var events = Enumerable.Range(1, 50).Select(i => CreateEvent($"Message {i}")).ToList();

        var stored = await _storage.StoreBatchAsync(events);

        stored.Should().Be(50);
        var count = await _storage.GetCountAsync();
        count.Should().Be(50);
    }

    [Fact]
    public async Task GetCountAsync_EmptyStorage_ReturnsZero()
    {
        var count = await _storage.GetCountAsync();

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetCountAsync_WithContext_FiltersCorrectly()
    {
        await _storage.StoreAsync(CreateEvent("Error message", severity: LogSeverity.Error));
        await _storage.StoreAsync(CreateEvent("Info message", severity: LogSeverity.Info));
        await _storage.StoreAsync(CreateEvent("Another error", severity: LogSeverity.Error));

        var count = await _storage.GetCountAsync(new QueryContext { MinSeverity = LogSeverity.Error });

        count.Should().Be(2);
    }

    [Fact]
    public async Task QueryAsync_SearchText_FiltersResults()
    {
        await _storage.StoreAsync(CreateEvent("Login successful"));
        await _storage.StoreAsync(CreateEvent("Login failed"));
        await _storage.StoreAsync(CreateEvent("User registered"));

        var context = new QueryContext { SearchText = "Login" };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(2);
        results.All(e => e.RawLine.Contains("Login")).Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_TimeRange_FiltersResults()
    {
        var now = DateTime.UtcNow;
        await _storage.StoreAsync(CreateEvent("Old event", timestamp: now.AddHours(-5)));
        await _storage.StoreAsync(CreateEvent("Recent event", timestamp: now.AddMinutes(-10)));
        await _storage.StoreAsync(CreateEvent("Very recent event", timestamp: now));

        var context = new QueryContext
        {
            StartTime = now.AddHours(-1),
            EndTime = now.AddMinutes(1)
        };

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task QueryAsync_SourceTypeFilter_FiltersResults()
    {
        await _storage.StoreAsync(CreateEvent("L4D2 event", sourceType: SourceTypes.L4D2));
        await _storage.StoreAsync(CreateEvent("Generic event", sourceType: SourceTypes.Generic));

        var context = new QueryContext { SourceType = SourceTypes.L4D2 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(1);
        results[0].SourceType.Should().Be(SourceTypes.L4D2);
    }

    [Fact]
    public async Task QueryAsync_Pagination_Works()
    {
        for (var i = 0; i < 20; i++)
        {
            await _storage.StoreAsync(CreateEvent($"Event {i}"));
        }

        var context = new QueryContext { Skip = 5, Limit = 5 };
        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(context))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task PurgeOldEventsAsync_RemovesOldEvents()
    {
        var now = DateTime.UtcNow;
        await _storage.StoreAsync(CreateEvent("Old event", timestamp: now.AddDays(-10)));
        await _storage.StoreAsync(CreateEvent("Recent event", timestamp: now));

        var purged = await _storage.PurgeOldEventsAsync(now.AddDays(-5));

        purged.Should().Be(1);
        var remaining = await _storage.GetCountAsync();
        remaining.Should().Be(1);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectStats()
    {
        await _storage.StoreAsync(CreateEvent("Error", severity: LogSeverity.Error, sourceType: SourceTypes.L4D2));
        await _storage.StoreAsync(CreateEvent("Info", severity: LogSeverity.Info, sourceType: SourceTypes.Generic));
        await _storage.StoreAsync(CreateEvent("Warning", severity: LogSeverity.Warning, sourceType: SourceTypes.L4D2));

        var stats = await _storage.GetStatsAsync();

        stats.TotalEvents.Should().Be(3);
        stats.OldestEvent.Should().NotBeNull();
        stats.NewestEvent.Should().NotBeNull();
        stats.EventsBySourceType.Should().ContainKey(SourceTypes.L4D2);
        stats.EventsBySourceType[SourceTypes.L4D2].Should().Be(2);
        stats.EventsBySeverity.Should().ContainKey(LogSeverity.Error);
    }

    [Fact]
    public async Task ConcurrentStoreAndQuery_DoesNotThrow()
    {
        // Seed some initial data
        for (var i = 0; i < 100; i++)
        {
            await _storage.StoreAsync(CreateEvent($"Event {i}"));
        }

        // Run concurrent writes and reads to verify thread safety
        var tasks = new List<Task>();

        for (var i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < 50; j++)
                {
                    await _storage.StoreAsync(CreateEvent($"Concurrent {idx}-{j}"));
                }
            }));

            tasks.Add(Task.Run(async () =>
            {
                var context = new QueryContext { Limit = 10 };
                var results = new List<LogEvent>();
                await foreach (var evt in _storage.QueryAsync(context))
                {
                    results.Add(evt);
                }
            }));
        }

        // Should not throw any exceptions (InvalidOperationException, etc.)
        await Task.WhenAll(tasks);

        var count = await _storage.GetCountAsync();
        count.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task ConcurrentStatsAndStore_DoesNotThrow()
    {
        for (var i = 0; i < 50; i++)
        {
            await _storage.StoreAsync(CreateEvent($"Event {i}"));
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < 20; j++)
                    await _storage.StoreAsync(CreateEvent($"Concurrent {j}"));
            }));
            tasks.Add(Task.Run(async () =>
            {
                for (var j = 0; j < 10; j++)
                {
                    var stats = await _storage.GetStatsAsync();
                    stats.TotalEvents.Should().BeGreaterThan(0);
                }
            }));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Eviction_RespectsMaxEvents()
    {
        var smallStorage = new InMemoryStorage(maxEvents: 10);

        for (var i = 0; i < 20; i++)
        {
            await smallStorage.StoreAsync(CreateEvent($"Event {i}", timestamp: DateTime.UtcNow.AddMinutes(i)));
        }

        var count = await smallStorage.GetCountAsync();
        count.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task QueryAsync_CancellationToken_StopsIteration()
    {
        for (var i = 0; i < 100; i++)
        {
            await _storage.StoreAsync(CreateEvent($"Event {i}"));
        }

        using var cts = new CancellationTokenSource();
        var results = new List<LogEvent>();

        await foreach (var evt in _storage.QueryAsync(new QueryContext { Limit = 1000 }, cts.Token))
        {
            results.Add(evt);
            if (results.Count >= 5)
                cts.Cancel();
        }

        results.Count.Should().BeLessThanOrEqualTo(6); // May get one more after cancel
    }

    [Fact]
    public async Task QueryAsync_IndexFilter_ReturnsOnlyMatchingIndex()
    {
        await _storage.StoreAsync(new LogEvent { RawLine = "main event", Index = "main" });
        await _storage.StoreAsync(new LogEvent { RawLine = "test event", Index = "test" });
        await _storage.StoreAsync(new LogEvent { RawLine = "main event 2", Index = "main" });

        var results = new List<LogEvent>();
        await foreach (var evt in _storage.QueryAsync(new QueryContext { Index = "test", Limit = 100 }))
        {
            results.Add(evt);
        }

        results.Should().HaveCount(1);
        results[0].Index.Should().Be("test");
    }

    [Fact]
    public async Task QueryAsync_FieldFilters_FiltersCorrectly()
    {
        var evt1 = CreateEvent("host a event");
        evt1.Fields["environment"] = "production";
        var evt2 = CreateEvent("host b event");
        evt2.Fields["environment"] = "staging";
        var evt3 = CreateEvent("host c event");
        evt3.Fields["environment"] = "production";

        await _storage.StoreBatchAsync(new[] { evt1, evt2, evt3 });

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

        results.Should().HaveCount(2);
        results.All(e => e.Fields["environment"]?.ToString() == "production").Should().BeTrue();
    }

    [Fact]
    public async Task QueryAsync_TagFilter_FiltersCorrectly()
    {
        var evt1 = CreateEvent("tagged event");
        evt1.Tags.Add("important");
        evt1.Tags.Add("security");
        var evt2 = CreateEvent("untagged event");
        var evt3 = CreateEvent("partially tagged");
        evt3.Tags.Add("important");

        await _storage.StoreBatchAsync(new[] { evt1, evt2, evt3 });

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

        results.Should().HaveCount(1);
        results[0].RawLine.Should().Be("tagged event");
    }

    [Fact]
    public async Task GetCountAsync_WithTimeRange_FiltersCorrectly()
    {
        var now = DateTime.UtcNow;
        await _storage.StoreAsync(CreateEvent("Old", timestamp: now.AddHours(-5)));
        await _storage.StoreAsync(CreateEvent("Recent", timestamp: now.AddMinutes(-10)));
        await _storage.StoreAsync(CreateEvent("Now", timestamp: now));

        var count = await _storage.GetCountAsync(new QueryContext
        {
            StartTime = now.AddHours(-1),
            EndTime = now.AddMinutes(1)
        });

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetCountAsync_WithFieldFilters_FiltersCorrectly()
    {
        var evt1 = CreateEvent("Event 1");
        evt1.Fields["region"] = "us-east";
        var evt2 = CreateEvent("Event 2");
        evt2.Fields["region"] = "eu-west";
        var evt3 = CreateEvent("Event 3");
        evt3.Fields["region"] = "us-east";

        await _storage.StoreBatchAsync(new[] { evt1, evt2, evt3 });

        var count = await _storage.GetCountAsync(new QueryContext
        {
            FieldFilters = new Dictionary<string, object?> { ["region"] = "us-east" }
        });

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetCountAsync_WithHost_FiltersCorrectly()
    {
        await _storage.StoreAsync(new LogEvent { RawLine = "a", Host = "web-01" });
        await _storage.StoreAsync(new LogEvent { RawLine = "b", Host = "web-02" });
        await _storage.StoreAsync(new LogEvent { RawLine = "c", Host = "web-01" });

        var count = await _storage.GetCountAsync(new QueryContext { Host = "web-01" });

        count.Should().Be(2);
    }

    private static LogEvent CreateEvent(
        string rawLine,
        LogSeverity severity = LogSeverity.Info,
        string? sourceType = null,
        DateTime? timestamp = null)
    {
        return new LogEvent
        {
            RawLine = rawLine,
            Severity = severity,
            SourceType = sourceType ?? SourceTypes.Generic,
            Timestamp = timestamp ?? DateTime.UtcNow
        };
    }
}
