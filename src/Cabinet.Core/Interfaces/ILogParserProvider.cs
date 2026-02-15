namespace Cabinet.Core.Interfaces;

/// <summary>
/// Provider for selecting appropriate log parser
/// </summary>
public interface ILogParserProvider
{
    /// <summary>
    /// Get appropriate parser for the given log line
    /// </summary>
    ILogParser? GetParser(string logLine);

    /// <summary>
    /// Register a parser
    /// </summary>
    void RegisterParser(ILogParser parser);
}