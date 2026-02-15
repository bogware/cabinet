using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Per-index retention policy management API.
/// </summary>
[ApiController]
[Route("api/v1/retention")]
[Authorize]
public class RetentionController : ControllerBase
{
    private static readonly Dictionary<string, RetentionPolicyDto> _policies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// List all retention policies.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<RetentionPolicyDto>), StatusCodes.Status200OK)]
    public IActionResult GetPolicies()
    {
        lock (_lock)
        {
            return Ok(_policies.Values.ToList());
        }
    }

    /// <summary>
    /// Get retention policy for a specific index.
    /// </summary>
    [HttpGet("{index}")]
    [ProducesResponseType(typeof(RetentionPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetPolicy(string index)
    {
        lock (_lock)
        {
            if (_policies.TryGetValue(index, out var policy))
                return Ok(policy);
            return NotFound();
        }
    }

    /// <summary>
    /// Set retention policy for an index.
    /// </summary>
    [HttpPut("{index}")]
    [ProducesResponseType(typeof(RetentionPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public IActionResult SetPolicy(string index, [FromBody] RetentionPolicyDto policy)
    {
        if (string.IsNullOrWhiteSpace(index) || index.Length > 256)
            return Problem(detail: "Index name must be 1-256 characters", statusCode: 400);
        if (policy.MaxAgeDays < 1 || policy.MaxAgeDays > 3650)
            return Problem(detail: "MaxAgeDays must be between 1 and 3650", statusCode: 400);

        policy.Index = index;
        lock (_lock)
        {
            _policies[index] = policy;
        }
        return Ok(policy);
    }

    /// <summary>
    /// Delete retention policy for an index.
    /// </summary>
    [HttpDelete("{index}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public IActionResult DeletePolicy(string index)
    {
        lock (_lock)
        {
            if (!_policies.Remove(index))
                return NotFound();
            return NoContent();
        }
    }
}

/// <summary>
/// Retention policy configuration for a specific index.
/// </summary>
public class RetentionPolicyDto
{
    public string Index { get; set; } = string.Empty;
    public int MaxAgeDays { get; set; } = 90;
    public bool ArchiveBeforeDelete { get; set; } = true;
    public long? MaxSizeBytes { get; set; }
}
