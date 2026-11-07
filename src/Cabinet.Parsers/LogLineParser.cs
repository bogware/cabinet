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
    /// <param name="cancellationToken">Cancels parsing.</param>
    public async Task<LogEvent> ParseAsync(
        string line,
        string? sourceType = null,
        DateTime? fallbackTime = null,
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
