using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.Parsers;

/// <summary>
/// Registry for managing log parsers
/// </summary>
public class ParserRegistry : IParserRegistry
{
    private readonly ConcurrentDictionary<string, ILogParser> _parsers;
    private readonly ConcurrentDictionary<string, ILogParser> _parsersBySourceType;

    public ParserRegistry()
    {
        _parsers = new ConcurrentDictionary<string, ILogParser>(StringComparer.OrdinalIgnoreCase);
        _parsersBySourceType = new ConcurrentDictionary<string, ILogParser>(StringComparer.OrdinalIgnoreCase);
    }

    public void Register(ILogParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);

        _parsers[parser.Name] = parser;
        _parsersBySourceType[parser.SourceType] = parser;
    }

    public ILogParser? GetParser(string name)
    {
        return _parsers.TryGetValue(name, out var parser) ? parser : null;
    }

    public ILogParser? GetParserForSourceType(string sourceType)
    {
        return _parsersBySourceType.TryGetValue(sourceType, out var parser) ? parser : null;
    }

    public ILogParser? FindParser(string line)
    {
        // Try parsers in priority order (specific before generic)
        var priorityOrder = new[]
        {
            SourceTypes.L4D2,
            SourceTypes.SourceMod,
            SourceTypes.SteamCMD,
            SourceTypes.Cef,
            SourceTypes.Json,
            SourceTypes.Csv,
            SourceTypes.Generic
        };

        foreach (var sourceType in priorityOrder)
        {
            if (_parsersBySourceType.TryGetValue(sourceType, out var parser) && parser.CanParse(line))
            {
                return parser;
            }
        }

        // Fallback to generic parser
        return GetParserForSourceType(SourceTypes.Generic);
    }

    public IEnumerable<ILogParser> GetAll()
    {
        return _parsers.Values;
    }
}