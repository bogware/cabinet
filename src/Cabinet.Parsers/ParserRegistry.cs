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
    private readonly object _orderLock = new();
    private volatile ILogParser[] _ordered = [];

    public ParserRegistry()
    {
        _parsers = new ConcurrentDictionary<string, ILogParser>(StringComparer.OrdinalIgnoreCase);
        _parsersBySourceType = new ConcurrentDictionary<string, ILogParser>(StringComparer.OrdinalIgnoreCase);
    }

    public void Register(ILogParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);

        lock (_orderLock)
        {
            var position = Array.FindIndex(_ordered, existing => existing.Name == parser.Name);
            var updated = position >= 0 ? (ILogParser[])_ordered.Clone() : [.. _ordered, parser];
            updated[position >= 0 ? position : updated.Length - 1] = parser;
            _ordered = updated;
        }

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
        // Registration order decides, as it does for ILogParserProvider
        foreach (var parser in _ordered)
        {
            if (parser.CanParse(line))
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