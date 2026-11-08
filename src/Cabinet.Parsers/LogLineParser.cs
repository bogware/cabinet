using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Parsers;

/// <summary>
/// Turns a raw line into an event. Every ingest route goes through this, so a line gets the same
/// fields, severity and timestamp however it arrived.
/// </summary>
public sealed class LogLineParser
{
    private readonly IParserRegistry _registry;
    private readonly ILogParserProvider _provider;

    public LogLineParser(IParserRegistry registry, ILogParserProvider provider)
    {
        _registry = registry;
        _provider = provider;
    }

    /// <summary>
    /// Parses a line. A source type naming a known parser selects it, as long as it accepts the
    /// line. Anything else, including a label like <c>app</c>, is detected from the line.
    /// </summary>
    /// <param name="line">The raw line.</param>
    /// <param name="sourceType">The sender's source type, if any.</param>
    /// <param name="fallbackTime">Used when the line has no timestamp of its own.</param>
    /// <param name="timeZone">
    /// The sender's time zone, for lines whose time has no offset, such as game servers' logs.
    /// Without it, those times are read as UTC.
    /// </param>
    /// <param name="cancellationToken">Cancels parsing.</param>
    public async Task<LogEvent> ParseAsync(
        string line,
        string? sourceType = null,
        DateTime? fallbackTime = null,
        TimeZoneInfo? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        var parser = SelectParser(line, sourceType);

        // Parsers default to the current time, so a time from before parsing came from the line.
        var startedAt = DateTime.UtcNow;
        var parsed = parser == null ? null : await parser.ParseAsync(line, null, 0, cancellationToken);

        var logEvent = parsed ?? new LogEvent
        {
            RawLine = line,
            SourceType = SourceTypes.Generic,
            Timestamp = startedAt,
        };

        logEvent.Timestamp = ToUtc(logEvent.Timestamp, timeZone);
        logEvent.IndexTime = startedAt;
        if (fallbackTime is { } time && logEvent.Timestamp >= startedAt)
        {
            logEvent.Timestamp = time;
        }

        // A sender's own label is what they'll search by.
        if (!string.IsNullOrWhiteSpace(sourceType) && !sourceType.Equals(SourceTypes.Generic, StringComparison.OrdinalIgnoreCase))
        {
            logEvent.SourceType = sourceType;
        }

        return logEvent;
    }

    /// <summary>A time with no zone, read in the sender's zone, or as UTC.</summary>
    public static DateTime ToUtc(DateTime time, TimeZoneInfo? timeZone)
    {
        if (time.Kind != DateTimeKind.Unspecified)
        {
            return time.ToUniversalTime();
        }

        if (timeZone == null)
        {
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        // An hour skipped when clocks go forward can't be converted; it can only be a slow clock
        if (timeZone.IsInvalidTime(time))
        {
            time = time.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(time, timeZone);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A time zone by IANA or Windows name, or null if it isn't known. Bounded, since senders
    /// supply the names.
    /// </summary>
    public static TimeZoneInfo? FindTimeZone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return null;
        }

        if (Zones.TryGetValue(name, out var cached))
        {
            return cached;
        }

        TimeZoneInfo? zone = null;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
        }

        if (Zones.Count < 1000)
        {
            Zones[name] = zone;
        }

        return zone;
    }

    private ILogParser? SelectParser(string line, string? sourceType)
    {
        if (!string.IsNullOrWhiteSpace(sourceType)
            && !sourceType.Equals(SourceTypes.Generic, StringComparison.OrdinalIgnoreCase)
            && _registry.GetParserForSourceType(sourceType) is { } named
            && named.CanParse(line))
        {
            return named;
        }

        return _provider.GetParser(line);
    }
}
