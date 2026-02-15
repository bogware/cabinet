using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-backed saved query storage using the same database as log events
/// </summary>
public sealed class SqliteSavedQueryStorage : ISavedQueryStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSavedQueryStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteSavedQueryStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteSavedQueryStorage> logger)
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
                CREATE TABLE IF NOT EXISTS saved_queries (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    query TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL,
                    last_used_at INTEGER,
                    usage_count INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_saved_queries_created_at ON saved_queries(created_at DESC);
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Saved query storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO saved_queries (id, name, query, description, created_by, created_at, last_used_at, usage_count)
            VALUES (@Id, @Name, @Query, @Description, @CreatedBy, @CreatedAt, @LastUsedAt, @UsageCount)";

        await connection.ExecuteAsync(sql, new
        {
            query.Id,
            query.Name,
            query.Query,
            query.Description,
            query.CreatedBy,
            CreatedAt = query.CreatedAt.Ticks,
            LastUsedAt = query.LastUsedAt?.Ticks,
            query.UsageCount
        });

        return query;
    }

    public async Task<SavedQuery?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SavedQueryRow>(
            "SELECT * FROM saved_queries WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    public async Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<SavedQueryRow>(
            "SELECT * FROM saved_queries ORDER BY created_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM saved_queries WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    private static SavedQuery MapFromRow(SavedQueryRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Query = row.query,
        Description = row.description,
        CreatedBy = row.created_by,
        CreatedAt = new DateTime(row.created_at),
        LastUsedAt = row.last_used_at.HasValue ? new DateTime(row.last_used_at.Value) : null,
        UsageCount = row.usage_count
    };

    private sealed class SavedQueryRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string query { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public long created_at { get; set; }
        public long? last_used_at { get; set; }
        public int usage_count { get; set; }
    }
}
