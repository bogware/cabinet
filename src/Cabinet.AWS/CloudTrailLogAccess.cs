using Amazon;
using Amazon.CloudTrail;
using Amazon.CloudTrail.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Cabinet.Core.Models;
using Cabinet.Core.Remote;
using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cabinet.AWS;

/// <summary>
/// Provides access to AWS CloudTrail logs
/// </summary>
public class CloudTrailLogAccess : IRemoteLogAccess
{
    private readonly ILogger<CloudTrailLogAccess> _logger;
    private readonly Dictionary<string, CloudTrailClients> _clients = new();

    public CloudTrailLogAccess(ILogger<CloudTrailLogAccess> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteConnection> ConnectAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != RemoteSourceType.CloudTrail)
        {
            throw new ArgumentException($"Unsupported connection type: {config.Type}");
        }

        var connection = new RemoteConnection
        {
            Type = config.Type,
            Host = config.Host ?? "aws-cloudtrail",
            IsConnected = false
        };

        try
        {
            // Create AWS clients
            var clients = CreateCloudTrailClients(config);

            // Test connection by describing trails
            var response = await clients.CloudTrail.DescribeTrailsAsync(new DescribeTrailsRequest(), cancellationToken);

            connection.IsConnected = true;
            connection.NativeConnection = clients;
            _clients[connection.Id] = clients;

            _logger.LogInformation("Connected to AWS CloudTrail in region: {Region}. Found {Count} trails",
                config.CustomSettings.GetValueOrDefault("Region", "us-east-1"),
                response.TrailList.Count);

            return connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to AWS CloudTrail");
            throw;
        }
    }

    public async Task<IEnumerable<RemoteLogFile>> ListLogsAsync(
        RemoteConnection connection,
        string path,
        string pattern = "*.json",
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var clients))
        {
            throw new InvalidOperationException("Not connected to CloudTrail");
        }

        var files = new List<RemoteLogFile>();

        try
        {
            // Get trails
            var trailsResponse = await clients.CloudTrail.DescribeTrailsAsync(new DescribeTrailsRequest(), cancellationToken);

            foreach (var trail in trailsResponse.TrailList)
            {
                if (string.IsNullOrEmpty(trail.S3BucketName)) continue;

                // List CloudTrail logs in S3
                var prefix = string.IsNullOrEmpty(trail.S3KeyPrefix)
                    ? "AWSLogs/"
                    : $"{trail.S3KeyPrefix}/AWSLogs/";

                if (!string.IsNullOrEmpty(path))
                {
                    prefix = Path.Combine(prefix, path).Replace('\\', '/');
                }

                var listRequest = new ListObjectsV2Request
                {
                    BucketName = trail.S3BucketName,
                    Prefix = prefix,
                    MaxKeys = 1000
                };

                string? continuationToken = null;
                do
                {
                    listRequest.ContinuationToken = continuationToken;
                    var s3Response = await clients.S3.ListObjectsV2Async(listRequest, cancellationToken);

                    foreach (var obj in s3Response.S3Objects)
                    {
                        // CloudTrail logs are .json.gz files
                        if (obj.Key.EndsWith(".json.gz") || obj.Key.EndsWith(".json"))
                        {
                            files.Add(new RemoteLogFile
                            {
                                Path = $"s3://{trail.S3BucketName}/{obj.Key}",
                                Name = Path.GetFileName(obj.Key),
                                Size = obj.Size,
                                LastModified = obj.LastModified,
                                SourceType = RemoteSourceType.CloudTrail,
                                RemoteHost = trail.S3BucketName
                            });
                        }
                    }

                    continuationToken = s3Response.NextContinuationToken;
                } while (!string.IsNullOrEmpty(continuationToken) && !cancellationToken.IsCancellationRequested);
            }

            // Also get recent events directly from CloudTrail
            await AddRecentEventsAsync(clients.CloudTrail, files, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list CloudTrail logs");
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
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var clients))
        {
            throw new InvalidOperationException("Not connected to CloudTrail");
        }

        // Handle S3 paths
        if (filePath.StartsWith("s3://"))
        {
            await foreach (var line in StreamFromS3Async(clients.S3, filePath, cancellationToken))
            {
                yield return line;
            }
        }
        // Handle direct CloudTrail event lookup
        else if (filePath == "recent-events")
        {
            await foreach (var line in StreamRecentEventsAsync(clients.CloudTrail, options, cancellationToken))
            {
                yield return line;
            }
        }
        else
        {
            throw new ArgumentException($"Unsupported CloudTrail path format: {filePath}");
        }
    }

    public async Task<string> CacheLogLocallyAsync(
        RemoteConnection connection,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var clients))
        {
            throw new InvalidOperationException("Not connected to CloudTrail");
        }

        // Create cache directory
        var cacheDir = Path.Combine(Path.GetTempPath(), "cabinet", "cache", "cloudtrail");
        Directory.CreateDirectory(cacheDir);

        if (remotePath.StartsWith("s3://"))
        {
            // Download from S3
            var s3Uri = new Uri(remotePath);
            var bucketName = s3Uri.Host;
            var key = s3Uri.AbsolutePath.TrimStart('/');
            var localFileName = Path.GetFileName(key);

            // If it's compressed, we'll decompress it
            if (localFileName.EndsWith(".gz"))
            {
                localFileName = localFileName[..^3]; // Remove .gz extension
            }

            var localPath = Path.Combine(cacheDir, $"{localFileName}.{DateTime.UtcNow:yyyyMMddHHmmss}");

            try
            {
                var getRequest = new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = key
                };

                using var response = await clients.S3.GetObjectAsync(getRequest, cancellationToken);

                if (key.EndsWith(".gz"))
                {
                    // Decompress gzip file
                    using var gzipStream = new GZipStream(response.ResponseStream, CompressionMode.Decompress);
                    using var writer = new FileStream(localPath, FileMode.Create);
                    await gzipStream.CopyToAsync(writer, cancellationToken);
                }
                else
                {
                    using var writer = new FileStream(localPath, FileMode.Create);
                    await response.ResponseStream.CopyToAsync(writer, cancellationToken);
                }

                _logger.LogInformation("Cached CloudTrail log locally: {Remote} -> {Local}", remotePath, localPath);
                return localPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cache CloudTrail log from S3: {Path}", remotePath);
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
        else
        {
            // Cache recent events
            var localPath = Path.Combine(cacheDir, $"recent-events.{DateTime.UtcNow:yyyyMMddHHmmss}.json");

            using var writer = new StreamWriter(localPath);
            await foreach (var line in StreamRecentEventsAsync(clients.CloudTrail, new StreamOptions(), cancellationToken))
            {
                await writer.WriteLineAsync(line);
            }

            return localPath;
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
            _logger.LogDebug(ex, "CloudTrail connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Lookup specific CloudTrail events
    /// </summary>
    public async Task<List<CloudTrailEvent>> LookupEventsAsync(
        RemoteConnection connection,
        LookupEventsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || !_clients.TryGetValue(connection.Id, out var clients))
        {
            throw new InvalidOperationException("Not connected to CloudTrail");
        }

        var events = new List<CloudTrailEvent>();
        string? nextToken = null;

        do
        {
            request.NextToken = nextToken;
            var response = await clients.CloudTrail.LookupEventsAsync(request, cancellationToken);

            foreach (var evt in response.Events)
            {
                events.Add(new CloudTrailEvent
                {
                    EventId = evt.EventId,
                    EventName = evt.EventName,
                    EventTime = evt.EventTime,
                    Username = evt.Username,
                    EventSource = evt.EventSource,
                    SourceIPAddress = evt.Resources?.FirstOrDefault()?.ResourceName,
                    RawEventJson = evt.CloudTrailEvent
                });
            }

            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken) && !cancellationToken.IsCancellationRequested);

        return events;
    }

    private async IAsyncEnumerable<string> StreamFromS3Async(
        IAmazonS3 s3Client,
        string s3Path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var s3Uri = new Uri(s3Path);
        var bucketName = s3Uri.Host;
        var key = s3Uri.AbsolutePath.TrimStart('/');

        var getRequest = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = key
        };

        using var response = await s3Client.GetObjectAsync(getRequest, cancellationToken);

        Stream stream = response.ResponseStream;
        if (key.EndsWith(".gz"))
        {
            stream = new GZipStream(stream, CompressionMode.Decompress);
        }

        using var reader = new StreamReader(stream);

        // CloudTrail logs are JSON, format them
        var json = await reader.ReadToEndAsync();
        var formatted = FormatCloudTrailJson(json);

        foreach (var line in formatted.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private async IAsyncEnumerable<string> StreamRecentEventsAsync(
        IAmazonCloudTrail cloudTrailClient,
        StreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new LookupEventsRequest
        {
            StartTime = DateTime.UtcNow.AddDays(-7),
            EndTime = DateTime.UtcNow,
            MaxResults = 50
        };

        string? nextToken = null;

        do
        {
            request.NextToken = nextToken;
            var response = await cloudTrailClient.LookupEventsAsync(request, cancellationToken);

            foreach (var evt in response.Events)
            {
                var formatted = FormatCloudTrailEvent(evt);
                yield return formatted;
            }

            nextToken = response.NextToken;

            if (options.Follow && string.IsNullOrEmpty(nextToken))
            {
                await Task.Delay(options.PollIntervalMs, cancellationToken);
                request.StartTime = DateTime.UtcNow.AddMinutes(-5);
            }
        } while ((!string.IsNullOrEmpty(nextToken) || options.Follow) && !cancellationToken.IsCancellationRequested);
    }

    private async Task AddRecentEventsAsync(
        IAmazonCloudTrail cloudTrailClient,
        List<RemoteLogFile> files,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = new LookupEventsRequest
            {
                StartTime = DateTime.UtcNow.AddDays(-1),
                EndTime = DateTime.UtcNow,
                MaxResults = 1
            };

            var response = await cloudTrailClient.LookupEventsAsync(request, cancellationToken);

            if (response.Events.Any())
            {
                files.Insert(0, new RemoteLogFile
                {
                    Path = "recent-events",
                    Name = "Recent CloudTrail Events (Last 7 Days)",
                    Size = 0,
                    LastModified = DateTime.UtcNow,
                    SourceType = RemoteSourceType.CloudTrail,
                    RemoteHost = "AWS CloudTrail API"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve recent CloudTrail events");
        }
    }

    private CloudTrailClients CreateCloudTrailClients(RemoteConnectionConfig config)
    {
        var region = config.CustomSettings.GetValueOrDefault("Region", "us-east-1");
        var profile = config.CustomSettings.GetValueOrDefault("Profile");

        AWSCredentials credentials;

        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(profile, out credentials))
            {
                throw new ArgumentException($"AWS profile '{profile}' not found");
            }
        }
        else if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
        {
            var sessionToken = config.CustomSettings.GetValueOrDefault("SessionToken");
            credentials = string.IsNullOrEmpty(sessionToken)
                ? new BasicAWSCredentials(config.Username, config.Password)
                : new SessionAWSCredentials(config.Username, config.Password, sessionToken);
        }
        else
        {
            credentials = FallbackCredentialsFactory.GetCredentials();
        }

        var regionEndpoint = RegionEndpoint.GetBySystemName(region);

        return new CloudTrailClients
        {
            CloudTrail = new AmazonCloudTrailClient(credentials, regionEndpoint),
            S3 = new AmazonS3Client(credentials, regionEndpoint)
        };
    }

    private string FormatCloudTrailJson(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var formatted = new List<string>();

            if (root.TryGetProperty("Records", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in records.EnumerateArray())
                {
                    formatted.Add(FormatCloudTrailRecord(record));
                }
            }
            else
            {
                formatted.Add(FormatCloudTrailRecord(root));
            }

            return string.Join("\n", formatted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to format CloudTrail JSON, returning raw");
            return json;
        }
    }

    private string FormatCloudTrailRecord(JsonElement record)
    {
        var time = record.TryGetProperty("eventTime", out var t) ? t.GetString() : "";
        var name = record.TryGetProperty("eventName", out var n) ? n.GetString() : "";
        var source = record.TryGetProperty("eventSource", out var s) ? s.GetString() : "";
        var user = record.TryGetProperty("userIdentity", out var u) && u.TryGetProperty("userName", out var un)
            ? un.GetString()
            : "unknown";
        var ip = record.TryGetProperty("sourceIPAddress", out var i) ? i.GetString() : "";

        return $"[{time}] {source}: {name} by {user} from {ip}";
    }

    private string FormatCloudTrailEvent(Event evt)
    {
        return $"[{evt.EventTime:yyyy-MM-dd HH:mm:ss}] {evt.EventSource}: {evt.EventName} by {evt.Username ?? "unknown"}";
    }

    private class CloudTrailClients
    {
        public IAmazonCloudTrail CloudTrail { get; set; } = null!;
        public IAmazonS3 S3 { get; set; } = null!;
    }
}

/// <summary>
/// Represents a CloudTrail event
/// </summary>
public class CloudTrailEvent
{
    public string? EventId { get; set; }
    public string? EventName { get; set; }
    public DateTime EventTime { get; set; }
    public string? Username { get; set; }
    public string? EventSource { get; set; }
    public string? SourceIPAddress { get; set; }
    public string? RawEventJson { get; set; } // Raw JSON
}