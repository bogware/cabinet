using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cabinet.Alerts;

/// <summary>
/// Default alert service implementation with deduplication and cooldown/throttling enforcement.
/// Tracks recently fired alerts by rule+dedup key to prevent alert storms.
/// </summary>
public class AlertService : IAlertService
{
    private readonly ILogger<AlertService> _logger;
    private readonly List<IAlertChannel> _channels = new();
    private readonly ConcurrentDictionary<string, AlertRule> _rules = new();
    private readonly ConcurrentDictionary<string, DateTime> _dedupTracker = new();
    private const int MaxDedupEntries = 100_000;

    public AlertService(ILogger<AlertService> logger, IEnumerable<IAlertChannel> channels)
    {
        _logger = logger;
        _channels.AddRange(channels);
    }

    /// <summary>
    /// Register an alert rule for dedup/throttle tracking
    /// </summary>
    public void RegisterRule(AlertRule rule)
    {
        _rules[rule.Id] = rule;
        _logger.LogInformation("Registered alert rule '{Name}' (id: {Id})", rule.Name, rule.Id);
    }

    /// <summary>
    /// Unregister an alert rule
    /// </summary>
    public bool UnregisterRule(string ruleId)
    {
        return _rules.TryRemove(ruleId, out _);
    }

    public async Task SendAlertAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending alert: {Title} (Severity: {Severity})", alert.Title, alert.Severity);

        var tasks = _channels.Select(channel => SendToChannelAsync(channel, alert, cancellationToken));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Send an alert with dedup and cooldown enforcement based on a rule.
    /// Returns true if the alert was actually sent, false if suppressed.
    /// </summary>
    public async Task<bool> SendAlertWithRuleAsync(Alert alert, AlertRule rule, CancellationToken cancellationToken = default)
    {
        if (!rule.Enabled)
        {
            _logger.LogDebug("Alert rule '{Name}' is disabled, skipping", rule.Name);
            return false;
        }

        // Check cooldown
        if (rule.LastTriggeredAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - rule.LastTriggeredAt.Value;
            if (elapsed < rule.Cooldown)
            {
                _logger.LogDebug(
                    "Alert rule '{Name}' in cooldown ({Remaining:F0}s remaining)",
                    rule.Name, (rule.Cooldown - elapsed).TotalSeconds);
                return false;
            }
        }

        // Check deduplication
        if (rule.DedupFields.Count > 0)
        {
            var dedupKey = BuildDedupKey(rule.Id, rule.DedupFields, alert.Metadata);
            if (!string.IsNullOrEmpty(dedupKey))
            {
                var now = DateTime.UtcNow;
                if (_dedupTracker.TryGetValue(dedupKey, out var lastSeen))
                {
                    if (now - lastSeen < rule.Cooldown)
                    {
                        _logger.LogDebug(
                            "Alert suppressed by dedup (rule: '{Name}', key: {DedupKey})",
                            rule.Name, dedupKey);
                        return false;
                    }
                }

                // Evict old entries if tracker is too large
                if (_dedupTracker.Count >= MaxDedupEntries)
                {
                    EvictOldDedupEntries();
                }

                _dedupTracker[dedupKey] = now;
            }
        }

        // Alert passes all checks — send it
        rule.LastTriggeredAt = DateTime.UtcNow;

        // Apply message template if configured
        if (!string.IsNullOrEmpty(rule.MessageTemplate))
        {
            alert.Message = ApplyMessageTemplate(rule.MessageTemplate, alert.Metadata);
        }

        // Send to specified channels or all channels
        if (rule.Channels.Count > 0)
        {
            var targetChannels = _channels.Where(c => rule.Channels.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
            var tasks = targetChannels.Select(channel => SendToChannelAsync(channel, alert, cancellationToken));
            await Task.WhenAll(tasks);
        }
        else
        {
            await SendAlertAsync(alert, cancellationToken);
        }

        _logger.LogInformation(
            "Alert fired for rule '{Name}': {Title} (Severity: {Severity})",
            rule.Name, alert.Title, alert.Severity);

        return true;
    }

    public void RegisterChannel(IAlertChannel channel)
    {
        if (!_channels.Any(c => c.Name == channel.Name))
        {
            _channels.Add(channel);
            _logger.LogInformation("Registered alert channel: {Channel}", channel.Name);
        }
    }

    public IEnumerable<IAlertChannel> GetChannels()
    {
        return _channels.AsEnumerable();
    }

    private async Task SendToChannelAsync(IAlertChannel channel, Alert alert, CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(alert, cancellationToken);
            _logger.LogDebug("Alert sent to channel {Channel}", channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert to channel {Channel}", channel.Name);
        }
    }

    /// <summary>
    /// Build a dedup key from the rule ID + values of dedup fields in the alert metadata.
    /// </summary>
    private static string BuildDedupKey(string ruleId, List<string> dedupFields, Dictionary<string, object> metadata)
    {
        var parts = new List<string> { ruleId };
        foreach (var field in dedupFields)
        {
            if (metadata.TryGetValue(field, out var value))
            {
                parts.Add($"{field}={value}");
            }
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Apply a message template, substituting {field} placeholders with metadata values.
    /// </summary>
    private static string ApplyMessageTemplate(string template, Dictionary<string, object> metadata)
    {
        var result = template;
        foreach (var kvp in metadata)
        {
            result = result.Replace($"{{{kvp.Key}}}", kvp.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>
    /// Evict old dedup entries when the tracker grows too large.
    /// First tries time-based eviction (>30min), then evicts oldest half if still over capacity.
    /// </summary>
    private void EvictOldDedupEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var keysToRemove = _dedupTracker
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _dedupTracker.TryRemove(key, out _);
        }

        // If time-based eviction wasn't enough, evict the oldest half
        if (_dedupTracker.Count >= MaxDedupEntries)
        {
            var oldestHalf = _dedupTracker
                .OrderBy(kvp => kvp.Value)
                .Take(_dedupTracker.Count / 2)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldestHalf)
            {
                _dedupTracker.TryRemove(key, out _);
            }

            keysToRemove.AddRange(oldestHalf);
        }

        _logger.LogDebug("Evicted {Count} stale dedup entries", keysToRemove.Count);
    }
}

/// <summary>
/// Console alert channel - outputs alerts to console
/// </summary>
public class ConsoleAlertChannel : IAlertChannel
{
    public string Name => "Console";

    public Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => ConsoleColor.Red,
            AlertSeverity.Error => ConsoleColor.DarkRed,
            AlertSeverity.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.White
        };

        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;

            Console.WriteLine($"[{alert.Timestamp:yyyy-MM-dd HH:mm:ss}] [{alert.Severity}] {alert.Title}");
            Console.WriteLine($"  {alert.Message}");

            if (alert.Metadata.Any())
            {
                Console.WriteLine("  Metadata:");
                foreach (var kvp in alert.Metadata)
                {
                    Console.WriteLine($"    {kvp.Key}: {kvp.Value}");
                }
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// File alert channel - writes alerts to a file
/// </summary>
public class FileAlertChannel : IAlertChannel
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Name => "File";

    public FileAlertChannel(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cabinet",
            "alerts.log"
        );

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var lines = new List<string>
            {
                $"[{alert.Timestamp:yyyy-MM-dd HH:mm:ss}] [{alert.Severity}] {alert.Title}",
                $"  {alert.Message}",
                $"  Source: {alert.Source}"
            };

            if (alert.Metadata.Any())
            {
                lines.Add("  Metadata:");
                foreach (var kvp in alert.Metadata)
                {
                    lines.Add($"    {kvp.Key}: {kvp.Value}");
                }
            }

            lines.Add(""); // Empty line separator

            await File.AppendAllLinesAsync(_filePath, lines, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
