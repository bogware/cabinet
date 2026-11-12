using System.Globalization;
using System.Net.Http.Json;
using Cabinet.Alerts;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;

namespace Cabinet.API.Services;

/// <summary>The facts of one rule firing, which actions turn into notifications, events or calls.</summary>
public sealed record DetectionFiring(DetectionRule Rule, string GroupValue, int Count, LogEvent Event);

/// <summary>
/// Runs what a <see cref="DetectionRule"/> says to do when it fires. Every firing is saved to alert
/// history; then each configured action runs — notify through the channels, write a searchable
/// event, or call a webhook. A rule with no actions notifies, which is how rules behaved before.
/// One action failing doesn't stop the others, and never loses the history record.
/// </summary>
public sealed class DetectionActionExecutor : BackgroundService
{
    /// <summary>Where <c>event</c> actions write when they name no index.</summary>
    public const string DefaultEventIndex = "detections";

    /// <summary>The source type on emitted events, so they're easy to find and never re-detected.</summary>
    public const string EventSourceType = "detection";

    private readonly IAlertHistoryStorage _history;
    private readonly IAlertService _alerts;
    private readonly ILogStorage _emitStore;
    private readonly HttpClient _http;
    private readonly ILogger<DetectionActionExecutor> _logger;
    private readonly FieldMaskingService? _masking;
    private readonly IActionConnectorRunner? _connectors;
    private readonly TimeSpan _actionTimeout;
    private readonly bool _background;
    private readonly System.Threading.Channels.Channel<DetectionFiring> _queue;
    private long _dropped;

    /// <param name="background">
    /// Run actions on their own queue, so a slow webhook or mail server can't hold up detection
    /// and, through it, ingest. Off when constructed directly (tests), where actions run inline.
    /// </param>
    public DetectionActionExecutor(
        IAlertHistoryStorage history,
        IAlertService alerts,
        ILogStorage emitStore,
        HttpClient http,
        ILogger<DetectionActionExecutor> logger,
        FieldMaskingService? masking = null,
        bool background = false,
        TimeSpan? actionTimeout = null,
        int queueCapacity = 1000,
        IActionConnectorRunner? connectors = null)
    {
        _history = history;
        _alerts = alerts;
        _emitStore = emitStore;
        _http = http;
        _logger = logger;
        _masking = masking;
        _background = background;
        _connectors = connectors;
        _actionTimeout = actionTimeout ?? TimeSpan.FromSeconds(15);
        _queue = System.Threading.Channels.Channel.CreateBounded<DetectionFiring>(new System.Threading.Channels.BoundedChannelOptions(Math.Max(1, queueCapacity))
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>Firings whose actions were skipped because the action queue was full.</summary>
    public long DroppedActions => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Records the firing in alert history, then runs its actions: on the action queue when running
    /// in the background, or right away otherwise. History is written first and always, so a full
    /// queue never loses the record that the rule fired.
    /// </summary>
    public async Task ExecuteAsync(DetectionFiring firing, CancellationToken cancellationToken = default)
    {
        await RecordAsync(firing);

        if (!_background)
        {
            await RunActionsAsync(firing, cancellationToken);
            return;
        }

        if (!_queue.Writer.TryWrite(firing))
        {
            Interlocked.Increment(ref _dropped);
            _logger.LogWarning("Detection {Rule} fired but its actions were skipped: the action queue is full. The firing is in alert history.", firing.Rule.Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var firing in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunActionsAsync(firing, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Detection action queue stopping");
        }
    }

    private Task RecordAsync(DetectionFiring firing)
    {
        var rule = firing.Rule;

        // History always records the firing, whatever the actions do, and keeps no log content.
        return _history.SaveAlertAsync(new AlertRecord
        {
            SearchName = rule.Name,
            Query = rule.Condition ?? rule.Pattern ?? string.Empty,
            Severity = rule.Severity,
            ResultCount = firing.Count,
            Message = DefaultMessage(firing, forHistory: true),
            Channel = "detection",
            RuleId = rule.Id,
            EventId = firing.Event.Id,
            Index = firing.Event.Index,
            GroupValue = firing.GroupValue,
            Technique = rule.Technique,
            FiredAt = firing.Event.Timestamp,
        });
    }

    private async Task RunActionsAsync(DetectionFiring firing, CancellationToken cancellationToken)
    {
        var rule = firing.Rule;

        // Actions see the event as the most restricted viewer would: anything masked for anyone is
        // masked here, since a webhook or channel reaches people outside Cabinet's access control.
        if (_masking != null)
        {
            firing = firing with { Event = _masking.MaskEvent(firing.Event, SystemRole.Viewer) };
        }

        // No actions means notify, as rules always did.
        var actions = rule.Actions is { Count: > 0 } configured
            ? configured
            : [new DetectionAction { Type = "notify" }];

        foreach (var action in actions)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_actionTimeout);
            try
            {
                await RunAsync(action, firing, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Detection {Rule}: the {Type} action took longer than {Seconds}s and was stopped", rule.Name, action.Type, _actionTimeout.TotalSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Detection {Rule}: the {Type} action failed, though the firing is in history", rule.Name, action.Type);
            }
        }
    }

    private Task RunAsync(DetectionAction action, DetectionFiring firing, CancellationToken cancellationToken) =>
        action.Type?.ToLowerInvariant() switch
        {
            "event" => EmitEventAsync(action, firing, cancellationToken),
            "webhook" => CallWebhookAsync(action, firing, cancellationToken),
            "connector" => RunConnectorAsync(action, firing, cancellationToken),
            _ => NotifyAsync(action, firing, cancellationToken),
        };

    private Task RunConnectorAsync(DetectionAction action, DetectionFiring firing, CancellationToken cancellationToken)
    {
        if (_connectors == null || string.IsNullOrWhiteSpace(action.Target))
        {
            _logger.LogWarning("Detection {Rule}: a connector action names no connector, or connectors aren't available", firing.Rule.Name);
            return Task.CompletedTask;
        }

        return _connectors.SendAsync(action.Target, ToNotification(action, firing), cancellationToken);
    }

    /// <summary>The firing as a connector delivers it: a title, a message and the facts.</summary>
    public ConnectorNotification ToNotification(DetectionAction action, DetectionFiring firing)
    {
        var rule = firing.Rule;
        return new ConnectorNotification
        {
            Title = $"Detection: {rule.Name}",
            Message = Fill(action.Message, firing) ?? DefaultMessage(firing, forHistory: false),
            Severity = action.Severity ?? rule.Severity,
            Source = $"Detection/{rule.Id}",
            Time = firing.Event.Timestamp,
            Facts = new Dictionary<string, string>
            {
                ["rule"] = rule.Name,
                ["group"] = firing.GroupValue,
                ["count"] = firing.Count.ToString(CultureInfo.InvariantCulture),
                ["host"] = firing.Event.Host ?? string.Empty,
                ["index"] = firing.Event.Index,
                ["event_id"] = firing.Event.Id,
                ["technique"] = rule.Technique ?? string.Empty,
            },
            DedupKey = $"{rule.Id}:{firing.GroupValue}",
        };
    }

    private Task NotifyAsync(DetectionAction action, DetectionFiring firing, CancellationToken cancellationToken)
    {
        var rule = firing.Rule;
        var alert = new Alert
        {
            Severity = ParseSeverity(action.Severity ?? rule.Severity),
            Title = $"Detection: {rule.Name}",
            Message = Fill(action.Message, firing) ?? DefaultMessage(firing, forHistory: false),
            Source = $"Detection/{rule.Id}",
            Metadata = new Dictionary<string, object>
            {
                ["rule_id"] = rule.Id,
                ["rule_name"] = rule.Name,
                ["event_id"] = firing.Event.Id,
                ["index"] = firing.Event.Index,
                ["host"] = firing.Event.Host ?? string.Empty,
                ["match_count"] = firing.Count,
                ["group"] = firing.GroupValue,
                ["technique"] = rule.Technique ?? string.Empty,
            },
        };
        return _alerts.SendAlertAsync(alert, cancellationToken);
    }

    private Task EmitEventAsync(DetectionAction action, DetectionFiring firing, CancellationToken cancellationToken)
    {
        var rule = firing.Rule;
        var index = string.IsNullOrWhiteSpace(action.Target) ? DefaultEventIndex : action.Target.Trim();
        var severity = action.Severity ?? rule.Severity;

        var logEvent = new LogEvent
        {
            Id = Ulid.NewUlid(),
            Timestamp = DateTime.UtcNow,
            Index = index,
            SourceType = EventSourceType,
            Host = firing.Event.Host,
            Severity = SeverityOf(severity),
            RawLine = Fill(action.Message, firing) ?? DefaultMessage(firing, forHistory: false),
        };

        logEvent.SetField("rule", rule.Name);
        logEvent.SetField("rule_id", rule.Id);
        logEvent.SetField("severity_name", severity);
        logEvent.SetField("count", firing.Count);
        logEvent.SetField("source_event_id", firing.Event.Id);
        logEvent.SetField("source_index", firing.Event.Index);
        if (!string.IsNullOrEmpty(firing.GroupValue))
        {
            logEvent.SetField(string.IsNullOrEmpty(rule.GroupBy) ? "group" : rule.GroupBy, firing.GroupValue);
        }

        if (!string.IsNullOrEmpty(rule.Technique))
        {
            logEvent.SetField("technique", rule.Technique);
        }

        if (action.Fields != null)
        {
            foreach (var (key, template) in action.Fields)
            {
                logEvent.SetField(key, Fill(template, firing) ?? string.Empty);
            }
        }

        // Written below the detection layer, so emitted events are searchable but never re-detected.
        return _emitStore.StoreAsync(logEvent, cancellationToken);
    }

    private async Task CallWebhookAsync(DetectionAction action, DetectionFiring firing, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(action.Target))
        {
            _logger.LogWarning("Detection {Rule}: a webhook action has no URL, so it's skipped", firing.Rule.Name);
            return;
        }

        var rule = firing.Rule;
        var payload = new Dictionary<string, object?>
        {
            ["rule"] = rule.Name,
            ["rule_id"] = rule.Id,
            ["severity"] = action.Severity ?? rule.Severity,
            ["message"] = Fill(action.Message, firing) ?? DefaultMessage(firing, forHistory: false),
            ["group"] = firing.GroupValue,
            ["group_by"] = rule.GroupBy,
            ["count"] = firing.Count,
            ["window_seconds"] = rule.WindowSeconds,
            ["host"] = firing.Event.Host,
            ["index"] = firing.Event.Index,
            ["event_id"] = firing.Event.Id,
            ["technique"] = rule.Technique,
            ["time"] = firing.Event.Timestamp,
        };

        using var response = await _http.PostAsJsonAsync(action.Target, payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Detection {Rule}: the webhook returned {Status}", rule.Name, (int)response.StatusCode);
        }
    }

    private string? Fill(string? template, DetectionFiring firing)
    {
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        var rule = firing.Rule;
        return template
            .Replace("{rule}", rule.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{group}", firing.GroupValue, StringComparison.OrdinalIgnoreCase)
            .Replace("{count}", firing.Count.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{window}", rule.WindowSeconds.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{severity}", rule.Severity, StringComparison.OrdinalIgnoreCase)
            .Replace("{host}", firing.Event.Host ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{index}", firing.Event.Index, StringComparison.OrdinalIgnoreCase)
            .Replace("{event_id}", firing.Event.Id, StringComparison.OrdinalIgnoreCase)
            .Replace("{technique}", rule.Technique ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", firing.Event.Timestamp.ToString("O", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{line}", firing.Event.RawLine, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultMessage(DetectionFiring firing, bool forHistory)
    {
        var rule = firing.Rule;
        var subject = string.IsNullOrEmpty(firing.GroupValue) ? string.Empty : $" for {rule.GroupBy}={firing.GroupValue}";
        if (rule.Threshold > 1)
        {
            return $"{firing.Count} matching events within {rule.WindowSeconds} seconds{subject}."
                + (forHistory ? string.Empty : $" {rule.Description}".TrimEnd());
        }

        return forHistory
            ? $"Matched{subject}. See event {firing.Event.Id} in index {firing.Event.Index}."
            : $"Matched{subject}: {firing.Event.RawLine[..Math.Min(500, firing.Event.RawLine.Length)]}";
    }

    private static AlertSeverity ParseSeverity(string? severity) =>
        Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var parsed) ? parsed : AlertSeverity.Warning;

    private static LogSeverity SeverityOf(string? severity) => ParseSeverity(severity) switch
    {
        AlertSeverity.Critical => LogSeverity.Critical,
        AlertSeverity.Error => LogSeverity.Error,
        AlertSeverity.Warning => LogSeverity.Warning,
        _ => LogSeverity.Info,
    };
}
