using System.Globalization;
using System.Text.RegularExpressions;
using Cabinet.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cabinet.Parsers;

/// <summary>
/// Generic fallback parser for unstructured log files.
/// Pre-compiles all regex patterns for O(1) initialization cost per parse.
/// </summary>
public partial class GenericParser : BaseLogParser
{
    // Pre-compiled regex patterns for performance (avoid recompilation per log line)
    private static readonly Regex PidRegex = new(@"[\[\(](\d+)[\]\)]", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex LogLevelRegex = new(@"\b(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL|CRITICAL)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex ClassMethodRegex = new(@"\b([a-zA-Z_][\w\.]+\.[a-zA-Z_]\w+)\s*\(", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex FilePathRegex = new(@"([A-Za-z]:)?[\\/](?:[^\\/\s]+[\\/])*[^\\/\s]+\.\w+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex UserRegex = new(@"(?:@|user:|User:)\s*([a-zA-Z0-9_]+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex IpRegex = new(@"\b(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\b", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex EmailRegex = new(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex PortRegex = new(@"(?<!\d):\s*(\d{2,5})\b(?![\d:])", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex DurationRegex = new(@"(\d+(?:\.\d+)?)\s*(ms|milliseconds?|s|seconds?|m|minutes?|h|hours?)", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex SizeRegex = new(@"(\d+(?:\.\d+)?)\s*(B|KB|MB|GB|TB)", RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);
    private static readonly Regex PercentRegex = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex QuotedStringRegex = new(@"""([^""]+)""|'([^']+)'", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex KvRegex = new(@"(?<![\w.-])(?<key>[A-Za-z_][\w.-]*)=(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^\s,;""']+))", RegexOptions.Compiled, RegexTimeout);

    // Enough for any real line, and it stops a crafted line creating unbounded fields.
    private const int MaxKeyValueFields = 64;

    public GenericParser(ILogger<GenericParser>? logger = null) : base(logger) { }

    public override string Name => "Generic";
    public override string SourceType => SourceTypes.Generic;

    public override bool CanParse(string line)
    {
        // Generic parser is the fallback — it can handle any line
        return true;
    }

    protected override void ExtractSpecificFields(string line, LogEvent logEvent)
    {
        // Try to extract as much structured data as possible
        ExtractCommonPatterns(line, logEvent);
        ExtractIpAddresses(line, logEvent);
        ExtractUrls(line, logEvent);
        ExtractEmailAddresses(line, logEvent);
        ExtractNumbers(line, logEvent);
        ExtractQuotedStrings(line, logEvent);
        ExtractKeyValuePairs(line, logEvent);
        DetectGenericEvents(line, logEvent);
    }

    private void ExtractCommonPatterns(string line, LogEvent logEvent)
    {
        try
        {
            var pidMatch = PidRegex.Match(line);
            if (pidMatch.Success)
                logEvent.SetField("ProcessId", pidMatch.Groups[1].Value);

            var levelMatch = LogLevelRegex.Match(line);
            if (levelMatch.Success)
                logEvent.SetField("LogLevel", levelMatch.Groups[1].Value.ToUpper());

            var classMatch = ClassMethodRegex.Match(line);
            if (classMatch.Success)
                logEvent.SetField("Method", classMatch.Groups[1].Value);

            var pathMatch = FilePathRegex.Match(line);
            if (pathMatch.Success)
                logEvent.SetField("FilePath", pathMatch.Value);

            var userMatch = UserRegex.Match(line);
            if (userMatch.Success)
                logEvent.SetField("Username", userMatch.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { /* skip extraction on timeout */ }
    }

    private void ExtractIpAddresses(string line, LogEvent logEvent)
    {
        try
        {
            var ipMatches = IpRegex.Matches(line);
            if (ipMatches.Count > 0)
            {
                var ips = ipMatches.Select(m => m.Value).Distinct().ToList();
                logEvent.SetField("IpAddresses", ips);
                logEvent.Tags.Add("contains_ip");
            }
        }
        catch (RegexMatchTimeoutException) { }
    }

    private void ExtractUrls(string line, LogEvent logEvent)
    {
        try
        {
            var urlMatches = UrlRegex.Matches(line);
            if (urlMatches.Count > 0)
            {
                var urls = urlMatches.Select(m => m.Value.TrimEnd('.', ',', ';', ')')).Distinct().ToList();
                logEvent.SetField("Urls", urls);
                logEvent.Tags.Add("contains_url");
            }
        }
        catch (RegexMatchTimeoutException) { }
    }

    private void ExtractEmailAddresses(string line, LogEvent logEvent)
    {
        try
        {
            var emailMatches = EmailRegex.Matches(line);
            if (emailMatches.Count > 0)
            {
                var emails = emailMatches.Select(m => m.Value).Distinct().ToList();
                logEvent.SetField("EmailAddresses", emails);
                logEvent.Tags.Add("contains_email");
            }
        }
        catch (RegexMatchTimeoutException) { }
    }

    private void ExtractNumbers(string line, LogEvent logEvent)
    {
        try
        {
            var portMatch = PortRegex.Match(line);
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port) && port >= 1 && port <= 65535)
                logEvent.SetField("Port", port);

            var durationMatch = DurationRegex.Match(line);
            if (durationMatch.Success)
            {
                logEvent.SetField("Duration", durationMatch.Groups[1].Value);
                logEvent.SetField("DurationUnit", durationMatch.Groups[2].Value);
            }

            var sizeMatch = SizeRegex.Match(line);
            if (sizeMatch.Success)
            {
                logEvent.SetField("Size", sizeMatch.Groups[1].Value);
                logEvent.SetField("SizeUnit", sizeMatch.Groups[2].Value);
            }

            var percentMatch = PercentRegex.Match(line);
            if (percentMatch.Success)
                logEvent.SetField("Percentage", percentMatch.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException) { }
    }

    private void ExtractQuotedStrings(string line, LogEvent logEvent)
    {
        try
        {
            var quotedMatches = QuotedStringRegex.Matches(line);
            if (quotedMatches.Count > 0)
            {
                var quotedStrings = quotedMatches
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .Where(s => s.Length > 3)
                    .ToList();

                if (quotedStrings.Count > 0)
                    logEvent.SetField("QuotedStrings", quotedStrings);
            }
        }
        catch (RegexMatchTimeoutException) { }
    }

    private void ExtractKeyValuePairs(string line, LogEvent logEvent)
    {
        try
        {
            foreach (var match in KvRegex.Matches(line).Take(MaxKeyValueFields))
            {
                var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value
                    : match.Groups["single"].Success ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
                var key = match.Groups["key"].Value;
                if (key.Equals("host", StringComparison.OrdinalIgnoreCase) || key.Equals("hostname", StringComparison.OrdinalIgnoreCase))
                {
                    logEvent.Host = value;
                    continue;
                }

                logEvent.SetField(key, TypedValue(value));
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogDebug(ex, "Key-value extraction timed out");
        }
    }

    /// <summary>Numbers stay numbers, so <c>where status >= 500</c> and <c>sum(bytes)</c> work.</summary>
    private static object TypedValue(string value)
    {
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : value;
    }

    private void DetectGenericEvents(string line, LogEvent logEvent)
    {
        var lower = line.ToLowerInvariant();

        // Severity comes from LogSeverities, which ignores "0 errors" and the like.
        if (logEvent.Severity >= LogSeverity.Error)
        {
            logEvent.Tags.Add("error");
        }
        else if (logEvent.Severity == LogSeverity.Warning)
        {
            logEvent.Tags.Add("warning");
        }

        // Success detection
        if (lower.Contains("success") || lower.Contains("completed") || lower.Contains("finished"))
        {
            logEvent.Tags.Add("success");
        }

        // Authentication/Security
        if (lower.Contains("login") || lower.Contains("logout") || lower.Contains("auth") ||
            lower.Contains("password") || lower.Contains("token"))
        {
            logEvent.Tags.Add("authentication");
        }

        // Performance
        if (lower.Contains("slow") || lower.Contains("timeout") || lower.Contains("latency") ||
            lower.Contains("performance"))
        {
            logEvent.Tags.Add("performance");
        }

        // Database
        if (lower.Contains("sql") || lower.Contains("database") || lower.Contains("query") ||
            lower.Contains("transaction"))
        {
            logEvent.Tags.Add("database");
        }

        // Network
        if (lower.Contains("connection") || lower.Contains("disconnect") || lower.Contains("network") ||
            lower.Contains("socket"))
        {
            logEvent.Tags.Add("network");
        }

        // System
        if (lower.Contains("startup") || lower.Contains("shutdown") || lower.Contains("restart") ||
            lower.Contains("reboot"))
        {
            logEvent.Tags.Add("system");
        }

        // Debug
        if (lower.Contains("debug") || lower.Contains("trace") || lower.Contains("verbose"))
        {
            logEvent.Tags.Add("debug");
        }
    }

    private bool IsNumeric(string value)
    {
        return value.All(char.IsDigit);
    }
}