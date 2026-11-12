using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Reads Windows event log channels, such as Security, resuming after the last record sent. Each
/// event's EventData values become fields under their own names (TargetUserName, LogonType,
/// IpAddress), which is what Sigma rules for Windows test.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogInput : BackgroundService
{
    private const int MaxPerRead = 1000;

    private readonly InputConfiguration _input;
    private readonly AgentConfiguration _config;
    private readonly LogForwarder _forwarder;
    private readonly InputCheckpoints _checkpoints;
    private readonly ILogger<WindowsEventLogInput> _logger;

    public WindowsEventLogInput(InputConfiguration input, AgentConfiguration config, LogForwarder forwarder, InputCheckpoints checkpoints, ILogger<WindowsEventLogInput> logger)
    {
        _input = input;
        _config = config;
        _forwarder = forwarder;
        _checkpoints = checkpoints;
        _logger = logger;
        _forwarder.BatchDurable += OnBatchDurable;
    }

    private static string Key(string channel) => $"wineventlog:{channel}";

    private void OnBatchDurable(IReadOnlyList<LogEventDto> batch)
    {
        var latest = batch
            .Where(e => e.InputKey?.StartsWith("wineventlog:", StringComparison.Ordinal) == true && e.Checkpoint != null)
            .GroupBy(e => e.InputKey!)
            .ToDictionary(g => g.Key, g => g.Max(e => long.Parse(e.Checkpoint!, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
        _checkpoints.Set(latest);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = string.IsNullOrWhiteSpace(_config.Hostname) ? Environment.MachineName : _config.Hostname;

        // Read but not yet durable: keeps a channel from being read twice before its checkpoint moves
        var sent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in _input.Channels)
        {
            sent[channel] = long.TryParse(_checkpoints.Get(Key(channel)), out var saved) ? saved : _input.FromBeginning ? 0 : Newest(channel);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var channel in _input.Channels)
            {
                try
                {
                    sent[channel] = await ReadAsync(channel, sent[channel], host, stoppingToken);
                }
                catch (UnauthorizedAccessException)
                {
                    _logger.LogError("Reading the {Channel} event log needs more rights; run the agent as an administrator or add it to Event Log Readers", channel);
                }
                catch (EventLogException ex)
                {
                    _logger.LogWarning(ex, "Couldn't read the {Channel} event log", channel);
                }
            }

            try
            {
                await Task.Delay(Math.Max(250, _config.PollIntervalMilliseconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private long Newest(string channel)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName) { ReverseDirection = true });
            using var record = reader.ReadEvent();
            return record?.RecordId ?? 0;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't find where the {Channel} event log ends; reading it from the start", channel);
            return 0;
        }
    }

    private async Task<long> ReadAsync(string channel, long after, string host, CancellationToken cancellationToken)
    {
        var query = new EventLogQuery(channel, PathType.LogName, $"*[System[EventRecordID > {after}]]");
        using var reader = new EventLogReader(query);
        var last = after;
        for (var read = 0; read < MaxPerRead; read++)
        {
            using var record = reader.ReadEvent();
            if (record?.RecordId is not { } id)
            {
                break;
            }

            await _forwarder.EnqueueAsync(ToEvent(record, channel, host), cancellationToken);
            last = Math.Max(last, id);
        }

        return last;
    }

    private LogEventDto ToEvent(EventRecord record, string channel, string host)
    {
        var fields = WindowsEventXml.Fields(record.ToXml());
        fields["Channel"] = channel;
        fields["EventID"] = record.Id.ToString(CultureInfo.InvariantCulture);
        fields["Provider"] = record.ProviderName ?? string.Empty;
        if (record.Level is { } level) fields["Level"] = level.ToString(CultureInfo.InvariantCulture);

        string message;
        try
        {
            message = record.FormatDescription() ?? string.Empty;
        }
        catch (EventLogException)
        {
            // The provider's message file is missing; the fields still say what happened
            message = string.Empty;
        }

        var computer = record.MachineName ?? host;
        return new LogEventDto
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"wineventlog:{computer}:{channel}:{record.RecordId}")).AsSpan(0, 16)),
            InputKey = Key(channel),
            Checkpoint = record.RecordId?.ToString(CultureInfo.InvariantCulture),
            Timestamp = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
            Host = computer,
            Source = $"WinEventLog:{channel}",
            SourceType = _input.SourceType == "generic" ? "wineventlog" : _input.SourceType,
            Index = _input.Index,
            Event = $"{channel} EventID={record.Id} {record.ProviderName}: {(message.Length > 0 ? message.ReplaceLineEndings(" ") : string.Join(' ', fields.Select(f => $"{f.Key}={f.Value}")))}",
            Fields = fields,
        };
    }

    public override void Dispose()
    {
        _forwarder.BatchDurable -= OnBatchDurable;
        base.Dispose();
    }
}

/// <summary>Reads the fields out of a Windows event's XML. Not Windows-specific, so it can be tested anywhere.</summary>
public static class WindowsEventXml
{
    /// <summary>The named values in an event's EventData and UserData.</summary>
    public static Dictionary<string, string> Fields(string xml)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = XDocument.Parse(xml);
            foreach (var data in document.Descendants().Where(e => e.Name.LocalName == "Data" && e.Attribute("Name") != null))
            {
                fields[data.Attribute("Name")!.Value] = data.Value;
            }

            foreach (var data in document.Descendants().Where(e => e.Parent?.Parent?.Name.LocalName == "UserData" && !e.HasElements))
            {
                fields.TryAdd(data.Name.LocalName, data.Value);
            }
        }
        catch (System.Xml.XmlException)
        {
            // An event whose XML doesn't parse still has its message
            return fields;
        }

        return fields;
    }
}
