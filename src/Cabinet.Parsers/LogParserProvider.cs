using Cabinet.Core.Interfaces;

namespace Cabinet.Parsers;

/// <summary>
/// Default implementation of log parser provider
/// </summary>
public class LogParserProvider : ILogParserProvider
{
    private readonly List<ILogParser> _parsers = new();

    public LogParserProvider(IEnumerable<ILogParser> parsers)
    {
        _parsers.AddRange(parsers);
    }

    public ILogParser? GetParser(string logLine)
    {
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(logLine))
            {
                return parser;
            }
        }
        return null;
    }

    public void RegisterParser(ILogParser parser)
    {
        _parsers.Add(parser);
    }
}