using Cabinet.Core.Models;

namespace Cabinet.Detection;

/// <summary>
/// Result of a detection analysis
/// </summary>
public class DetectionResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DetectionType { get; set; } = string.Empty;
    public string? PlayerId { get; set; }
    public string? PlayerName { get; set; }
    public string? Server { get; set; }
    public DetectionSeverity Severity { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Evidence { get; set; }
    public double ConfidenceScore { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public LogEvent? SourceEvent { get; set; }
}