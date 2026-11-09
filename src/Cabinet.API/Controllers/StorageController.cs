using Cabinet.API.Middleware;
using Cabinet.API.Services.Settings;
using Cabinet.API.Services.Storage;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cabinet.API.Controllers;

/// <summary>
/// Where events are stored, and moving them: to another SQLite file, to PostgreSQL, or to a new
/// empty database. Admin only: it can read every event and replace the database.
/// </summary>
[ApiController]
[Route("api/v1/storage")]
[Authorize]
[RequireCapability(Capability.UserManagement)]
public class StorageController : ControllerBase
{
    private readonly StorageMigrationService _migration;

    public StorageController(StorageMigrationService migration)
    {
        _migration = migration;
    }

    private string Principal => User.FindFirst("ClientId")?.Value ?? User.Identity?.Name ?? "unknown";

    /// <summary>
    /// Where events are stored now, how much, and any move under way
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(StorageOverview), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(
        [FromServices] IConfiguration configuration,
        [FromServices] ILogStorage storage,
        CancellationToken cancellationToken = default)
    {
        var current = await StorageInfoService.DescribeAsync(configuration, storage, cancellationToken);
        return Ok(new StorageOverview
        {
            Current = current,
            Type = StorageBackends.Normalize(configuration["Storage:Type"]) ?? "SQLite",
            Migration = _migration.Status,
            MovableTo = StorageBackends.Movable,
        });
    }

    /// <summary>
    /// Connect to a database and report what's in it, changing nothing
    /// </summary>
    [HttpPost("test")]
    [ProducesResponseType(typeof(StorageTestResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Test([FromBody] StorageTarget target, CancellationToken cancellationToken = default) =>
        Ok(await _migration.TestAsync(target.Type, target.ConnectionString, cancellationToken));

    /// <summary>
    /// Create the PostgreSQL database a connection string names, using its credentials
    /// </summary>
    [HttpPost("postgres/database")]
    [ProducesResponseType(typeof(StorageTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateDatabase([FromBody] StorageTarget target, CancellationToken cancellationToken = default)
    {
        try
        {
            var connectionString = await _migration.CreatePostgresDatabaseAsync(target.ConnectionString, cancellationToken);
            return Ok(await _migration.TestAsync("PostgreSQL", connectionString, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            return Problem(detail: ex is Npgsql.PostgresException pg ? $"PostgreSQL said: {pg.MessageText}" : ex.Message, statusCode: 400);
        }
    }

    /// <summary>
    /// Move to another database. With copy, events and every kind of metadata are copied into
    /// an empty database first; without, Cabinet starts on it as it is. Either way the old
    /// database is left as it was, and Cabinet restarts on the new one when done.
    /// </summary>
    [HttpPost("migrate")]
    [ProducesResponseType(typeof(MigrationStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Migrate([FromBody] StorageMoveRequest request)
    {
        try
        {
            return Accepted(_migration.Start(request.Type, request.ConnectionString, request.Copy, Principal));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409);
        }
    }

    /// <summary>
    /// Progress of a move
    /// </summary>
    [HttpGet("migrate")]
    [ProducesResponseType(typeof(MigrationStatus), StatusCodes.Status200OK)]
    public IActionResult MigrationStatus() => Ok(_migration.Status);

    /// <summary>
    /// Stop a move before it switches; the current database stays in use
    /// </summary>
    [HttpPost("migrate/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Cancel() =>
        _migration.Cancel() ? NoContent() : Problem(detail: "No move is under way.", statusCode: 409);
}

/// <summary>A database to test or create.</summary>
public class StorageTarget
{
    /// <summary>SQLite or PostgreSQL.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>For SQLite, "Data Source=path". For PostgreSQL, host, database and credentials.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>A move to another database.</summary>
public class StorageMoveRequest : StorageTarget
{
    /// <summary>Copy events and metadata into it first; it must be empty.</summary>
    public bool Copy { get; set; } = true;
}

/// <summary>Where events are, and any move under way.</summary>
public class StorageOverview
{
    public required StorageInfo Current { get; init; }
    public required string Type { get; init; }
    public required MigrationStatus Migration { get; init; }
    public required IReadOnlyList<string> MovableTo { get; init; }
}
