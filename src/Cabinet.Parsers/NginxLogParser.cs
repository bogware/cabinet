using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for Nginx access logs (default combined format).
/// Default: $remote_addr - $remote_user [$time_local] "$request" $status $body_bytes_sent "$http_referer" "$http_user_agent"
/// Also handles Nginx error log format.
/// </summary>
public class NginxLogParser : BaseLogParser
{
    // Nginx access log (same as Apache Combined, but Nginx is the most common web server)
    private static readonly Regex AccessPattern = new(
        @"^(\S+)\s+-\s+(\S+)\s+\[([^\]]+)\]\s+""([^""]*)""\s+(\d{3})\s+(\d+)\s+""([^""]*)""\s+""([^""]*)""",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Nginx error log: YYYY/MM/DD HH:MM:SS [level] PID#TID: *CID message
    private static readonly Regex ErrorPattern = new(
        @"^(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s+\[(\w+)\]\s+(\d+)#(\d+):\s+(?:\*(\d+)\s+)?(.+)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public override string Name => "Nginx";
    public override string SourceType => "Nginx";

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            return AccessPattern.IsMatch(line) || ErrorPattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        logEvent.SourceType = "Nginx";

        try
        {
            // Try access log first (more common)
            var match = AccessPattern.Match(line);
            if (match.Success)
            {
                ParseAccessLog(match, logEvent);
                return;
            }

            // Try error log
            match = ErrorPattern.Match(line);
            if (match.Success)
            {
                ParseErrorLog(match, logEvent);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            logEvent.Tags.Add("parse_timeout");
        }
    }

    private static void ParseAccessLog(Match match, LogEvent logEvent)
    {
        var clientIp = match.Groups[1].Value;
        var user = match.Groups[2].Value;
        var timestampStr = match.Groups[3].Value;
        var request = match.Groups[4].Value;
        var statusCode = match.Groups[5].Value;
        var bytes = match.Groups[6].Value;
        var referer = match.Groups[7].Value;
        var userAgent = match.Groups[8].Value;

        logEvent.SetField("ClientIP", clientIp);
        logEvent.Host = clientIp;

        if (user != "-")
            logEvent.SetField("User", user);

        // Parse timestamp: 10/Oct/2000:13:55:36 +0000
        if (DateTime.TryParseExact(timestampStr, "dd/MMM/yyyy:HH:mm:ss zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            logEvent.Timestamp = timestamp;
        }

        // Parse request line
        var requestParts = request.Split(' ', 3);
        if (requestParts.Length >= 2)
        {
            logEvent.SetField("Method", requestParts[0]);
            logEvent.SetField("Uri", requestParts[1]);
            if (requestParts.Length >= 3)
                logEvent.SetField("HttpVersion", requestParts[2]);
        }

        if (int.TryParse(statusCode, out var status))
        {
            logEvent.SetField("Status", status);
            logEvent.Severity = status switch
            {
                >= 500 => LogSeverity.Error,
                >= 400 => LogSeverity.Warning,
                _ => LogSeverity.Info
            };
        }

        if (long.TryParse(bytes, out var byteCount))
            logEvent.SetField("Bytes", byteCount);

        if (referer != "-")
            logEvent.SetField("Referer", referer);
        if (userAgent != "-")
            logEvent.SetField("UserAgent", userAgent);

        logEvent.Tags.Add("access_log");
        logEvent.Tags.Add("http");
    }

    private static void ParseErrorLog(Match match, LogEvent logEvent)
    {
        var timestampStr = match.Groups[1].Value;
        var level = match.Groups[2].Value;
        var pid = match.Groups[3].Value;
        var tid = match.Groups[4].Value;
        var connectionId = match.Groups[5].Value;
        var message = match.Groups[6].Value;

        // Parse timestamp: 2024/01/15 13:55:36
        if (DateTime.TryParseExact(timestampStr, "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            logEvent.Timestamp = timestamp.ToUniversalTime();
        }

        logEvent.SetField("PID", pid);
        logEvent.SetField("TID", tid);
        if (!string.IsNullOrEmpty(connectionId))
            logEvent.SetField("ConnectionID", connectionId);
        logEvent.SetField("Message", message);

        logEvent.Severity = level.ToLowerInvariant() switch
        {
            "emerg" or "alert" or "crit" => LogSeverity.Critical,
            "error" => LogSeverity.Error,
            "warn" => LogSeverity.Warning,
            "notice" or "info" => LogSeverity.Info,
            "debug" => LogSeverity.Debug,
            _ => LogSeverity.Info
        };

        logEvent.Tags.Add("error_log");
    }
}
