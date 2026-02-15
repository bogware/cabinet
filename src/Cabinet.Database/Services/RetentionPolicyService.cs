#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using Cabinet.Database.Abstractions;
using Cabinet.Database.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cabinet.Database.Services;

/// <summary>
/// Background service that enforces retention policies
/// </summary>
public class RetentionPolicyService : BackgroundService
{
    private readonly ILogDatabaseProvider _databaseProvider;
    private readonly RetentionPolicyConfiguration _config;
    private readonly string _archivePath;
    private readonly ILogger<RetentionPolicyService> _logger;
    private readonly TimeSpan _checkInterval;

    public RetentionPolicyService(
        ILogDatabaseProvider databaseProvider,
        IOptions<DatabaseConfiguration> config,
        ILogger<RetentionPolicyService> logger)
    {
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
        _config = config?.Value?.RetentionPolicy ?? throw new ArgumentNullException(nameof(config));
        _archivePath = config?.Value?.Archive?.ArchivePath ?? "./archives";
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _checkInterval = TimeSpan.FromHours(_config.CleanupIntervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Retention policy service is disabled");
            return;
        }

        _logger.LogInformation("Retention policy service started. Retention: {Days} days, Interval: {Hours} hours",
            _config.RetentionDays, _config.CleanupIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await EnforceRetentionPolicyAsync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enforcing retention policy");
                // Continue running despite errors
            }
        }
    }

    private async Task EnforceRetentionPolicyAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Enforcing retention policy");

        var cutoffDate = DateTime.UtcNow.AddDays(-_config.RetentionDays);

        try
        {
            // Archive before delete if configured
            if (_config.ArchiveBeforeDelete)
            {
                await ArchiveOldLogsAsync(cutoffDate, cancellationToken);
            }

            // Delete old logs
            var deleted = await _databaseProvider.DeleteOlderThanAsync(cutoffDate, cancellationToken);
            _logger.LogInformation("Deleted {Count} logs older than {CutoffDate}", deleted, cutoffDate);

            // Optimize database after cleanup
            await _databaseProvider.OptimizeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce retention policy");
            throw;
        }
    }

    private async Task ArchiveOldLogsAsync(DateTime cutoffDate, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Archiving logs before deletion");

        try
        {
            // Archive logs in daily batches
            var startDate = cutoffDate.AddDays(-30); // Archive 30 days worth
            var endDate = cutoffDate;

            for (var date = startDate; date < endDate; date = date.AddDays(1))
            {
                var dayStart = date.Date;
                var dayEnd = dayStart.AddDays(1).AddSeconds(-1);

                var archivePath = await _databaseProvider.ArchiveLogsAsync(
                    dayStart,
                    dayEnd,
                    _archivePath,
                    cancellationToken);

                if (!string.IsNullOrEmpty(archivePath))
                {
                    _logger.LogInformation("Archived logs for {Date} to {Path}", dayStart, archivePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive logs");
            // Don't throw - allow deletion to proceed even if archiving fails
        }
    }
}

/// <summary>
/// Service for manual backup operations
/// </summary>
public class BackupService : IBackupService
{
    private readonly ILogDatabaseProvider _databaseProvider;
    private readonly ArchiveConfiguration _config;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        ILogDatabaseProvider databaseProvider,
        IOptions<DatabaseConfiguration> config,
        ILogger<BackupService> logger)
    {
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
        _config = config?.Value?.Archive ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> BackupAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? customPath = null,
        CancellationToken cancellationToken = default)
    {
        var start = startDate ?? DateTime.UtcNow.AddDays(-7); // Default to last 7 days
        var end = endDate ?? DateTime.UtcNow;
        var path = customPath ?? _config.ArchivePath;

        _logger.LogInformation("Creating backup from {StartDate} to {EndDate}", start, end);

        try
        {
            var archivePath = await _databaseProvider.ArchiveLogsAsync(start, end, path, cancellationToken);

            if (string.IsNullOrEmpty(archivePath))
            {
                _logger.LogWarning("No logs found to backup");
                return string.Empty;
            }

            _logger.LogInformation("Backup created successfully: {Path}", archivePath);

            // Upload to S3 if configured
            if (!string.IsNullOrEmpty(_config.S3Bucket))
            {
                await UploadToS3Async(archivePath, cancellationToken);
            }

            return archivePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed");
            throw;
        }
    }

    public async Task<string> BackupIncrementalAsync(CancellationToken cancellationToken = default)
    {
        // Get last backup timestamp from metadata
        var lastBackup = await GetLastBackupTimestampAsync(cancellationToken);
        var now = DateTime.UtcNow;

        if (lastBackup == null)
        {
            _logger.LogWarning("No previous backup found, creating full backup");
            return await BackupAsync(null, null, null, cancellationToken);
        }

        _logger.LogInformation("Creating incremental backup from {LastBackup} to {Now}", lastBackup, now);
        var result = await BackupAsync(lastBackup.Value, now, null, cancellationToken);

        // Update last backup timestamp
        await SetLastBackupTimestampAsync(now, cancellationToken);

        return result;
    }

    private async Task<DateTime?> GetLastBackupTimestampAsync(CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(_config.ArchivePath, ".backup_metadata.json");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            var metadata = System.Text.Json.JsonSerializer.Deserialize<BackupMetadata>(json);
            return metadata?.LastBackupTimestamp;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read backup metadata from {Path}", metadataPath);
            return null;
        }
    }

    private async Task SetLastBackupTimestampAsync(DateTime timestamp, CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(_config.ArchivePath, ".backup_metadata.json");
        try
        {
            Directory.CreateDirectory(_config.ArchivePath);
            var metadata = new BackupMetadata
            {
                LastBackupTimestamp = timestamp,
                UpdatedAt = DateTime.UtcNow
            };
            var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(metadataPath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write backup metadata to {Path}", metadataPath);
        }
    }

    private async Task UploadToS3Async(string filePath, CancellationToken cancellationToken)
    {
        // S3 upload requires Cabinet.AWS integration (IAmazonS3 client).
        // For now, create a gzipped copy of the archive in the configured backup directory
        // with metadata for tracking purposes.
        try
        {
            var backupDir = Path.Combine(_config.ArchivePath, "backups");
            Directory.CreateDirectory(backupDir);

            var fileName = Path.GetFileName(filePath);
            var gzipPath = Path.Combine(backupDir, $"{fileName}.gz");

            // Create gzipped backup
            await using (var sourceStream = File.OpenRead(filePath))
            await using (var targetStream = File.Create(gzipPath))
            await using (var gzipStream = new System.IO.Compression.GZipStream(targetStream, System.IO.Compression.CompressionLevel.Optimal))
            {
                await sourceStream.CopyToAsync(gzipStream, cancellationToken);
            }

            // Write backup manifest with metadata
            var sourceFileInfo = new FileInfo(filePath);
            var gzipFileInfo = new FileInfo(gzipPath);
            var manifest = new BackupManifest
            {
                OriginalFile = filePath,
                BackupFile = gzipPath,
                OriginalSizeBytes = sourceFileInfo.Length,
                CompressedSizeBytes = gzipFileInfo.Length,
                CreatedAt = DateTime.UtcNow,
                S3Bucket = _config.S3Bucket ?? string.Empty,
                S3Prefix = _config.S3Prefix
            };

            var manifestPath = Path.ChangeExtension(gzipPath, ".manifest.json");
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);

            _logger.LogInformation(
                "Backup archived: {GzipPath} ({OriginalSize:N0} -> {CompressedSize:N0} bytes). " +
                "S3 upload pending — configure Cabinet.AWS for automatic S3 delivery.",
                gzipPath, sourceFileInfo.Length, gzipFileInfo.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create gzipped backup from {FilePath}", filePath);
            throw;
        }
    }
}

/// <summary>
/// Metadata for tracking incremental backup state.
/// </summary>
internal sealed class BackupMetadata
{
    public DateTime? LastBackupTimestamp { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Manifest for a backup archive file.
/// </summary>
internal sealed class BackupManifest
{
    public string OriginalFile { get; set; } = string.Empty;
    public string BackupFile { get; set; } = string.Empty;
    public long OriginalSizeBytes { get; set; }
    public long CompressedSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string S3Bucket { get; set; } = string.Empty;
    public string S3Prefix { get; set; } = string.Empty;
}

/// <summary>
/// Interface for backup operations
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Create a backup of logs
    /// </summary>
    Task<string> BackupAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        string? customPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Create an incremental backup since last backup
    /// </summary>
    Task<string> BackupIncrementalAsync(CancellationToken cancellationToken = default);
}