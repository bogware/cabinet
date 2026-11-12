using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Models;
using Cabinet.API.Middleware;
using Cabinet.API.Services;

namespace Cabinet.API.Controllers;

/// <summary>
/// Data model acceleration API — manage accelerated data models and execute tstats queries.
/// </summary>
[ApiController]
[Route("api/v1/datamodels")]
[Authorize]
public class DataModelController : ControllerBase
{
    private readonly DataModelAccelerationService _accelerationService;
    private readonly ILogger<DataModelController> _logger;

    public DataModelController(
        DataModelAccelerationService accelerationService,
        ILogger<DataModelController> logger)
    {
        _accelerationService = accelerationService;
        _logger = logger;
    }

    /// <summary>List all registered data models with pagination</summary>
    [HttpGet]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(IEnumerable<DataModelResponse>), StatusCodes.Status200OK)]
    public IActionResult ListModels([FromQuery] int offset = 0, [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var allModels = _accelerationService.ListModels().ToList();
        Response.Headers["X-Total-Count"] = allModels.Count.ToString();

        var models = allModels
            .Skip(offset)
            .Take(count)
            .Select(m => new DataModelResponse
            {
                Id = m.Id,
                Name = m.Name,
                Description = m.Description,
                BaseQuery = m.BaseQuery,
                Fields = m.Fields,
                BucketSpanMinutes = (int)m.BucketSpan.TotalMinutes,
                AccelerationEnabled = m.AccelerationEnabled,
                LastRebuiltAt = m.LastRebuiltAt,
                RetentionDays = m.RetentionDays
            });

        return Ok(models);
    }

    /// <summary>Register a new data model for acceleration</summary>
    [HttpPost]
    [RequireEveryIndex(Capability.IndexManagement)]
    [RequireCapability(Capability.IndexManagement)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(typeof(DataModelResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateModel([FromBody] CreateDataModelRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Name is required", statusCode: 400);

        if (request.Name.Length > 200)
            return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);

        if (string.IsNullOrWhiteSpace(request.BaseQuery))
            return Problem(detail: "BaseQuery is required", statusCode: 400);

        if (request.Fields == null || request.Fields.Count == 0)
            return Problem(detail: "At least one field is required", statusCode: 400);

        if (request.Fields.Count > 50)
            return Problem(detail: "Maximum 50 fields allowed", statusCode: 400);

        if (request.Fields.FirstOrDefault(f => !DataModelAccelerationService.IsValidFieldName(f)) is { } badField)
            return Problem(detail: $"Field names are letters, digits, '_' and '.', starting with a letter or '_': '{badField}'", statusCode: 400);

        var model = new DataModel
        {
            Name = request.Name,
            Description = request.Description,
            BaseQuery = request.BaseQuery,
            Fields = request.Fields,
            BucketSpan = TimeSpan.FromMinutes(Math.Clamp(request.BucketSpanMinutes, 1, 1440)),
            AccelerationEnabled = request.AccelerationEnabled,
            RetentionDays = Math.Clamp(request.RetentionDays, 1, 365)
        };

        await _accelerationService.RegisterModelAsync(model, cancellationToken);

        return CreatedAtAction(nameof(GetModel), new { id = model.Id }, ToResponse(model));
    }

    /// <summary>Get a data model by ID</summary>
    [HttpGet("{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(DataModelResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetModel(string id)
    {
        var model = _accelerationService.GetModel(id);
        if (model == null)
            return Problem(detail: "Data model not found", statusCode: 404);

        return Ok(ToResponse(model));
    }

    /// <summary>Delete a data model</summary>
    [HttpDelete("{id}")]
    [RequireEveryIndex(Capability.IndexManagement)]
    [RequireCapability(Capability.IndexManagement)]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteModelAsync(string id, CancellationToken cancellationToken)
    {
        if (!await _accelerationService.DeleteModelAsync(id, cancellationToken))
            return Problem(detail: "Data model not found", statusCode: 404);

        return NoContent();
    }

    /// <summary>
    /// Execute a tstats-like query against a pre-aggregated data model.
    /// Returns results from summary tables rather than scanning raw events.
    /// </summary>
    [HttpPost("{id}/tstats")]
    [RequireEveryIndex(Capability.Search)]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(TstatsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExecuteTstats(
        string id,
        [FromBody] TstatsRequest request,
        CancellationToken cancellationToken)
    {
        var model = _accelerationService.GetModel(id);
        if (model == null)
            return Problem(detail: "Data model not found", statusCode: 404);

        var startTime = DateTime.UtcNow;
        var result = await _accelerationService.ExecuteTstatsAsync(
            id,
            request.Where,
            request.GroupBy,
            request.StartTime,
            request.EndTime,
            cancellationToken);

        return Ok(new TstatsResponse
        {
            ModelId = id,
            ModelName = model.Name,
            Results = result.Events.Cast<object>().ToList(),
            Count = result.Events.Count,
            ExecutionTimeMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds,
            LastRebuiltAt = model.LastRebuiltAt
        });
    }

    private static DataModelResponse ToResponse(DataModel m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        Description = m.Description,
        BaseQuery = m.BaseQuery,
        Fields = m.Fields,
        BucketSpanMinutes = (int)m.BucketSpan.TotalMinutes,
        AccelerationEnabled = m.AccelerationEnabled,
        LastRebuiltAt = m.LastRebuiltAt,
        RetentionDays = m.RetentionDays
    };
}

// DTOs
public class CreateDataModelRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseQuery { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = new();
    public int BucketSpanMinutes { get; set; } = 60;
    public bool AccelerationEnabled { get; set; } = true;
    public int RetentionDays { get; set; } = 90;
}

public class DataModelResponse
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string BaseQuery { get; set; } = string.Empty;
    public List<string> Fields { get; set; } = new();
    public int BucketSpanMinutes { get; set; }
    public bool AccelerationEnabled { get; set; }
    public DateTime? LastRebuiltAt { get; set; }
    public int RetentionDays { get; set; }
}

public class TstatsRequest
{
    public string? Where { get; set; }
    public List<string>? GroupBy { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public class TstatsResponse
{
    public string ModelId { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public List<object> Results { get; set; } = new();
    public int Count { get; set; }
    public int ExecutionTimeMs { get; set; }
    public DateTime? LastRebuiltAt { get; set; }
}
