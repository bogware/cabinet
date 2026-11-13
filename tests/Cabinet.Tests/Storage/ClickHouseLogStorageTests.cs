using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Database.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Storage;

/// <summary>ClickHouse behaviour the cross-backend suites don't reach. Needs CABINET_TEST_CLICKHOUSE_URL.</summary>
[Trait("Category", "Integration")]
public sealed class ClickHouseLogStorageTests : IAsyncLifetime
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CABINET_TEST_CLICKHOUSE_URL");
    private static readonly DateTime Start = new(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc);
    private ClickHouseLogStorage _storage = null!;

    public Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(Url))
        {
            _storage = new ClickHouseLogStorage(
                Url,
                NullLogger<ClickHouseLogStorage>.Instance,
                database: "cabinet_tests",
                table: $"unit_{Guid.NewGuid():N}",
                username: Environment.GetEnvironmentVariable("CABINET_TEST_CLICKHOUSE_USER"),
                password: Environment.GetEnvironmentVariable("CABINET_TEST_CLICKHOUSE_PASS"));
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_storage != null)
        {
            await _storage.DropAsync();
            _storage.Dispose();
        }
    }

    private static LogEvent Event(int i, string index = "main", int minutes = 0) => new()
    {
        Id = $"ch-{i:D6}",
        RawLine = $"line {i} {(i % 2 == 0 ? "even" : "odd")}",
        Host = i % 3 == 0 ? null : $"host-{i % 3}",
        Index = index,
        Severity = (LogSeverity)(i % 5),
        Timestamp = Start.AddSeconds(i).AddMinutes(minutes),
        Tags = { "t" + (i % 2) },
        Fields = { ["n"] = (long)i, ["ratio"] = i / 4.0, ["flag"] = i % 2 == 0, ["name"] = $"N{i}" },
    };

    [SkippableFact]
    public async Task ResentIds_AreStoredOnce_EvenWithANewTime()
    {
        Skip.If(string.IsNullOrEmpty(Url), "CABINET_TEST_CLICKHOUSE_URL is not set");

        (await _storage.StoreBatchAsync(Enumerable.Range(0, 100).Select(i => Event(i)))).Should().Be(100);
        (await _storage.StoreBatchAsync(Enumerable.Range(50, 100).Select(i => Event(i, minutes: 30)))).Should().Be(50);
        (await _storage.StoreBatchAsync([Event(1), Event(1)])).Should().Be(0);
        (await _storage.GetCountAsync()).Should().Be(150);
    }

    [SkippableFact]
    public async Task Events_ComeBackAsStored()
    {
        Skip.If(string.IsNullOrEmpty(Url), "CABINET_TEST_CLICKHOUSE_URL is not set");
        var original = Event(4);
        original.Timestamp = Start.AddTicks(1234567);
        await _storage.StoreBatchAsync([original]);

        var read = (await _storage.GetByIdAsync("ch-000004"))!;

        read.Timestamp.Should().Be(original.Timestamp, "times keep every tick");
        read.Host.Should().Be("host-1");
        read.Tags.Should().BeEquivalentTo(["t0"]);
        read.GetField<long>("n").Should().Be(4, "whole numbers come back whole");
        read.Fields["ratio"].Should().Be(1L, "JSON can't tell 1.0 from 1, on any backend that stores it");
        read.Fields["flag"].Should().Be(true);
        (await _storage.GetByIdAsync("missing")).Should().BeNull();
    }

    [SkippableFact]
    public async Task Purges_AndDeletingAnIndex_RemoveExactlyWhatTheySay()
    {
        Skip.If(string.IsNullOrEmpty(Url), "CABINET_TEST_CLICKHOUSE_URL is not set");
        await _storage.StoreBatchAsync(Enumerable.Range(0, 100).Select(i => Event(i)));
        await _storage.StoreBatchAsync(Enumerable.Range(100, 20).Select(i => Event(i, "other")));

        (await _storage.PurgeIndexEventsAsync("main", Start.AddSeconds(40))).Should().Be(40);
        (await _storage.GetCountAsync(new QueryContext { Index = "main" })).Should().Be(60);
        (await _storage.DeleteByIndexAsync("other")).Should().Be(20);
        (await _storage.PurgeOldEventsAsync(Start.AddSeconds(50))).Should().Be(10);

        var stats = await _storage.GetStatsAsync();
        stats.TotalEvents.Should().Be(50);
        stats.EventsByIndex.Should().ContainSingle().Which.Key.Should().Be("main");
        stats.OldestEvent.Should().Be(Start.AddSeconds(50));
    }

    [SkippableFact]
    public async Task FieldFilters_Tags_AndIndexPatterns_Filter()
    {
        Skip.If(string.IsNullOrEmpty(Url), "CABINET_TEST_CLICKHOUSE_URL is not set");
        await _storage.StoreBatchAsync(Enumerable.Range(0, 20).Select(i => Event(i, i < 10 ? "web_prod" : "db")));

        (await _storage.GetCountAsync(new QueryContext { FieldFilters = new() { ["name"] = "n3" } })).Should().Be(1, "text compares ignoring case");
        (await _storage.GetCountAsync(new QueryContext { FieldFilters = new() { ["n"] = "7" } })).Should().Be(1);
        (await _storage.GetCountAsync(new QueryContext { FieldFilters = new() { ["flag"] = true } })).Should().Be(10);
        (await _storage.GetCountAsync(new QueryContext { FieldFilters = new() { ["n"] = new object?[] { 1L, 2L, 3L } } })).Should().Be(3);
        (await _storage.GetCountAsync(new QueryContext { Tags = ["t1"] })).Should().Be(10);
        (await _storage.GetCountAsync(new QueryContext { AllowedIndexes = ["web*"] })).Should().Be(10);
        (await _storage.GetCountAsync(new QueryContext { AllowedIndexes = ["web_prod", "db"] })).Should().Be(20);
        (await _storage.GetCountAsync(new QueryContext { AllowedIndexes = ["webXprod"] })).Should().Be(0, "an underscore is a literal");
        (await _storage.GetCountAsync(new QueryContext { Host = "HOST-1" })).Should().Be(7);
    }
}
