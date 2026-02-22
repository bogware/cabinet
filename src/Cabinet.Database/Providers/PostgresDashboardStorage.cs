using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL implementation of <see cref="IDashboardStorage"/> with JSONB panels storage
/// and connection pooling via <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgresDashboardStorage : IDashboardStorage, IDisposable
{
    private readonly ILogger<PostgresDashboardStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates a new PostgreSQL dashboard storage instance.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="logger">Logger instance</param>
    public PostgresDashboardStorage(string connectionString, ILogger<PostgresDashboardStorage> logger)
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

            _logger.LogInformation("Initializing PostgreSQL dashboard storage");
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS dashboards (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    is_shared BOOLEAN NOT NULL DEFAULT FALSE,
                    refresh_interval_seconds INTEGER NOT NULL DEFAULT 0,
                    default_time_range TEXT NOT NULL DEFAULT '-24h',
                    panels JSONB NOT NULL DEFAULT '[]'
                )");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_dashboards_updated_at
                ON dashboards (updated_at DESC)");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_dashboards_created_by
                ON dashboards (created_by)
                WHERE created_by != ''");

            _initialized = true;
            _logger.LogInformation("PostgreSQL dashboard storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Dashboard> SaveAsync(Dashboard dashboard, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO dashboards
                (id, title, description, created_by, created_at, updated_at, is_shared,
                 refresh_interval_seconds, default_time_range, panels)
            VALUES
                (@Id, @Title, @Description, @CreatedBy, @CreatedAt, @UpdatedAt, @IsShared,
                 @RefreshIntervalSeconds, @DefaultTimeRange, @Panels::jsonb)
            ON CONFLICT (id) DO UPDATE SET
                title = EXCLUDED.title,
                description = EXCLUDED.description,
                created_by = EXCLUDED.created_by,
                updated_at = EXCLUDED.updated_at,
                is_shared = EXCLUDED.is_shared,
                refresh_interval_seconds = EXCLUDED.refresh_interval_seconds,
                default_time_range = EXCLUDED.default_time_range,
                panels = EXCLUDED.panels",
            new
            {
                dashboard.Id,
                dashboard.Title,
                dashboard.Description,
                dashboard.CreatedBy,
                dashboard.CreatedAt,
                dashboard.UpdatedAt,
                dashboard.IsShared,
                dashboard.RefreshIntervalSeconds,
                dashboard.DefaultTimeRange,
                Panels = JsonSerializer.Serialize(dashboard.Panels, JsonOptions)
            });

        return dashboard;
    }

    /// <inheritdoc />
    public async Task<Dashboard?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<DashboardRow>(
            "SELECT * FROM dashboards WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dashboard>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<DashboardRow>(
            "SELECT * FROM dashboards ORDER BY updated_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM dashboards WHERE id = @Id", new { Id = id });

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

    private Dashboard MapFromRow(DashboardRow row)
    {
        List<DashboardPanel> panels;
        try
        {
            panels = string.IsNullOrEmpty(row.panels)
                ? new List<DashboardPanel>()
                : JsonSerializer.Deserialize<List<DashboardPanel>>(row.panels, JsonOptions)
                  ?? new List<DashboardPanel>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize panels for dashboard {Id}", row.id);
            panels = new List<DashboardPanel>();
        }

        return new Dashboard
        {
            Id = row.id,
            Title = row.title,
            Description = row.description,
            CreatedBy = row.created_by,
            CreatedAt = row.created_at,
            UpdatedAt = row.updated_at,
            IsShared = row.is_shared,
            RefreshIntervalSeconds = row.refresh_interval_seconds,
            DefaultTimeRange = row.default_time_range ?? "-24h",
            Panels = panels
        };
    }

    /// <summary>
    /// Internal row type for Dapper mapping from PostgreSQL
    /// </summary>
    private sealed class DashboardRow
    {
        public string id { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public bool is_shared { get; set; }
        public int refresh_interval_seconds { get; set; }
        public string? default_time_range { get; set; }
        public string? panels { get; set; }
    }
}
