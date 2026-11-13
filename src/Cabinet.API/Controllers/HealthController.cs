using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Reflection;

namespace Cabinet.API.Controllers;

/// <summary>
/// Health and status API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    // Monotonic: the wall clock can step back, which made uptime briefly negative
    private static readonly Stopwatch Running = Stopwatch.StartNew();
    private static long _totalRequests;
    private static long _totalErrors;

    private readonly HealthCheckService _healthCheckService;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        HealthCheckService healthCheckService,
        ILogger<HealthController> logger)
    {
        _healthCheckService = healthCheckService;
        _logger = logger;
    }

    /// <summary>
    /// Increment request counter (called from middleware)
    /// </summary>
    internal static void RecordRequest(bool isError = false)
    {
        Interlocked.Increment(ref _totalRequests);
        if (isError)
            Interlocked.Increment(ref _totalErrors);
    }

    /// <summary>
    /// Get basic health status
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetHealth()
    {
        var report = await _healthCheckService.CheckHealthAsync();

        var response = new HealthResponse
        {
            Status = report.Status.ToString(),
            TotalDuration = report.TotalDuration.TotalMilliseconds,
            Checks = report.Entries.Select(e => new HealthCheckResult
            {
                Name = e.Key,
                Status = e.Value.Status.ToString(),
                Duration = e.Value.Duration.TotalMilliseconds,
                Description = e.Value.Description
            }).ToList()
        };

        return report.Status == HealthStatus.Healthy
            ? Ok(response)
            : StatusCode(503, response);
    }

    /// <summary>
    /// Get liveness probe (minimal check)
    /// </summary>
    [HttpGet("live")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetLiveness()
    {
        return Ok(new { status = "alive", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get readiness probe
    /// </summary>
    [HttpGet("ready")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetReadiness()
    {
        var report = await _healthCheckService.CheckHealthAsync(
            predicate: check => check.Tags.Contains("ready"));

        return report.Status == HealthStatus.Healthy
            ? Ok(new { status = "ready", timestamp = DateTime.UtcNow })
            : StatusCode(503, new { status = "not ready", timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Get API version (safe — no host/OS/runtime disclosure)
    /// </summary>
    [HttpGet("version")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(VersionInfo), StatusCodes.Status200OK)]
    public IActionResult GetVersion()
    {
        // As released, such as 3.0.0 or 3.0.0-rc.1, not the assembly's four-part number
        return Ok(new VersionInfo
        {
            Version = Cabinet.API.Services.Updates.ReleaseVersions.CurrentFull,
            ApiVersion = "v1"
        });
    }

    /// <summary>
    /// Get real API metrics — requires authentication.
    /// </summary>
    [HttpGet("metrics")]
    [Authorize]
    [ProducesResponseType(typeof(ApiMetrics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetMetrics()
    {
        var process = Process.GetCurrentProcess();
        var uptime = Running.Elapsed;
        var totalRequests = Interlocked.Read(ref _totalRequests);
        var totalErrors = Interlocked.Read(ref _totalErrors);

        var metrics = new ApiMetrics
        {
            Timestamp = DateTime.UtcNow,
            Uptime = uptime,
            RequestsTotal = totalRequests,
            RequestsPerSecond = uptime.TotalSeconds > 0 ? totalRequests / uptime.TotalSeconds : 0,
            ErrorRate = totalRequests > 0 ? (double)totalErrors / totalRequests : 0,
            MemoryUsageMB = process.WorkingSet64 / (1024.0 * 1024.0),
            ThreadCount = process.Threads.Count,
            GcGen0Collections = GC.CollectionCount(0),
            GcGen1Collections = GC.CollectionCount(1),
            GcGen2Collections = GC.CollectionCount(2),
            GcTotalMemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0)
        };

        return Ok(metrics);
    }

    /// <summary>
    /// Get API status and dependencies
    /// </summary>
    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(typeof(ApiStatus), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus()
    {
        var report = await _healthCheckService.CheckHealthAsync();

        var services = new Dictionary<string, ServiceStatus>();
        foreach (var entry in report.Entries)
        {
            services[entry.Key] = new ServiceStatus
            {
                Status = entry.Value.Status.ToString(),
                Message = entry.Value.Description
            };
        }

        var status = new ApiStatus
        {
            Status = report.Status.ToString(),
            Timestamp = DateTime.UtcNow,
            Services = services
        };

        return Ok(status);
    }
}

// DTOs
public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public double TotalDuration { get; set; }
    public List<HealthCheckResult> Checks { get; set; } = new();
}

public class HealthCheckResult
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double Duration { get; set; }
    public string? Description { get; set; }
}

public class VersionInfo
{
    public string Version { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
}

public class ApiMetrics
{
    public DateTime Timestamp { get; set; }
    public TimeSpan Uptime { get; set; }
    public long RequestsTotal { get; set; }
    public double RequestsPerSecond { get; set; }
    public double ErrorRate { get; set; }
    public double MemoryUsageMB { get; set; }
    public int ThreadCount { get; set; }
    public int GcGen0Collections { get; set; }
    public int GcGen1Collections { get; set; }
    public int GcGen2Collections { get; set; }
    public double GcTotalMemoryMB { get; set; }
}

public class ApiStatus
{
    public string Status { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public Dictionary<string, ServiceStatus> Services { get; set; } = new();
}

public class ServiceStatus
{
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
}
