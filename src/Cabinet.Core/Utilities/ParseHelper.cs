using System;

namespace Cabinet.Core.Utilities;

/// <summary>
/// Helper methods for safe parsing of values
/// </summary>
public static class ParseHelper
{
    /// <summary>
    /// Safely parse integer with default value
    /// </summary>
    public static int ParseInt(string? value, int defaultValue = 0)
    {
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Safely parse nullable integer
    /// </summary>
    public static int? ParseIntNullable(string? value)
    {
        return int.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Safely parse double with default value
    /// </summary>
    public static double ParseDouble(string? value, double defaultValue = 0.0)
    {
        return double.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Safely parse nullable double
    /// </summary>
    public static double? ParseDoubleNullable(string? value)
    {
        return double.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Safely parse long with default value
    /// </summary>
    public static long ParseLong(string? value, long defaultValue = 0)
    {
        return long.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Safely parse nullable long
    /// </summary>
    public static long? ParseLongNullable(string? value)
    {
        return long.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Safely parse decimal with default value
    /// </summary>
    public static decimal ParseDecimal(string? value, decimal defaultValue = 0m)
    {
        return decimal.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Safely parse DateTime with default value
    /// </summary>
    public static DateTime ParseDateTime(string? value, DateTime defaultValue)
    {
        return DateTime.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Safely parse nullable DateTime
    /// </summary>
    public static DateTime? ParseDateTimeNullable(string? value)
    {
        return DateTime.TryParse(value, out var result) ? result : null;
    }

    /// <summary>
    /// Safely parse boolean with default value
    /// </summary>
    public static bool ParseBool(string? value, bool defaultValue = false)
    {
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// Parse time range string (e.g., "5m", "2h", "7d") to TimeSpan
    /// </summary>
    public static TimeSpan? ParseTimeRange(string? timeRange)
    {
        if (string.IsNullOrWhiteSpace(timeRange))
            return null;

        var lower = timeRange.ToLower().Trim();

        if (lower.EndsWith("ms"))
        {
            if (int.TryParse(lower[..^2], out var ms))
                return TimeSpan.FromMilliseconds(ms);
        }
        else if (lower.EndsWith("s"))
        {
            if (int.TryParse(lower[..^1], out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        else if (lower.EndsWith("m"))
        {
            if (int.TryParse(lower[..^1], out var minutes))
                return TimeSpan.FromMinutes(minutes);
        }
        else if (lower.EndsWith("h"))
        {
            if (int.TryParse(lower[..^1], out var hours))
                return TimeSpan.FromHours(hours);
        }
        else if (lower.EndsWith("d"))
        {
            if (int.TryParse(lower[..^1], out var days))
                return TimeSpan.FromDays(days);
        }
        else if (lower.EndsWith("w"))
        {
            if (int.TryParse(lower[..^1], out var weeks))
                return TimeSpan.FromDays(weeks * 7);
        }

        return null;
    }

    /// <summary>
    /// Parse memory size string (e.g., "128MB", "2GB") to bytes
    /// </summary>
    public static long? ParseMemorySize(string? memorySize)
    {
        if (string.IsNullOrWhiteSpace(memorySize))
            return null;

        var lower = memorySize.ToLower().Trim();

        if (lower.EndsWith("kb"))
        {
            if (long.TryParse(lower[..^2], out var kb))
                return kb * 1024;
        }
        else if (lower.EndsWith("mb"))
        {
            if (long.TryParse(lower[..^2], out var mb))
                return mb * 1024 * 1024;
        }
        else if (lower.EndsWith("gb"))
        {
            if (long.TryParse(lower[..^2], out var gb))
                return gb * 1024 * 1024 * 1024;
        }
        else if (lower.EndsWith("tb"))
        {
            if (long.TryParse(lower[..^2], out var tb))
                return tb * 1024L * 1024 * 1024 * 1024;
        }
        else if (long.TryParse(lower, out var bytes))
        {
            return bytes;
        }

        return null;
    }
}