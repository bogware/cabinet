#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using System.Data;
using System.Text;
using System.Text.Json;
using Cabinet.Core.Models;
using Cabinet.Database.Abstractions;
using Cabinet.Database.Configuration;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Polly;
using Polly.Retry;

namespace Cabinet.Database.Providers;

/// <summary>
/// PostgreSQL database provider implementation
/// </summary>
public class PostgreSQLProvider : ILogDatabaseProvider
{
    private readonly DatabaseConfiguration _config;
    private readonly ILogger<PostgreSQLProvider> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly NpgsqlDataSource _dataSource;

    public DatabaseProviderType ProviderType => DatabaseProviderType.PostgreSQL;

    public PostgreSQLProvider(DatabaseConfiguration config, ILogger<PostgreSQLProvider> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use NpgsqlDataSource for proper connection pooling (thread-safe)
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(config.ConnectionString);
        _dataSource = dataSourceBuilder.Build();

        _retryPolicy = Policy
            .Handle<NpgsqlException>()
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception,
                        "PostgreSQL operation failed. Retry {RetryCount} after {TimeSpan}ms",
                        retryCount, timeSpan.TotalMilliseconds);
                });
    }

    private async Task<NpgsqlConnection> GetConnectionAsync()
    {
        var connection = await _dataSource.OpenConnectionAsync();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing PostgreSQL database");

        var sql = @"
            CREATE TABLE IF NOT EXISTS cabinet_logs (
                id BIGSERIAL PRIMARY KEY,
                timestamp TIMESTAMPTZ NOT NULL,
                source_type VARCHAR(50) NOT NULL,
                severity VARCHAR(20) NOT NULL,
                message TEXT NOT NULL,
                fields JSONB,
                tags TEXT[],
                raw_log TEXT,
                created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_timestamp ON cabinet_logs (timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_source_type ON cabinet_logs (source_type);
            CREATE INDEX IF NOT EXISTS idx_severity ON cabinet_logs (severity);
            CREATE INDEX IF NOT EXISTS idx_tags ON cabinet_logs USING GIN (tags);
            CREATE INDEX IF NOT EXISTS idx_fields ON cabinet_logs USING GIN (fields);

            CREATE INDEX IF NOT EXISTS idx_fulltext ON cabinet_logs
                USING GIN (to_tsvector('english', message || ' ' || COALESCE(raw_log, '')));

            CREATE TABLE IF NOT EXISTS cabinet_metadata (
                key VARCHAR(255) PRIMARY KEY,
                value JSONB NOT NULL,
                updated_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
            );";

        await using var conn = await GetConnectionAsync();
        await _retryPolicy.ExecuteAsync(async () =>
        {
            await conn.ExecuteAsync(sql, commandTimeout: _config.CommandTimeout);
        });

        _logger.LogInformation("PostgreSQL database initialized successfully");
    }

    public async Task<int> InsertBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (!eventList.Any()) return 0;

        _logger.LogDebug("Inserting batch of {Count} events", eventList.Count);

        var sql = @"
            INSERT INTO cabinet_logs (timestamp, source_type, severity, message, fields, tags, raw_log)
            VALUES (@Timestamp, @SourceType, @Severity, @Message, @Fields::jsonb, @Tags, @RawLog)";

        await using var conn = await GetConnectionAsync();
        var inserted = 0;

        // Process in batches to avoid parameter limit
        foreach (var batch in eventList.Chunk(_config.BatchSize))
        {
            var parameters = batch.Select(e => new
            {
                Timestamp = e.Timestamp,
                SourceType = e.SourceType.ToString(),
                Severity = e.Severity.ToString(),
                Message = e.RawLine,
                Fields = JsonSerializer.Serialize(e.Fields),
                Tags = e.Tags.ToArray(),
                RawLog = e.RawLine
            });

            inserted += await _retryPolicy.ExecuteAsync(async () =>
            {
                return await conn.ExecuteAsync(sql, parameters, commandTimeout: _config.CommandTimeout);
            });
        }

        _logger.LogDebug("Inserted {Count} events", inserted);
        return inserted;
    }

    public async Task<IEnumerable<LogEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? searchText = null,
        Dictionary<string, string>? filters = null,
        int limit = 1000,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT * FROM cabinet_logs WHERE 1=1");
        var parameters = new DynamicParameters();

        if (startTime.HasValue)
        {
            sql.Append(" AND timestamp >= @StartTime");
            parameters.Add("StartTime", startTime.Value);
        }

        if (endTime.HasValue)
        {
            sql.Append(" AND timestamp <= @EndTime");
            parameters.Add("EndTime", endTime.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(" AND to_tsvector('english', message || ' ' || COALESCE(raw_log, '')) @@ plainto_tsquery('english', @SearchText)");
            parameters.Add("SearchText", searchText);
        }

        if (filters != null && filters.Any())
        {
            var filterIndex = 0;
            foreach (var filter in filters)
            {
                // Validate field name to prevent SQL injection — only allow alphanumeric and underscores
                if (!System.Text.RegularExpressions.Regex.IsMatch(filter.Key, @"^[a-zA-Z_][a-zA-Z0-9_]*$"))
                {
                    _logger.LogWarning("Skipping invalid filter key: {Key}", filter.Key);
                    continue;
                }

                var paramName = $"FilterValue{filterIndex}";
                var keyParamName = $"FilterKey{filterIndex}";
                sql.Append($" AND fields->>@{keyParamName} = @{paramName}");
                parameters.Add(keyParamName, filter.Key);
                parameters.Add(paramName, filter.Value);
                filterIndex++;
            }
        }

        sql.Append(" ORDER BY timestamp DESC");
        sql.Append(" LIMIT @Limit OFFSET @Offset");
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        await using var conn = await GetConnectionAsync();
        var results = await _retryPolicy.ExecuteAsync(async () =>
        {
            return await conn.QueryAsync<dynamic>(sql.ToString(), parameters, commandTimeout: _config.CommandTimeout);
        });

        return results.Select(r => MapToLogEvent(r)).Cast<LogEvent>().ToList();
    }

    public async Task<long> GetCountAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? searchText = null,
        Dictionary<string, string>? filters = null,
        CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT COUNT(*) FROM cabinet_logs WHERE 1=1");
        var parameters = new DynamicParameters();

        if (startTime.HasValue)
        {
            sql.Append(" AND timestamp >= @StartTime");
            parameters.Add("StartTime", startTime.Value);
        }

        if (endTime.HasValue)
        {
            sql.Append(" AND timestamp <= @EndTime");
            parameters.Add("EndTime", endTime.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            sql.Append(" AND to_tsvector('english', message || ' ' || COALESCE(raw_log, '')) @@ plainto_tsquery('english', @SearchText)");
            parameters.Add("SearchText", searchText);
        }

        await using var conn = await GetConnectionAsync();
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            return await conn.ExecuteScalarAsync<long>(sql.ToString(), parameters, commandTimeout: _config.CommandTimeout);
        });
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting logs older than {CutoffDate}", cutoffDate);

        var sql = "DELETE FROM cabinet_logs WHERE timestamp < @CutoffDate";

        await using var conn = await GetConnectionAsync();
        var deleted = await _retryPolicy.ExecuteAsync(async () =>
        {
            return await conn.ExecuteAsync(sql, new { CutoffDate = cutoffDate }, commandTimeout: _config.CommandTimeout);
        });

        _logger.LogInformation("Deleted {Count} logs older than {CutoffDate}", deleted, cutoffDate);
        return deleted;
    }

    public async Task<string> ArchiveLogsAsync(
        DateTime startDate,
        DateTime endDate,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Archiving logs from {StartDate} to {EndDate}", startDate, endDate);

        var events = await QueryAsync(startDate, endDate, limit: int.MaxValue, cancellationToken: cancellationToken);
        var eventList = events.ToList();

        if (!eventList.Any())
        {
            _logger.LogWarning("No logs found to archive");
            return string.Empty;
        }

        var fileName = $"archive_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}_{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl";
        var fullPath = Path.Combine(archivePath, fileName);

        Directory.CreateDirectory(archivePath);

        if (_config.Archive.Format.Contains("gz"))
        {
            fullPath += ".gz";
            await using var fileStream = File.Create(fullPath);
            await using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionLevel.Optimal);
            await using var writer = new StreamWriter(gzipStream);

            foreach (var evt in eventList)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(evt));
            }
        }
        else
        {
            await using var writer = new StreamWriter(fullPath);
            foreach (var evt in eventList)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(evt));
            }
        }

        _logger.LogInformation("Archived {Count} logs to {Path}", eventList.Count, fullPath);
        return fullPath;
    }

    public async Task<string> ExportAsync(
        DateTime? startTime,
        DateTime? endTime,
        ExportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting logs in {Format} format", format);

        var events = await QueryAsync(startTime, endTime, limit: int.MaxValue, cancellationToken: cancellationToken);
        var eventList = events.ToList();

        if (!eventList.Any())
        {
            _logger.LogWarning("No logs found to export");
            return string.Empty;
        }

        var fileName = $"export_{DateTime.UtcNow:yyyyMMddHHmmss}.{format.ToString().ToLower()}";
        var fullPath = Path.Combine(outputPath, fileName);

        Directory.CreateDirectory(outputPath);

        switch (format)
        {
            case ExportFormat.JSON:
                await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(eventList, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                break;

            case ExportFormat.JSONL:
                await using (var writer = new StreamWriter(fullPath))
                {
                    foreach (var evt in eventList)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(evt));
                    }
                }
                break;

            case ExportFormat.CSV:
                await ExportToCsvAsync(eventList, fullPath);
                break;

            case ExportFormat.TSV:
                await ExportToTsvAsync(eventList, fullPath);
                break;

            default:
                throw new NotSupportedException($"Export format {format} is not yet supported");
        }

        _logger.LogInformation("Exported {Count} logs to {Path}", eventList.Count, fullPath);
        return fullPath;
    }

    public async Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await GetConnectionAsync();

        var stats = new DatabaseStatistics();

        // Get total count
        stats.TotalEvents = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM cabinet_logs");

        // Get date range
        var dateRange = await conn.QueryFirstOrDefaultAsync<dynamic>(
            "SELECT MIN(timestamp) as min_date, MAX(timestamp) as max_date FROM cabinet_logs");

        if (dateRange != null)
        {
            stats.OldestEvent = dateRange.min_date ?? DateTime.MinValue;
            stats.NewestEvent = dateRange.max_date ?? DateTime.MaxValue;
        }

        // Get events by type
        var byType = await conn.QueryAsync<dynamic>(
            "SELECT source_type, COUNT(*) as count FROM cabinet_logs GROUP BY source_type");
        stats.EventsByType = byType.ToDictionary(x => (string)x.source_type, x => (long)x.count);

        // Get events by severity
        var bySeverity = await conn.QueryAsync<dynamic>(
            "SELECT severity, COUNT(*) as count FROM cabinet_logs GROUP BY severity");
        stats.EventsBySeverity = bySeverity.ToDictionary(x => (string)x.severity, x => (long)x.count);

        // Get database size
        var sizeResult = await conn.QueryFirstOrDefaultAsync<long>(
            "SELECT pg_database_size(current_database())");
        stats.DatabaseSizeBytes = sizeResult;

        return stats;
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Optimizing PostgreSQL database");

        await using var conn = await GetConnectionAsync();

        // Vacuum and analyze tables
        await conn.ExecuteAsync("VACUUM ANALYZE cabinet_logs");

        // Reindex
        await conn.ExecuteAsync("REINDEX TABLE cabinet_logs");

        _logger.LogInformation("PostgreSQL database optimization completed");
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new HealthCheckResult();

        try
        {
            await using var conn = await GetConnectionAsync();

            // Check connection
            var pingResult = await conn.ExecuteScalarAsync<int>("SELECT 1");
            if (pingResult != 1)
            {
                result.IsHealthy = false;
                result.Issues.Add("Database connection test failed");
            }

            // Check table exists
            var tableExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'cabinet_logs')");

            if (!tableExists)
            {
                result.IsHealthy = false;
                result.Issues.Add("Required table 'cabinet_logs' does not exist");
            }

            // Get database stats
            var stats = await GetStatisticsAsync(cancellationToken);
            result.Metadata["total_events"] = stats.TotalEvents;
            result.Metadata["database_size_mb"] = stats.DatabaseSizeBytes / (1024 * 1024);

            if (result.Issues.Count == 0)
            {
                result.IsHealthy = true;
                result.Status = "Healthy";
            }
            else
            {
                result.Status = "Unhealthy";
            }
        }
        catch (Exception ex)
        {
            result.IsHealthy = false;
            result.Status = "Error";
            result.Issues.Add($"Health check failed: {ex.Message}");
        }

        return result;
    }

    private LogEvent MapToLogEvent(dynamic row)
    {
        var logEvent = new LogEvent
        {
            Timestamp = row.timestamp,
            SourceType = row.source_type?.ToString() ?? SourceTypes.Generic,
            Severity = Enum.TryParse<LogSeverity>(row.severity?.ToString(), true, out LogSeverity sev) ? sev : LogSeverity.Info,
            RawLine = row.message ?? row.raw_log
        };

        if (row.fields != null)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(row.fields.ToString());
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    logEvent.SetField(field.Key, field.Value?.ToString() ?? string.Empty);
                }
            }
        }

        if (row.tags != null)
        {
            logEvent.Tags = new HashSet<string>((string[])row.tags);
        }

        return logEvent;
    }

    private async Task ExportToCsvAsync(List<LogEvent> events, string path)
    {
        await using var writer = new StreamWriter(path);

        // Write header
        await writer.WriteLineAsync("Timestamp,SourceType,Severity,Message,Tags,Fields");

        // Write data
        foreach (var evt in events)
        {
            var fields = JsonSerializer.Serialize(evt.Fields);
            var tags = string.Join(";", evt.Tags);
            var message = evt.RawLine.Replace("\"", "\"\""); // Escape quotes

            await writer.WriteLineAsync($"\"{evt.Timestamp:O}\",\"{evt.SourceType}\",\"{evt.Severity}\",\"{message}\",\"{tags}\",\"{fields}\"");
        }
    }

    private async Task ExportToTsvAsync(List<LogEvent> events, string path)
    {
        await using var writer = new StreamWriter(path);

        // Write header
        await writer.WriteLineAsync("Timestamp\tSourceType\tSeverity\tMessage\tTags\tFields");

        // Write data
        foreach (var evt in events)
        {
            var fields = JsonSerializer.Serialize(evt.Fields);
            var tags = string.Join(";", evt.Tags);
            var message = evt.RawLine.Replace("\t", " "); // Replace tabs

            await writer.WriteLineAsync($"{evt.Timestamp:O}\t{evt.SourceType}\t{evt.Severity}\t{message}\t{tags}\t{fields}");
        }
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }
}