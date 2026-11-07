using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL implementation of <see cref="IScheduledSearchStorage"/> with JSONB alert configuration
/// and connection pooling via <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgresScheduledSearchStorage : IScheduledSearchStorage, IDisposable
{
    private readonly ILogger<PostgresScheduledSearchStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a new PostgreSQL scheduled search storage instance.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="logger">Logger instance</param>
    public PostgresScheduledSearchStorage(string connectionString, ILogger<PostgresScheduledSearchStorage> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing PostgreSQL scheduled search storage");
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var schemaLock = await PostgresSchemaLock.BeginAsync(conn, cancellationToken);

            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS scheduled_searches (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    query TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    cron_expression TEXT NOT NULL,
                    enabled BOOLEAN NOT NULL DEFAULT TRUE,
                    time_range TEXT NOT NULL DEFAULT '-15m',
                    last_run_at TIMESTAMPTZ,
                    last_run_result_count INTEGER NOT NULL DEFAULT 0,
                    last_run_success BOOLEAN NOT NULL DEFAULT TRUE,
                    last_run_error TEXT,
                    alert_action JSONB
                )");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_scheduled_searches_created_at
                ON scheduled_searches (created_at DESC)");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_scheduled_searches_enabled
                ON scheduled_searches (enabled)
                WHERE enabled = TRUE");

            await schemaLock.CommitAsync(cancellationToken);
            _initialized = true;
            _logger.LogInformation("PostgreSQL scheduled search storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ScheduledSearch> SaveAsync(ScheduledSearch search, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO scheduled_searches
                (id, name, query, description, created_by, created_at, cron_expression,
                 enabled, time_range, last_run_at, last_run_result_count, last_run_success,
                 last_run_error, alert_action)
            VALUES
                (@Id, @Name, @Query, @Description, @CreatedBy, @CreatedAt, @CronExpression,
                 @Enabled, @TimeRange, @LastRunAt, @LastRunResultCount, @LastRunSuccess,
                 @LastRunError, @AlertAction::jsonb)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                query = EXCLUDED.query,
                description = EXCLUDED.description,
                created_by = EXCLUDED.created_by,
                cron_expression = EXCLUDED.cron_expression,
                enabled = EXCLUDED.enabled,
                time_range = EXCLUDED.time_range,
                last_run_at = EXCLUDED.last_run_at,
                last_run_result_count = EXCLUDED.last_run_result_count,
                last_run_success = EXCLUDED.last_run_success,
                last_run_error = EXCLUDED.last_run_error,
                alert_action = EXCLUDED.alert_action",
            new
            {
                search.Id,
                search.Name,
                search.Query,
                search.Description,
                search.CreatedBy,
                search.CreatedAt,
                search.CronExpression,
                search.Enabled,
                search.TimeRange,
                search.LastRunAt,
                search.LastRunResultCount,
                search.LastRunSuccess,
                search.LastRunError,
                AlertAction = search.AlertAction != null
                    ? JsonSerializer.Serialize(search.AlertAction, JsonOptions)
                    : null
            });

        return search;
    }

    /// <inheritdoc />
    public async Task<ScheduledSearch?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<ScheduledSearchRow>(
            "SELECT * FROM scheduled_searches WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduledSearch>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<ScheduledSearchRow>(
            "SELECT * FROM scheduled_searches ORDER BY created_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM scheduled_searches WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _dataSource.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    private ScheduledSearch MapFromRow(ScheduledSearchRow row)
    {
        ScheduledSearchAlert? alertAction = null;
        if (!string.IsNullOrEmpty(row.alert_action))
        {
            try
            {
                alertAction = JsonSerializer.Deserialize<ScheduledSearchAlert>(row.alert_action, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize alert config for scheduled search {Id}", row.id);
            }
        }

        return new ScheduledSearch
        {
            Id = row.id,
            Name = row.name,
            Query = row.query,
            Description = row.description,
            CreatedBy = row.created_by,
            CreatedAt = row.created_at,
            CronExpression = row.cron_expression,
            Enabled = row.enabled,
            TimeRange = row.time_range ?? "-15m",
            LastRunAt = row.last_run_at,
            LastRunResultCount = row.last_run_result_count,
            LastRunSuccess = row.last_run_success,
            LastRunError = row.last_run_error,
            AlertAction = alertAction
        };
    }

    /// <summary>
    /// Internal row type for Dapper mapping from PostgreSQL
    /// </summary>
    private sealed class ScheduledSearchRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string query { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
        public string cron_expression { get; set; } = string.Empty;
        public bool enabled { get; set; }
        public string? time_range { get; set; }
        public DateTime? last_run_at { get; set; }
        public int last_run_result_count { get; set; }
        public bool last_run_success { get; set; }
        public string? last_run_error { get; set; }
        public string? alert_action { get; set; }
    }
}
