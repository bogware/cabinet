using System.Globalization;
using System.Text.Json;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for structured JSON log lines (e.g., from Docker, Kubernetes, application frameworks).
/// Extracts well-known fields (timestamp, level, message, host) and maps all other
/// JSON properties into LogEvent.Fields for full-text search and BQL queries.
/// </summary>
public sealed class JsonLogParser : BaseLogParser
{
    private static readonly string[] TimestampFieldNames =
        { "timestamp", "@timestamp", "time", "datetime", "date", "ts", "eventTime", "created_at", "_time" };

    private static readonly string[] MessageFieldNames =
        { "message", "msg", "text", "body", "log", "description" };

    private static readonly string[] LevelFieldNames =
        { "level", "severity", "loglevel", "log_level", "priority", "sev" };

    private static readonly string[] HostFieldNames =
        { "host", "hostname", "server", "source", "node", "host.name" };

    public JsonLogParser(ILogger<JsonLogParser>? logger = null) : base(logger) { }

    public override string Name => "JSON";
    public override string SourceType => SourceTypes.Json;

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.AsSpan().Trim();
        return trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[^1] == '}';
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;

            foreach (var prop in root.EnumerateObject())
            {
                var key = prop.Name;
                var value = ExtractJsonValue(prop.Value);

                if (TimestampFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value is string ts)
                {
                    if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                    {
                        logEvent.Timestamp = dt.ToUniversalTime();
                    }
                    else if (long.TryParse(ts, out var epoch))
                    {
                        try { logEvent.Timestamp = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime; }
                        catch (ArgumentOutOfRangeException) { /* invalid epoch, keep default */ }
                    }
                }
                else if (MessageFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value != null)
                {
                    logEvent.SetField("Message", value.ToString()!);
                }
                else if (LevelFieldNames.Contains(key, StringComparer.OrdinalIgnoreCase) && value is string lvl)
                {
                    logEvent.Severity = ParseLevel(lvl);
                    logEvent.SetField("LogLevel", lvl);
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

            logEvent.Tags.Add("json");
        }
        catch (JsonException)
        {
            // Not valid JSON despite passing CanParse — treat as generic text
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

    private static LogSeverity ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "trace" or "verbose" or "trc" => LogSeverity.Trace,
        "debug" or "dbg" => LogSeverity.Debug,
        "info" or "information" or "inf" or "notice" => LogSeverity.Info,
        "warn" or "warning" or "wrn" => LogSeverity.Warning,
        "error" or "err" or "fail" => LogSeverity.Error,
        "fatal" or "critical" or "crit" or "emergency" or "alert" => LogSeverity.Fatal,
        _ => LogSeverity.Info
    };
}
