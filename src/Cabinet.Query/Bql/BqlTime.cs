using System.Globalization;
using System.Text;

namespace Cabinet.Query.Bql;

/// <summary>
/// Time conversions for BQL functions: strftime-style formats, relative time specs and epochs.
/// BQL works in UTC, whatever the host's time zone.
/// </summary>
internal static class BqlTime
{
    /// <summary>Treats a time with no zone as UTC, which is how Cabinet stores timestamps.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static long ToEpoch(DateTime value) => new DateTimeOffset(ToUtc(value)).ToUnixTimeSeconds();

    /// <summary>Converts a DateTime or epoch seconds to UTC. Returns null for anything else.</summary>
    public static DateTime? FromValue(object? value, double? epoch)
    {
        if (value is DateTime time)
        {
            return ToUtc(time);
        }

        if (!epoch.HasValue || !double.IsFinite(epoch.Value))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)epoch.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a time with strftime tokens such as <c>%Y-%m-%d</c>. A format with no <c>%</c> is
    /// a .NET format string.
    /// </summary>
    public static string? Format(DateTime value, string format)
    {
        if (!format.Contains('%'))
        {
            try
            {
                return value.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        var result = new StringBuilder(format.Length + 8);
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i == format.Length - 1)
            {
                result.Append(format[i]);
                continue;
            }

            var token = format[++i];
            if (token == '%')
            {
                result.Append('%');
            }
            else if (token == 's')
            {
                result.Append(ToEpoch(value).ToString(CultureInfo.InvariantCulture));
            }
            else if (token == 'j')
            {
                result.Append(value.DayOfYear.ToString("000", CultureInfo.InvariantCulture));
            }
            else if (DotNetFormat(token) is { } dotNet)
            {
                result.Append(value.ToString(dotNet, CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append('%').Append(token);
            }
        }

        return result.ToString();
    }

    /// <summary>Parses text using strftime tokens, reading it as UTC unless it has an offset.</summary>
    public static DateTime? Parse(string text, string? format)
    {
        const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        if (!string.IsNullOrEmpty(format)
            && ToDotNetPattern(format) is { } pattern
            && DateTime.TryParseExact(text, pattern, CultureInfo.InvariantCulture, Styles, out var exact))
        {
            return exact;
        }

        return string.IsNullOrEmpty(format)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, Styles, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Applies a spec such as <c>-1d</c>, <c>+30m</c>, <c>@d</c> or <c>-1d@d</c>, which also snaps
    /// down to the start of the given unit.
    /// </summary>
    public static DateTime? ApplyRelative(DateTime value, string spec)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            spec.Trim(),
            @"^(?:(?<amount>[+-]?\d+)(?<unit>[smhdwMy]))?(?:@(?<snap>[smhdwMy]))?$",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));
        if (!match.Success || spec.Trim().Length == 0)
        {
            return null;
        }

        var result = value;
        if (match.Groups["amount"].Success)
        {
            if (!int.TryParse(match.Groups["amount"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            try
            {
                result = match.Groups["unit"].Value switch
                {
                    "s" => result.AddSeconds(amount),
                    "m" => result.AddMinutes(amount),
                    "h" => result.AddHours(amount),
                    "d" => result.AddDays(amount),
                    "w" => result.AddDays(amount * 7.0),
                    "M" => result.AddMonths(amount),
                    _ => result.AddYears(amount),
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return match.Groups["snap"].Value switch
        {
            "s" => new DateTime(result.Year, result.Month, result.Day, result.Hour, result.Minute, result.Second, DateTimeKind.Utc),
            "m" => new DateTime(result.Year, result.Month, result.Day, result.Hour, result.Minute, 0, DateTimeKind.Utc),
            "h" => new DateTime(result.Year, result.Month, result.Day, result.Hour, 0, 0, DateTimeKind.Utc),
            "d" => result.Date,
            "w" => result.Date.AddDays(-(int)result.DayOfWeek),
            "M" => new DateTime(result.Year, result.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            "y" => new DateTime(result.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => result,
        };
    }

    private static string? DotNetFormat(char token) => token switch
    {
        'Y' => "yyyy",
        'y' => "yy",
        'm' => "MM",
        'd' => "dd",
        'H' => "HH",
        'I' => "hh",
        'M' => "mm",
        'S' => "ss",
        'p' => "tt",
        'a' => "ddd",
        'A' => "dddd",
        'b' => "MMM",
        'B' => "MMMM",
        'F' => "yyyy-MM-dd",
        'T' => "HH:mm:ss",
        'z' => "zzz",
        'Z' => "'UTC'",
        _ => null,
    };

    private static string? ToDotNetPattern(string format)
    {
        var result = new StringBuilder(format.Length + 8);
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i < format.Length - 1)
            {
                var token = format[++i];
                if (token == '%')
                {
                    result.Append("'%'");
                }
                else if (DotNetFormat(token) is { } dotNet)
                {
                    result.Append(dotNet);
                }
                else
                {
                    return null;
                }
            }
            else
            {
                result.Append('\'').Append(format[i] == '\'' ? "''" : format[i].ToString()).Append('\'');
            }
        }

        return result.ToString();
    }
}
