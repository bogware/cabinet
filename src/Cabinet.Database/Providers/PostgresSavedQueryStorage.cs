using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL implementation of <see cref="ISavedQueryStorage"/> with native timestamp types
/// and connection pooling via <see cref="NpgsqlDataSource"/>.
/// </summary>
public sealed class PostgresSavedQueryStorage : ISavedQueryStorage, IDisposable
{
    private readonly ILogger<PostgresSavedQueryStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Creates a new PostgreSQL saved query storage instance.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="logger">Logger instance</param>
    public PostgresSavedQueryStorage(string connectionString, ILogger<PostgresSavedQueryStorage> logger)
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

            _logger.LogInformation("Initializing PostgreSQL saved query storage");
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var schemaLock = await PostgresSchemaLock.BeginAsync(conn, cancellationToken);

            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS saved_queries (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    query TEXT NOT NULL,
                    description TEXT,
                    created_by TEXT NOT NULL DEFAULT '',
                    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_used_at TIMESTAMPTZ,
                    usage_count INTEGER NOT NULL DEFAULT 0
                )");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_saved_queries_created_at
                ON saved_queries (created_at DESC)");

            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_saved_queries_created_by
                ON saved_queries (created_by)
                WHERE created_by != ''");

            await schemaLock.CommitAsync(cancellationToken);
            _initialized = true;
            _logger.LogInformation("PostgreSQL saved query storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SavedQuery> SaveAsync(SavedQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        await conn.ExecuteAsync(@"
            INSERT INTO saved_queries (id, name, query, description, created_by, created_at, last_used_at, usage_count)
            VALUES (@Id, @Name, @Query, @Description, @CreatedBy, @CreatedAt, @LastUsedAt, @UsageCount)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                query = EXCLUDED.query,
                description = EXCLUDED.description,
                created_by = EXCLUDED.created_by,
                last_used_at = EXCLUDED.last_used_at,
                usage_count = EXCLUDED.usage_count",
            new
            {
                query.Id,
                query.Name,
                query.Query,
                query.Description,
                query.CreatedBy,
                query.CreatedAt,
                query.LastUsedAt,
                query.UsageCount
            });

        return query;
    }

    /// <inheritdoc />
    public async Task<SavedQuery?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<SavedQueryRow>(
            "SELECT * FROM saved_queries WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedQuery>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await conn.QueryAsync<SavedQueryRow>(
            "SELECT * FROM saved_queries ORDER BY created_at DESC");

        return rows.Select(MapFromRow).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var affected = await conn.ExecuteAsync(
            "DELETE FROM saved_queries WHERE id = @Id", new { Id = id });

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

    private static SavedQuery MapFromRow(SavedQueryRow row) => new()
    {
        Id = row.id,
        Name = row.name,
        Query = row.query,
        Description = row.description,
        CreatedBy = row.created_by,
        CreatedAt = row.created_at,
        LastUsedAt = row.last_used_at,
        UsageCount = row.usage_count
    };

    /// <summary>
    /// Internal row type for Dapper mapping from PostgreSQL
    /// </summary>
    private sealed class SavedQueryRow
    {
        public string id { get; set; } = string.Empty;
        public string name { get; set; } = string.Empty;
        public string query { get; set; } = string.Empty;
        public string? description { get; set; }
        public string created_by { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
        public DateTime? last_used_at { get; set; }
        public int usage_count { get; set; }
    }
}
