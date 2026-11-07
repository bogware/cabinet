using System.Globalization;
using System.Text.Json;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for structured JSON log lines (e.g., from Docker, Kubernetes, application frameworks).
/// Extracts well-known fields (timestamp, level, message, host) and maps all other
/// JSON properties into LogEvent.Fields for full-text search and BQL queries.
/// Nested objects flatten to dotted names, so ECS's <c>{"log":{"level":"error"}}</c> is
/// recognized as a level and searchable as <c>log.level</c>.
/// </summary>
public sealed class JsonLogParser : BaseLogParser
{
    private const int MaxFlattenDepth = 8;

    private static readonly string[] TimestampFieldNames =
        { "timestamp", "@timestamp", "time", "datetime", "date", "ts", "eventTime", "created_at", "_time", "event.created" };

    private static readonly string[] MessageFieldNames =
        { "message", "msg", "text", "body", "log", "description", "event.original" };

    private static readonly string[] LevelFieldNames =
        { "level", "severity", "loglevel", "log_level", "priority", "sev", "log.level" };

    private static readonly string[] HostFieldNames =
        { "host", "hostname", "server", "node", "host.name" };

    public JsonLogParser(ILogger<JsonLogParser>? logger = null) : base(logger) { }

    public override string Name => "JSON";
    public override string SourceType => SourceTypes.Json;

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.AsSpan().Trim();
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}';
    }

    /// <summary>The time comes from the JSON, not from text inside a message.</summary>
    protected override void ExtractTimestamp(string line, LogEvent logEvent)
    {
        logEvent.Timestamp = DateTime.UtcNow;
    }

    /// <summary>The level comes from the JSON, not from words inside a message.</summary>
    protected override void ExtractSeverity(string line, LogEvent logEvent)
    {
        logEvent.Severity = LogSeverity.Info;
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            ExtractObject(doc.RootElement, string.Empty, 0, logEvent);
            logEvent.Tags.Add("json");
        }
        catch (JsonException ex)
        {
            // It only looks like JSON, so read it as text.
            _logger.LogDebug(ex, "Line is not valid JSON");
            base.ExtractTimestamp(line, logEvent);
            base.ExtractSeverity(line, logEvent);
        }
    }

    private static void ExtractObject(JsonElement element, string prefix, int depth, LogEvent logEvent)
    {
        foreach (var prop in element.EnumerateObject())
        {
            var key = prefix + prop.Name;
            if (prop.Value.ValueKind == JsonValueKind.Object && depth < MaxFlattenDepth)
            {
                ExtractObject(prop.Value, key + ".", depth + 1, logEvent);
                continue;
            }

            var value = ExtractJsonValue(prop.Value);
            if (TimestampFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase)
                && LogTimestamps.TryParseValue(value, out var timestamp))
            {
                logEvent.Timestamp = timestamp;
            }
            else if (MessageFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value != null)
            {
                logEvent.SetField("Message", value.ToString()!);
            }
            else if (LevelFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value != null)
            {
                ApplyLevel(value, logEvent);
                logEvent.SetField("LogLevel", value.ToString()!);
            }
            else if (HostFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value is string host)
            {
                logEvent.Host = host;
            }
            else if (value != null)
            {
                logEvent.SetField(key, value);
            }
        }
    }

    private static void ApplyLevel(object value, LogEvent logEvent)
    {
        if (value is string text)
        {
            logEvent.Severity = LogSeverities.TryParse(text, out var named) ? named : LogSeverity.Info;
        }
        else if (value is long or double
            && LogSeverities.TryParseNumeric(Convert.ToDouble(value, CultureInfo.InvariantCulture), out var numeric))
        {
            logEvent.Severity = numeric;
        }
    }

    private static object? ExtractJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText()
    };
}
