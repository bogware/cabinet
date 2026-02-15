using System.Text.Json;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-backed storage for scheduled searches
/// </summary>
public sealed class SqliteScheduledSearchStorage : IScheduledSearchStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteScheduledSearchStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqliteScheduledSearchStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteScheduledSearchStorage> logger)
    {
        _connectionString = config.Value.Storage.ConnectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = @"
                CREATE TABLE IF NOT EXISTS scheduled_searches (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    query TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,
                    cron_expression TEXT NOT NULL,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    time_range TEXT NOT NULL DEFAULT '-15m',
                    last_run_at INTEGER,
                    last_run_result_count INTEGER NOT NULL DEFAULT 0,
                    last_run_success INTEGER NOT NULL DEFAULT 1,
                    last_run_error TEXT,
                    alert_action_json TEXT
                );
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Scheduled search storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ScheduledSearch> SaveAsync(ScheduledSearch search, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO scheduled_searches
                (id, name, query, description, created_by, created_at, cron_expression,
                 enabled, time_range, last_run_at, last_run_result_count, last_run_success,
                 last_run_error, alert_action_json)
            VALUES
                (@Id, @Name, @Query, @Description, @CreatedBy, @CreatedAt, @CronExpression,
                 @Enabled, @TimeRange, @LastRunAt, @LastRunResultCount, @LastRunSuccess,
                 @LastRunError, @AlertActionJson)";

        await connection.ExecuteAsync(sql, new
        {
            search.Id,
            search.Name,
            search.Query,
            search.Description,
            search.CreatedBy,
            CreatedAt = search.CreatedAt.Ticks,
            search.CronExpression,
            Enabled = search.Enabled ? 1 : 0,
            search.TimeRange,
            LastRunAt = search.LastRunAt?.Ticks,
            search.LastRunResultCount,
            LastRunSuccess = search.LastRunSuccess ? 1 : 0,
            search.LastRunError,
            AlertActionJson = search.AlertAction != null
                ? JsonSerializer.Serialize(search.AlertAction, JsonOptions)
                : null
        });

        return search;
    }

    public async Task<ScheduledSearch?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<ScheduledSearchRow>(
            "SELECT * FROM scheduled_searches WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    public async Task<IReadOnlyList<ScheduledSearch>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<ScheduledSearchRow>(
            "SELECT * FROM scheduled_searches ORDER BY created_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM scheduled_searches WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    private ScheduledSearch MapFromRow(ScheduledSearchRow row)
    {
        ScheduledSearchAlert? alertAction = null;
        if (!string.IsNullOrEmpty(row.alert_action_json))
        {
            try
            {
                alertAction = JsonSerializer.Deserialize<ScheduledSearchAlert>(row.alert_action_json, JsonOptions);
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
            CreatedAt = new DateTime(row.created_at),
            CronExpression = row.cron_expression,
            Enabled = row.enabled != 0,
            TimeRange = row.time_range ?? "-15m",
            LastRunAt = row.last_run_at.HasValue ? new DateTime(row.last_run_at.Value) : null,
            LastRunResultCount = row.last_run_result_count,
            LastRunSuccess = row.last_run_success != 0,
            LastRunError = row.last_run_error,
            AlertAction = alertAction
        };
    }

    private sealed class ScheduledSearchRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string query { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public long created_at { get; set; }
        public string cron_expression { get; set; } = string.Empty;
        public int enabled { get; set; }
        public string? time_range { get; set; }
        public long? last_run_at { get; set; }
        public int last_run_result_count { get; set; }
        public int last_run_success { get; set; }
        public string? last_run_error { get; set; }
        public string? alert_action_json { get; set; }
    }
}
