using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Cabinet.Core.Models;
using Cabinet.Core.Remote;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cabinet.AWS;

/// <summary>
/// Provides access to AWS CloudWatch Logs
/// </summary>
public class CloudWatchLogAccess : IRemoteLogAccess
{
    private readonly ILogger<CloudWatchLogAccess> _logger;
    private readonly Dictionary<string, AmazonCloudWatchLogsClient> _clients = new();

    public CloudWatchLogAccess(ILogger<CloudWatchLogAccess> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteConnection> ConnectAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != RemoteSourceType.CloudWatch)
        {
            throw new ArgumentException($"Unsupported connection type: {config.Type}");
        }

        var connection = new RemoteConnection
        {
            Type = config.Type,
            Host = config.Host ?? "aws-cloudwatch",
            IsConnected = false
        };

        try
        {
            // Create AWS client
            var client = CreateCloudWatchClient(config);

            // Test connection by listing log groups
            var request = new DescribeLogGroupsRequest { Limit = 1 };
            var response = await client.DescribeLogGroupsAsync(request, cancellationToken);

            connection.IsConnected = true;
            connection.NativeConnection = client;
            _clients[connection.Id] = client;

            _logger.LogInformation("Connected to AWS CloudWatch Logs in region: {Region}",
                config.CustomSettings.GetValueOrDefault("Region", "us-east-1"));

            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to AWS CloudWatch Logs");
            throw;
        }
    }

    public async Task<IEnumerable<RemoteLogFile>> ListLogsAsync(
        RemoteConnection connection,
        string path,
        string pattern = "*.log",
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var client))
        {
            throw new System.InvalidOperationException("Not connected to CloudWatch");
        }

        var files = new List<RemoteLogFile>();

        try
        {
            // List log groups
            var logGroupsRequest = new DescribeLogGroupsRequest
            {
                LogGroupNamePrefix = string.IsNullOrEmpty(path) ? null : path
            };

            string? nextToken = null;
            do
            {
                logGroupsRequest.NextToken = nextToken;
                var groupsResponse = await client.DescribeLogGroupsAsync(logGroupsRequest, cancellationToken);

                foreach (var group in groupsResponse.LogGroups)
                {
                    // List log streams in each group
                    var streamsRequest = new DescribeLogStreamsRequest
                    {
                        LogGroupName = group.LogGroupName,
                        OrderBy = OrderBy.LastEventTime,
                        Descending = true,
                        Limit = 50
                    };

                    var streamsResponse = await client.DescribeLogStreamsAsync(streamsRequest, cancellationToken);

                    foreach (var stream in streamsResponse.LogStreams)
                    {
                        files.Add(new RemoteLogFile
                        {
                            Path = $"{group.LogGroupName}/{stream.LogStreamName}",
                            Name = stream.LogStreamName,
                            Size = 0, // StoredBytes is deprecated for log streams as of June 17, 2019
                            LastModified = stream.LastEventTimestamp,
                            SourceType = RemoteSourceType.CloudWatch,
                            RemoteHost = "AWS CloudWatch"
                        });
                    }
                }

                nextToken = groupsResponse.NextToken;
            } while (!string.IsNullOrEmpty(nextToken) && !cancellationToken.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list CloudWatch logs");
            throw;
        }

        return files;
    }

    public async IAsyncEnumerable<string> StreamLogAsync(
        RemoteConnection connection,
        string filePath,
        StreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var client))
        {
            throw new System.InvalidOperationException("Not connected to CloudWatch");
        }

        // Parse log group and stream from path
        var parts = filePath.Split('/', 2);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid CloudWatch log path format. Expected: logGroup/logStream, got: {filePath}");
        }

        var logGroupName = parts[0];
        var logStreamName = parts[1];

        // Set up time range
        var startTime = options.FromEnd
            ? DateTimeOffset.UtcNow.AddMinutes(-5)
            : DateTimeOffset.UtcNow.AddDays(-7);

        var request = new GetLogEventsRequest
        {
            LogGroupName = logGroupName,
            LogStreamName = logStreamName,
            StartTime = startTime.UtcDateTime,
            StartFromHead = !options.FromEnd
        };

        string? nextToken = null;
        var seenTokens = new HashSet<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            GetLogEventsResponse response;
            try
            {
                request.NextToken = nextToken;
                response = await client.GetLogEventsAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error streaming CloudWatch logs");
                throw;
            }

            foreach (var evt in response.Events)
            {
                // CloudWatch returns JSON messages, parse them if possible
                var message = evt.Message;
                if (IsJson(message))
                {
                    message = FormatJsonLog(message);
                }

                yield return $"[{evt.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            }

            // Check if we've seen this token before (CloudWatch returns same token when no new data)
            if (!string.IsNullOrEmpty(response.NextForwardToken))
            {
                if (seenTokens.Contains(response.NextForwardToken))
                {
                    if (options.Follow)
                    {
                        await Task.Delay(options.PollIntervalMs, cancellationToken);
                        seenTokens.Clear(); // Reset to check for new events
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    seenTokens.Add(response.NextForwardToken);
                    nextToken = response.NextForwardToken;
                }
            }
            else
            {
                break;
            }
        }
    }

    public async Task<string> CacheLogLocallyAsync(
        RemoteConnection connection,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var client))
        {
            throw new System.InvalidOperationException("Not connected to CloudWatch");
        }

        // Create cache directory
        var cacheDir = Path.Combine(Path.GetTempPath(), "cabinet", "cache", "cloudwatch");
        Directory.CreateDirectory(cacheDir);

        var fileName = remotePath.Replace('/', '_') + $".{DateTime.UtcNow:yyyyMMddHHmmss}.log";
        var localPath = Path.Combine(cacheDir, fileName);

        try
        {
            using var writer = new StreamWriter(localPath);

            await foreach (var line in StreamLogAsync(connection, remotePath, new StreamOptions { FromEnd = false }, cancellationToken))
            {
                await writer.WriteLineAsync(line);
            }

            _logger.LogInformation("Cached CloudWatch log locally: {Remote} -> {Local}", remotePath, localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache CloudWatch log: {Path}", remotePath);

            // Clean up partial file
            if (File.Exists(localPath))
            {
                try
                {
                    File.Delete(localPath);
                }
                catch (Exception deleteEx)
                {
                    _logger.LogWarning(deleteEx, "Failed to delete partial cache file: {Path}", localPath);
                }
            }

            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await ConnectAsync(config, cancellationToken);
            return connection.IsConnected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CloudWatch connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Execute CloudWatch Insights query
    /// </summary>
    public async Task<CloudWatchInsightsResult> RunInsightsQueryAsync(
        RemoteConnection connection,
        string logGroupName,
        string query,
        DateTime startTime,
        DateTime endTime,
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var client))
        {
            throw new System.InvalidOperationException("Not connected to CloudWatch");
        }

        var startQueryRequest = new StartQueryRequest
        {
            LogGroupName = logGroupName,
            StartTime = ((DateTimeOffset)startTime).ToUnixTimeSeconds(),
            EndTime = ((DateTimeOffset)endTime).ToUnixTimeSeconds(),
            QueryString = query
        };

        var startResponse = await client.StartQueryAsync(startQueryRequest, cancellationToken);
        var queryId = startResponse.QueryId;

        // Poll for results
        var results = new GetQueryResultsResponse();
        var maxAttempts = 60; // 1 minute timeout
        var attempts = 0;

        do
        {
            await Task.Delay(1000, cancellationToken); // Wait 1 second between polls

            results = await client.GetQueryResultsAsync(new GetQueryResultsRequest
            {
                QueryId = queryId
            }, cancellationToken);

            attempts++;
        } while (results.Status == QueryStatus.Running && attempts < maxAttempts && !cancellationToken.IsCancellationRequested);

        if (results.Status != QueryStatus.Complete)
        {
            throw new TimeoutException($"CloudWatch Insights query timed out or failed. Status: {results.Status}");
        }

        return new CloudWatchInsightsResult
        {
            QueryId = queryId,
            Status = results.Status.Value,
            Results = results.Results,
            Statistics = new CloudWatchQueryStatistics
            {
                RecordsMatched = results.Statistics?.RecordsMatched ?? 0,
                RecordsScanned = results.Statistics?.RecordsScanned ?? 0,
                BytesScanned = results.Statistics?.BytesScanned ?? 0
            }
        };
    }

    private AmazonCloudWatchLogsClient CreateCloudWatchClient(RemoteConnectionConfig config)
    {
        var region = config.CustomSettings.GetValueOrDefault("Region", "us-east-1");
        var profile = config.CustomSettings.GetValueOrDefault("Profile");

        AWSCredentials credentials;

        if (!string.IsNullOrEmpty(profile))
        {
            // Use named profile from ~/.aws/credentials
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out credentials))
            {
                throw new ArgumentException($"AWS profile '{profile}' not found");
            }
        }
        else if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
        {
            // Use explicit credentials (username = access key, password = secret key)
            var sessionToken = config.CustomSettings.GetValueOrDefault("SessionToken");
            credentials = string.IsNullOrEmpty(sessionToken)
                ? new BasicAWSCredentials(config.Username, config.Password)
                : new SessionAWSCredentials(config.Username, config.Password, sessionToken);
        }
        else
        {
            // Use default credential chain (IAM role, environment variables, etc.)
            credentials = FallbackCredentialsFactory.GetCredentials();
        }

        var clientConfig = new AmazonCloudWatchLogsConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region)
        };

        return new AmazonCloudWatchLogsClient(credentials, clientConfig);
    }

    private bool IsJson(string text)
    {
        text = text.Trim();
        return (text.StartsWith("{") && text.EndsWith("}")) ||
               (text.StartsWith("[") && text.EndsWith("]"));
    }

    private string FormatJsonLog(string json)
    {
        try
        {
            var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            // Extract common fields
            var timestamp = TryGetJsonValue(root, "timestamp", "@timestamp", "time");
            var level = TryGetJsonValue(root, "level", "severity", "logLevel");
            var message = TryGetJsonValue(root, "message", "msg", "@message");
            var logger = TryGetJsonValue(root, "logger", "loggerName", "source");

            var formatted = "";
            if (!string.IsNullOrEmpty(level)) formatted += $"[{level}] ";
            if (!string.IsNullOrEmpty(logger)) formatted += $"{logger}: ";
            formatted += message ?? json;

            return formatted;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse JSON log, returning raw: {Json}", json);
            return json;
        }
    }

    private string? TryGetJsonValue(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ToString();
            }
        }
        return null;
    }
}

/// <summary>
/// Result from CloudWatch Insights query
/// </summary>
public class CloudWatchInsightsResult
{
    public string QueryId { get; set; } = "";
    public string Status { get; set; } = "";
    public List<List<ResultField>> Results { get; set; } = new();
    public CloudWatchQueryStatistics Statistics { get; set; } = new();
}

/// <summary>
/// CloudWatch query statistics
/// </summary>
public class CloudWatchQueryStatistics
{
    public double RecordsMatched { get; set; }
    public double RecordsScanned { get; set; }
    public double BytesScanned { get; set; }
}