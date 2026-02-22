using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Runtime field extraction configuration API.
/// Manages regex patterns applied at search time to extract fields from raw log data.
/// </summary>
[ApiController]
[Route("api/v1/field-extractions")]
[Authorize]
public class FieldExtractionsController : ControllerBase
{
    private readonly IFieldExtractionStorage _storage;

    public FieldExtractionsController(IFieldExtractionStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// List all field extraction configurations ordered by priority.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<FieldExtraction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll([FromQuery] int offset = 0, [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);
        var all = (await _storage.GetAllAsync()).ToList();
        Response.Headers["X-Total-Count"] = all.Count.ToString();
        return Ok(all.Skip(offset).Take(count).ToList());
    }

    /// <summary>
    /// Get a field extraction by ID.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(FieldExtraction), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string id)
    {
        var extraction = await _storage.GetByIdAsync(id);
        if (extraction == null) return NotFound();
        return Ok(extraction);
    }

    /// <summary>
    /// Create a new field extraction configuration.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(FieldExtraction), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> Create([FromBody] FieldExtraction extraction)
    {
        if (string.IsNullOrWhiteSpace(extraction.Name) || extraction.Name.Length > 256)
            return Problem(detail: "Name is required and must be 256 characters or less", statusCode: 400);
        if (string.IsNullOrWhiteSpace(extraction.Pattern))
            return Problem(detail: "Pattern is required", statusCode: 400);
        try { Regex.Match("", extraction.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return Problem(detail: "Invalid regex pattern", statusCode: 400); }

        extraction.Id = Guid.NewGuid().ToString();
        extraction.CreatedAt = DateTime.UtcNow;
        var saved = await _storage.SaveAsync(extraction);
        return CreatedAtAction(nameof(GetById), new { id = saved.Id }, saved);
    }

    /// <summary>
    /// Update an existing field extraction configuration.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(FieldExtraction), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> Update(string id, [FromBody] FieldExtraction extraction)
    {
        var existing = await _storage.GetByIdAsync(id);
        if (existing == null) return NotFound();
        if (string.IsNullOrWhiteSpace(extraction.Pattern))
            return Problem(detail: "Pattern is required", statusCode: 400);
        try { Regex.Match("", extraction.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return Problem(detail: "Invalid regex pattern", statusCode: 400); }

        extraction.Id = id;
        extraction.CreatedAt = existing.CreatedAt;
        var saved = await _storage.SaveAsync(extraction);
        return Ok(saved);
    }

    /// <summary>
    /// Delete a field extraction configuration.
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> Delete(string id)
    {
        var deleted = await _storage.DeleteAsync(id);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
