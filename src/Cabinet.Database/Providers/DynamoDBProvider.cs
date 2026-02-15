#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;
using Cabinet.Database.Abstractions;
using Cabinet.Database.Configuration;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Cabinet.Database.Providers;

/// <summary>
/// DynamoDB database provider implementation
/// </summary>
public class DynamoDBProvider : ILogDatabaseProvider
{
    private readonly DatabaseConfiguration _config;
    private readonly ILogger<DynamoDBProvider> _logger;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly string _tableName;

    public DatabaseProviderType ProviderType => DatabaseProviderType.DynamoDB;

    public DynamoDBProvider(DatabaseConfiguration config, ILogger<DynamoDBProvider> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dynamoConfig = _config.DynamoDB ?? new DynamoDBConfiguration();
        _tableName = dynamoConfig.TableName;

        var clientConfig = new AmazonDynamoDBConfig();
        if (!string.IsNullOrEmpty(dynamoConfig.Region))
        {
            clientConfig.RegionEndpoint = RegionEndpoint.GetBySystemName(dynamoConfig.Region);
        }

        _dynamoClient = new AmazonDynamoDBClient(clientConfig);

        _retryPolicy = Policy
            .Handle<AmazonDynamoDBException>()
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception,
                        "DynamoDB operation failed. Retry {RetryCount} after {TimeSpan}ms",
                        retryCount, timeSpan.TotalMilliseconds);
                });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing DynamoDB table {TableName}", _tableName);

        var dynamoConfig = _config.DynamoDB ?? new DynamoDBConfiguration();

        try
        {
            // Check if table exists
            await _dynamoClient.DescribeTableAsync(_tableName, cancellationToken);
            _logger.LogInformation("DynamoDB table {TableName} already exists", _tableName);
        }
        catch (ResourceNotFoundException)
        {
            // Create table if it doesn't exist
            var request = new CreateTableRequest
            {
                TableName = _tableName,
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new() { AttributeName = "id", AttributeType = "S" },
                    new() { AttributeName = "source_date", AttributeType = "S" },
                    new() { AttributeName = "timestamp_id", AttributeType = "S" },
                    new() { AttributeName = "timestamp", AttributeType = "N" },
                    new() { AttributeName = "source_type", AttributeType = "S" },
                    new() { AttributeName = "severity", AttributeType = "S" }
                },
                KeySchema = new List<KeySchemaElement>
                {
                    new() { AttributeName = "source_date", KeyType = "HASH" },
                    new() { AttributeName = "timestamp_id", KeyType = "RANGE" }
                },
                ProvisionedThroughput = new ProvisionedThroughput
                {
                    ReadCapacityUnits = dynamoConfig.ReadCapacityUnits,
                    WriteCapacityUnits = dynamoConfig.WriteCapacityUnits
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new()
                    {
                        IndexName = "id-index",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new() { AttributeName = "id", KeyType = "HASH" }
                        },
                        Projection = new Projection { ProjectionType = "ALL" },
                        ProvisionedThroughput = new ProvisionedThroughput
                        {
                            ReadCapacityUnits = dynamoConfig.ReadCapacityUnits,
                            WriteCapacityUnits = dynamoConfig.WriteCapacityUnits
                        }
                    },
                    new()
                    {
                        IndexName = "timestamp-index",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new() { AttributeName = "timestamp", KeyType = "HASH" }
                        },
                        Projection = new Projection { ProjectionType = "ALL" },
                        ProvisionedThroughput = new ProvisionedThroughput
                        {
                            ReadCapacityUnits = dynamoConfig.ReadCapacityUnits,
                            WriteCapacityUnits = dynamoConfig.WriteCapacityUnits
                        }
                    },
                    new()
                    {
                        IndexName = "source-type-index",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new() { AttributeName = "source_type", KeyType = "HASH" },
                            new() { AttributeName = "timestamp", KeyType = "RANGE" }
                        },
                        Projection = new Projection { ProjectionType = "ALL" },
                        ProvisionedThroughput = new ProvisionedThroughput
                        {
                            ReadCapacityUnits = dynamoConfig.ReadCapacityUnits,
                            WriteCapacityUnits = dynamoConfig.WriteCapacityUnits
                        }
                    },
                    new()
                    {
                        IndexName = "severity-index",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new() { AttributeName = "severity", KeyType = "HASH" },
                            new() { AttributeName = "timestamp", KeyType = "RANGE" }
                        },
                        Projection = new Projection { ProjectionType = "ALL" },
                        ProvisionedThroughput = new ProvisionedThroughput
                        {
                            ReadCapacityUnits = dynamoConfig.ReadCapacityUnits,
                            WriteCapacityUnits = dynamoConfig.WriteCapacityUnits
                        }
                    }
                }
            };

            // Add custom GSIs if configured
            foreach (var gsi in dynamoConfig.GlobalSecondaryIndexes)
            {
                request.GlobalSecondaryIndexes.Add(new GlobalSecondaryIndex
                {
                    IndexName = gsi.IndexName,
                    KeySchema = new List<KeySchemaElement>
                    {
                        new() { AttributeName = gsi.PartitionKey, KeyType = "HASH" },
                        new() { AttributeName = gsi.SortKey ?? "timestamp", KeyType = "RANGE" }
                    },
                    Projection = new Projection { ProjectionType = "ALL" },
                    ProvisionedThroughput = new ProvisionedThroughput
                    {
                        ReadCapacityUnits = gsi.ReadCapacityUnits,
                        WriteCapacityUnits = gsi.WriteCapacityUnits
                    }
                });
            }

            await _dynamoClient.CreateTableAsync(request, cancellationToken);

            // Wait for table to be active
            await WaitForTableActiveAsync(_tableName, cancellationToken);

            // Enable TTL if configured
            if (!string.IsNullOrEmpty(dynamoConfig.TtlAttributeName))
            {
                await _dynamoClient.UpdateTimeToLiveAsync(new UpdateTimeToLiveRequest
                {
                    TableName = _tableName,
                    TimeToLiveSpecification = new TimeToLiveSpecification
                    {
                        Enabled = true,
                        AttributeName = dynamoConfig.TtlAttributeName
                    }
                }, cancellationToken);
            }

            // Enable point-in-time recovery if configured
            if (dynamoConfig.EnablePointInTimeRecovery)
            {
                await _dynamoClient.UpdateContinuousBackupsAsync(new UpdateContinuousBackupsRequest
                {
                    TableName = _tableName,
                    PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification
                    {
                        PointInTimeRecoveryEnabled = true
                    }
                }, cancellationToken);
            }

            _logger.LogInformation("DynamoDB table {TableName} created successfully", _tableName);
        }
    }

    public async Task<int> InsertBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        var eventList = events.ToList();
        if (!eventList.Any()) return 0;

        _logger.LogDebug("Inserting batch of {Count} events to DynamoDB", eventList.Count);

        var inserted = 0;
        var table = Table.LoadTable(_dynamoClient, _tableName);

        // DynamoDB batch write has a limit of 25 items
        foreach (var batch in eventList.Chunk(25))
        {
            var batchWrite = table.CreateBatchWrite();

            foreach (var evt in batch)
            {
                var id = Ulid.NewUlid();
                var ticks = new DateTimeOffset(evt.Timestamp).ToUnixTimeSeconds();
                var sourceDate = $"{evt.SourceType}#{evt.Timestamp:yyyy-MM-dd}";
                var timestampId = $"{ticks}#{id}";

                var document = new Document
                {
                    ["id"] = id,
                    ["source_date"] = sourceDate,
                    ["timestamp_id"] = timestampId,
                    ["timestamp"] = ticks,
                    ["timestamp_iso"] = evt.Timestamp.ToString("O"),
                    ["source_type"] = evt.SourceType.ToString(),
                    ["severity"] = evt.Severity.ToString(),
                    ["message"] = evt.RawLine,
                    ["raw_log"] = evt.RawLine,
                    ["created_at"] = DateTime.UtcNow.ToString("O")
                };

                // Add fields as a JSON string
                if (evt.Fields.Any())
                {
                    document["fields"] = JsonSerializer.Serialize(evt.Fields);
                }

                // Add tags as a string set
                if (evt.Tags.Any())
                {
                    document["tags"] = evt.Tags.ToList();
                }

                // Add TTL if configured
                var dynamoConfig = _config.DynamoDB;
                if (dynamoConfig != null && !string.IsNullOrEmpty(dynamoConfig.TtlAttributeName) &&
                    _config.RetentionPolicy.Enabled)
                {
                    var ttlDate = DateTime.UtcNow.AddDays(_config.RetentionPolicy.RetentionDays);
                    document[dynamoConfig.TtlAttributeName] = new DateTimeOffset(ttlDate).ToUnixTimeSeconds();
                }

                batchWrite.AddDocumentToPut(document);
            }

            try
            {
                await _retryPolicy.ExecuteAsync(async () =>
                {
                    await batchWrite.ExecuteAsync(cancellationToken);
                });

                inserted += batch.Length;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DynamoDB batch write failed for {Count} items — these items were NOT inserted", batch.Length);
                // Do NOT increment inserted — these items failed
            }
        }

        _logger.LogDebug("Inserted {Count} events to DynamoDB", inserted);
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
        var table = Table.LoadTable(_dynamoClient, _tableName);
        var results = new List<LogEvent>();

        // Use appropriate index based on filters
        Search search;
        if (filters?.ContainsKey("source_type") == true)
        {
            // Use source-type-index
            var filter = new QueryFilter("source_type", QueryOperator.Equal, filters["source_type"]);
            if (startTime.HasValue)
            {
                filter.AddCondition("timestamp", QueryOperator.GreaterThanOrEqual,
                    new DateTimeOffset(startTime.Value).ToUnixTimeSeconds());
            }
            if (endTime.HasValue)
            {
                filter.AddCondition("timestamp", QueryOperator.LessThanOrEqual,
                    new DateTimeOffset(endTime.Value).ToUnixTimeSeconds());
            }

            var queryConfig = new QueryOperationConfig
            {
                IndexName = "source-type-index",
                Filter = filter,
                Limit = limit
            };

            search = table.Query(queryConfig);
        }
        else if (filters?.ContainsKey("severity") == true)
        {
            // Use severity-index
            var filter = new QueryFilter("severity", QueryOperator.Equal, filters["severity"]);
            if (startTime.HasValue)
            {
                filter.AddCondition("timestamp", QueryOperator.GreaterThanOrEqual,
                    new DateTimeOffset(startTime.Value).ToUnixTimeSeconds());
            }
            if (endTime.HasValue)
            {
                filter.AddCondition("timestamp", QueryOperator.LessThanOrEqual,
                    new DateTimeOffset(endTime.Value).ToUnixTimeSeconds());
            }

            var queryConfig = new QueryOperationConfig
            {
                IndexName = "severity-index",
                Filter = filter,
                Limit = limit
            };

            search = table.Query(queryConfig);
        }
        else
        {
            // Use Scan with filters
            var scanFilter = new ScanFilter();
            if (startTime.HasValue)
            {
                scanFilter.AddCondition("timestamp", ScanOperator.GreaterThanOrEqual,
                    new DateTimeOffset(startTime.Value).ToUnixTimeSeconds());
            }
            if (endTime.HasValue)
            {
                scanFilter.AddCondition("timestamp", ScanOperator.LessThanOrEqual,
                    new DateTimeOffset(endTime.Value).ToUnixTimeSeconds());
            }

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                scanFilter.AddCondition("message", ScanOperator.Contains, searchText);
            }

            var scanConfig = new ScanOperationConfig
            {
                Filter = scanFilter,
                Limit = limit
            };

            search = table.Scan(scanConfig);
        }

        // Get results — cap total fetched documents to prevent OOM on large scans
        var maxFetch = offset + limit;
        var documents = new List<Document>();
        while (!search.IsDone && documents.Count < maxFetch)
        {
            var batch = await search.GetNextSetAsync(cancellationToken);
            documents.AddRange(batch);
        }

        // Skip offset
        documents = documents.Skip(offset).Take(limit).ToList();

        foreach (var doc in documents)
        {
            results.Add(MapToLogEvent(doc));
        }

        return results.OrderByDescending(e => e.Timestamp);
    }

    public async Task<long> GetCountAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? searchText = null,
        Dictionary<string, string>? filters = null,
        CancellationToken cancellationToken = default)
    {
        // For simplicity, using DescribeTable for total count
        // For filtered counts, would need to perform actual query/scan
        if (!startTime.HasValue && !endTime.HasValue && string.IsNullOrWhiteSpace(searchText) &&
            (filters == null || !filters.Any()))
        {
            var response = await _dynamoClient.DescribeTableAsync(_tableName, cancellationToken);
            return response.Table.ItemCount;
        }

        // For filtered counts, perform query and count (capped to prevent OOM)
        var items = await QueryAsync(startTime, endTime, searchText, filters, 100_000, 0, cancellationToken);
        return items.Count();
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting logs older than {CutoffDate}", cutoffDate);

        var table = Table.LoadTable(_dynamoClient, _tableName);
        var cutoffTimestamp = new DateTimeOffset(cutoffDate).ToUnixTimeSeconds();

        // Scan for items older than cutoff
        var scanFilter = new ScanFilter();
        scanFilter.AddCondition("timestamp", ScanOperator.LessThan, cutoffTimestamp);

        var scanConfig = new ScanOperationConfig { Filter = scanFilter };
        var search = table.Scan(scanConfig);
        var documents = await search.GetRemainingAsync(cancellationToken);

        var deleted = 0;

        // Delete in batches
        foreach (var batch in documents.Chunk(25))
        {
            var batchWrite = table.CreateBatchWrite();

            foreach (var doc in batch)
            {
                var sourceDate = doc["source_date"].AsString();
                var timestampId = doc["timestamp_id"].AsString();
                batchWrite.AddKeyToDelete(sourceDate, timestampId);
            }

            await _retryPolicy.ExecuteAsync(async () =>
            {
                await batchWrite.ExecuteAsync(cancellationToken);
            });

            deleted += batch.Length;
        }

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

        var events = await QueryAsync(startDate, endDate, limit: 1_000_000, cancellationToken: cancellationToken);
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
        Abstractions.ExportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting logs in {Format} format", format);

        var events = await QueryAsync(startTime, endTime, limit: 1_000_000, cancellationToken: cancellationToken);
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
            case Abstractions.ExportFormat.JSON:
                await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(eventList, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
                break;

            case Abstractions.ExportFormat.JSONL:
                await using (var writer = new StreamWriter(fullPath))
                {
                    foreach (var evt in eventList)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(evt));
                    }
                }
                break;

            case Abstractions.ExportFormat.CSV:
                await ExportToCsvAsync(eventList, fullPath);
                break;

            default:
                throw new NotSupportedException($"Export format {format} is not yet supported for DynamoDB");
        }

        _logger.LogInformation("Exported {Count} logs to {Path}", eventList.Count, fullPath);
        return fullPath;
    }

    public async Task<DatabaseStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _dynamoClient.DescribeTableAsync(_tableName, cancellationToken);
        var table = response.Table;

        var stats = new DatabaseStatistics
        {
            TotalEvents = table.ItemCount,
            DatabaseSizeBytes = table.TableSizeBytes
        };

        // Get sample of items to determine date range
        var scanRequest = new ScanRequest
        {
            TableName = _tableName,
            Limit = 1000,
            ProjectionExpression = "timestamp_iso, source_type, severity"
        };

        var scanResponse = await _dynamoClient.ScanAsync(scanRequest, cancellationToken);

        if (scanResponse.Items.Any())
        {
            var timestamps = scanResponse.Items
                .Where(item => item.ContainsKey("timestamp_iso"))
                .Select(item => DateTime.Parse(item["timestamp_iso"].S))
                .ToList();

            if (timestamps.Any())
            {
                stats.OldestEvent = timestamps.Min();
                stats.NewestEvent = timestamps.Max();
            }

            // Count by type
            stats.EventsByType = scanResponse.Items
                .Where(item => item.ContainsKey("source_type"))
                .GroupBy(item => item["source_type"].S)
                .ToDictionary(g => g.Key, g => (long)g.Count());

            // Count by severity
            stats.EventsBySeverity = scanResponse.Items
                .Where(item => item.ContainsKey("severity"))
                .GroupBy(item => item["severity"].S)
                .ToDictionary(g => g.Key, g => (long)g.Count());
        }

        return stats;
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("DynamoDB does not require manual optimization");
        await Task.CompletedTask;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new HealthCheckResult();

        try
        {
            var response = await _dynamoClient.DescribeTableAsync(_tableName, cancellationToken);

            result.IsHealthy = response.Table.TableStatus == TableStatus.ACTIVE;
            result.Status = response.Table.TableStatus.ToString();

            result.Metadata["table_name"] = _tableName;
            result.Metadata["item_count"] = response.Table.ItemCount;
            result.Metadata["size_bytes"] = response.Table.TableSizeBytes;
            result.Metadata["read_capacity"] = response.Table.ProvisionedThroughput.ReadCapacityUnits;
            result.Metadata["write_capacity"] = response.Table.ProvisionedThroughput.WriteCapacityUnits;

            if (!result.IsHealthy)
            {
                result.Issues.Add($"Table status is {response.Table.TableStatus}");
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

    private async Task WaitForTableActiveAsync(string tableName, CancellationToken cancellationToken)
    {
        var isActive = false;
        var attempts = 0;
        const int maxAttempts = 30;

        while (!isActive && attempts < maxAttempts)
        {
            var response = await _dynamoClient.DescribeTableAsync(tableName, cancellationToken);
            isActive = response.Table.TableStatus == TableStatus.ACTIVE;

            if (!isActive)
            {
                await Task.Delay(2000, cancellationToken);
                attempts++;
            }
        }

        if (!isActive)
        {
            throw new TimeoutException($"Table {tableName} did not become active within timeout period");
        }
    }

    private LogEvent MapToLogEvent(Document document)
    {
        var logEvent = new LogEvent
        {
            Timestamp = document.ContainsKey("timestamp_iso")
                ? DateTime.Parse(document["timestamp_iso"])
                : DateTimeOffset.FromUnixTimeSeconds(document["timestamp"].AsLong()).UtcDateTime,
            SourceType = document.ContainsKey("source_type") ? document["source_type"].AsString() : SourceTypes.Generic,
            Severity = Enum.Parse<LogSeverity>(document["severity"]),
            RawLine = document.ContainsKey("message") ? document["message"] : (document.ContainsKey("raw_log") ? document["raw_log"] : string.Empty)
        };

        if (document.ContainsKey("fields"))
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(document["fields"]);
            if (fields != null)
            {
                foreach (var field in fields)
                {
                    logEvent.SetField(field.Key, field.Value?.ToString() ?? string.Empty);
                }
            }
        }

        if (document.ContainsKey("tags"))
        {
            var tags = document["tags"].AsListOfString();
            logEvent.Tags = new HashSet<string>(tags);
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

    public void Dispose()
    {
        _dynamoClient?.Dispose();
    }
}