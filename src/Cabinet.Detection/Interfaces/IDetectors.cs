using Cabinet.Core.Models;

namespace Cabinet.Detection;

/// <summary>
/// Interface for Scumscan detection
/// </summary>
public interface IScumscanDetector
{
    Task<DetectionResult?> AnalyzeAsync(LogEvent logEvent);
    Task<List<DetectionResult>> AnalyzeBatchAsync(IEnumerable<LogEvent> events);
}

/// <summary>
/// Interface for Hackwatch detection
/// </summary>
public interface IHackwatchDetector
{
    Task<DetectionResult?> AnalyzeAsync(LogEvent logEvent);
    Task<List<DetectionResult>> AnalyzeBatchAsync(IEnumerable<LogEvent> events);
}

/// <summary>
/// Interface for Adminabuse detection
/// </summary>
public interface IAdminabuseDetector
{
    Task<DetectionResult?> AnalyzeAsync(LogEvent logEvent);
    Task<List<DetectionResult>> AnalyzeBatchAsync(IEnumerable<LogEvent> events);
}