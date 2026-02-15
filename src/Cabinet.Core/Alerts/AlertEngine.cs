using System.Collections.Concurrent;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Core.Alerts;

/// <summary>
/// Real-time alert engine with configurable rules and actions
/// </summary>
public interface IAlertEngine
{
    Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteRuleAsync(string ruleId, CancellationToken cancellationToken = default);
    Task<List<AlertRule>> GetRulesAsync(CancellationToken cancellationToken = default);
    Task<List<AlertInstance>> GetActiveAlertsAsync(CancellationToken cancellationToken = default);
    Task ProcessEventAsync(LogEvent evt, CancellationToken cancellationToken = default);
    Task<AlertStats> GetStatsAsync(CancellationToken cancellationToken = default);
}

public class AlertEngine : IAlertEngine
{
    private readonly ConcurrentDictionary<string, AlertRule> _rules = new();
    private readonly ConcurrentDictionary<string, AlertInstance> _activeAlerts = new();
    private readonly ConcurrentDictionary<string, AlertThrottle> _throttles = new();
    private readonly ILogger<AlertEngine> _logger;
    private readonly List<IAlertAction> _actions;
    private long _totalAlertsTriggered;
    private long _totalEventsProcessed;

    public AlertEngine(ILogger<AlertEngine> logger, IEnumerable<IAlertAction>? actions = null)
    {
        _logger = logger;
        _actions = actions?.ToList() ?? new List<IAlertAction>();
    }

    public Task<AlertRule> CreateRuleAsync(AlertRule rule, CancellationToken cancellationToken = default)
    {
        rule.Id = string.IsNullOrEmpty(rule.Id) ? Guid.NewGuid().ToString() : rule.Id;
        rule.CreatedAt = DateTime.UtcNow;
        rule.IsActive = true;

        _rules[rule.Id] = rule;
        _logger.LogInformation("Created alert rule: {RuleName} ({RuleId})", rule.Name, rule.Id);

        return Task.FromResult(rule);
    }

    public Task<bool> DeleteRuleAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        if (_rules.TryRemove(ruleId, out var rule))
        {
            _logger.LogInformation("Deleted alert rule: {RuleName} ({RuleId})", rule.Name, ruleId);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<List<AlertRule>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rules.Values.ToList());
    }

    public Task<List<AlertInstance>> GetActiveAlertsAsync(CancellationToken cancellationToken = default)
    {
        // Clean up resolved alerts
        var now = DateTime.UtcNow;
        var expiredAlerts = _activeAlerts
            .Where(a => a.Value.ResolvedAt.HasValue &&
                       now - a.Value.ResolvedAt.Value > TimeSpan.FromMinutes(5))
            .Select(a => a.Key)
            .ToList();

        foreach (var id in expiredAlerts)
        {
            _activeAlerts.TryRemove(id, out _);
        }

        return Task.FromResult(_activeAlerts.Values.OrderByDescending(a => a.TriggeredAt).ToList());
    }

    public async Task ProcessEventAsync(LogEvent evt, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _totalEventsProcessed);

        foreach (var rule in _rules.Values.Where(r => r.IsActive))
        {
            if (await EvaluateRuleAsync(rule, evt, cancellationToken))
            {
                await TriggerAlertAsync(rule, evt, cancellationToken);
            }
        }
    }

    public Task<AlertStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new AlertStats
        {
            TotalRules = _rules.Count,
            ActiveRules = _rules.Count(r => r.Value.IsActive),
            ActiveAlerts = _activeAlerts.Count(a => !a.Value.ResolvedAt.HasValue),
            TotalAlertsTriggered = _totalAlertsTriggered,
            TotalEventsProcessed = _totalEventsProcessed,
            TopTriggeredRules = _rules.Values
                .OrderByDescending(r => r.TriggerCount)
                .Take(5)
                .Select(r => new { r.Name, r.TriggerCount })
                .ToList()
        };

        return Task.FromResult(stats);
    }

    private async Task<bool> EvaluateRuleAsync(AlertRule rule, LogEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            // Check conditions
            foreach (var condition in rule.Conditions)
            {
                if (!EvaluateCondition(condition, evt))
                {
                    return false;
                }
            }

            // Check threshold if configured
            if (rule.Threshold != null)
            {
                return await CheckThresholdAsync(rule, evt, cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating rule {RuleName}", rule.Name);
            return false;
        }
    }

    private bool EvaluateCondition(AlertCondition condition, LogEvent evt)
    {
        var fieldValue = GetFieldValue(evt, condition.Field);
        if (fieldValue == null) return false;

        var compareValue = condition.Value;

        return condition.Operator switch
        {
            ConditionOperator.Equals => fieldValue.Equals(compareValue, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !fieldValue.Equals(compareValue, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => fieldValue.Contains(compareValue, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => CompareValues(fieldValue, compareValue) > 0,
            ConditionOperator.LessThan => CompareValues(fieldValue, compareValue) < 0,
            ConditionOperator.Regex => TryRegexMatch(fieldValue, compareValue),
            _ => false
        };
    }

    private string? GetFieldValue(LogEvent evt, string field)
    {
        return field.ToLower() switch
        {
            "severity" => evt.Severity.ToString(),
            "sourcetype" => evt.SourceType,
            "serverid" or "host" => evt.Host,
            "rawline" => evt.RawLine,
            "sourcefile" => evt.SourceFile,
            _ => evt.GetFieldString(field)
        };
    }

    private static bool TryRegexMatch(string input, string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private int CompareValues(string a, string b)
    {
        if (double.TryParse(a, out var aNum) && double.TryParse(b, out var bNum))
        {
            return aNum.CompareTo(bNum);
        }
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private Task<bool> CheckThresholdAsync(AlertRule rule, LogEvent evt, CancellationToken cancellationToken)
    {
        var throttleKey = $"{rule.Id}:{rule.Threshold!.Window}";

        if (!_throttles.TryGetValue(throttleKey, out var throttle))
        {
            throttle = new AlertThrottle
            {
                Window = TimeSpan.FromSeconds(rule.Threshold.Window),
                Threshold = rule.Threshold.Count
            };
            _throttles[throttleKey] = throttle;
        }

        throttle.AddEvent(evt.Timestamp);

        return Task.FromResult(throttle.IsThresholdMet());
    }

    private async Task TriggerAlertAsync(AlertRule rule, LogEvent evt, CancellationToken cancellationToken)
    {
        // Check if already alerting for this rule
        var alertKey = $"{rule.Id}:{evt.Id}";
        if (_activeAlerts.ContainsKey(alertKey))
        {
            return;
        }

        // Check cooldown
        if (rule.CooldownMinutes > 0)
        {
            var lastTrigger = _activeAlerts.Values
                .Where(a => a.RuleId == rule.Id)
                .OrderByDescending(a => a.TriggeredAt)
                .FirstOrDefault();

            if (lastTrigger != null)
            {
                var timeSinceLastTrigger = DateTime.UtcNow - lastTrigger.TriggeredAt;
                if (timeSinceLastTrigger < TimeSpan.FromMinutes(rule.CooldownMinutes))
                {
                    return;
                }
            }
        }

        // Create alert instance
        var alert = new AlertInstance
        {
            Id = Guid.NewGuid().ToString(),
            RuleId = rule.Id,
            RuleName = rule.Name,
            Severity = rule.Severity,
            TriggeredAt = DateTime.UtcNow,
            TriggeringEvent = evt,
            Message = FormatAlertMessage(rule, evt),
            Context = new Dictionary<string, object>
            {
                ["EventId"] = evt.Id,
                ["Timestamp"] = evt.Timestamp,
                ["Severity"] = evt.Severity.ToString(),
                ["RawLine"] = evt.RawLine
            }
        };

        _activeAlerts[alert.Id] = alert;
        Interlocked.Increment(ref rule._triggerCount);
        rule.LastTriggered = alert.TriggeredAt;
        Interlocked.Increment(ref _totalAlertsTriggered);

        _logger.LogWarning("Alert triggered: {RuleName} - {Message}", rule.Name, alert.Message);

        // Execute actions
        foreach (var action in rule.Actions)
        {
            await ExecuteActionAsync(action, alert, cancellationToken);
        }
    }

    private string FormatAlertMessage(AlertRule rule, LogEvent evt)
    {
        var message = rule.MessageTemplate ?? "Alert: {RuleName} triggered";

        message = message.Replace("{RuleName}", rule.Name);
        message = message.Replace("{Severity}", evt.Severity.ToString());
        message = message.Replace("{Timestamp}", evt.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        message = message.Replace("{RawLine}", evt.RawLine);

        foreach (var field in evt.Fields)
        {
            message = message.Replace($"{{{field.Key}}}", field.Value?.ToString() ?? "");
        }

        return message;
    }

    private async Task ExecuteActionAsync(AlertActionConfig actionConfig, AlertInstance alert, CancellationToken cancellationToken)
    {
        var action = _actions.FirstOrDefault(a => a.Name == actionConfig.Type);
        if (action != null)
        {
            try
            {
                await action.ExecuteAsync(alert, actionConfig.Parameters, cancellationToken);
                _logger.LogInformation("Executed alert action {ActionType} for {AlertId}", actionConfig.Type, alert.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute alert action {ActionType}", actionConfig.Type);
            }
        }
    }
}

// Alert models
public class AlertRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Medium;
    public List<AlertCondition> Conditions { get; set; } = new();
    public AlertThreshold? Threshold { get; set; }
    public List<AlertActionConfig> Actions { get; set; } = new();
    public string? MessageTemplate { get; set; }
    public int CooldownMinutes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTriggered { get; set; }
    internal int _triggerCount;
    public int TriggerCount => _triggerCount;
}

public class AlertCondition
{
    public string Field { get; set; } = "";
    public ConditionOperator Operator { get; set; }
    public string Value { get; set; } = "";
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    LessThan,
    Regex
}

public class AlertThreshold
{
    public int Count { get; set; }
    public int Window { get; set; } // seconds
}

public class AlertActionConfig
{
    public string Type { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class AlertInstance
{
    public string Id { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string RuleName { get; set; } = "";
    public AlertSeverity Severity { get; set; }
    public DateTime TriggeredAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public LogEvent? TriggeringEvent { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<string, object> Context { get; set; } = new();
}

public enum AlertSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public class AlertStats
{
    public int TotalRules { get; set; }
    public int ActiveRules { get; set; }
    public int ActiveAlerts { get; set; }
    public long TotalAlertsTriggered { get; set; }
    public long TotalEventsProcessed { get; set; }
    public object? TopTriggeredRules { get; set; }
}

public class AlertThrottle
{
    private readonly Queue<DateTime> _eventTimes = new();
    private readonly object _lock = new();
    public TimeSpan Window { get; set; }
    public int Threshold { get; set; }

    public void AddEvent(DateTime timestamp)
    {
        lock (_lock)
        {
            _eventTimes.Enqueue(timestamp);
            CleanOldEvents(timestamp);
        }
    }

    public bool IsThresholdMet()
    {
        lock (_lock)
        {
            return _eventTimes.Count >= Threshold;
        }
    }

    private void CleanOldEvents(DateTime now)
    {
        var cutoff = now - Window;
        while (_eventTimes.Count > 0 && _eventTimes.Peek() < cutoff)
        {
            _eventTimes.Dequeue();
        }
    }
}

// Alert action interface
public interface IAlertAction
{
    string Name { get; }
    Task ExecuteAsync(AlertInstance alert, Dictionary<string, string> parameters, CancellationToken cancellationToken = default);
}

// Built-in alert actions
public class ConsoleAlertAction : IAlertAction
{
    public string Name => "console";

    public Task ExecuteAsync(AlertInstance alert, Dictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => ConsoleColor.Red,
            AlertSeverity.High => ConsoleColor.DarkRed,
            AlertSeverity.Medium => ConsoleColor.Yellow,
            _ => ConsoleColor.White
        };

        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[ALERT] {alert.TriggeredAt:yyyy-MM-dd HH:mm:ss} - {alert.RuleName}: {alert.Message}");
        Console.ForegroundColor = originalColor;

        return Task.CompletedTask;
    }
}

public class FileAlertAction : IAlertAction
{
    public string Name => "file";

    public async Task ExecuteAsync(AlertInstance alert, Dictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        var filePath = parameters.GetValueOrDefault("path", "alerts.log");
        var json = JsonSerializer.Serialize(alert, new JsonSerializerOptions { WriteIndented = true });
        await File.AppendAllTextAsync(filePath, $"{json}\n", cancellationToken);
    }
}