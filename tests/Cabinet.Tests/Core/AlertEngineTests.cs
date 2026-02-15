using Cabinet.Core.Alerts;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Core;

public class AlertEngineTests
{
    private readonly AlertEngine _engine;

    public AlertEngineTests()
    {
        _engine = new AlertEngine(NullLogger<AlertEngine>.Instance);
    }

    [Fact]
    public async Task CreateRuleAsync_ValidRule_AddsSuccessfully()
    {
        var rule = CreateRule("test-rule", "Error Alert");

        await _engine.CreateRuleAsync(rule);

        var rules = await _engine.GetRulesAsync();
        rules.Should().ContainSingle(r => r.Id == "test-rule");
    }

    [Fact]
    public async Task DeleteRuleAsync_ExistingRule_RemovesSuccessfully()
    {
        var rule = CreateRule("test-rule", "Error Alert");
        await _engine.CreateRuleAsync(rule);

        var deleted = await _engine.DeleteRuleAsync("test-rule");

        deleted.Should().BeTrue();
        var rules = await _engine.GetRulesAsync();
        rules.Should().NotContain(r => r.Id == "test-rule");
    }

    [Fact]
    public async Task DeleteRuleAsync_NonExistent_ReturnsFalse()
    {
        var deleted = await _engine.DeleteRuleAsync("nonexistent");

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessEventAsync_MatchingCondition_TriggersAlert()
    {
        var rule = CreateRule("error-rule", "Error Alert");
        rule.Conditions.Add(new AlertCondition
        {
            Field = "Severity",
            Operator = ConditionOperator.Equals,
            Value = LogSeverity.Error.ToString()
        });
        await _engine.CreateRuleAsync(rule);

        var evt = new LogEvent
        {
            RawLine = "Error occurred",
            Severity = LogSeverity.Error,
            Timestamp = DateTime.UtcNow
        };

        await _engine.ProcessEventAsync(evt);

        var stats = await _engine.GetStatsAsync();
        stats.TotalAlertsTriggered.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProcessEventAsync_NonMatchingCondition_DoesNotTrigger()
    {
        var rule = CreateRule("error-rule", "Error Alert");
        rule.Conditions.Add(new AlertCondition
        {
            Field = "Severity",
            Operator = ConditionOperator.Equals,
            Value = LogSeverity.Error.ToString()
        });
        await _engine.CreateRuleAsync(rule);

        var evt = new LogEvent
        {
            RawLine = "Info message",
            Severity = LogSeverity.Info,
            Timestamp = DateTime.UtcNow
        };

        await _engine.ProcessEventAsync(evt);

        var alerts = await _engine.GetActiveAlertsAsync();
        alerts.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessEventAsync_ContainsCondition_MatchesSubstring()
    {
        var rule = CreateRule("keyword-rule", "Keyword Alert");
        rule.Conditions.Add(new AlertCondition
        {
            Field = "RawLine",
            Operator = ConditionOperator.Contains,
            Value = "critical failure"
        });
        await _engine.CreateRuleAsync(rule);

        var evt = new LogEvent
        {
            RawLine = "System experienced a critical failure in module X",
            Severity = LogSeverity.Critical,
            Timestamp = DateTime.UtcNow
        };

        await _engine.ProcessEventAsync(evt);

        var stats = await _engine.GetStatsAsync();
        stats.TotalAlertsTriggered.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStatsAsync_TracksProcessedEvents()
    {
        var rule = CreateRule("stat-rule", "Stats Rule");
        await _engine.CreateRuleAsync(rule);

        for (var i = 0; i < 5; i++)
        {
            await _engine.ProcessEventAsync(new LogEvent
            {
                RawLine = $"Event {i}",
                Timestamp = DateTime.UtcNow
            });
        }

        var stats = await _engine.GetStatsAsync();
        stats.TotalEventsProcessed.Should().Be(5);
        stats.TotalRules.Should().Be(1);
        stats.ActiveRules.Should().Be(1);
    }

    [Fact]
    public void AlertThrottle_BelowThreshold_NotMet()
    {
        var throttle = new AlertThrottle
        {
            Window = TimeSpan.FromMinutes(5),
            Threshold = 10
        };

        for (var i = 0; i < 5; i++)
        {
            throttle.AddEvent(DateTime.UtcNow.AddSeconds(i));
        }

        throttle.IsThresholdMet().Should().BeFalse();
    }

    [Fact]
    public void AlertThrottle_AboveThreshold_IsMet()
    {
        var throttle = new AlertThrottle
        {
            Window = TimeSpan.FromMinutes(5),
            Threshold = 10
        };

        for (var i = 0; i < 15; i++)
        {
            throttle.AddEvent(DateTime.UtcNow.AddSeconds(i));
        }

        throttle.IsThresholdMet().Should().BeTrue();
    }

    [Fact]
    public void AlertThrottle_OldEventsExpired_NotMet()
    {
        var throttle = new AlertThrottle
        {
            Window = TimeSpan.FromMinutes(1),
            Threshold = 10
        };

        for (var i = 0; i < 15; i++)
        {
            throttle.AddEvent(DateTime.UtcNow.AddMinutes(-10).AddSeconds(i));
        }

        // Recent event triggers cleanup of old events
        throttle.AddEvent(DateTime.UtcNow);

        throttle.IsThresholdMet().Should().BeFalse();
    }

    [Fact]
    public async Task AlertThrottle_ThreadSafety_ConcurrentAccess()
    {
        var throttle = new AlertThrottle
        {
            Window = TimeSpan.FromMinutes(5),
            Threshold = 100
        };

        var tasks = Enumerable.Range(0, 50).Select(n =>
            Task.Run(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    throttle.AddEvent(DateTime.UtcNow);
                    throttle.IsThresholdMet();
                }
            }));

        await Task.WhenAll(tasks);

        // 500 events added, threshold is 100, so it should be met
        throttle.IsThresholdMet().Should().BeTrue();
    }

    [Fact]
    public async Task TriggerCount_IsThreadSafe()
    {
        var rule = CreateRule("concurrent-rule", "Concurrent Test");

        var tasks = Enumerable.Range(0, 100).Select(_ =>
            Task.Run(() => Interlocked.Increment(ref rule._triggerCount)));

        await Task.WhenAll(tasks);

        rule.TriggerCount.Should().Be(100);
    }

    private static AlertRule CreateRule(string id, string name)
    {
        return new AlertRule
        {
            Id = id,
            Name = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            Actions = new List<AlertActionConfig>()
        };
    }
}
