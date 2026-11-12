using Cabinet.API.Services;
using Cabinet.Core.Models;
using Cabinet.Query.Bql;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class AnomalyDetectorTests
{
    private static readonly DateTime WindowEnd = new(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Eight quiet baseline windows, then the current one.</summary>
    private static async Task<BqlQueryEngine> EngineAsync(Action<List<LogEvent>, DateTime, DateTime> current)
    {
        var events = new List<LogEvent>();
        for (var w = 1; w <= 8; w++)
        {
            var windowStart = WindowEnd - Window * (w + 1);
            for (var i = 0; i < 5; i++)
            {
                events.Add(Event($"request {w}{i} served in {i * 7}ms", "web", windowStart.AddMinutes(i), ("user", i % 2 == 0 ? "alice" : "bob")));
                events.Add(Event($"request {w}{i} served in {i * 3}ms", "db", windowStart.AddMinutes(i + 1), ("user", "alice")));
            }
        }

        current(events, WindowEnd - Window, WindowEnd);
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(events);
        return new BqlQueryEngine(storage, new BqlParser(), new BqlOptimizer(), NullLogger<BqlQueryEngine>.Instance);
    }

    private static LogEvent Event(string line, string host, DateTime at, params (string Name, string Value)[] fields)
    {
        var logEvent = new LogEvent { RawLine = line, Host = host, Timestamp = at };
        foreach (var (name, value) in fields)
        {
            logEvent.SetField(name, value);
        }

        return logEvent;
    }

    private static AnomalyRule Rule(AnomalyKind kind, string? groupBy = null, string? field = null) => new()
    {
        Name = "test", Kind = kind, GroupBy = groupBy, Field = field, WindowMinutes = 15, BaselineWindows = 8, MinCount = 10, Threshold = 3,
    };

    [Fact]
    public async Task Spike_IsFound_ForTheGroupThatJumped()
    {
        var engine = await EngineAsync((events, start, _) =>
        {
            for (var i = 0; i < 60; i++) events.Add(Event($"request x{i} served in 1ms", "web", start.AddSeconds(i * 10)));
            for (var i = 0; i < 5; i++) events.Add(Event($"request y{i} served in 1ms", "db", start.AddMinutes(i)));
        });

        var findings = await AnomalyDetector.EvaluateAsync(Rule(AnomalyKind.Spike, groupBy: "host"), WindowEnd, engine, CancellationToken.None);

        findings.Should().ContainSingle().Which.Key.Should().Be("web");
        findings[0].Message.Should().Contain("60 in the last 15 minutes");
    }

    [Fact]
    public async Task Silence_IsFound_ForTheGroupThatStopped()
    {
        var engine = await EngineAsync((events, start, _) =>
        {
            for (var i = 0; i < 5; i++) events.Add(Event($"request z{i} served in 1ms", "web", start.AddMinutes(i)));
        });

        var findings = await AnomalyDetector.EvaluateAsync(Rule(AnomalyKind.Silence, groupBy: "host"), WindowEnd, engine, CancellationToken.None);

        findings.Should().ContainSingle().Which.Key.Should().Be("db");
    }

    [Fact]
    public async Task NewValue_IsOnlyWhatTheBaselineNeverHad()
    {
        var engine = await EngineAsync((events, start, _) =>
        {
            events.Add(Event("login", "web", start.AddMinutes(1), ("user", "alice")));
            events.Add(Event("login", "web", start.AddMinutes(2), ("user", "mallory")));
        });

        var findings = await AnomalyDetector.EvaluateAsync(Rule(AnomalyKind.NewValue, field: "user"), WindowEnd, engine, CancellationToken.None);

        findings.Should().ContainSingle().Which.Key.Should().Be("mallory");
    }

    [Fact]
    public async Task NewPattern_IsALineUnlikeAnyBefore()
    {
        var engine = await EngineAsync((events, start, _) =>
        {
            events.Add(Event("request q1 served in 4ms", "web", start.AddMinutes(1)));
            events.Add(Event("segfault at 0x0000 in worker 7", "web", start.AddMinutes(2)));
        });

        var findings = await AnomalyDetector.EvaluateAsync(Rule(AnomalyKind.NewPattern), WindowEnd, engine, CancellationToken.None);

        findings.Should().ContainSingle().Which.Key.Should().StartWith("segfault");
    }

    [Fact]
    public async Task QuietWindow_FindsNothing()
    {
        var engine = await EngineAsync((events, start, _) =>
        {
            for (var i = 0; i < 5; i++)
            {
                events.Add(Event($"request a{i} served in 1ms", "web", start.AddMinutes(i)));
                events.Add(Event($"request b{i} served in 1ms", "db", start.AddMinutes(i)));
            }
        });

        foreach (var kind in new[] { AnomalyKind.Spike, AnomalyKind.Silence, AnomalyKind.NewPattern })
        {
            (await AnomalyDetector.EvaluateAsync(Rule(kind, groupBy: kind == AnomalyKind.NewPattern ? null : "host"), WindowEnd, engine, CancellationToken.None))
                .Should().BeEmpty(kind.ToString());
        }
    }

    [Fact]
    public void Windows_EndOnTheirBoundary()
    {
        AnomalyDetector.WindowEnd(new DateTime(2026, 3, 4, 12, 7, 30, DateTimeKind.Utc), 15).Should().Be(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc));
    }
}
