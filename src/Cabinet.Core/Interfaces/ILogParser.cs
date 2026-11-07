using Cabinet.Core.Models;

namespace Cabinet.Core.Interfaces;

/// <summary>
/// Interface for log parsers
/// </summary>
public interface ILogParser
{
    /// <summary>
    /// Parser name
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Source type this parser handles (e.g., SourceTypes.Generic, SourceTypes.Json)
    /// </summary>
    string SourceType { get; }

    /// <summary>
    /// Check if this parser can handle the given line
    /// </summary>
    bool CanParse(string line);

    /// <summary>
    /// Parse a log line into a LogEvent
    /// </summary>
    Task<LogEvent?> ParseAsync(string line, string? sourceFile = null, long lineNumber = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parse multiple lines efficiently
    /// </summary>
    IAsyncEnumerable<LogEvent> ParseBatchAsync(IEnumerable<string> lines, string? sourceFile = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Parser registry for managing available parsers
/// </summary>
public interface IParserRegistry
{
    /// <summary>
    /// Register a parser
    /// </summary>
    void Register(ILogParser parser);

    /// <summary>
    /// Get parser by name
    /// </summary>
    ILogParser? GetParser(string name);

    /// <summary>
    /// Get parser for source type
    /// </summary>
    ILogParser? GetParserForSourceType(string sourceType);

    /// <summary>
    /// Find best parser for a line
    /// </summary>
    ILogParser? FindParser(string line);

    /// <summary>
    /// Get all registered parsers
    /// </summary>
    IEnumerable<ILogParser> GetAll();
}