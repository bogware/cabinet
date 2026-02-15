using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.Parsers;

/// <summary>
/// Parser for Apache HTTP Server access logs (Combined Log Format and Common Log Format).
/// Combined: %h %l %u %t \"%r\" %&gt;s %b \"%{Referer}i\" \"%{User-Agent}i\"
/// Common:   %h %l %u %t \"%r\" %&gt;s %b
/// </summary>
public class ApacheLogParser : BaseLogParser
{
    // Combined Log Format:  host ident user [time] "request" status bytes "referer" "useragent"
    private static readonly Regex CombinedPattern = new(
        @"^(\S+)\s+(\S+)\s+(\S+)\s+\[([^\]]+)\]\s+""([^""]*)""\s+(\d{3})\s+(\S+)\s+""([^""]*)""\s+""([^""]*)""",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Common Log Format: host ident user [time] "request" status bytes
    private static readonly Regex CommonPattern = new(
        @"^(\S+)\s+(\S+)\s+(\S+)\s+\[([^\]]+)\]\s+""([^""]*)""\s+(\d{3})\s+(\S+)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public override string Name => "Apache";
    public override string SourceType => "Apache";

    public override bool CanParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            // Quick heuristic: contains bracketed date and quoted request
            return line.Contains('[') && line.Contains(']') &&
                   line.Contains('"') &&
                   (CombinedPattern.IsMatch(line) || CommonPattern.IsMatch(line));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        logEvent.SourceType = "Apache";

        try
        {
            var match = CombinedPattern.Match(line);
            var isCombined = match.Success;
            if (!isCombined)
                match = CommonPattern.Match(line);

            if (!match.Success) return;

            var clientIp = match.Groups[1].Value;
            var user = match.Groups[3].Value;
            var timestampStr = match.Groups[4].Value;
            var request = match.Groups[5].Value;
            var statusCode = match.Groups[6].Value;
            var bytes = match.Groups[7].Value;

            logEvent.SetField("ClientIP", clientIp);
            logEvent.Host = clientIp;

            if (user != "-")
                logEvent.SetField("User", user);

            // Parse Apache timestamp: 10/Oct/2000:13:55:36 -0700
            if (DateTime.TryParseExact(timestampStr, "dd/MMM/yyyy:HH:mm:ss zzz",
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                logEvent.Timestamp = timestamp;
            }

            // Parse request line: "GET /path HTTP/1.1"
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

            if (bytes != "-" && long.TryParse(bytes, out var byteCount))
                logEvent.SetField("Bytes", byteCount);

            if (isCombined)
            {
                var referer = match.Groups[8].Value;
                var userAgent = match.Groups[9].Value;

                if (referer != "-")
                    logEvent.SetField("Referer", referer);
                if (userAgent != "-")
                    logEvent.SetField("UserAgent", userAgent);
            }

            logEvent.Tags.Add("access_log");
            logEvent.Tags.Add("http");
        }
        catch (RegexMatchTimeoutException)
        {
            logEvent.Tags.Add("parse_timeout");
        }
    }
}
