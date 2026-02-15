using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;
using Cabinet.Query;

namespace Cabinet.API.Controllers;

/// <summary>
/// Async search jobs API for long-running queries
/// </summary>
[ApiController]
[Route("api/v1/query/jobs")]
[Authorize]
public class SearchJobController : ControllerBase
{
    private readonly ISearchJobStorage _jobStorage;
    private readonly IQueryParser _queryParser;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<SearchJobController> _logger;

    public SearchJobController(
        ISearchJobStorage jobStorage,
        IQueryParser queryParser,
        IAuditLogger auditLogger,
        ILogger<SearchJobController> logger)
    {
        _jobStorage = jobStorage;
        _queryParser = queryParser;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    /// <summary>
    /// Submit a new async search job
    /// </summary>
    [HttpPost]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(SearchJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateJob([FromBody] CreateSearchJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return Problem(detail: "Query is required", statusCode: 400);

        if (request.Query.Length > 10_000)
            return Problem(detail: "Query too long. Maximum length is 10,000 characters.", statusCode: 400);

        // Validate BQL syntax before queuing
        try
        {
            _queryParser.Parse(request.Query);
        }
        catch (QueryParseException ex)
        {
            return Problem(detail: $"Invalid query syntax: {ex.Message}", statusCode: 400);
        }

        var tenantIndex = HttpContext.GetTenantIndexPrefix();

        var job = new SearchJob
        {
            Query = request.Query,
            Owner = User.FindFirst("ClientId")?.Value ?? "unknown",
            Index = tenantIndex,
            Limit = Math.Clamp(request.MaxResults, 1, 100_000),
            Ttl = TimeSpan.FromMinutes(Math.Clamp(request.TtlMinutes, 1, 1440))
        };

        await _jobStorage.CreateAsync(job, cancellationToken);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = job.Owner,
            Action = AuditAction.Create,
            ResourceType = "SearchJob",
            ResourceId = job.Id,
            Description = $"Created async search job: {request.Query[..Math.Min(200, request.Query.Length)]}",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        _logger.LogInformation("Search job {JobId} queued by {Owner}", job.Id, job.Owner);

        return AcceptedAtAction(nameof(GetJob), new { id = job.Id }, ToResponse(job));
    }

    /// <summary>
    /// Get search job status
    /// </summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(SearchJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJob(string id, CancellationToken cancellationToken)
    {
        var job = await _jobStorage.GetByIdAsync(id, cancellationToken);
        if (job == null)
            return Problem(detail: "Search job not found", statusCode: 404);

        return Ok(ToResponse(job));
    }

    /// <summary>
    /// Get search job results
    /// </summary>
    [HttpGet("{id}/results")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(SearchJobResultsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobResults(
        string id,
        [FromQuery] int offset = 0,
        [FromQuery] int count = 1000,
        CancellationToken cancellationToken = default)
    {
        var job = await _jobStorage.GetByIdAsync(id, cancellationToken);
        if (job == null)
            return Problem(detail: "Search job not found", statusCode: 404);

        if (job.Status != SearchJobStatus.Completed)
            return Problem(detail: $"Job is not complete (status: {job.Status})", statusCode: 409);

        count = Math.Clamp(count, 1, 10_000);
        offset = Math.Max(0, offset);

        // Deserialize results with pagination
        List<object>? allResults = null;
        if (!string.IsNullOrEmpty(job.ResultsJson))
        {
            allResults = JsonSerializer.Deserialize<List<object>>(job.ResultsJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        var results = allResults?.Skip(offset).Take(count).ToList() ?? new List<object>();
        Response.Headers["X-Total-Count"] = (allResults?.Count ?? 0).ToString();

        return Ok(new SearchJobResultsResponse
        {
            JobId = job.Id,
            TotalResults = job.ResultCount,
            Offset = offset,
            Count = results.Count,
            Results = results,
            EventsScanned = job.EventsScanned,
            ExecutionTimeMs = (int)(job.ExecutionTime?.TotalMilliseconds ?? 0)
        });
    }

    /// <summary>
    /// List all search jobs
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(IEnumerable<SearchJobResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListJobs(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100,
        CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var owner = User.FindFirst("ClientId")?.Value;
        var jobs = await _jobStorage.GetAllAsync(owner, cancellationToken);

        Response.Headers["X-Total-Count"] = jobs.Count.ToString();
        var page = jobs.Skip(offset).Take(count).Select(ToResponse);

        return Ok(page);
    }

    /// <summary>
    /// Cancel a running search job
    /// </summary>
    [HttpPost("{id}/cancel")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(SearchJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelJob(string id, CancellationToken cancellationToken)
    {
        var job = await _jobStorage.GetByIdAsync(id, cancellationToken);
        if (job == null)
            return Problem(detail: "Search job not found", statusCode: 404);

        if (job.Status is SearchJobStatus.Completed or SearchJobStatus.Failed or SearchJobStatus.Cancelled)
            return Problem(detail: $"Cannot cancel job in {job.Status} state", statusCode: 409);

        job.Status = SearchJobStatus.Cancelled;
        job.CompletedAt = DateTime.UtcNow;
        job.Error = "Cancelled by user";
        await _jobStorage.UpdateAsync(job, cancellationToken);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "SearchJob",
            ResourceId = id,
            Description = "Cancelled search job",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(ToResponse(job));
    }

    /// <summary>
    /// Delete a search job and its results
    /// </summary>
    [HttpDelete("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteJob(string id, CancellationToken cancellationToken)
    {
        var deleted = await _jobStorage.DeleteAsync(id, cancellationToken);
        if (!deleted)
            return Problem(detail: "Search job not found", statusCode: 404);

        return NoContent();
    }

    private static SearchJobResponse ToResponse(SearchJob job) => new()
    {
        Id = job.Id,
        Query = job.Query,
        Status = job.Status.ToString(),
        Progress = job.Progress,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.CompletedAt,
        ResultCount = job.ResultCount,
        EventsScanned = job.EventsScanned,
        ExecutionTimeMs = (int)(job.ExecutionTime?.TotalMilliseconds ?? 0),
        Error = job.Error
    };
}

// DTOs
public class CreateSearchJobRequest
{
    public string Query { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 50_000;
    public int TtlMinutes { get; set; } = 30;
}

public class SearchJobResponse
{
    public string Id { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Progress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int ResultCount { get; set; }
    public long EventsScanned { get; set; }
    public int ExecutionTimeMs { get; set; }
    public string? Error { get; set; }
}

public class SearchJobResultsResponse
{
    public string JobId { get; set; } = string.Empty;
    public int TotalResults { get; set; }
    public int Offset { get; set; }
    public int Count { get; set; }
    public List<object> Results { get; set; } = new();
    public long EventsScanned { get; set; }
    public int ExecutionTimeMs { get; set; }
}
