using System.Text.RegularExpressions;
using Cabinet.Core.Models;

namespace Cabinet.Parsers;

/// <summary>
/// Works out how severe a line is. Alert rules count on this, so a line that merely mentions an
/// error, like "0 errors" or "error_count=0", must not count as one.
/// </summary>
public static class LogSeverities
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Severity words are near the start of a line, and scanning all of a huge line is wasteful.
    private const int SearchLength = 2048;

    // level=warn, "severity": "error", lvl:debug
    private static readonly Regex DeclaredLevel = new(
        @"(?:^|[\s{,""'\[(])(?:level|lvl|severity|loglevel|log_level)""?'?\s*[=:]\s*""?'?(?<level>[a-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // ERROR, [WARN], <INFO>, E: only in upper case, which is how loggers write levels
    private static readonly Regex UpperCaseLevel = new(
        @"(?<![\w-])(?<level>TRACE|TRC|DEBUG|DBG|INFO|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|FATAL|FTL|CRITICAL|CRIT|PANIC|EMERG|EMERGENCY|ALERT)(?![\w-])",
        RegexOptions.Compiled, RegexTimeout);

    // Prose: "connection failed", "disk error: full", "Unhandled exception"
    private static readonly Regex ProseLevel = new(
        @"(?<![\w./\-])(?<word>fatal|panic|critical|errors?|exception|failed|failure|warnings?)(?![\w/\-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // "0 errors", "no errors", "without error", "not failed", "zero failures"
    private static readonly Regex NegatedBefore = new(
        @"(?:^|[\s(,])(?:0|no|zero|without|not|non|never|none)\s+(?:\w+\s+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    // "errors=0", "failed: 0", "error_count = 0", "errors: none", "warnings 0"
    private static readonly Regex NegatedAfter = new(
        @"^(?:_\w+)?\s*[=:]?\s*(?:0|none|false|no|null)(?![\w.])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexTimeout);

    /// <summary>
    /// Works out a line's severity: a declared level first, then an upper-case level word, then
    /// words in the text. Lines with none of these are <see cref="LogSeverity.Info"/>.
    /// </summary>
    public static LogSeverity Detect(string line)
    {
        var text = line.Length > SearchLength ? line[..SearchLength] : line;

        try
        {
            var declared = DeclaredLevel.Match(text);
            if (declared.Success && TryParse(declared.Groups["level"].Value, out var fromDeclared))
            {
                return fromDeclared;
            }

            var upper = UpperCaseLevel.Match(text);
            if (upper.Success && TryParse(upper.Groups["level"].Value, out var fromUpper))
            {
                return fromUpper;
            }

            var highest = LogSeverity.Info;
            foreach (Match word in ProseLevel.Matches(text))
            {
                if (IsNegated(text, word))
                {
                    continue;
                }

                var severity = FromProse(word.Groups["word"].Value);
                if (severity > highest)
                {
                    highest = severity;
                }
            }

            return highest;
        }
        catch (RegexMatchTimeoutException)
        {
            return LogSeverity.Info;
        }
    }

    /// <summary>Reads a level name such as <c>warn</c>, <c>ERROR</c> or <c>information</c>.</summary>
    public static bool TryParse(string level, out LogSeverity severity)
    {
        switch (level.Trim().ToLowerInvariant())
        {
            case "trace" or "trc" or "verbose" or "vrb":
                severity = LogSeverity.Trace;
                return true;
            case "debug" or "dbg":
                severity = LogSeverity.Debug;
                return true;
            case "info" or "inf" or "information" or "informational" or "notice":
                severity = LogSeverity.Info;
                return true;
            case "warn" or "warning" or "wrn":
                severity = LogSeverity.Warning;
                return true;
            case "error" or "err" or "erro" or "fail" or "failure":
                severity = LogSeverity.Error;
                return true;
            case "critical" or "crit":
                severity = LogSeverity.Critical;
                return true;
            case "fatal" or "ftl" or "panic" or "emerg" or "emergency" or "alert":
                severity = LogSeverity.Fatal;
                return true;
            default:
                severity = LogSeverity.Info;
                return false;
        }
    }

    /// <summary>
    /// Reads a numeric level: pino and bunyan use 10 to 60, syslog uses 0 (emergency) to 7 (debug).
    /// </summary>
    public static bool TryParseNumeric(double level, out LogSeverity severity)
    {
        severity = level switch
        {
            0 or 1 => LogSeverity.Fatal,
            2 => LogSeverity.Critical,
            3 => LogSeverity.Error,
            4 => LogSeverity.Warning,
            5 or 6 => LogSeverity.Info,
            7 => LogSeverity.Debug,
            10 => LogSeverity.Trace,
            20 => LogSeverity.Debug,
            30 => LogSeverity.Info,
            40 => LogSeverity.Warning,
            50 => LogSeverity.Error,
            60 => LogSeverity.Fatal,
            _ => LogSeverity.Info,
        };

        return level is (>= 0 and <= 7) or 10 or 20 or 30 or 40 or 50 or 60;
    }

    private static bool IsNegated(string text, Match word)
    {
        var start = Math.Max(0, word.Index - 40);
        var end = word.Index + word.Length;
        return NegatedBefore.IsMatch(text[start..word.Index])
            || NegatedAfter.IsMatch(text[end..Math.Min(text.Length, end + 40)]);
    }

    private static LogSeverity FromProse(string word) => word.ToLowerInvariant() switch
    {
        "fatal" or "panic" => LogSeverity.Fatal,
        "critical" => LogSeverity.Critical,
        "warning" or "warnings" => LogSeverity.Warning,
        _ => LogSeverity.Error,
    };
}
