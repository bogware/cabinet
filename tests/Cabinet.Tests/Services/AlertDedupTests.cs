using Cabinet.Alerts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Services;

public class AlertDedupTests
{
    private readonly AlertService _alertService;

    public AlertDedupTests()
    {
        var channels = new List<IAlertChannel> { new ConsoleAlertChannel() };
        _alertService = new AlertService(NullLogger<AlertService>.Instance, channels);
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_FirstAlert_SendsSuccessfully()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        var alert = CreateAlert("Test Alert");

        var sent = await _alertService.SendAlertWithRuleAsync(alert, rule);

        sent.Should().BeTrue();
        rule.LastTriggeredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_WithinCooldown_Suppressed()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.Cooldown = TimeSpan.FromMinutes(15);

        var alert1 = CreateAlert("Alert 1");
        await _alertService.SendAlertWithRuleAsync(alert1, rule);

        var alert2 = CreateAlert("Alert 2");
        var sent = await _alertService.SendAlertWithRuleAsync(alert2, rule);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_AfterCooldown_Sends()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.Cooldown = TimeSpan.FromMilliseconds(1);

        var alert1 = CreateAlert("Alert 1");
        await _alertService.SendAlertWithRuleAsync(alert1, rule);

        await Task.Delay(10); // Wait past cooldown

        var alert2 = CreateAlert("Alert 2");
        var sent = await _alertService.SendAlertWithRuleAsync(alert2, rule);

        sent.Should().BeTrue();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_DisabledRule_NotSent()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.Enabled = false;

        var alert = CreateAlert("Test Alert");
        var sent = await _alertService.SendAlertWithRuleAsync(alert, rule);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_DedupFields_SuppressesDuplicate()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.DedupFields = new List<string> { "host" };
        rule.Cooldown = TimeSpan.FromMinutes(15);

        var alert1 = CreateAlert("Alert 1");
        alert1.Metadata["host"] = "web-01";
        await _alertService.SendAlertWithRuleAsync(alert1, rule);

        // Reset cooldown so only dedup blocks it
        rule.LastTriggeredAt = null;

        var alert2 = CreateAlert("Alert 2");
        alert2.Metadata["host"] = "web-01"; // Same host — dedup should suppress
        var sent = await _alertService.SendAlertWithRuleAsync(alert2, rule);

        sent.Should().BeFalse();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_DedupFields_DifferentValues_Sends()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.DedupFields = new List<string> { "host" };
        rule.Cooldown = TimeSpan.FromMinutes(15);

        var alert1 = CreateAlert("Alert 1");
        alert1.Metadata["host"] = "web-01";
        await _alertService.SendAlertWithRuleAsync(alert1, rule);

        // Reset cooldown so only dedup logic runs
        rule.LastTriggeredAt = null;

        var alert2 = CreateAlert("Alert 2");
        alert2.Metadata["host"] = "web-02"; // Different host — should send
        var sent = await _alertService.SendAlertWithRuleAsync(alert2, rule);

        sent.Should().BeTrue();
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_MessageTemplate_Applied()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.MessageTemplate = "Found {count} errors on {host}";

        var alert = CreateAlert("Test Alert");
        alert.Metadata["count"] = 42;
        alert.Metadata["host"] = "web-01";

        await _alertService.SendAlertWithRuleAsync(alert, rule);

        alert.Message.Should().Be("Found 42 errors on web-01");
    }

    [Fact]
    public async Task SendAlertWithRuleAsync_SpecificChannels_UsesCorrectChannels()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        rule.Channels = new List<string> { "Console" };

        var alert = CreateAlert("Test Alert");
        var sent = await _alertService.SendAlertWithRuleAsync(alert, rule);

        sent.Should().BeTrue();
    }

    [Fact]
    public void RegisterRule_AddsRule()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        _alertService.RegisterRule(rule);
        // Should not throw
    }

    [Fact]
    public void UnregisterRule_ExistingRule_ReturnsTrue()
    {
        var rule = CreateRule("rule-1", "Test Rule");
        _alertService.RegisterRule(rule);

        var removed = _alertService.UnregisterRule("rule-1");
        removed.Should().BeTrue();
    }

    [Fact]
    public void UnregisterRule_NonExistent_ReturnsFalse()
    {
        var removed = _alertService.UnregisterRule("nonexistent");
        removed.Should().BeFalse();
    }

    private static AlertRule CreateRule(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Query = "find errors",
        ConditionType = AlertConditionType.Threshold,
        Cooldown = TimeSpan.FromMinutes(15)
    };

    private static Alert CreateAlert(string title) => new()
    {
        Title = title,
        Message = "Test message",
        Severity = AlertSeverity.Warning,
        Source = "test"
    };
}
