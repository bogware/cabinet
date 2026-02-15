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
/// SQLite-backed dashboard storage using the same database as log events
/// </summary>
public sealed class SqliteDashboardStorage : IDashboardStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDashboardStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SqliteDashboardStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteDashboardStorage> logger)
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
                CREATE TABLE IF NOT EXISTS dashboards (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL,
                    is_shared INTEGER NOT NULL DEFAULT 0,
                    refresh_interval_seconds INTEGER NOT NULL DEFAULT 0,
                    default_time_range TEXT NOT NULL DEFAULT '-24h',
                    panels_json TEXT NOT NULL DEFAULT '[]'
                );
                CREATE INDEX IF NOT EXISTS idx_dashboards_updated_at ON dashboards(updated_at DESC);
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Dashboard storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<Dashboard> SaveAsync(Dashboard dashboard, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO dashboards
                (id, title, description, created_by, created_at, updated_at, is_shared,
                 refresh_interval_seconds, default_time_range, panels_json)
            VALUES
                (@Id, @Title, @Description, @CreatedBy, @CreatedAt, @UpdatedAt, @IsShared,
                 @RefreshIntervalSeconds, @DefaultTimeRange, @PanelsJson)";

        await connection.ExecuteAsync(sql, new
        {
            dashboard.Id,
            dashboard.Title,
            dashboard.Description,
            dashboard.CreatedBy,
            CreatedAt = dashboard.CreatedAt.Ticks,
            UpdatedAt = dashboard.UpdatedAt.Ticks,
            IsShared = dashboard.IsShared ? 1 : 0,
            dashboard.RefreshIntervalSeconds,
            dashboard.DefaultTimeRange,
            PanelsJson = JsonSerializer.Serialize(dashboard.Panels, JsonOptions)
        });

        return dashboard;
    }

    public async Task<Dashboard?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<DashboardRow>(
            "SELECT * FROM dashboards WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    public async Task<IReadOnlyList<Dashboard>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<DashboardRow>(
            "SELECT * FROM dashboards ORDER BY updated_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM dashboards WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    private static Dashboard MapFromRow(DashboardRow row)
    {
        var panels = string.IsNullOrEmpty(row.panels_json)
            ? new List<DashboardPanel>()
            : JsonSerializer.Deserialize<List<DashboardPanel>>(row.panels_json, JsonOptions)
              ?? new List<DashboardPanel>();

        return new Dashboard
        {
            Id = row.id,
            Title = row.title,
            Description = row.description,
            CreatedBy = row.created_by,
            CreatedAt = new DateTime(row.created_at),
            UpdatedAt = new DateTime(row.updated_at),
            IsShared = row.is_shared != 0,
            RefreshIntervalSeconds = row.refresh_interval_seconds,
            DefaultTimeRange = row.default_time_range ?? "-24h",
            Panels = panels
        };
    }

    private sealed class DashboardRow
    {
        public string id { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public long created_at { get; set; }
        public long updated_at { get; set; }
        public int is_shared { get; set; }
        public int refresh_interval_seconds { get; set; }
        public string? default_time_range { get; set; }
        public string? panels_json { get; set; }
    }
}
