using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.API.Middleware;
using System.Text.RegularExpressions;

namespace Cabinet.API.Controllers;

/// <summary>
/// Knowledge objects management API (lookups, macros, field aliases)
/// </summary>
[ApiController]
[Route("api/v1/knowledge")]
[Authorize]
public class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeStorage _storage;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(IKnowledgeStorage storage, IAuditLogger auditLogger, ILogger<KnowledgeController> logger)
    {
        _storage = storage;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    // ===== Lookup Tables =====

    /// <summary>
    /// List all lookup tables
    /// </summary>
    [HttpGet("lookups")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<LookupTable>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListLookups(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var lookups = await _storage.GetAllLookupsAsync();
        var total = lookups.Count();
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(lookups.Skip(offset).Take(count));
    }

    /// <summary>
    /// Get a lookup table by ID
    /// </summary>
    [HttpGet("lookups/{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(LookupTable), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLookup(string id)
    {
        var lookup = await _storage.GetLookupByIdAsync(id);
        if (lookup == null)
            return Problem(detail: "Lookup table not found", statusCode: 404);

        return Ok(lookup);
    }

    /// <summary>
    /// Create a lookup table
    /// </summary>
    [HttpPost("lookups")]
    [ProducesResponseType(typeof(LookupTable), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> CreateLookup([FromBody] CreateLookupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Name is required", statusCode: 400);

        if (request.Name.Length > 200)
            return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);

        if (request.Rows is { Count: > 10_000 })
            return Problem(detail: "Maximum 10,000 rows per lookup table", statusCode: 400);

        if (request.Columns is { Count: > 100 })
            return Problem(detail: "Maximum 100 columns per lookup table", statusCode: 400);

        var lookup = new LookupTable
        {
            Name = request.Name,
            Description = request.Description,
            Columns = request.Columns ?? new List<string>(),
            Rows = request.Rows ?? new List<Dictionary<string, string>>()
        };

        await _storage.SaveLookupAsync(lookup);
        _logger.LogInformation("Created lookup table '{Name}' (id: {Id})", lookup.Name, lookup.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "LookupTable",
            ResourceId = lookup.Id,
            Description = $"Created lookup table '{lookup.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetLookup), new { id = lookup.Id }, lookup);
    }

    /// <summary>
    /// Update a lookup table
    /// </summary>
    [HttpPut("lookups/{id}")]
    [ProducesResponseType(typeof(LookupTable), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> UpdateLookup(string id, [FromBody] UpdateLookupRequest request)
    {
        var lookup = await _storage.GetLookupByIdAsync(id);
        if (lookup == null)
            return Problem(detail: "Lookup table not found", statusCode: 404);

        if (request.Name != null)
        {
            if (request.Name.Length > 200)
                return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);
            lookup.Name = request.Name;
        }
        if (request.Description != null)
            lookup.Description = request.Description;
        if (request.Columns != null)
        {
            if (request.Columns.Count > 100)
                return Problem(detail: "Maximum 100 columns per lookup table", statusCode: 400);
            lookup.Columns = request.Columns;
        }
        if (request.Rows != null)
        {
            if (request.Rows.Count > 10_000)
                return Problem(detail: "Maximum 10,000 rows per lookup table", statusCode: 400);
            lookup.Rows = request.Rows;
        }

        lookup.UpdatedAt = DateTime.UtcNow;
        await _storage.SaveLookupAsync(lookup);
        _logger.LogInformation("Updated lookup table '{Name}' (id: {Id})", lookup.Name, id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "LookupTable",
            ResourceId = id,
            Description = $"Updated lookup table '{lookup.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(lookup);
    }

    /// <summary>
    /// Delete a lookup table
    /// </summary>
    [HttpDelete("lookups/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteLookup(string id)
    {
        var deleted = await _storage.DeleteLookupAsync(id);
        if (!deleted)
            return Problem(detail: "Lookup table not found", statusCode: 404);

        _logger.LogInformation("Deleted lookup table (id: {Id})", id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "LookupTable",
            ResourceId = id,
            Description = $"Deleted lookup table",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    // ===== Query Macros =====

    /// <summary>
    /// List all query macros
    /// </summary>
    [HttpGet("macros")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<QueryMacro>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMacros(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var macros = await _storage.GetAllMacrosAsync();
        var total = macros.Count();
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(macros.Skip(offset).Take(count));
    }

    /// <summary>
    /// Get a query macro by ID
    /// </summary>
    [HttpGet("macros/{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(QueryMacro), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMacro(string id)
    {
        var macro = await _storage.GetMacroByIdAsync(id);
        if (macro == null)
            return Problem(detail: "Query macro not found", statusCode: 404);

        return Ok(macro);
    }

    /// <summary>
    /// Create a query macro
    /// </summary>
    [HttpPost("macros")]
    [ProducesResponseType(typeof(QueryMacro), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> CreateMacro([FromBody] CreateMacroRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem(detail: "Name is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.Definition))
            return Problem(detail: "Definition is required", statusCode: 400);

        if (request.Name.Length > 200)
            return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);

        // Validate that ValidationRegex compiles if provided
        if (!string.IsNullOrWhiteSpace(request.ValidationRegex))
        {
            try
            {
                _ = new Regex(request.ValidationRegex, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return Problem(detail: $"Invalid ValidationRegex pattern: {ex.Message}", statusCode: 400);
            }
        }

        var macro = new QueryMacro
        {
            Name = request.Name,
            Description = request.Description,
            Definition = request.Definition,
            Parameters = request.Parameters ?? new List<string>(),
            ValidationRegex = request.ValidationRegex
        };

        await _storage.SaveMacroAsync(macro);
        _logger.LogInformation("Created query macro '{Name}' (id: {Id})", macro.Name, macro.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "QueryMacro",
            ResourceId = macro.Id,
            Description = $"Created query macro '{macro.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetMacro), new { id = macro.Id }, macro);
    }

    /// <summary>
    /// Update a query macro
    /// </summary>
    [HttpPut("macros/{id}")]
    [ProducesResponseType(typeof(QueryMacro), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> UpdateMacro(string id, [FromBody] UpdateMacroRequest request)
    {
        var macro = await _storage.GetMacroByIdAsync(id);
        if (macro == null)
            return Problem(detail: "Query macro not found", statusCode: 404);

        if (request.Name != null)
        {
            if (request.Name.Length > 200)
                return Problem(detail: "Name must be 200 characters or fewer", statusCode: 400);
            macro.Name = request.Name;
        }
        if (request.Description != null)
            macro.Description = request.Description;
        if (request.Definition != null)
            macro.Definition = request.Definition;
        if (request.Parameters != null)
            macro.Parameters = request.Parameters;
        if (request.ValidationRegex != null)
        {
            if (!string.IsNullOrWhiteSpace(request.ValidationRegex))
            {
                try
                {
                    _ = new Regex(request.ValidationRegex, RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    return Problem(detail: $"Invalid ValidationRegex pattern: {ex.Message}", statusCode: 400);
                }
            }
            macro.ValidationRegex = request.ValidationRegex;
        }

        await _storage.SaveMacroAsync(macro);
        _logger.LogInformation("Updated query macro '{Name}' (id: {Id})", macro.Name, id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "QueryMacro",
            ResourceId = id,
            Description = $"Updated query macro '{macro.Name}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(macro);
    }

    /// <summary>
    /// Delete a query macro
    /// </summary>
    [HttpDelete("macros/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteMacro(string id)
    {
        var deleted = await _storage.DeleteMacroAsync(id);
        if (!deleted)
            return Problem(detail: "Query macro not found", statusCode: 404);

        _logger.LogInformation("Deleted query macro (id: {Id})", id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "QueryMacro",
            ResourceId = id,
            Description = $"Deleted query macro",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    // ===== Field Aliases =====

    /// <summary>
    /// List all field aliases
    /// </summary>
    [HttpGet("aliases")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(List<FieldAlias>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAliases(
        [FromQuery] int offset = 0,
        [FromQuery] int count = 100)
    {
        count = Math.Clamp(count, 1, 1000);
        offset = Math.Max(0, offset);

        var aliases = await _storage.GetAllAliasesAsync();
        var total = aliases.Count();
        Response.Headers["X-Total-Count"] = total.ToString();
        return Ok(aliases.Skip(offset).Take(count));
    }

    /// <summary>
    /// Create a field alias
    /// </summary>
    [HttpPost("aliases")]
    [ProducesResponseType(typeof(FieldAlias), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> CreateAlias([FromBody] CreateAliasRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalField))
            return Problem(detail: "OriginalField is required", statusCode: 400);
        if (string.IsNullOrWhiteSpace(request.AliasField))
            return Problem(detail: "AliasField is required", statusCode: 400);

        // Prevent self-referential aliases (circular reference)
        if (string.Equals(request.OriginalField, request.AliasField, StringComparison.OrdinalIgnoreCase))
            return Problem(detail: "OriginalField and AliasField cannot be the same (circular reference)", statusCode: 400);

        var alias = new FieldAlias
        {
            SourceType = request.SourceType ?? "*",
            OriginalField = request.OriginalField,
            AliasField = request.AliasField
        };

        await _storage.SaveAliasAsync(alias);
        _logger.LogInformation("Created field alias '{Original}' -> '{Alias}' (id: {Id})",
            alias.OriginalField, alias.AliasField, alias.Id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "FieldAlias",
            ResourceId = alias.Id,
            Description = $"Created field alias '{alias.OriginalField}' -> '{alias.AliasField}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return CreatedAtAction(nameof(GetAlias), new { id = alias.Id }, alias);
    }

    /// <summary>
    /// Get a field alias by ID
    /// </summary>
    [HttpGet("aliases/{id}")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(typeof(FieldAlias), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAlias(string id)
    {
        var alias = await _storage.GetAliasByIdAsync(id);
        if (alias == null)
            return Problem(detail: "Field alias not found", statusCode: 404);

        return Ok(alias);
    }

    /// <summary>
    /// Update a field alias
    /// </summary>
    [HttpPut("aliases/{id}")]
    [ProducesResponseType(typeof(FieldAlias), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> UpdateAlias(string id, [FromBody] UpdateAliasRequest request)
    {
        var alias = await _storage.GetAliasByIdAsync(id);
        if (alias == null)
            return Problem(detail: "Field alias not found", statusCode: 404);

        if (request.OriginalField != null)
            alias.OriginalField = request.OriginalField;
        if (request.AliasField != null)
            alias.AliasField = request.AliasField;
        if (request.SourceType != null)
            alias.SourceType = request.SourceType;

        // Prevent self-referential aliases (circular reference)
        if (string.Equals(alias.OriginalField, alias.AliasField, StringComparison.OrdinalIgnoreCase))
            return Problem(detail: "OriginalField and AliasField cannot be the same (circular reference)", statusCode: 400);

        await _storage.SaveAliasAsync(alias);
        _logger.LogInformation("Updated field alias '{Original}' -> '{Alias}' (id: {Id})",
            alias.OriginalField, alias.AliasField, id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Update,
            ResourceType = "FieldAlias",
            ResourceId = id,
            Description = $"Updated field alias '{alias.OriginalField}' -> '{alias.AliasField}'",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(alias);
    }

    /// <summary>
    /// Delete a field alias
    /// </summary>
    [HttpDelete("aliases/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Authorize(Policy = "RequireWritePermission")]
    public async Task<IActionResult> DeleteAlias(string id)
    {
        var deleted = await _storage.DeleteAliasAsync(id);
        if (!deleted)
            return Problem(detail: "Field alias not found", statusCode: 404);

        _logger.LogInformation("Deleted field alias (id: {Id})", id);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Delete,
            ResourceType = "FieldAlias",
            ResourceId = id,
            Description = $"Deleted field alias",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return NoContent();
    }

    // ===== Import / Export =====

    /// <summary>
    /// Export all knowledge objects (lookups, macros, field aliases).
    /// </summary>
    [HttpGet("export")]
    [RequireCapability(Capability.Search)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportAll()
    {
        var lookups = await _storage.GetAllLookupsAsync();
        var macros = await _storage.GetAllMacrosAsync();
        var aliases = await _storage.GetAllAliasesAsync();

        var export = new
        {
            Version = "1.0",
            ExportedAt = DateTime.UtcNow,
            Lookups = lookups,
            Macros = macros,
            FieldAliases = aliases
        };

        return Ok(export);
    }

    /// <summary>
    /// Import knowledge objects (lookups, macros, field aliases).
    /// Imported objects receive new IDs to prevent collisions.
    /// </summary>
    [HttpPost("import")]
    [Authorize(Policy = "RequireWritePermission")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportAll([FromBody] KnowledgeImportDto import)
    {
        int lookupCount = 0, macroCount = 0, aliasCount = 0;

        if (import.Lookups != null)
        {
            foreach (var lookup in import.Lookups)
            {
                lookup.Id = Guid.NewGuid().ToString();
                await _storage.SaveLookupAsync(lookup);
                lookupCount++;
            }
        }
        if (import.Macros != null)
        {
            foreach (var macro in import.Macros)
            {
                macro.Id = Guid.NewGuid().ToString();
                await _storage.SaveMacroAsync(macro);
                macroCount++;
            }
        }
        if (import.FieldAliases != null)
        {
            foreach (var alias in import.FieldAliases)
            {
                alias.Id = Guid.NewGuid().ToString();
                await _storage.SaveAliasAsync(alias);
                aliasCount++;
            }
        }

        _logger.LogInformation("Imported {Lookups} lookups, {Macros} macros, {Aliases} field aliases",
            lookupCount, macroCount, aliasCount);

        await _auditLogger.LogAsync(new AuditEvent
        {
            PrincipalId = User.FindFirst("ClientId")?.Value ?? "unknown",
            Action = AuditAction.Create,
            ResourceType = "KnowledgeImport",
            ResourceId = "bulk",
            Description = $"Imported {lookupCount} lookups, {macroCount} macros, {aliasCount} aliases",
            SourceIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        });

        return Ok(new { Lookups = lookupCount, Macros = macroCount, FieldAliases = aliasCount });
    }
}

// DTOs
public class CreateLookupRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string>? Columns { get; set; }
    public List<Dictionary<string, string>>? Rows { get; set; }
}

public class UpdateLookupRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<string>? Columns { get; set; }
    public List<Dictionary<string, string>>? Rows { get; set; }
}

public class CreateMacroRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Definition { get; set; } = string.Empty;
    public List<string>? Parameters { get; set; }
    public string? ValidationRegex { get; set; }
}

public class UpdateMacroRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Definition { get; set; }
    public List<string>? Parameters { get; set; }
    public string? ValidationRegex { get; set; }
}

public class CreateAliasRequest
{
    public string? SourceType { get; set; }
    public string OriginalField { get; set; } = string.Empty;
    public string AliasField { get; set; } = string.Empty;
}

public class UpdateAliasRequest
{
    public string? SourceType { get; set; }
    public string? OriginalField { get; set; }
    public string? AliasField { get; set; }
}

public class KnowledgeImportDto
{
    public List<LookupTable>? Lookups { get; set; }
    public List<QueryMacro>? Macros { get; set; }
    public List<FieldAlias>? FieldAliases { get; set; }
}
