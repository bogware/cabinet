using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL ILogStorage implementation with JSONB indexing, BRIN timestamps, and connection pooling.
/// This is the production-grade storage backend for Cabinet.
/// </summary>
public sealed partial class PostgresLogStorage : ILogStorage, IDisposable
{
    private const string InsertColumns = "id, timestamp, source_type, severity, server_id, raw_line, source_file, line_number, parser_name, fields, tags, index_time, index_name";

    // Splits text at anything but letters and digits, as the other backends do. The default parser
    // would keep /var/log/app.log as one word, so searching for "log" wouldn't find it.
    private const string SearchVector = "to_tsvector('simple'::regconfig, regexp_replace(raw_line, '[^[:alnum:]]+', ' ', 'g'))";

    private readonly string _connectionString;
    private readonly ILogger<PostgresLogStorage> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    private readonly int _workMemMb;

    public PostgresLogStorage(string connectionString, ILogger<PostgresLogStorage> logger, int workMemMb = 256)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workMemMb = Math.Clamp(workMemMb, 4, 4096);

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
            await using var schemaLock = await PostgresSchemaLock.BeginAsync(conn, cancellationToken);

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

            // Every read is by time, newest first, a page at a time. This index answers one without
            // sorting all the matches, which a BRIN index can't.
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_timestamp_id
                ON log_events (timestamp DESC, id DESC)");
            await conn.ExecuteAsync("DROP INDEX IF EXISTS idx_log_events_timestamp_brin");

            // Most reads are on one index. Without this, each page reads all of it and sorts.
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_index_timestamp_id
                ON log_events (index_name, timestamp DESC, id DESC)");

            // B-tree indexes for common filters
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_source_type ON log_events (lower(source_type))");
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_severity ON log_events (severity)");
            await conn.ExecuteAsync(@"
                CREATE INDEX IF NOT EXISTS idx_log_events_server_id ON log_events (lower(server_id))
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

            // Full-text search index on the raw line. Searches run with a higher work_mem (set on
            // every connection) so the match bitmap stays exact and nothing is recomputed per row.
            await conn.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS idx_log_events_search
                ON log_events USING GIN ({SearchVector})");
            await conn.ExecuteAsync("DROP INDEX IF EXISTS idx_log_events_fts");

            await schemaLock.CommitAsync(cancellationToken);

            // Give every connection to this database enough work_mem that a full-text search keeps
            // an exact match bitmap (see the note above). Set on the database, not per connection,
            // so it survives connection pooling; a plain SET is wiped when a connection is reused.
            try
            {
                var database = conn.Database.Replace("\"", "\"\"");
                await conn.ExecuteAsync($"ALTER DATABASE \"{database}\" SET work_mem = '{_workMemMb}MB'");
            }
            catch (PostgresException ex)
            {
                _logger.LogWarning("Couldn't set work_mem on the database ({Message}); broad searches may be slow. Set it by hand with ALTER DATABASE ... SET work_mem.", ex.MessageText);
            }

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

        // COPY is far cheaper than a statement per row, but can't skip an existing ID. So it loads
        // a temporary table, and one INSERT moves the rows, ignoring IDs already stored.
        await conn.ExecuteAsync(
            "CREATE TEMP TABLE log_events_load (LIKE log_events INCLUDING DEFAULTS) ON COMMIT DROP",
            transaction: transaction);

        await using (var importer = await conn.BeginBinaryImportAsync(
            $"COPY log_events_load ({InsertColumns}) FROM STDIN (FORMAT BINARY)", cancellationToken))
        {
            foreach (var logEvent in eventList)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(logEvent.Id, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(DateTime.SpecifyKind(logEvent.Timestamp, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(logEvent.SourceType, NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync((short)logEvent.Severity, NpgsqlDbType.Smallint, cancellationToken);
                await WriteNullableAsync(importer, logEvent.Host, cancellationToken);
                await importer.WriteAsync(RemoveNulls(logEvent.RawLine), NpgsqlDbType.Text, cancellationToken);
                await WriteNullableAsync(importer, logEvent.SourceFile, cancellationToken);
                await importer.WriteAsync(logEvent.LineNumber, NpgsqlDbType.Bigint, cancellationToken);
                await WriteNullableAsync(importer, logEvent.ParserName, cancellationToken);
                await importer.WriteAsync(RemoveNulls(JsonSerializer.Serialize(logEvent.Fields)), NpgsqlDbType.Jsonb, cancellationToken);
                await importer.WriteAsync(logEvent.Tags.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text, cancellationToken);
                await importer.WriteAsync(DateTime.SpecifyKind(logEvent.IndexTime, DateTimeKind.Utc), NpgsqlDbType.TimestampTz, cancellationToken);
                await importer.WriteAsync(logEvent.Index, NpgsqlDbType.Text, cancellationToken);
            }

            await importer.CompleteAsync(cancellationToken);
        }

        var inserted = await conn.ExecuteAsync(
            $"INSERT INTO log_events ({InsertColumns}) SELECT {InsertColumns} FROM log_events_load ON CONFLICT (id) DO NOTHING",
            transaction: transaction);

        await transaction.CommitAsync(cancellationToken);
        _logger.LogDebug("Batch stored {Count} events", inserted);
        return inserted;
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter importer, string? value, CancellationToken cancellationToken)
    {
        if (value == null)
        {
            await importer.WriteNullAsync(cancellationToken);
        }
        else
        {
            await importer.WriteAsync(RemoveNulls(value), NpgsqlDbType.Text, cancellationToken);
        }
    }

    /// <summary>Postgres text can't hold a NUL character, and a stray one mustn't lose the batch.</summary>
    private static string RemoveNulls(string value) =>
        value.Contains('\0') ? value.Replace("\0", string.Empty, StringComparison.Ordinal) : value;

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
            $"SELECT {InsertColumns} FROM log_events WHERE id = @Id", new { Id = id });

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

    public async Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await DeleteInBatchesAsync(
            "index_name = @IndexName AND timestamp < @Before",
            new { IndexName = indexName, Before = DateTime.SpecifyKind(before, DateTimeKind.Utc) },
            cancellationToken);
    }

    /// <summary>Rows deleted per statement, so retention never runs one huge, lock-holding delete.</summary>
    public const int DeleteBatchSize = 10_000;

    /// <summary>
    /// Deletes matching rows a batch at a time, each its own short transaction. One statement over
    /// millions of rows would time out, hold its locks throughout and write it all to the WAL at once.
    /// </summary>
    private async Task<int> DeleteInBatchesAsync(string where, object parameters, CancellationToken cancellationToken)
    {
        var sql = $"DELETE FROM log_events WHERE id IN (SELECT id FROM log_events WHERE {where} LIMIT {DeleteBatchSize})";
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            var deleted = await conn.ExecuteAsync(new CommandDefinition(sql, parameters, commandTimeout: 300, cancellationToken: cancellationToken));
            total += deleted;
            if (deleted < DeleteBatchSize)
            {
                return total;
            }
        }
    }

    public async Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var deleted = await DeleteInBatchesAsync(
            "timestamp < @Before",
            new { Before = DateTime.SpecifyKind(before, DateTimeKind.Utc) },
            cancellationToken);

        _logger.LogInformation("Purged {Count} events older than {Before}", deleted, before);
        return deleted;
    }

    public async Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var deleted = await DeleteInBatchesAsync("index_name = @IndexName", new { IndexName = indexName }, cancellationToken);

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
                        evt.SetField(key, JsonFieldValue.Unwrap(value));
                    }
                }
            }
            catch (JsonException)
            {
                // A row whose fields aren't valid JSON still has its line; leave the fields off
                evt.SetField("_fields_unreadable", true);
            }
        }

        if (row.tags != null)
        {
            foreach (var tag in row.tags)
                evt.Tags.Add(tag);
        }

        return evt;
    }

    private (string sql, DynamicParameters parameters) BuildQuerySql(QueryContext context, bool includeLimit)
    {
        var sb = new StringBuilder($"SELECT {InsertColumns} FROM log_events WHERE 1=1");
        var p = new DynamicParameters();

        AppendFilters(sb, p, context);

        if (context.After != null)
        {
            sb.Append(context.SortDescending
                ? " AND (timestamp, id) < (@AfterTimestamp, @AfterId)"
                : " AND (timestamp, id) > (@AfterTimestamp, @AfterId)");
            p.Add("AfterTimestamp", context.After.Timestamp);
            p.Add("AfterId", context.After.Id);
        }

        // Sort
        var sortCol = context.SortField.ToLowerInvariant() switch
        {
            "timestamp" => "timestamp",
            "severity" => "severity",
            "sourcetype" or "source_type" => "source_type",
            _ => "timestamp"
        };

        // ID breaks ties, so pages of events with the same time are stable
        sb.Append(context.SortDescending ? $" ORDER BY {sortCol} DESC, id DESC" : $" ORDER BY {sortCol} ASC, id ASC");

        if (includeLimit)
        {
            sb.Append(" OFFSET @Skip LIMIT @Limit");
            p.Add("Skip", context.Skip);
            p.Add("Limit", context.Limit);
        }

        return (sb.ToString(), p);
    }

    private (string sql, DynamicParameters parameters) BuildCountSql(QueryContext context)
    {
        var sb = new StringBuilder("SELECT COUNT(*) FROM log_events WHERE 1=1");
        var p = new DynamicParameters();
        AppendFilters(sb, p, context);
        return (sb.ToString(), p);
    }

    private void AppendFilters(StringBuilder sb, DynamicParameters p, QueryContext context)
    {
        var terms = SearchTerms.Parse(context.SearchText);
        if (terms.Count > 0)
        {
            sb.Append($" AND {SearchVector} @@ plainto_tsquery('simple', @SearchText)");
            p.Add("SearchText", string.Join(' ', terms));
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
            sb.Append(" AND lower(source_type) = lower(@SourceType)");
            p.Add("SourceType", context.SourceType);
        }

        if (context.MinSeverity.HasValue)
        {
            sb.Append(" AND severity >= @MinSeverity");
            p.Add("MinSeverity", (int)context.MinSeverity.Value);
        }

        if (!string.IsNullOrWhiteSpace(context.Host))
        {
            sb.Append(" AND lower(server_id) = lower(@Host)");
            p.Add("Host", context.Host);
        }

        if (!string.IsNullOrEmpty(context.Index))
        {
            sb.Append(" AND index_name = @IndexName");
            p.Add("IndexName", context.Index);
        }

        if (context.AllowedIndexes != null)
        {
            var conditions = context.AllowedIndexes.Select((_, i) => $"index_name ILIKE @AllowedIndex{i} ESCAPE '\\'").ToList();
            sb.Append($" AND ({(conditions.Count == 0 ? "FALSE" : string.Join(" OR ", conditions))})");
            for (var i = 0; i < context.AllowedIndexes.Count; i++)
            {
                var escaped = context.AllowedIndexes[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
                p.Add($"AllowedIndex{i}", escaped.EndsWith('*') ? $"{escaped[..^1]}%" : escaped);
            }
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
