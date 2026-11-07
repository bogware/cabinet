using Cabinet.Core.Models;

namespace Cabinet.Extras.Gaming.Detection;

/// <summary>
/// Interface for detection suites
/// </summary>
public interface IDetectionSuite
{
    string Name { get; }
    string Description { get; }
    Task<DetectionReport> AnalyzeAsync(IEnumerable<LogEvent> events, DetectionOptions options, CancellationToken cancellationToken = default);
}

public class DetectionOptions
{
    public int MinSeverity { get; set; } = 3;
    public int ContextWindow { get; set; } = 5;
    public double Threshold { get; set; } = 0.8;
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

public class DetectionReport
{
    public string SuiteName { get; set; } = "";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public int EventsAnalyzed { get; set; }
    public List<Detection> Detections { get; set; } = new();
    public Dictionary<string, object> Summary { get; set; } = new();
}

public class Detection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime Timestamp { get; set; }
    public DetectionSeverity Severity { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, object> Evidence { get; set; } = new();
    public List<string> RelatedEvents { get; set; } = new();
    public double ConfidenceScore { get; set; }
}

public enum DetectionSeverity
{
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}