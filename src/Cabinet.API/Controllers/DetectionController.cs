using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Detection;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;

namespace Cabinet.API.Controllers;

/// <summary>
/// Detection and analysis API
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class DetectionController : ControllerBase
{
    private const int MaxEventsPerRequest = 10_000;

    private readonly IScumscanDetector _scumscanDetector;
    private readonly IHackwatchDetector _hackwatchDetector;
    private readonly IAdminabuseDetector _adminabuseDetector;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<DetectionController> _logger;

    public DetectionController(
        IScumscanDetector scumscanDetector,
        IHackwatchDetector hackwatchDetector,
        IAdminabuseDetector adminabuseDetector,
        IAuditLogger auditLogger,
        ILogger<DetectionController> logger)
    {
        _scumscanDetector = scumscanDetector;
        _hackwatchDetector = hackwatchDetector;
        _adminabuseDetector = adminabuseDetector;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// Run Scumscan detection for toxic behavior
    /// </summary>
    [HttpPost("scumscan")]
    [RequireCapability(Capability.Detection)]
    [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunScumscan([FromBody] DetectionRequest request, CancellationToken cancellationToken = default)
    {
        var events = request.Events ?? new List<LogEvent>();
        if (events.Count > MaxEventsPerRequest)
            return Problem(detail: $"Too many events. Maximum {MaxEventsPerRequest} per request.", statusCode: 400);

        var results = new List<DetectionResult>();

        foreach (var evt in events)
        {
            var result = await _scumscanDetector.AnalyzeAsync(evt);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return Ok(new DetectionResponse
        {
            DetectionType = "Scumscan",
            TotalAnalyzed = events.Count,
            TotalDetections = results.Count,
            Results = results,
            Summary = GenerateSummary(results)
        });
    }

    /// <summary>
    /// Run Hackwatch detection for cheating
    /// </summary>
    [HttpPost("hackwatch")]
    [RequireCapability(Capability.Detection)]
    [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunHackwatch([FromBody] DetectionRequest request, CancellationToken cancellationToken = default)
    {
        var events = request.Events ?? new List<LogEvent>();
        if (events.Count > MaxEventsPerRequest)
            return Problem(detail: $"Too many events. Maximum {MaxEventsPerRequest} per request.", statusCode: 400);

        var results = new List<DetectionResult>();

        foreach (var evt in events)
        {
            var result = await _hackwatchDetector.AnalyzeAsync(evt);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return Ok(new DetectionResponse
        {
            DetectionType = "Hackwatch",
            TotalAnalyzed = events.Count,
            TotalDetections = results.Count,
            Results = results,
            Summary = GenerateSummary(results)
        });
    }

    /// <summary>
    /// Run Adminabuse detection for admin misconduct
    /// </summary>
    [HttpPost("adminabuse")]
    [RequireCapability(Capability.Detection)]
    [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RunAdminabuse([FromBody] DetectionRequest request, CancellationToken cancellationToken = default)
    {
        var events = request.Events ?? new List<LogEvent>();
        if (events.Count > MaxEventsPerRequest)
            return Problem(detail: $"Too many events. Maximum {MaxEventsPerRequest} per request.", statusCode: 400);

        var results = new List<DetectionResult>();

        foreach (var evt in events)
        {
            var result = await _adminabuseDetector.AnalyzeAsync(evt);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return Ok(new DetectionResponse
        {
            DetectionType = "Adminabuse",
            TotalAnalyzed = events.Count,
            TotalDetections = results.Count,
            Results = results,
            Summary = GenerateSummary(results)
        });
    }

    /// <summary>
    /// Run all detection suites
    /// </summary>
    [HttpPost("analyze")]
    [RequireCapability(Capability.Detection)]
    [ProducesResponseType(typeof(ComprehensiveAnalysisResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> AnalyzeComprehensive([FromBody] DetectionRequest request, CancellationToken cancellationToken = default)
    {
        var events = request.Events ?? new List<LogEvent>();
        if (events.Count > MaxEventsPerRequest)
            return Problem(detail: $"Too many events. Maximum {MaxEventsPerRequest} per request.", statusCode: 400);

        var response = new ComprehensiveAnalysisResponse();

        // Run all detectors in parallel using async tasks (no Task.Run to avoid thread pool starvation)
        async Task<List<DetectionResult>> RunDetectorAsync<T>(T detector, Func<T, LogEvent, Task<DetectionResult?>> analyzeFunc)
        {
            var results = new List<DetectionResult>();
            foreach (var evt in events)
            {
                var result = await analyzeFunc(detector, evt);
                if (result != null) results.Add(result);
            }
            return results;
        }

        var scumscanTask = RunDetectorAsync(_scumscanDetector, (d, e) => d.AnalyzeAsync(e));
        var hackwatchTask = RunDetectorAsync(_hackwatchDetector, (d, e) => d.AnalyzeAsync(e));
        var adminabuseTask = RunDetectorAsync(_adminabuseDetector, (d, e) => d.AnalyzeAsync(e));

        await Task.WhenAll(scumscanTask, hackwatchTask, adminabuseTask);

        response.ScumscanResults = scumscanTask.Result;
        response.HackwatchResults = hackwatchTask.Result;
        response.AdminabuseResults = adminabuseTask.Result;
        response.TotalAnalyzed = events.Count;
        response.TotalDetections = response.ScumscanResults.Count +
                                   response.HackwatchResults.Count +
                                   response.AdminabuseResults.Count;

        // Generate risk assessment
        response.RiskAssessment = GenerateRiskAssessment(
            response.ScumscanResults,
            response.HackwatchResults,
            response.AdminabuseResults);

        // Audit log the detection run
        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.DetectionRun,
            ResourceType = "Detection",
            Description = $"Comprehensive analysis: {events.Count} events, {response.TotalDetections} detections, risk={response.RiskAssessment.Level}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(response);
    }

    /// <summary>
    /// Get detection statistics
    /// </summary>
    [HttpGet("stats")]
    [RequireCapability(Capability.Detection)]
    [ProducesResponseType(typeof(DetectionStatistics), StatusCodes.Status200OK)]
    public Task<IActionResult> GetStatistics(
        [FromQuery] DateTime? startTime = null,
        [FromQuery] DateTime? endTime = null)
    {
        // Return empty stats structure — detection stats are populated from
        // actual analysis runs, not hardcoded placeholder data
        var stats = new DetectionStatistics
        {
            Period = new DateRange
            {
                Start = startTime ?? DateTime.UtcNow.AddDays(-7),
                End = endTime ?? DateTime.UtcNow
            },
            TotalEventsAnalyzed = 0,
            DetectionCounts = new Dictionary<string, int>(),
            TopOffenders = new List<OffenderInfo>(),
            TrendData = new List<TrendPoint>()
        };

        return Task.FromResult<IActionResult>(Ok(stats));
    }

    private DetectionSummary GenerateSummary(List<DetectionResult> results)
    {
        return new DetectionSummary
        {
            TotalDetections = results.Count,
            SeverityBreakdown = results.GroupBy(r => r.Severity)
                .ToDictionary(g => g.Key.ToString(), g => g.Count()),
            TopPlayers = results.Where(r => r.PlayerId != null)
                .GroupBy(r => r.PlayerId)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => new PlayerSummary
                {
                    PlayerId = g.Key!,
                    PlayerName = g.First().PlayerName ?? "Unknown",
                    DetectionCount = g.Count()
                })
                .ToList()
        };
    }

    private RiskAssessment GenerateRiskAssessment(
        List<DetectionResult> scumscan,
        List<DetectionResult> hackwatch,
        List<DetectionResult> adminabuse)
    {
        var totalScore = 0;
        var factors = new List<string>();

        // Calculate risk based on detection counts and severity
        if (scumscan.Any())
        {
            totalScore += scumscan.Count * 2;
            factors.Add($"{scumscan.Count} toxic behavior incidents");
        }

        if (hackwatch.Any())
        {
            totalScore += hackwatch.Count * 5;
            factors.Add($"{hackwatch.Count} potential cheating incidents");
        }

        if (adminabuse.Any())
        {
            totalScore += adminabuse.Count * 10;
            factors.Add($"{adminabuse.Count} admin abuse incidents");
        }

        var level = totalScore switch
        {
            >= 50 => "Critical",
            >= 30 => "High",
            >= 15 => "Medium",
            >= 5 => "Low",
            _ => "Minimal"
        };

        return new RiskAssessment
        {
            Level = level,
            Score = totalScore,
            Factors = factors,
            Recommendations = GenerateRecommendations(level, scumscan, hackwatch, adminabuse)
        };
    }

    private List<string> GenerateRecommendations(
        string riskLevel,
        List<DetectionResult> scumscan,
        List<DetectionResult> hackwatch,
        List<DetectionResult> adminabuse)
    {
        var recommendations = new List<string>();

        if (riskLevel == "Critical" || riskLevel == "High")
        {
            recommendations.Add("Immediate review of flagged players recommended");
            recommendations.Add("Consider implementing stricter server rules");
        }

        if (hackwatch.Any())
        {
            recommendations.Add("Review anti-cheat measures");
            recommendations.Add("Consider implementing additional cheat detection");
        }

        if (adminabuse.Any())
        {
            recommendations.Add("Review admin permissions and guidelines");
            recommendations.Add("Implement admin action logging and review process");
        }

        if (scumscan.Count > 10)
        {
            recommendations.Add("Consider implementing chat moderation");
            recommendations.Add("Review and update community guidelines");
        }

        return recommendations;
    }
}

// DTOs
public class DetectionRequest
{
    public List<LogEvent>? Events { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public Dictionary<string, object>? Filters { get; set; }
}

public class DetectionResponse
{
    public string DetectionType { get; set; } = string.Empty;
    public int TotalAnalyzed { get; set; }
    public int TotalDetections { get; set; }
    public List<DetectionResult> Results { get; set; } = new();
    public DetectionSummary Summary { get; set; } = new();
}

public class ComprehensiveAnalysisResponse
{
    public int TotalAnalyzed { get; set; }
    public int TotalDetections { get; set; }
    public List<DetectionResult> ScumscanResults { get; set; } = new();
    public List<DetectionResult> HackwatchResults { get; set; } = new();
    public List<DetectionResult> AdminabuseResults { get; set; } = new();
    public RiskAssessment RiskAssessment { get; set; } = new();
}

public class DetectionSummary
{
    public int TotalDetections { get; set; }
    public Dictionary<string, int> SeverityBreakdown { get; set; } = new();
    public List<PlayerSummary> TopPlayers { get; set; } = new();
}

public class PlayerSummary
{
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public int DetectionCount { get; set; }
}

public class RiskAssessment
{
    public string Level { get; set; } = string.Empty;
    public int Score { get; set; }
    public List<string> Factors { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
}

public class DetectionStatistics
{
    public DateRange Period { get; set; } = new();
    public long TotalEventsAnalyzed { get; set; }
    public Dictionary<string, int> DetectionCounts { get; set; } = new();
    public List<OffenderInfo> TopOffenders { get; set; } = new();
    public List<TrendPoint> TrendData { get; set; } = new();
}

public class DateRange
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
}

public class OffenderInfo
{
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public int DetectionCount { get; set; }
}

public class TrendPoint
{
    public DateTime Timestamp { get; set; }
    public int Count { get; set; }
}