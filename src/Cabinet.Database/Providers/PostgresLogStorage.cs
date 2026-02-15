using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL ILogStorage implementation with JSONB indexing, BRIN timestamps, and connection pooling.
/// This is the production-grade storage backend for Cabinet.
/// </summary>
public sealed class PostgresLogStorage : ILogStorage, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresLogStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public PostgresLogStorage(string connectionString, ILogger<PostgresLogStorage> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing PostgreSQL storage");
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Main log events table with proper types
            await conn.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS log_events (
                    id TEXT PRIMARY KEY,
                    timestamp TIMESTAMPTZ NOT NULL,
                    source_type TEXT NOT NULL DEFAULT 'Generic',
                    severity SMALLINT NOT NULL DEFAULT 0,
                    server_id TEXT,
                    raw_line TEXT NOT NULL,
                    source_file TEXT,
                    line_number BIGINT DEFAULT 0,
                    parser_name TEXT,
                    fields JSONB DEFAULT '{}',
                    tags TEXT[] DEFAULT '{}',
                    index_time TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    index_name TEXT NOT NULL DEFAULT 'main',
                    created_at TIMESTAMPTZ DEFAULT NOW()
                )");

            // BRIN index on timestamp — optimal for time-series append-only data
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_timestamp_brin
                ON log_events USING BRIN (timestamp) WITH (pages_per_range = 32)");

            // B-tree indexes for common filters
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_source_type ON log_events (source_type)");
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_severity ON log_events (severity)");
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_server_id ON log_events (server_id)
                WHERE server_id IS NOT NULL");

            // B-tree index on index_name for per-index queries
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_index_name ON log_events (index_name)");

            // GIN index on JSONB fields for field-level queries
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_fields ON log_events USING GIN (fields)");

            // GIN index on tags array
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_tags ON log_events USING GIN (tags)");

            // Full-text search index on raw_line
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_fts
                ON log_events USING GIN (to_tsvector('english', raw_line))");

            _initialized = true;
            _logger.LogInformation("PostgreSQL storage initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var result = await conn.ExecuteAsync(@"
            INSERT INTO log_events (id, timestamp, source_type, severity, server_id, raw_line, source_file, line_number, parser_name, fields, tags, index_time, index_name)
            VALUES (@Id, @Timestamp, @SourceType, @Severity, @Host, @RawLine, @SourceFile, @LineNumber, @ParserName, @Fields::jsonb, @Tags, @IndexTime, @IndexName)
            ON CONFLICT (id) DO NOTHING",
            MapToParams(logEvent));

        return result > 0;
    }

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var eventList = events.ToList();
        if (eventList.Count == 0) return 0;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await conn.BeginTransactionAsync(cancellationToken);

        var inserted = 0;
        foreach (var batch in eventList.Chunk(500))
        {
            inserted += await conn.ExecuteAsync(@"
                INSERT INTO log_events (id, timestamp, source_type, severity, server_id, raw_line, source_file, line_number, parser_name, fields, tags, index_time, index_name)
                VALUES (@Id, @Timestamp, @SourceType, @Severity, @Host, @RawLine, @SourceFile, @LineNumber, @ParserName, @Fields::jsonb, @Tags, @IndexTime, @IndexName)
                ON CONFLICT (id) DO NOTHING",
                batch.Select(MapToParams),
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        _logger.LogDebug("Batch stored {Count} events", inserted);
        return inserted;
    }

    public async IAsyncEnumerable<LogEvent> QueryAsync(
        QueryContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var (sql, parameters) = BuildQuerySql(context, includeLimit: true);

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        // Use unbuffered streaming — Dapper's QueryAsync loads all rows into memory,
        // but QueryUnbufferedAsync streams row-by-row via IAsyncEnumerable
        await foreach (var row in conn.QueryUnbufferedAsync<LogEventRow>(sql, parameters).WithCancellation(cancellationToken))
        {
            yield return MapFromRow(row);
        }
    }

    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var row = await conn.QueryFirstOrDefaultAsync<LogEventRow>(
            "SELECT * FROM log_events WHERE id = @Id", new { Id = id });

        return row == null ? null : MapFromRow(row);
    }

    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (context == null)
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM log_events");
        }

        var (sql, parameters) = BuildCountSql(context);
        await using var conn2 = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await conn2.ExecuteScalarAsync<long>(sql, parameters);
    }

    public async Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var deleted = await conn.ExecuteAsync(
            "DELETE FROM log_events WHERE timestamp < @Before",
            new { Before = before });

        _logger.LogInformation("Purged {Count} events older than {Before}", deleted, before);
        return deleted;
    }

    public async Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var deleted = await conn.ExecuteAsync(
            "DELETE FROM log_events WHERE index_name = @IndexName",
            new { IndexName = indexName });

        _logger.LogInformation("Deleted {Count} events from index '{Index}'", deleted, indexName);
        return deleted;
    }

    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);

        var stats = new StorageStats();

        stats.TotalEvents = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM log_events");

        if (stats.TotalEvents > 0)
        {
            var dateRange = await conn.QueryFirstOrDefaultAsync<dynamic>(
                "SELECT MIN(timestamp) AS min_ts, MAX(timestamp) AS max_ts FROM log_events");
            if (dateRange != null)
            {
                stats.OldestEvent = dateRange.min_ts;
                stats.NewestEvent = dateRange.max_ts;
            }

            var bySource = await conn.QueryAsync<dynamic>(
                "SELECT source_type, COUNT(*) AS cnt FROM log_events GROUP BY source_type");
            foreach (var row in bySource)
            {
                stats.EventsBySourceType[row.source_type.ToString()] = (long)row.cnt;
            }

            var byIndex = await conn.QueryAsync<dynamic>(
                "SELECT index_name, COUNT(*) AS cnt FROM log_events GROUP BY index_name");
            foreach (var row in byIndex)
            {
                stats.EventsByIndex[row.index_name.ToString()] = (long)row.cnt;
            }

            var bySeverity = await conn.QueryAsync<dynamic>(
                "SELECT severity, COUNT(*) AS cnt FROM log_events GROUP BY severity");
            foreach (var row in bySeverity)
            {
                if (Enum.TryParse<LogSeverity>(row.severity.ToString(), out LogSeverity sev))
                    stats.EventsBySeverity[sev] = (long)row.cnt;
            }

            stats.StorageSizeBytes = await conn.ExecuteScalarAsync<long>(
                "SELECT pg_total_relation_size('log_events')");
        }

        return stats;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken);
    }

    private static object MapToParams(LogEvent evt)
    {
        return new
        {
            evt.Id,
            evt.Timestamp,
            SourceType = evt.SourceType,
            Severity = (int)evt.Severity,
            Host = evt.Host,
            evt.RawLine,
            evt.SourceFile,
            evt.LineNumber,
            evt.ParserName,
            Fields = JsonSerializer.Serialize(evt.Fields),
            Tags = evt.Tags.ToArray(),
            evt.IndexTime,
            IndexName = evt.Index
        };
    }

    private static LogEvent MapFromRow(LogEventRow row)
    {
        var evt = new LogEvent
        {
            Id = row.id,
            Timestamp = row.timestamp,
            SourceType = row.source_type ?? SourceTypes.Generic,
            Severity = (LogSeverity)row.severity,
            Host = row.server_id,
            RawLine = row.raw_line,
            SourceFile = row.source_file,
            LineNumber = row.line_number,
            ParserName = row.parser_name,
            IndexTime = row.index_time,
            Index = row.index_name ?? "main"
        };

        if (!string.IsNullOrEmpty(row.fields))
        {
            try
            {
                var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.fields);
                if (fields != null)
                {
                    foreach (var (key, value) in fields)
                    {
                        evt.SetField(key, value.ValueKind switch
                        {
                            JsonValueKind.String => value.GetString(),
                            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            _ => value.ToString()
                        });
                    }
                }
            }
            catch { /* fields deserialization is best-effort */ }
        }

        if (row.tags != null)
        {
            foreach (var tag in row.tags)
                evt.Tags.Add(tag);
        }

        return evt;
    }

    private static (string sql, DynamicParameters parameters) BuildQuerySql(QueryContext context, bool includeLimit)
    {
        var sb = new StringBuilder("SELECT * FROM log_events WHERE 1=1");
        var p = new DynamicParameters();

        AppendFilters(sb, p, context);

        // Sort
        var sortCol = context.SortField.ToLowerInvariant() switch
        {
            "timestamp" => "timestamp",
            "severity" => "severity",
            "sourcetype" or "source_type" => "source_type",
            _ => "timestamp"
        };
        sb.Append(context.SortDescending ? $" ORDER BY {sortCol} DESC" : $" ORDER BY {sortCol} ASC");

        if (includeLimit)
        {
            sb.Append(" OFFSET @Skip LIMIT @Limit");
            p.Add("Skip", context.Skip);
            p.Add("Limit", context.Limit);
        }

        return (sb.ToString(), p);
    }

    private static (string sql, DynamicParameters parameters) BuildCountSql(QueryContext context)
    {
        var sb = new StringBuilder("SELECT COUNT(*) FROM log_events WHERE 1=1");
        var p = new DynamicParameters();
        AppendFilters(sb, p, context);
        return (sb.ToString(), p);
    }

    private static void AppendFilters(StringBuilder sb, DynamicParameters p, QueryContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SearchText) && context.SearchText != "*")
        {
            sb.Append(" AND to_tsvector('english', raw_line) @@ websearch_to_tsquery('english', @SearchText)");
            p.Add("SearchText", context.SearchText);
        }

        if (context.StartTime.HasValue)
        {
            sb.Append(" AND timestamp >= @StartTime");
            p.Add("StartTime", context.StartTime.Value);
        }

        if (context.EndTime.HasValue)
        {
            sb.Append(" AND timestamp <= @EndTime");
            p.Add("EndTime", context.EndTime.Value);
        }

        if (!string.IsNullOrEmpty(context.SourceType))
        {
            sb.Append(" AND source_type = @SourceType");
            p.Add("SourceType", context.SourceType);
        }

        if (context.MinSeverity.HasValue)
        {
            sb.Append(" AND severity >= @MinSeverity");
            p.Add("MinSeverity", (int)context.MinSeverity.Value);
        }

        if (!string.IsNullOrWhiteSpace(context.Host))
        {
            sb.Append(" AND server_id = @Host");
            p.Add("Host", context.Host);
        }

        if (!string.IsNullOrEmpty(context.Index))
        {
            sb.Append(" AND index_name = @IndexName");
            p.Add("IndexName", context.Index);
        }

        if (context.Tags != null && context.Tags.Count > 0)
        {
            sb.Append(" AND tags @> @Tags");
            p.Add("Tags", context.Tags.ToArray());
        }

        if (context.FieldFilters != null)
        {
            var idx = 0;
            foreach (var (key, value) in context.FieldFilters)
            {
                // Use parameterized JSONB containment — no string interpolation of field names into SQL
                sb.Append($" AND fields @> @FieldFilter{idx}::jsonb");
                p.Add($"FieldFilter{idx}", JsonSerializer.Serialize(
                    new Dictionary<string, object?> { [key] = value }));
                idx++;
            }
        }
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }

    /// <summary>
    /// Internal row type for Dapper mapping
    /// </summary>
    private sealed class LogEventRow
    {
        public string id { get; set; } = "";
        public DateTime timestamp { get; set; }
        public string? source_type { get; set; }
        public int severity { get; set; }
        public string? server_id { get; set; }
        public string raw_line { get; set; } = "";
        public string? source_file { get; set; }
        public long line_number { get; set; }
        public string? parser_name { get; set; }
        public string? fields { get; set; }
        public string[]? tags { get; set; }
        public DateTime index_time { get; set; }
        public string? index_name { get; set; }
    }
}
