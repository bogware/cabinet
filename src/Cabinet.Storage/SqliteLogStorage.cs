using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-based storage implementation with partitioning support
/// </summary>
public partial class SqliteLogStorage : ILogStorage
{
    private readonly StorageConfiguration _config;
    private readonly ILogger<SqliteLogStorage> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteLogStorage(
        IOptions<CabinetConfiguration> config,
        ILogger<SqliteLogStorage> logger)
    {
        _config = config.Value.Storage;
        _logger = logger;

        // Enable connection pooling via Cache=Shared for concurrent read performance.
        // WAL mode lets readers and a writer run concurrently. Shared cache must stay off: it
        // makes an open reader block writers with SQLITE_LOCKED, which fails ingest.
        _connectionString = _config.ConnectionString;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (!_initialized)
            {
                await InitializeAsync();
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Current schema version. Increment when adding migrations.</summary>
    private const int CurrentSchemaVersion = 2;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await connection.ExecuteAsync("PRAGMA synchronous=NORMAL;");

        // Schema version tracking table
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS _schema_version (
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            );
        ");

        var currentVersion = await connection.ExecuteScalarAsync<int?>(
            "SELECT MAX(version) FROM _schema_version") ?? 0;

        // v1: Initial schema (log_events + FTS + indexes)
        if (currentVersion < 1)
        {
            await ApplyMigrationV1(connection);
        }

        // v2: Full-text index on rowid, and paging by time and ID
        if (currentVersion < 2)
        {
            await ApplyMigrationV2(connection);
        }

        // Verify FTS5 integrity and rebuild if corrupted
        await VerifyAndRepairFtsAsync(connection);

        _logger.LogInformation("Storage initialized (schema v{Version})", CurrentSchemaVersion);
    }

    private async Task VerifyAndRepairFtsAsync(SqliteConnection connection)
    {
        try
        {
            // Quick integrity check — tries a simple FTS query
            await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM log_events_fts WHERE log_events_fts MATCH '\"__integrity_check__\"'");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FTS5 index corrupted, rebuilding...");
            try
            {
                await connection.ExecuteAsync("INSERT INTO log_events_fts(log_events_fts) VALUES ('rebuild')");
                _logger.LogInformation("FTS5 index rebuilt successfully");
            }
            catch (Exception rebuildEx)
            {
                _logger.LogError(rebuildEx, "FTS5 rebuild failed — full-text search may be unavailable");
            }
        }
    }

    /// <summary>
    /// Version 1's triggers wrote to the full-text index without a rowid. The index is external
    /// content, so it finds rows by rowid, and after a purge a search could return the wrong events
    /// or none. This rebuilds the index from the events, and adds the index that pages by time.
    /// </summary>
    private static async Task ApplyMigrationV2(SqliteConnection connection)
    {
        await connection.ExecuteAsync(@"
            DROP TRIGGER IF EXISTS log_events_ai;
            DROP TRIGGER IF EXISTS log_events_ad;
            DROP TRIGGER IF EXISTS log_events_au;
            DROP TABLE IF EXISTS log_events_fts;

            CREATE VIRTUAL TABLE log_events_fts USING fts5(
                raw_line, content=log_events, content_rowid=rowid
            );

            CREATE TRIGGER log_events_ai AFTER INSERT ON log_events
            BEGIN
                INSERT INTO log_events_fts(rowid, raw_line)
                VALUES (new.rowid, new.raw_line);
            END;

            CREATE TRIGGER log_events_ad AFTER DELETE ON log_events
            BEGIN
                INSERT INTO log_events_fts(log_events_fts, rowid, raw_line)
                VALUES ('delete', old.rowid, old.raw_line);
            END;

            CREATE TRIGGER log_events_au AFTER UPDATE ON log_events
            BEGIN
                INSERT INTO log_events_fts(log_events_fts, rowid, raw_line)
                VALUES ('delete', old.rowid, old.raw_line);
                INSERT INTO log_events_fts(rowid, raw_line)
                VALUES (new.rowid, new.raw_line);
            END;

            INSERT INTO log_events_fts(log_events_fts) VALUES ('rebuild');

            CREATE INDEX IF NOT EXISTS idx_timestamp_id ON log_events(timestamp DESC, id DESC);

            -- Without this, a query on one index reads all of it and sorts, for every page
            CREATE INDEX IF NOT EXISTS idx_index_timestamp_id ON log_events(index_name, timestamp DESC, id DESC);
            DROP INDEX IF EXISTS idx_timestamp;
        ");

        await connection.ExecuteAsync(
            "INSERT INTO _schema_version (version, applied_at, description) VALUES (@v, @at, @desc)",
            new { v = 2, at = DateTime.UtcNow.ToString("o"), desc = "Rebuild FTS on rowid; page by timestamp and id" });
    }

    private static async Task ApplyMigrationV1(SqliteConnection connection)
    {
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS log_events (
                id TEXT PRIMARY KEY,
                timestamp INTEGER NOT NULL,
                source_type TEXT NOT NULL,
                server_id TEXT,
                severity INTEGER NOT NULL,
                raw_line TEXT NOT NULL,
                source_file TEXT,
                line_number INTEGER,
                fields TEXT,
                tags TEXT,
                parser_name TEXT,
                partition_id INTEGER,
                index_time INTEGER NOT NULL,
                index_name TEXT NOT NULL DEFAULT 'main'
            );

            CREATE INDEX IF NOT EXISTS idx_timestamp ON log_events(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_source_type ON log_events(source_type);
            CREATE INDEX IF NOT EXISTS idx_severity ON log_events(severity);
            CREATE INDEX IF NOT EXISTS idx_server_id ON log_events(server_id);
            CREATE INDEX IF NOT EXISTS idx_partition ON log_events(partition_id);
            CREATE INDEX IF NOT EXISTS idx_index_name ON log_events(index_name);

            -- Composite indexes for common multi-column query patterns
            CREATE INDEX IF NOT EXISTS idx_ts_severity ON log_events(timestamp DESC, severity);
            CREATE INDEX IF NOT EXISTS idx_ts_source ON log_events(timestamp DESC, source_type);
            CREATE INDEX IF NOT EXISTS idx_ts_server ON log_events(timestamp DESC, server_id);

            -- Full-text search
            CREATE VIRTUAL TABLE IF NOT EXISTS log_events_fts USING fts5(
                id, raw_line, fields, content=log_events
            );

            -- Trigger to keep FTS in sync
            CREATE TRIGGER IF NOT EXISTS log_events_ai AFTER INSERT ON log_events
            BEGIN
                INSERT INTO log_events_fts(id, raw_line, fields)
                VALUES (new.id, new.raw_line, new.fields);
            END;

            CREATE TRIGGER IF NOT EXISTS log_events_ad AFTER DELETE ON log_events
            BEGIN
                INSERT INTO log_events_fts(log_events_fts, id, raw_line, fields)
                VALUES ('delete', old.id, old.raw_line, old.fields);
            END;

            -- Keep FTS in sync on UPDATE (e.g. field enrichment after initial ingest)
            CREATE TRIGGER IF NOT EXISTS log_events_au AFTER UPDATE ON log_events
            BEGIN
                INSERT INTO log_events_fts(log_events_fts, id, raw_line, fields)
                VALUES ('delete', old.id, old.raw_line, old.fields);
                INSERT INTO log_events_fts(id, raw_line, fields)
                VALUES (new.id, new.raw_line, new.fields);
            END;
        ";

        await connection.ExecuteAsync(createTableSql);

        await connection.ExecuteAsync(
            "INSERT INTO _schema_version (version, applied_at, description) VALUES (@v, @at, @desc)",
            new { v = 1, at = DateTime.UtcNow.ToString("o"), desc = "Initial schema: log_events table, indexes, FTS" });
    }

    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Use a transaction so the main table insert + FTS trigger are atomic
            using var transaction = connection.BeginTransaction();

            var sql = @"
                INSERT INTO log_events (
                    id, timestamp, source_type, server_id, severity,
                    raw_line, source_file, line_number, fields, tags,
                    parser_name, partition_id, index_time, index_name
                ) VALUES (
                    @Id, @Timestamp, @SourceType, @Host, @Severity,
                    @RawLine, @SourceFile, @LineNumber, @Fields, @Tags,
                    @ParserName, @PartitionId, @IndexTime, @IndexName
                )";

            var parameters = MapEventToParameters(logEvent);
            var result = await connection.ExecuteAsync(sql, parameters, transaction);

            await transaction.CommitAsync(cancellationToken);
            return result > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store log event {Id}", logEvent.Id);
            return false;
        }
    }

    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        var eventList = events.ToList();
        if (!eventList.Any()) return 0;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            using var transaction = connection.BeginTransaction();

            var sql = @"
                INSERT OR IGNORE INTO log_events (
                    id, timestamp, source_type, server_id, severity,
                    raw_line, source_file, line_number, fields, tags,
                    parser_name, partition_id, index_time, index_name
                ) VALUES (
                    @Id, @Timestamp, @SourceType, @Host, @Severity,
                    @RawLine, @SourceFile, @LineNumber, @Fields, @Tags,
                    @ParserName, @PartitionId, @IndexTime, @IndexName
                )";

            var parameters = eventList.Select(MapEventToParameters);
            var result = await connection.ExecuteAsync(sql, parameters, transaction);

            await transaction.CommitAsync(cancellationToken);

            // INSERT OR IGNORE silently drops duplicates (idempotent batch insert).
            // Log when duplicates are skipped so operators can detect unexpected re-ingestion.
            var skipped = eventList.Count - result;
            if (skipped > 0)
            {
                _logger.LogDebug(
                    "StoreBatchAsync: {Inserted}/{Total} events inserted, {Skipped} duplicates skipped",
                    result, eventList.Count, skipped);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store batch of {Count} events", eventList.Count);
            throw;
        }
    }

    public async IAsyncEnumerable<LogEvent> QueryAsync(
        QueryContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = BuildQuerySql(context);
        var parameters = BuildQueryParameters(context);

        IDataReader reader;
        try
        {
            reader = await connection.ExecuteReaderAsync(sql, parameters);
        }
        catch (SqliteException ex) when (!string.IsNullOrEmpty(context.SearchText) && ex.SqliteErrorCode == 11)
        {
            // FTS5 index corrupted — fall back to LIKE-based search
            _logger.LogWarning(ex, "FTS5 query failed, falling back to LIKE search");
            sql = BuildQuerySql(context, useFts: false);
            reader = await connection.ExecuteReaderAsync(sql, parameters);
        }

        using (reader)
        {
            while (await ((DbDataReader)reader).ReadAsync(cancellationToken))
            {
                yield return MapReaderToEvent(reader);
            }
        }
    }

    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = "SELECT * FROM log_events WHERE id = @Id LIMIT 1";
        using var reader = await connection.ExecuteReaderAsync(sql, new { Id = id });

        if (await reader.ReadAsync(cancellationToken))
        {
            return MapReaderToEvent(reader);
        }

        return null;
    }

    public async Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = "DELETE FROM log_events WHERE index_name = @IndexName";
        return await connection.ExecuteAsync(sql, new { IndexName = indexName });
    }

    public async Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        if (context == null)
        {
            return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM log_events");
        }

        var sql = BuildCountSql(context);
        var parameters = BuildQueryParameters(context);
        return await connection.ExecuteScalarAsync<long>(sql, parameters);
    }

    public async Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = "DELETE FROM log_events WHERE timestamp < @Before";
        var beforeTicks = before.Ticks;
        return await connection.ExecuteAsync(sql, new { Before = beforeTicks });
    }

    public async Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var stats = new StorageStats();

        // Get total count
        stats.TotalEvents = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM log_events");

        // Get date range
        var dateRangeSql = "SELECT MIN(timestamp), MAX(timestamp) FROM log_events";
        var (minTicks, maxTicks) = await connection.QuerySingleAsync<(long?, long?)>(dateRangeSql);

        if (minTicks.HasValue)
            stats.OldestEvent = new DateTime(minTicks.Value);
        if (maxTicks.HasValue)
            stats.NewestEvent = new DateTime(maxTicks.Value);

        // Get counts by source type
        var sourceTypeSql = "SELECT source_type, COUNT(*) FROM log_events GROUP BY source_type";
        var sourceTypeCounts = await connection.QueryAsync<(string, long)>(sourceTypeSql);
        foreach (var (sourceType, count) in sourceTypeCounts)
        {
            stats.EventsBySourceType[sourceType] = count;
        }

        // Get counts by index
        var indexSql = "SELECT index_name, COUNT(*) FROM log_events GROUP BY index_name";
        var indexCounts = await connection.QueryAsync<(string, long)>(indexSql);
        foreach (var (indexName, count) in indexCounts)
        {
            stats.EventsByIndex[indexName] = count;
        }

        // Get counts by severity
        var severitySql = "SELECT severity, COUNT(*) FROM log_events GROUP BY severity";
        var severityCounts = await connection.QueryAsync<(int, long)>(severitySql);
        foreach (var (severity, count) in severityCounts)
        {
            stats.EventsBySeverity[(LogSeverity)severity] = count;
        }

        // Get storage size
        var sizeSql = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        stats.StorageSizeBytes = await connection.ExecuteScalarAsync<long>(sizeSql);

        return stats;
    }

    /// <summary>Turns an index pattern into a LIKE pattern, escaping the text around its wildcard.</summary>
    private static string IndexPatternToLike(string pattern)
    {
        var escaped = pattern.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        return escaped.EndsWith('*') ? $"{escaped[..^1]}%" : escaped;
    }

    private string BuildQuerySql(QueryContext context, bool useFts = true)
    {
        var sql = new StringBuilder();
        sql.Append("SELECT * FROM ");

        if (SearchTerms.Parse(context.SearchText).Count > 0 && useFts)
        {
            sql.Append(@"log_events WHERE rowid IN (
                SELECT rowid FROM log_events_fts WHERE log_events_fts MATCH @SearchText
            )");
        }
        else if (!string.IsNullOrEmpty(context.SearchText))
        {
            // Fallback: LIKE-based search when FTS is unavailable
            sql.Append("log_events WHERE (raw_line LIKE @SearchTextLike OR fields LIKE @SearchTextLike)");
        }
        else
        {
            sql.Append("log_events WHERE 1=1");
        }

        if (context.StartTime.HasValue)
            sql.Append(" AND timestamp >= @StartTime");

        if (context.EndTime.HasValue)
            sql.Append(" AND timestamp <= @EndTime");

        if (!string.IsNullOrEmpty(context.SourceType))
            sql.Append(" AND source_type = @SourceType COLLATE NOCASE");

        if (context.MinSeverity.HasValue)
            sql.Append(" AND severity >= @MinSeverity");

        if (!string.IsNullOrEmpty(context.Host))
            sql.Append(" AND server_id = @Host COLLATE NOCASE");

        if (!string.IsNullOrEmpty(context.Index))
            sql.Append(" AND index_name = @IndexName");

        if (context.AllowedIndexes != null)
        {
            var conditions = context.AllowedIndexes.Select((_, i) => $"index_name LIKE @AllowedIndex{i} ESCAPE '\\'");
            sql.Append($" AND ({string.Join(" OR ", conditions.DefaultIfEmpty("1=0"))})");
        }

        if (context.FieldFilters != null)
        {
            var fieldIdx = 0;
            foreach (var filter in context.FieldFilters)
            {
                // Validate field name to prevent JSON path injection — only allow alphanumeric, underscore, dot
                if (!SafeFieldNameRegex().IsMatch(filter.Key))
                {
                    _logger.LogWarning("Skipping invalid field filter key: {Key}", filter.Key);
                    continue;
                }
                sql.Append($" AND json_extract(fields, '$.{filter.Key}') = @FieldFilter{fieldIdx}");
                fieldIdx++;
            }
        }

        // Apply tag filters — AND semantics: event must contain ALL specified tags
        if (context.Tags is { Count: > 0 })
        {
            var tagIdx = 0;
            foreach (var _ in context.Tags)
            {
                sql.Append($" AND EXISTS (SELECT 1 FROM json_each(tags) WHERE json_each.value = @Tag{tagIdx})");
                tagIdx++;
            }
        }

        // Validate sort field to prevent SQL injection
        var allowedSortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "timestamp", "severity", "source_type", "event_type", "server_id", "id"
        };

        var sortField = allowedSortFields.Contains(context.SortField)
            ? context.SortField
            : "timestamp";

        var direction = context.SortDescending ? "DESC" : "ASC";
        if (context.After != null)
        {
            sql.Append(context.SortDescending
                ? " AND (timestamp, id) < (@AfterTimestamp, @AfterId)"
                : " AND (timestamp, id) > (@AfterTimestamp, @AfterId)");
            sortField = "timestamp";
        }

        // ID breaks ties, so pages of events with the same time are stable
        sql.Append($" ORDER BY {sortField} {direction}, id {direction}");
        sql.Append($" LIMIT {context.Limit} OFFSET {context.Skip}");

        return sql.ToString();
    }

    private string BuildCountSql(QueryContext context)
    {
        var sql = new StringBuilder();

        if (!string.IsNullOrEmpty(context.SearchText))
        {
            sql.Append(@"SELECT COUNT(*) FROM log_events WHERE rowid IN (
                SELECT rowid FROM log_events_fts WHERE log_events_fts MATCH @SearchText
            )");
        }
        else
        {
            sql.Append("SELECT COUNT(*) FROM log_events WHERE 1=1");
        }

        if (context.StartTime.HasValue)
            sql.Append(" AND timestamp >= @StartTime");

        if (context.EndTime.HasValue)
            sql.Append(" AND timestamp <= @EndTime");

        if (!string.IsNullOrEmpty(context.SourceType))
            sql.Append(" AND source_type = @SourceType COLLATE NOCASE");

        if (context.MinSeverity.HasValue)
            sql.Append(" AND severity >= @MinSeverity");

        if (!string.IsNullOrEmpty(context.Host))
            sql.Append(" AND server_id = @Host COLLATE NOCASE");

        if (!string.IsNullOrEmpty(context.Index))
            sql.Append(" AND index_name = @IndexName");

        if (context.AllowedIndexes != null)
        {
            var conditions = context.AllowedIndexes.Select((_, i) => $"index_name LIKE @AllowedIndex{i} ESCAPE '\\'");
            sql.Append($" AND ({string.Join(" OR ", conditions.DefaultIfEmpty("1=0"))})");
        }

        // Apply field filters (matching QueryAsync behavior)
        if (context.FieldFilters != null)
        {
            var fieldIdx = 0;
            foreach (var filter in context.FieldFilters)
            {
                if (!SafeFieldNameRegex().IsMatch(filter.Key))
                {
                    _logger.LogWarning("Skipping invalid field filter key in count query: {Key}", filter.Key);
                    continue;
                }
                sql.Append($" AND json_extract(fields, '$.{filter.Key}') = @FieldFilter{fieldIdx}");
                fieldIdx++;
            }
        }

        // Apply tag filters — AND semantics: event must contain ALL specified tags
        if (context.Tags is { Count: > 0 })
        {
            var tagIdx = 0;
            foreach (var _ in context.Tags)
            {
                sql.Append($" AND EXISTS (SELECT 1 FROM json_each(tags) WHERE json_each.value = @Tag{tagIdx})");
                tagIdx++;
            }
        }

        return sql.ToString();
    }

    private object BuildQueryParameters(QueryContext context)
    {
        var parameters = new DynamicParameters();
        // Wrap search text in double quotes to escape FTS5 query syntax operators (AND, OR, NOT, *)
        // Every term has to appear in the line, as on the other backends
        var terms = SearchTerms.Parse(context.SearchText);
        var safeSearchText = terms.Count > 0
            ? "raw_line : (" + string.Join(" AND ", terms.Select(term => $"\"{term}\"")) + ")"
            : null;
        parameters.Add("SearchText", safeSearchText);
        parameters.Add("AfterTimestamp", context.After?.Timestamp.Ticks);
        var allowed = context.AllowedIndexes ?? [];
        for (var i = 0; i < allowed.Count; i++)
        {
            parameters.Add($"AllowedIndex{i}", IndexPatternToLike(allowed[i]));
        }

        parameters.Add("AfterId", context.After?.Id);
        // LIKE-based fallback parameter (used when FTS is unavailable)
        parameters.Add("SearchTextLike", context.SearchText != null ? $"%{context.SearchText}%" : null);
        parameters.Add("StartTime", context.StartTime?.Ticks);
        parameters.Add("EndTime", context.EndTime?.Ticks);
        parameters.Add("SourceType", context.SourceType);
        parameters.Add("MinSeverity", (int?)context.MinSeverity);
        parameters.Add("Host", context.Host);
        parameters.Add("IndexName", context.Index);

        if (context.FieldFilters != null)
        {
            var fieldIdx = 0;
            foreach (var filter in context.FieldFilters)
            {
                if (!SafeFieldNameRegex().IsMatch(filter.Key))
                    continue;
                parameters.Add($"FieldFilter{fieldIdx}", filter.Value?.ToString());
                fieldIdx++;
            }
        }

        if (context.Tags is { Count: > 0 })
        {
            var tagIdx = 0;
            foreach (var tag in context.Tags)
            {
                parameters.Add($"Tag{tagIdx}", tag);
                tagIdx++;
            }
        }

        return parameters;
    }

    private object MapEventToParameters(LogEvent logEvent)
    {
        var partitionId = _config.EnablePartitioning ?
            logEvent.Timestamp.Ticks / (TimeSpan.TicksPerHour * _config.PartitionHours) : 0;

        return new
        {
            logEvent.Id,
            Timestamp = logEvent.Timestamp.Ticks,
            SourceType = logEvent.SourceType,
            Host = logEvent.Host,
            Severity = (int)logEvent.Severity,
            logEvent.RawLine,
            logEvent.SourceFile,
            logEvent.LineNumber,
            Fields = JsonSerializer.Serialize(logEvent.Fields),
            Tags = JsonSerializer.Serialize(logEvent.Tags),
            logEvent.ParserName,
            PartitionId = partitionId,
            IndexTime = logEvent.IndexTime.Ticks,
            IndexName = logEvent.Index
        };
    }

    /// <summary>Only allow safe characters in field names for JSON path queries</summary>
    [GeneratedRegex(@"^[a-zA-Z0-9_][a-zA-Z0-9_.]*$")]
    private static partial Regex SafeFieldNameRegex();

    private LogEvent MapReaderToEvent(IDataReader reader)
    {
        var logEvent = new LogEvent
        {
            Id = reader.GetString(0),
            Timestamp = new DateTime(reader.GetInt64(1)),
            SourceType = reader.GetString(2),
            Host = reader.IsDBNull(3) ? null : reader.GetString(3),
            Severity = (LogSeverity)reader.GetInt32(4),
            RawLine = reader.GetString(5),
            SourceFile = reader.IsDBNull(6) ? null : reader.GetString(6),
            LineNumber = reader.GetInt64(7),
            ParserName = reader.IsDBNull(10) ? null : reader.GetString(10),
            IndexTime = reader.FieldCount > 12 && !reader.IsDBNull(12)
                ? new DateTime(reader.GetInt64(12))
                : DateTime.UtcNow,
            Index = reader.FieldCount > 13 && !reader.IsDBNull(13)
                ? reader.GetString(13)
                : "main"
        };

        var fieldsJson = reader.IsDBNull(8) ? null : reader.GetString(8);
        if (!string.IsNullOrEmpty(fieldsJson))
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fieldsJson);
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    logEvent.Fields[field.Key] = JsonFieldValue.Unwrap(field.Value);
                }
            }
        }

        var tagsJson = reader.IsDBNull(9) ? null : reader.GetString(9);
        if (!string.IsNullOrEmpty(tagsJson))
        {
            var tags = JsonSerializer.Deserialize<HashSet<string>>(tagsJson);
            if (tags != null)
            {
                logEvent.Tags = tags;
            }
        }

        return logEvent;
    }
}