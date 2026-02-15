using System.Text;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for ArcSight Common Event Format (CEF) log lines.
/// CEF format: CEF:Version|Device Vendor|Device Product|Device Version|Signature ID|Name|Severity|Extension
/// Supports both standalone CEF lines and syslog-prefixed CEF (common in SIEM pipelines).
/// </summary>
public sealed class CefParser : BaseLogParser
{
    public CefParser(ILogger<CefParser>? logger = null) : base(logger) { }

    public override string Name => "CEF";
    public override string SourceType => SourceTypes.Cef;

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains("CEF:0|") || line.Contains("CEF:1|");
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        var cefIndex = line.IndexOf("CEF:", StringComparison.Ordinal);
        if (cefIndex < 0) return;

        // Extract syslog header if CEF is embedded in a syslog message
        if (cefIndex > 0)
        {
            logEvent.SetField("SyslogHeader", line[..cefIndex].Trim());
        }

        var cefPart = line[cefIndex..];
        var headerFields = SplitCefHeader(cefPart);
        if (headerFields.Count < 7) return;

        logEvent.SetField("CEFVersion", headerFields[0].Replace("CEF:", ""));
        logEvent.SetField("DeviceVendor", headerFields[1]);
        logEvent.SetField("DeviceProduct", headerFields[2]);
        logEvent.SetField("DeviceVersion", headerFields[3]);
        logEvent.SetField("SignatureId", headerFields[4]);
        logEvent.SetField("EventName", headerFields[5]);
        logEvent.SetField("CEFSeverity", headerFields[6]);

        logEvent.Severity = MapCefSeverity(headerFields[6]);

        // Parse extension key-value pairs (everything after the 7th pipe delimiter)
        if (headerFields.Count > 7 && !string.IsNullOrWhiteSpace(headerFields[7]))
        {
            ParseExtensions(headerFields[7], logEvent);
        }

        logEvent.Tags.Add("cef");
    }

    /// <summary>
    /// Split CEF header into up to 8 fields (7 pipe-delimited header fields + extension).
    /// Handles escaped pipes (\|) within header fields.
    /// </summary>
    private static List<string> SplitCefHeader(string cef)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var pipeCount = 0;

        for (var i = 0; i < cef.Length; i++)
        {
            var c = cef[i];

            if (c == '|' && pipeCount < 7)
            {
                if (i > 0 && cef[i - 1] == '\\')
                {
                    current.Length--;
                    current.Append('|');
                }
                else
                {
                    fields.Add(current.ToString());
                    current.Clear();
                    pipeCount++;

                    if (pipeCount == 7)
                    {
                        // Everything after the 7th pipe is the extension
                        fields.Add(cef[(i + 1)..]);
                        return fields;
                    }
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            fields.Add(current.ToString());
        }

        return fields;
    }

    /// <summary>
    /// Parse CEF extension key-value pairs.
    /// Keys are alphanumeric, values extend until the next "key=" boundary.
    /// </summary>
    private static void ParseExtensions(string extensions, LogEvent logEvent)
    {
        var pos = 0;
        var span = extensions.AsSpan();

        while (pos < span.Length)
        {
            // Skip whitespace
            while (pos < span.Length && span[pos] == ' ') pos++;
            if (pos >= span.Length) break;

            // Find key (up to '=')
            var eqPos = span[pos..].IndexOf('=');
            if (eqPos < 0) break;

            var key = span.Slice(pos, eqPos).ToString().Trim();
            pos += eqPos + 1;

            // Find value end (next unquoted key= pattern)
            var valueEnd = FindValueEnd(span, pos);
            var value = span[pos..valueEnd].ToString().Trim();
            pos = valueEnd;

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            {
                logEvent.SetField(key, value);
                MapWellKnownExtension(key, value, logEvent);
            }
        }
    }

    private static int FindValueEnd(ReadOnlySpan<char> span, int start)
    {
        for (var i = start; i < span.Length; i++)
        {
            if (span[i] == ' ' && i + 1 < span.Length)
            {
                // Look ahead for "key=" pattern
                var rest = span[(i + 1)..];
                var nextEq = rest.IndexOf('=');
                if (nextEq > 0 && nextEq < 64)
                {
                    var potentialKey = rest[..nextEq];
                    var isKey = true;
                    foreach (var c in potentialKey)
                    {
                        if (!char.IsLetterOrDigit(c))
                        {
                            isKey = false;
                            break;
                        }
                    }
                    if (isKey) return i;
                }
            }
        }
        return span.Length;
    }

    private static void MapWellKnownExtension(string key, string value, LogEvent logEvent)
    {
        switch (key)
        {
            case "src":
                logEvent.SetField("SourceAddress", value);
                break;
            case "dst":
                logEvent.SetField("DestinationAddress", value);
                break;
            case "shost":
                logEvent.Host ??= value;
                break;
            case "dhost":
                logEvent.SetField("DestinationHost", value);
                break;
            case "suser":
                logEvent.SetField("SourceUser", value);
                break;
            case "duser":
                logEvent.SetField("DestinationUser", value);
                break;
            case "msg":
                logEvent.SetField("Message", value);
                break;
            case "rt" or "end" or "start":
                if (long.TryParse(value, out var epoch))
                {
                    try
                    {
                        logEvent.SetField($"{key}_time", DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime);
                    }
                    catch (ArgumentOutOfRangeException) { /* invalid epoch, skip */ }
                }
                break;
        }
    }

    private static LogSeverity MapCefSeverity(string severity)
    {
        if (int.TryParse(severity, out var level))
        {
            return level switch
            {
                <= 3 => LogSeverity.Info,
                <= 6 => LogSeverity.Warning,
                <= 8 => LogSeverity.Error,
                _ => LogSeverity.Critical
            };
        }

        return severity.Trim().ToLowerInvariant() switch
        {
            "low" => LogSeverity.Info,
            "medium" => LogSeverity.Warning,
            "high" => LogSeverity.Error,
            "very-high" or "critical" => LogSeverity.Critical,
            _ => LogSeverity.Info
        };
    }
}
