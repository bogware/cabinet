// This file is now obsolete - ILogParserProvider is in Cabinet.Core.Interfaces
// and LogParserProvider is in Cabinet.Parsers

// Keeping file for backward compatibility but just redirecting to the proper location
using Cabinet.Core.Interfaces;
using Cabinet.Parsers;

namespace Cabinet.API.Services
{
    // Type alias for backward compatibility
    [Obsolete("Use Cabinet.Parsers.LogParserProvider instead")]
    public class LogParserProvider : Cabinet.Parsers.LogParserProvider
    {
        public LogParserProvider(IEnumerable<ILogParser> parsers) : base(parsers)
        {
        }
    }
}