using Cabinet.API.Services;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class LiveEventTailTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryStorage _storage = new();

    private static LogEvent Event(string line, DateTime timestamp) => new() { RawLine = line, Index = "main", Timestamp = timestamp };

    [Fact]
    public async Task EventsWithTheSameTimestamp_ArrivingInDifferentPolls_AreAllSent()
    {
        var tail = new LiveEventTail(_storage, "main", null, Start);
        var time = Start.AddSeconds(1);

        await _storage.StoreAsync(Event("first", time));
        var first = await tail.PollAsync(time);

        await _storage.StoreAsync(Event("second", time));
        var second = await tail.PollAsync(time.AddSeconds(1));

        first.Select(e => e.RawLine).Should().Equal("first");
        second.Select(e => e.RawLine).Should().Equal("second");
    }

    [Fact]
    public async Task EventArrivingLate_WithAnOlderTimestamp_IsStillSent()
    {
        var tail = new LiveEventTail(_storage, "main", null, Start);

        await _storage.StoreAsync(Event("newer", Start.AddSeconds(10)));
        await tail.PollAsync(Start.AddSeconds(10));

        // An agent's batch: read a few seconds ago, stored just now
        await _storage.StoreAsync(Event("older", Start.AddSeconds(5)));
        var polled = await tail.PollAsync(Start.AddSeconds(11));

        polled.Select(e => e.RawLine).Should().Equal("older");
    }

    [Fact]
    public async Task EventsFromBeforeTheStreamStarted_AreNotSent()
    {
        await _storage.StoreAsync(Event("history", Start.AddSeconds(-1)));
        var tail = new LiveEventTail(_storage, "main", null, Start);

        (await tail.PollAsync(Start.AddSeconds(1))).Should().BeEmpty();
    }

    [Fact]
    public async Task NoEventIsSentTwice_AndTheyArriveOldestFirst()
    {
        var tail = new LiveEventTail(_storage, "main", null, Start);
        await _storage.StoreAsync(Event("b", Start.AddSeconds(2)));
        await _storage.StoreAsync(Event("a", Start.AddSeconds(1)));

        var first = await tail.PollAsync(Start.AddSeconds(3));
        var second = await tail.PollAsync(Start.AddSeconds(4));

        first.Select(e => e.RawLine).Should().Equal("a", "b");
        second.Should().BeEmpty();
    }

    [Fact]
    public async Task Filter_IsApplied()
    {
        var tail = new LiveEventTail(_storage, "main", "error", Start);
        await _storage.StoreAsync(Event("an error here", Start.AddSeconds(1)));
        await _storage.StoreAsync(Event("all good", Start.AddSeconds(1)));

        var polled = await tail.PollAsync(Start.AddSeconds(2));

        polled.Select(e => e.RawLine).Should().Equal("an error here");
    }
}
