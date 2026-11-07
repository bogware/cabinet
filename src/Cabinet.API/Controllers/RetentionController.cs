using Cabinet.API.Middleware;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Retention for individual indexes, applied hourly. An index without a policy follows
/// <c>Storage:RetentionDays</c>.
/// </summary>
[ApiController]
[Route("api/v1/retention")]
[Authorize]
public class RetentionController : ControllerBase
{
    private readonly IDocumentStore _documents;

    public RetentionController(IDocumentStore documents)
    {
        _documents = documents;
    }

    /// <summary>
    /// List all retention policies with pagination.
    /// </summary>
    [HttpGet]
    [RequireCapability(Capability.SystemHealth)]
    [ProducesResponseType(typeof(List<RetentionPolicyDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPolicies([FromQuery] int offset = 0, [FromQuery] int count = 100, CancellationToken cancellationToken = default)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var all = await _documents.ListAsync<RetentionPolicyDto>(RetentionService.PoliciesCollection, cancellationToken);
        Response.Headers["X-Total-Count"] = all.Count.ToString();
        return Ok(all.Skip(offset).Take(count).ToList());
    }

    /// <summary>
    /// Get retention policy for a specific index.
    /// </summary>
    [HttpGet("{index}")]
    [RequireCapability(Capability.SystemHealth)]
    [ProducesResponseType(typeof(RetentionPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPolicy(string index, CancellationToken cancellationToken = default)
    {
        var policy = await _documents.GetAsync<RetentionPolicyDto>(RetentionService.PoliciesCollection, index, cancellationToken);
        return policy == null ? NotFound() : Ok(policy);
    }

    /// <summary>
    /// Set retention policy for an index.
    /// </summary>
    [HttpPut("{index}")]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(typeof(RetentionPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetPolicy(string index, [FromBody] RetentionPolicyDto policy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(index) || index.Length > 256)
            return Problem(detail: "Index name must be 1-256 characters", statusCode: 400);
        if (policy.MaxAgeDays < 1 || policy.MaxAgeDays > 3650)
            return Problem(detail: "MaxAgeDays must be between 1 and 3650", statusCode: 400);

        policy.Index = index;
        await _documents.PutAsync(RetentionService.PoliciesCollection, index, policy, cancellationToken);
        return Ok(policy);
    }

    /// <summary>
    /// Delete retention policy for an index.
    /// </summary>
    [HttpDelete("{index}")]
    [RequireCapability(Capability.IndexManagement)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeletePolicy(string index, CancellationToken cancellationToken = default)
    {
        return await _documents.DeleteAsync(RetentionService.PoliciesCollection, index, cancellationToken) ? NoContent() : NotFound();
    }
}
