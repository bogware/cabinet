using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Logging;

namespace Cabinet.AWS.Services;

/// <summary>
/// S3 archive service for uploading backup files to AWS S3.
/// Supports multipart upload for large files.
/// </summary>
public sealed class S3ArchiveService : IDisposable
{
    private readonly IAmazonS3 _s3Client;
    private readonly ILogger<S3ArchiveService> _logger;
    private readonly string _defaultBucket;
    private readonly string _prefix;

    public S3ArchiveService(
        IAmazonS3 s3Client,
        ILogger<S3ArchiveService> logger,
        string defaultBucket,
        string prefix = "cabinet-archives/")
    {
        _s3Client = s3Client;
        _logger = logger;
        _defaultBucket = defaultBucket;
        _prefix = prefix;
    }

    /// <summary>
    /// Upload a backup file to S3. Uses multipart upload for files > 16 MB.
    /// </summary>
    public async Task<S3UploadResult> UploadAsync(
        string localFilePath,
        string? bucket = null,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default)
    {
        var targetBucket = bucket ?? _defaultBucket;
        var targetPrefix = keyPrefix ?? _prefix;
        var fileName = Path.GetFileName(localFilePath);
        var s3Key = $"{targetPrefix.TrimEnd('/')}/{fileName}";

        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Backup file not found", localFilePath);

        var fileInfo = new FileInfo(localFilePath);
        _logger.LogInformation("Uploading {File} ({Size:F2} MB) to s3://{Bucket}/{Key}",
            fileName, fileInfo.Length / (1024.0 * 1024.0), targetBucket, s3Key);

        try
        {
            using var transferUtility = new TransferUtility(_s3Client);
            var request = new TransferUtilityUploadRequest
            {
                BucketName = targetBucket,
                Key = s3Key,
                FilePath = localFilePath,
                ContentType = GetContentType(fileName),
                StorageClass = S3StorageClass.StandardInfrequentAccess, // IA for archives
                ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
                PartSize = 16 * 1024 * 1024 // 16 MB parts
            };

            // Add metadata
            request.Metadata.Add("x-amz-meta-source", "cabinet-backup");
            request.Metadata.Add("x-amz-meta-timestamp", DateTime.UtcNow.ToString("o"));
            request.Metadata.Add("x-amz-meta-size", fileInfo.Length.ToString());

            await transferUtility.UploadAsync(request, cancellationToken);

            _logger.LogInformation("Upload complete: s3://{Bucket}/{Key}", targetBucket, s3Key);

            return new S3UploadResult
            {
                Success = true,
                Bucket = targetBucket,
                Key = s3Key,
                SizeBytes = fileInfo.Length,
                UploadedAt = DateTime.UtcNow
            };
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "S3 upload failed: {ErrorCode} — {Message}", ex.ErrorCode, ex.Message);
            return new S3UploadResult
            {
                Success = false,
                Error = $"{ex.ErrorCode}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// List archives in S3 bucket.
    /// </summary>
    public async Task<List<S3ArchiveInfo>> ListArchivesAsync(
        string? bucket = null,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var targetBucket = bucket ?? _defaultBucket;
        var targetPrefix = prefix ?? _prefix;
        var archives = new List<S3ArchiveInfo>();

        var request = new ListObjectsV2Request
        {
            BucketName = targetBucket,
            Prefix = targetPrefix,
            MaxKeys = 1000
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
            foreach (var obj in response.S3Objects)
            {
                archives.Add(new S3ArchiveInfo
                {
                    Key = obj.Key,
                    SizeBytes = obj.Size,
                    LastModified = obj.LastModified,
                    StorageClass = obj.StorageClass?.Value ?? "STANDARD"
                });
            }
            request.ContinuationToken = response.NextContinuationToken;
        } while (response.IsTruncated);

        return archives;
    }

    /// <summary>
    /// Download an archive from S3.
    /// </summary>
    public async Task<string> DownloadAsync(
        string s3Key,
        string localDirectory,
        string? bucket = null,
        CancellationToken cancellationToken = default)
    {
        var targetBucket = bucket ?? _defaultBucket;
        var fileName = Path.GetFileName(s3Key);
        var localPath = Path.Combine(localDirectory, fileName);

        Directory.CreateDirectory(localDirectory);

        using var transferUtility = new TransferUtility(_s3Client);
        await transferUtility.DownloadAsync(localPath, targetBucket, s3Key, cancellationToken);

        _logger.LogInformation("Downloaded s3://{Bucket}/{Key} to {Path}", targetBucket, s3Key, localPath);
        return localPath;
    }

    /// <summary>
    /// Delete an archive from S3.
    /// </summary>
    public async Task<bool> DeleteAsync(
        string s3Key,
        string? bucket = null,
        CancellationToken cancellationToken = default)
    {
        var targetBucket = bucket ?? _defaultBucket;
        try
        {
            await _s3Client.DeleteObjectAsync(targetBucket, s3Key, cancellationToken);
            _logger.LogInformation("Deleted s3://{Bucket}/{Key}", targetBucket, s3Key);
            return true;
        }
        catch (AmazonS3Exception ex)
        {
            _logger.LogError(ex, "Failed to delete s3://{Bucket}/{Key}", targetBucket, s3Key);
            return false;
        }
    }

    private static string GetContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".gz" => "application/gzip",
        ".json" => "application/json",
        ".jsonl" => "application/x-ndjson",
        ".csv" => "text/csv",
        ".zip" => "application/zip",
        _ => "application/octet-stream"
    };

    public void Dispose() => _s3Client.Dispose();
}

public sealed class S3UploadResult
{
    public bool Success { get; set; }
    public string? Bucket { get; set; }
    public string? Key { get; set; }
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class S3ArchiveInfo
{
    public string Key { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string StorageClass { get; set; } = string.Empty;
}
