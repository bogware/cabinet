using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Parses syslog lines: RFC 5424, RFC 3164, and the header-only form that files such as
/// /var/log/syslog contain.
/// </summary>
public sealed class SyslogParser : BaseLogParser
{
    private static readonly string[] FacilityNames =
    [
        "kern", "user", "mail", "daemon", "auth", "syslog", "lpr", "news",
        "uucp", "cron", "authpriv", "ftp", "ntp", "audit", "alert", "clock",
        "local0", "local1", "local2", "local3", "local4", "local5", "local6", "local7",
    ];

    // <34>1 2026-01-01T10:00:00.000Z host app 1234 ID47 [sd@1 a="b"] message
    private static readonly Regex Rfc5424 = new(
        @"^<(?<pri>\d{1,3})>(?<version>[1-9]\d?) (?<time>\S+) (?<host>\S+) (?<app>\S+) (?<procid>\S+) (?<msgid>\S+) (?<sd>-|(?:\[(?:[^\]\\]|\\.)*\])+)(?: (?<msg>.*))?$",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    // <34>Oct 11 22:14:15 host su[123]: message
    private static readonly Regex Rfc3164 = new(
        @"^(?:<(?<pri>\d{1,3})>)?(?<time>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ \d]\d \d{2}:\d{2}:\d{2}) (?<host>\S+) (?:(?<app>[^\s:\[]+)(?:\[(?<procid>[^\]]+)\])?: ?)?(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    // <34>message, with no recognizable header
    private static readonly Regex PriorityOnly = new(
        @"^<(?<pri>\d{1,3})>(?<msg>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline, RegexTimeout);

    private static readonly Regex StructuredElement = new(
        @"\[(?<id>[^\s\]=""]+)(?<params>(?: [^\s\]=""]+=""(?:[^""\\\]]|\\.)*"")*)\]",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex StructuredParam = new(
        @" (?<name>[^\s\]=""]+)=""(?<value>(?:[^""\\\]]|\\.)*)""",
        RegexOptions.Compiled, RegexTimeout);

    public SyslogParser(ILogger<SyslogParser>? logger = null)
        : base(logger)
    {
    }

    public override string Name => "Syslog";

    public override string SourceType => SourceTypes.Syslog;

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            return line[0] == '<' ? PriorityOnly.IsMatch(line) : Rfc3164.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    protected override void ExtractTimestamp(string line, LogEvent logEvent)
    {
        // Set from the header in ExtractSpecificFields. Without one, use the text.
        logEvent.Timestamp = LogTimestamps.TryFind(line, out var timestamp) ? timestamp : DateTime.UtcNow;
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        var rfc5424 = Rfc5424.Match(line);
        if (rfc5424.Success)
        {
            ApplyRfc5424(rfc5424, logEvent);
            return;
        }

        var rfc3164 = Rfc3164.Match(line);
        if (rfc3164.Success)
        {
            ApplyRfc3164(rfc3164, logEvent);
            return;
        }

        var priorityOnly = PriorityOnly.Match(line);
        if (priorityOnly.Success)
        {
            ApplyMessage(priorityOnly.Groups["msg"].Value, logEvent);
            ApplyPriority(priorityOnly.Groups["pri"].Value, logEvent);
        }
    }

    private static void ApplyRfc5424(Match match, LogEvent logEvent)
    {
        if (LogTimestamps.TryParseValue(match.Groups["time"].Value, out var timestamp))
        {
            logEvent.Timestamp = timestamp;
        }

        SetUnlessNil(match.Groups["host"].Value, value => logEvent.Host = value);
        SetUnlessNil(match.Groups["app"].Value, value => logEvent.SetField("app", value));
        SetUnlessNil(match.Groups["procid"].Value, value => logEvent.SetField("pid", value));
        SetUnlessNil(match.Groups["msgid"].Value, value => logEvent.SetField("msgid", value));
        ApplyStructuredData(match.Groups["sd"].Value, logEvent);
        ApplyMessage(match.Groups["msg"].Value, logEvent);
        ApplyPriority(match.Groups["pri"].Value, logEvent);
    }

    private static void ApplyRfc3164(Match match, LogEvent logEvent)
    {
        if (LogTimestamps.TryParseSyslogTime(match.Groups["time"].Value, DateTime.UtcNow, out var timestamp))
        {
            logEvent.Timestamp = timestamp;
        }

        logEvent.Host = match.Groups["host"].Value;
        if (match.Groups["app"].Success)
        {
            logEvent.SetField("app", match.Groups["app"].Value);
        }

        if (match.Groups["procid"].Success)
        {
            logEvent.SetField("pid", match.Groups["procid"].Value);
        }

        ApplyMessage(match.Groups["msg"].Value, logEvent);
        ApplyPriority(match.Groups["pri"].Value, logEvent);
    }

    private static void ApplyMessage(string message, LogEvent logEvent)
    {
        // A BOM may precede the message in RFC 5424.
        var text = message.TrimStart('\uFEFF');
        logEvent.SetField("Message", text);

        // The priority wins if there is one, but a file's lines often have none.
        logEvent.Severity = LogSeverities.Detect(text);
    }

    private static void ApplyPriority(string priorityText, LogEvent logEvent)
    {
        if (!int.TryParse(priorityText, NumberStyles.None, CultureInfo.InvariantCulture, out var priority) || priority > 191)
        {
            return;
        }

        var facility = priority / 8;
        var severity = priority % 8;
        logEvent.SetField("facility", facility);
        logEvent.SetField("facility_name", FacilityNames[facility]);
        logEvent.SetField("syslog_severity", severity);

        if (LogSeverities.TryParseNumeric(severity, out var parsed))
        {
            logEvent.Severity = parsed;
        }
    }

    private static void ApplyStructuredData(string data, LogEvent logEvent)
    {
        if (data == "-")
        {
            return;
        }

        foreach (Match element in StructuredElement.Matches(data))
        {
            var id = element.Groups["id"].Value;
            foreach (Match param in StructuredParam.Matches(element.Groups["params"].Value))
            {
                var value = param.Groups["value"].Value
                    .Replace("\\\"", "\"", StringComparison.Ordinal)
                    .Replace("\\]", "]", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal);
                logEvent.SetField($"{id}.{param.Groups["name"].Value}", value);
            }
        }
    }

    private static void SetUnlessNil(string value, Action<string> set)
    {
        if (value != "-")
        {
            set(value);
        }
    }
}
