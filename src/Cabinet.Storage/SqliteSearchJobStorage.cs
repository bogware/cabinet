using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-backed search job storage using the same database as log events.
/// Manages async search job lifecycle and result persistence.
/// </summary>
public sealed class SqliteSearchJobStorage : ISearchJobStorage
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSearchJobStorage> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteSearchJobStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteSearchJobStorage> logger)
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
                CREATE TABLE IF NOT EXISTS search_jobs (
                    id TEXT PRIMARY KEY,
                    query TEXT NOT NULL,
                    status INTEGER NOT NULL DEFAULT 0,
                    progress INTEGER NOT NULL DEFAULT 0,
                    created_at INTEGER NOT NULL,
                    started_at INTEGER,
                    completed_at INTEGER,
                    ttl_ticks INTEGER NOT NULL,
                    error TEXT,
                    result_count INTEGER NOT NULL DEFAULT 0,
                    events_scanned INTEGER NOT NULL DEFAULT 0,
                    execution_time_ticks INTEGER,
                    owner TEXT,
                    index_name TEXT,
                    max_limit INTEGER NOT NULL DEFAULT 50000,
                    results_json TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_search_jobs_status ON search_jobs(status);
                CREATE INDEX IF NOT EXISTS idx_search_jobs_owner ON search_jobs(owner);
                CREATE INDEX IF NOT EXISTS idx_search_jobs_created_at ON search_jobs(created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_search_jobs_status_completed ON search_jobs(status, completed_at);
            ";

            await connection.ExecuteAsync(sql);
            _initialized = true;
            _logger.LogInformation("Search job storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<SearchJob> CreateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT INTO search_jobs (
                id, query, status, progress, created_at, started_at, completed_at,
                ttl_ticks, error, result_count, events_scanned, execution_time_ticks,
                owner, index_name, max_limit, results_json
            ) VALUES (
                @Id, @Query, @Status, @Progress, @CreatedAt, @StartedAt, @CompletedAt,
                @TtlTicks, @Error, @ResultCount, @EventsScanned, @ExecutionTimeTicks,
                @Owner, @IndexName, @MaxLimit, @ResultsJson
            )";

        await connection.ExecuteAsync(sql, MapToParameters(job));
        return job;
    }

    public async Task<SearchJob?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<SearchJobRow>(
            "SELECT * FROM search_jobs WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    public async Task<IReadOnlyList<SearchJob>> GetAllAsync(string? owner = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        IEnumerable<SearchJobRow> rows;
        if (!string.IsNullOrEmpty(owner))
        {
            rows = await connection.QueryAsync<SearchJobRow>(
                "SELECT * FROM search_jobs WHERE owner = @Owner ORDER BY created_at DESC",
                new { Owner = owner });
        }
        else
        {
            rows = await connection.QueryAsync<SearchJobRow>(
                "SELECT * FROM search_jobs ORDER BY created_at DESC");
        }

        return rows.Select(MapFromRow).ToList();
    }

    public async Task UpdateAsync(SearchJob job, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            UPDATE search_jobs SET
                query = @Query,
                status = @Status,
                progress = @Progress,
                created_at = @CreatedAt,
                started_at = @StartedAt,
                completed_at = @CompletedAt,
                ttl_ticks = @TtlTicks,
                error = @Error,
                result_count = @ResultCount,
                events_scanned = @EventsScanned,
                execution_time_ticks = @ExecutionTimeTicks,
                owner = @Owner,
                index_name = @IndexName,
                max_limit = @MaxLimit,
                results_json = @ResultsJson
            WHERE id = @Id";

        await connection.ExecuteAsync(sql, MapToParameters(job));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(
            "DELETE FROM search_jobs WHERE id = @Id", new { Id = id });

        return affected > 0;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Purge jobs in terminal states (Completed=2, Failed=3, Cancelled=4) where
        // the current time exceeds (completed_at or created_at) + ttl.
        // All times are stored as DateTime.Ticks (100-nanosecond intervals).
        var nowTicks = DateTime.UtcNow.Ticks;

        var sql = @"
            DELETE FROM search_jobs
            WHERE status IN (2, 3, 4)
              AND (COALESCE(completed_at, created_at) + ttl_ticks) < @NowTicks";

        return await connection.ExecuteAsync(sql, new { NowTicks = nowTicks });
    }

    private static object MapToParameters(SearchJob job)
    {
        return new
        {
            job.Id,
            job.Query,
            Status = (int)job.Status,
            job.Progress,
            CreatedAt = job.CreatedAt.Ticks,
            StartedAt = job.StartedAt?.Ticks,
            CompletedAt = job.CompletedAt?.Ticks,
            TtlTicks = job.Ttl.Ticks,
            job.Error,
            job.ResultCount,
            job.EventsScanned,
            ExecutionTimeTicks = job.ExecutionTime?.Ticks,
            job.Owner,
            IndexName = job.Index,
            MaxLimit = job.Limit,
            ResultsJson = job.ResultsJson
        };
    }

    private static SearchJob MapFromRow(SearchJobRow row)
    {
        return new SearchJob
        {
            Id = row.id,
            Query = row.query,
            Status = (SearchJobStatus)row.status,
            Progress = row.progress,
            CreatedAt = new DateTime(row.created_at, DateTimeKind.Utc),
            StartedAt = row.started_at.HasValue ? new DateTime(row.started_at.Value, DateTimeKind.Utc) : null,
            CompletedAt = row.completed_at.HasValue ? new DateTime(row.completed_at.Value, DateTimeKind.Utc) : null,
            Ttl = TimeSpan.FromTicks(row.ttl_ticks),
            Error = row.error,
            ResultCount = row.result_count,
            EventsScanned = row.events_scanned,
            ExecutionTime = row.execution_time_ticks.HasValue ? TimeSpan.FromTicks(row.execution_time_ticks.Value) : null,
            Owner = row.owner,
            Index = row.index_name,
            Limit = row.max_limit,
            ResultsJson = row.results_json
        };
    }

    /// <summary>
    /// Dapper row mapping class matching SQLite column names (snake_case).
    /// </summary>
    private sealed class SearchJobRow
    {
        public string id { get; set; } = string.Empty;
        public string query { get; set; } = string.Empty;
        public int status { get; set; }
        public int progress { get; set; }
        public long created_at { get; set; }
        public long? started_at { get; set; }
        public long? completed_at { get; set; }
        public long ttl_ticks { get; set; }
        public string? error { get; set; }
        public int result_count { get; set; }
        public long events_scanned { get; set; }
        public long? execution_time_ticks { get; set; }
        public string? owner { get; set; }
        public string? index_name { get; set; }
        public int max_limit { get; set; }
        public string? results_json { get; set; }
    }
}
