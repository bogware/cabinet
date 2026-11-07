using System.Globalization;
using System.Text.RegularExpressions;

namespace Cabinet.Parsers;

/// <summary>
/// Finds the timestamp in a log line. Times without an offset are read as UTC, so the same line
/// gives the same instant on every host.
/// </summary>
public static class LogTimestamps
{
    private const DateTimeStyles Utc = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // A timestamp is near the start of a line. Don't scan megabytes of message for one.
    private const int SearchLength = 256;

    private static readonly Regex Iso8601 = new(
        @"(?<!\d)\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d{1,9})?(?:\s?(?:Z|[+-]\d{2}(?::?\d{2})?))?(?!\d)",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex CommonLog = new(
        @"(?<!\d)\d{2}/[A-Z][a-z]{2}/\d{4}:\d{2}:\d{2}:\d{2}(?: [+-]\d{4})?",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex SlashedYearFirst = new(
        @"(?<!\d)\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex SlashedYearLast = new(
        @"(?<!\d)\d{2}/\d{2}/\d{4}[ ,]+\d{2}:\d{2}:\d{2}",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex Syslog = new(
        @"\b(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) [ \d]\d \d{2}:\d{2}:\d{2}\b",
        RegexOptions.Compiled, RegexTimeout);

    /// <summary>Finds a timestamp in the line, as UTC.</summary>
    public static bool TryFind(string line, out DateTime timestamp) =>
        TryFind(line, DateTime.UtcNow, out timestamp);

    /// <summary>
    /// Finds a timestamp in the line, as UTC. <paramref name="now"/> supplies the year for formats
    /// that leave it out.
    /// </summary>
    public static bool TryFind(string line, DateTime now, out DateTime timestamp)
    {
        var text = line.Length > SearchLength ? line[..SearchLength] : line;

        try
        {
            return TryIso8601(text, out timestamp)
                || TryExact(CommonLog, text, ["dd/MMM/yyyy:HH:mm:ss zzz", "dd/MMM/yyyy:HH:mm:ss"], out timestamp)
                || TryExact(SlashedYearFirst, text, ["yyyy/MM/dd HH:mm:ss"], out timestamp)
                || TryExact(SlashedYearLast, text, ["MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy, HH:mm:ss"], out timestamp)
                || TrySyslog(text, now, out timestamp);
        }
        catch (RegexMatchTimeoutException)
        {
            timestamp = default;
            return false;
        }
    }

    /// <summary>
    /// Reads a time from a structured value: an ISO 8601 string, or an epoch in seconds,
    /// milliseconds, microseconds or nanoseconds.
    /// </summary>
    public static bool TryParseValue(object? value, out DateTime timestamp)
    {
        timestamp = default;
        switch (value)
        {
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return TryFromEpoch(number, out timestamp);
            case string text:
                return DateTime.TryParse(text, CultureInfo.InvariantCulture, Utc, out timestamp);
            case long number:
                return TryFromEpoch(number, out timestamp);
            case double number:
                return TryFromEpoch(number, out timestamp);
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads a syslog time, which has no year. Assumes the current one, unless that would put it
    /// in the future: a December line read in January is from last year.
    /// </summary>
    public static bool TryParseSyslogTime(string text, DateTime now, out DateTime timestamp)
    {
        var normalized = Regex.Replace(text.Trim(), @"\s+", " ", RegexOptions.None, RegexTimeout);
        var withYear = $"{now.Year} {normalized}";

        // Parsing with a year also accepts Feb 29 in leap years only.
        if (!DateTime.TryParseExact(withYear, "yyyy MMM d HH:mm:ss", CultureInfo.InvariantCulture, Utc, out timestamp))
        {
            return DateTime.TryParseExact($"{now.Year - 1} {normalized}", "yyyy MMM d HH:mm:ss", CultureInfo.InvariantCulture, Utc, out timestamp);
        }

        if (timestamp > now.AddDays(1))
        {
            timestamp = timestamp.AddYears(-1);
        }

        return true;
    }

    private static bool TryIso8601(string text, out DateTime timestamp)
    {
        timestamp = default;
        var match = Iso8601.Match(text);
        if (!match.Success)
        {
            return false;
        }

        // log4j writes a comma before milliseconds, and some loggers put a space before the offset.
        var value = match.Value.Replace(',', '.');
        value = Regex.Replace(value, @"\s(Z|[+-]\d{2}(?::?\d{2})?)$", "$1", RegexOptions.None, RegexTimeout);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, Utc, out timestamp);
    }

    private static bool TryExact(Regex pattern, string text, string[] formats, out DateTime timestamp)
    {
        timestamp = default;
        var match = pattern.Match(text);
        return match.Success
            && DateTime.TryParseExact(match.Value, formats, CultureInfo.InvariantCulture, Utc, out timestamp);
    }

    private static bool TrySyslog(string text, DateTime now, out DateTime timestamp)
    {
        timestamp = default;
        var match = Syslog.Match(text);
        return match.Success && TryParseSyslogTime(match.Value, now, out timestamp);
    }

    private static bool TryFromEpoch(double value, out DateTime timestamp)
    {
        timestamp = default;
        if (!double.IsFinite(value) || value <= 0)
        {
            return false;
        }

        // Tell the unit from the size: seconds are about 1e9 until the year 2286.
        var seconds = value switch
        {
            < 1e11 => value,
            < 1e14 => value / 1e3,
            < 1e17 => value / 1e6,
            _ => value / 1e9,
        };

        try
        {
            timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds).UtcDateTime;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
