using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cabinet.AWS.Services;
using Microsoft.Data.Sqlite;

namespace Cabinet.API.Services;

/// <summary>
/// Configuration for the local backup service.
/// Bind from the "Backup" configuration section.
/// </summary>
public sealed class BackupConfiguration
{
    /// <summary>Directory to store backup files</summary>
    public string ArchivePath { get; set; } = "./backups";

    /// <summary>Days to retain local backups (0 = unlimited)</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>S3 bucket for optional cloud upload after backup</summary>
    public string? S3Bucket { get; set; }

    /// <summary>S3 key prefix for uploaded backups</summary>
    public string S3Prefix { get; set; } = "cabinet-backups/";

    /// <summary>Enable automatic periodic backup</summary>
    public bool AutoBackupEnabled { get; set; }

    /// <summary>Hours between automatic backups (default 24)</summary>
    public int AutoBackupIntervalHours { get; set; } = 24;
}

/// <summary>
/// A single entry in the backup manifest file.
/// </summary>
public sealed class BackupManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? S3Key { get; set; }
    public string? Checksum { get; set; }
    public string BackupType { get; set; } = "full";
    public string? SourceDatabase { get; set; }
}

/// <summary>
/// Backup manifest tracking all backup history. Persisted as JSON.
/// </summary>
public sealed class BackupManifest
{
    public List<BackupManifestEntry> Entries { get; set; } = new();
    public DateTime? LastBackupUtc { get; set; }
    public int SchemaVersion { get; set; } = 1;
}

/// <summary>
/// Local backup service for SQLite databases.
/// Creates gzipped copies of the database file, maintains a JSON manifest with
/// backup history, supports retention-based cleanup, and optionally uploads to S3.
/// </summary>
public sealed class LocalBackupService
{
    private readonly BackupConfiguration _config;
    private readonly ILogger<LocalBackupService> _logger;
    private readonly S3ArchiveService? _s3Service;
    private readonly string _manifestPath;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);
    private readonly string _databasePath;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public LocalBackupService(
        BackupConfiguration config,
        string databasePath,
        ILogger<LocalBackupService> logger,
        S3ArchiveService? s3Service = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _s3Service = s3Service;
        _manifestPath = Path.Combine(config.ArchivePath, "backup-manifest.json");
    }

    /// <summary>
    /// Create a gzipped backup of the SQLite database file.
    /// The backup is copied with FileShare.ReadWrite so it does not block ongoing writes.
    /// After compression, an SHA-256 checksum is computed and the entry is recorded in the manifest.
    /// If an S3 bucket is configured, the backup is uploaded there as well.
    /// </summary>
    public async Task<BackupManifestEntry> CreateBackupAsync(
        string backupType = "full",
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_config.ArchivePath);

        if (!File.Exists(_databasePath))
        {
            throw new FileNotFoundException(
                $"Source database file not found: {_databasePath}", _databasePath);
        }

        var timestamp = DateTime.UtcNow;
        var backupId = $"backup-{timestamp:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var fileName = $"{backupId}.db.gz";
        var backupPath = Path.Combine(_config.ArchivePath, fileName);

        _logger.LogInformation(
            "Creating {BackupType} backup of {Database} to {Path}",
            backupType, _databasePath, backupPath);

        try
        {
            // SQLite's backup API gives a consistent copy, including what's still in the WAL,
            // while the database stays in use. Copying the file wouldn't.
            var snapshot = $"{backupPath}.snapshot";
            var partial = $"{backupPath}.partial";
            try
            {
                await CopyDatabaseAsync(snapshot, cancellationToken);

                await CheckIntegrityAsync(snapshot, cancellationToken);

                await using (var sourceStream = File.OpenRead(snapshot))
                await using (var destStream = File.Create(partial))
                await using (var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal))
                {
                    await sourceStream.CopyToAsync(gzipStream, cancellationToken);
                }

                // Only a complete backup ever has the final name
                File.Move(partial, backupPath);
            }
            finally
            {
                DeleteIfExists(snapshot);
                DeleteIfExists(partial);
            }

            var fileInfo = new FileInfo(backupPath);
            var sizeBytes = fileInfo.Length;

            // Compute SHA-256 checksum of the compressed file
            var checksum = await ComputeChecksumAsync(backupPath, cancellationToken);

            var entry = new BackupManifestEntry
            {
                Id = backupId,
                FileName = fileName,
                FilePath = Path.GetFullPath(backupPath),
                SizeBytes = sizeBytes,
                CreatedAtUtc = timestamp,
                Checksum = checksum,
                BackupType = backupType,
                SourceDatabase = Path.GetFileName(_databasePath)
            };

            // Upload to S3 if configured
            if (_s3Service != null && !string.IsNullOrEmpty(_config.S3Bucket))
            {
                await TryUploadToS3Async(entry, backupPath, cancellationToken);
            }

            // Record in manifest
            await AddManifestEntryAsync(entry, cancellationToken);

            _logger.LogInformation(
                "Backup created: {Id} ({SizeMB:F2} MB, checksum: {Checksum})",
                backupId, sizeBytes / (1024.0 * 1024.0), checksum[..Math.Min(12, checksum.Length)]);

            return entry;
        }
        catch (OperationCanceledException)
        {
            DeleteIfExists(backupPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create backup");

            // Clean up partial backup file
            if (File.Exists(backupPath))
            {
                try { File.Delete(backupPath); }
                catch (Exception cleanEx)
                {
                    _logger.LogWarning(cleanEx, "Failed to clean up partial backup file {Path}", backupPath);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// List all backups from the manifest, ordered by creation date descending.
    /// </summary>
    public async Task<IReadOnlyList<BackupManifestEntry>> ListBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        return manifest.Entries
            .OrderByDescending(e => e.CreatedAtUtc)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Delete backups older than the configured retention period.
    /// Returns the number of backups deleted.
    /// </summary>
    public async Task<int> CleanupOldBackupsAsync(CancellationToken cancellationToken = default)
    {
        if (_config.RetentionDays <= 0)
        {
            _logger.LogDebug("Backup retention disabled (RetentionDays=0)");
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-_config.RetentionDays);

        await _manifestLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestInternalAsync(cancellationToken);
            var toRemove = manifest.Entries.Where(e => e.CreatedAtUtc < cutoff).ToList();

            if (toRemove.Count == 0)
            {
                _logger.LogDebug("No backups older than {Cutoff} to clean up", cutoff);
                return 0;
            }

            var deleted = 0;
            foreach (var entry in toRemove)
            {
                try
                {
                    // Delete local file
                    if (File.Exists(entry.FilePath))
                    {
                        File.Delete(entry.FilePath);
                    }

                    // Delete from S3 if applicable
                    if (_s3Service != null && !string.IsNullOrEmpty(entry.S3Key))
                    {
                        await _s3Service.DeleteAsync(entry.S3Key, _config.S3Bucket, cancellationToken);
                    }

                    manifest.Entries.Remove(entry);
                    deleted++;

                    _logger.LogInformation(
                        "Deleted old backup: {Id} (created {Date})", entry.Id, entry.CreatedAtUtc);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old backup: {Id}", entry.Id);
                }
            }

            // Persist updated manifest
            await SaveManifestInternalAsync(manifest, cancellationToken);

            _logger.LogInformation("Cleanup complete: deleted {Count} old backups", deleted);
            return deleted;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// Get the timestamp of the last successful backup, or null if none.
    /// </summary>
    public async Task<DateTime?> GetLastBackupTimeAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        return manifest.LastBackupUtc;
    }

    /// <summary>
    /// Verify a backup file by decompressing it and computing its checksum.
    /// Returns true if the file can be fully decompressed.
    /// </summary>
    public async Task<bool> VerifyBackupAsync(
        string backupId, CancellationToken cancellationToken = default)
    {
        var manifest = await LoadManifestAsync(cancellationToken);
        var entry = manifest.Entries.FirstOrDefault(e => e.Id == backupId);

        if (entry == null)
        {
            _logger.LogWarning("Backup {Id} not found in manifest", backupId);
            return false;
        }

        if (!File.Exists(entry.FilePath))
        {
            _logger.LogWarning("Backup file {Path} not found on disk", entry.FilePath);
            return false;
        }

        try
        {
            // Re-compute checksum and compare
            var currentChecksum = await ComputeChecksumAsync(entry.FilePath, cancellationToken);
            if (entry.Checksum != null && currentChecksum != entry.Checksum)
            {
                _logger.LogWarning(
                    "Backup {Id} checksum mismatch: expected {Expected}, got {Actual}",
                    backupId, entry.Checksum, currentChecksum);
                return false;
            }

            // A file that decompresses isn't necessarily a database that opens, so open it
            var unpacked = $"{entry.FilePath}.verify";
            try
            {
                await DecompressAsync(entry.FilePath, unpacked, cancellationToken);
                await CheckIntegrityAsync(unpacked, cancellationToken);
            }
            finally
            {
                DeleteIfExists(unpacked);
            }

            _logger.LogInformation("Backup {Id} verified successfully", backupId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backup {Id} verification failed", backupId);
            return false;
        }
    }

    /// <summary>
    /// Restores a backup file to a database path, for use while nothing has the database open.
    /// The backup is checked before anything is replaced, so a bad one leaves the database alone.
    /// </summary>
    /// <param name="backupFile">The <c>.db.gz</c> file.</param>
    /// <param name="databasePath">The database to replace.</param>
    /// <param name="overwrite">Whether replacing an existing database is intended.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    public static async Task RestoreAsync(string backupFile, string databasePath, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupFile))
        {
            throw new FileNotFoundException($"Backup not found: {backupFile}", backupFile);
        }

        if (File.Exists(databasePath) && !overwrite)
        {
            throw new InvalidOperationException($"{databasePath} already exists. Pass --force to replace it.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(directory);
        var unpacked = $"{databasePath}.restoring";
        try
        {
            await DecompressAsync(backupFile, unpacked, cancellationToken);
            await CheckIntegrityAsync(unpacked, cancellationToken);

            // WAL files from the old database would corrupt the restored one
            DeleteIfExists($"{databasePath}-wal");
            DeleteIfExists($"{databasePath}-shm");
            File.Move(unpacked, databasePath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(unpacked);
        }
    }

    /// <summary>
    /// A checkpoint or a write can hold the database for a moment, and the backup API reports that
    /// rather than waiting, so try again before giving up.
    /// </summary>
    private async Task CopyDatabaseAsync(string destinationPath, CancellationToken cancellationToken)
    {
        const int SqliteBusy = 5;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var source = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
                await using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteBusy && attempt < 40)
            {
                _logger.LogDebug("The database is busy, so the backup will try again");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private static async Task DecompressAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var fileStream = File.OpenRead(source);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await using var output = File.Create(destination);
        await gzipStream.CopyToAsync(output, cancellationToken);
    }

    private static async Task CheckIntegrityAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        var result = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (result != "ok")
        {
            throw new InvalidDataException($"The database failed its integrity check: {result}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task TryUploadToS3Async(
        BackupManifestEntry entry, string backupPath, CancellationToken cancellationToken)
    {
        try
        {
            var s3Result = await _s3Service!.UploadAsync(
                backupPath, _config.S3Bucket, _config.S3Prefix, cancellationToken);

            if (s3Result.Success)
            {
                entry.S3Key = s3Result.Key;
                _logger.LogInformation("Backup uploaded to S3: {Key}", s3Result.Key);
            }
            else
            {
                _logger.LogWarning(
                    "S3 upload failed: {Error}. Backup saved locally only.", s3Result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "S3 upload failed. Backup saved locally only.");
        }
    }

    private static async Task<string> ComputeChecksumAsync(
        string filePath, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task AddManifestEntryAsync(
        BackupManifestEntry entry, CancellationToken cancellationToken)
    {
        await _manifestLock.WaitAsync(cancellationToken);
        try
        {
            var manifest = await LoadManifestInternalAsync(cancellationToken);
            manifest.Entries.Add(entry);
            manifest.LastBackupUtc = entry.CreatedAtUtc;
            await SaveManifestInternalAsync(manifest, cancellationToken);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    private async Task<BackupManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        await _manifestLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadManifestInternalAsync(cancellationToken);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>Must be called while holding _manifestLock.</summary>
    private async Task<BackupManifest> LoadManifestInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_manifestPath))
            return new BackupManifest();

        try
        {
            await using var stream = File.OpenRead(_manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                stream, ManifestJsonOptions, cancellationToken);
            return manifest ?? new BackupManifest();
        }
        catch (JsonException ex)
        {
            // Saving over it would lose the only record of older backups' checksums
            var saved = $"{_manifestPath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _logger.LogError(ex, "Corrupt backup manifest at {Path}, kept as {Saved}. The backup files themselves are untouched.", _manifestPath, saved);
            File.Move(_manifestPath, saved);
            return new BackupManifest();
        }
    }

    /// <summary>Must be called while holding _manifestLock.</summary>
    private async Task SaveManifestInternalAsync(
        BackupManifest manifest, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);

        // Atomic write: write to temp file, then move
        var tempPath = _manifestPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
        }

        File.Move(tempPath, _manifestPath, overwrite: true);
    }

    /// <summary>
    /// Extract the database file path from a SQLite connection string.
    /// Handles formats like "Data Source=cabinet.db" and "Data Source=./data/cabinet.db;Cache=Shared".
    /// </summary>
    public static string ExtractDatabasePath(string connectionString)
    {
        // Parse "Data Source=<path>" from the connection string
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Data Source=".Length..].Trim();
            }
        }

        throw new InvalidOperationException(
            $"Cannot extract database path from connection string: missing 'Data Source=' component");
    }
}

/// <summary>
/// Background service that runs automatic backups and cleanup on a schedule.
/// Only active when Backup:AutoBackupEnabled is true.
/// </summary>
public sealed class BackupBackgroundService : BackgroundService
{
    private readonly LocalBackupService _backupService;
    private readonly BackupConfiguration _config;
    private readonly ILogger<BackupBackgroundService> _logger;

    public BackupBackgroundService(
        LocalBackupService backupService,
        BackupConfiguration config,
        ILogger<BackupBackgroundService> logger)
    {
        _backupService = backupService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.AutoBackupEnabled)
        {
            _logger.LogInformation("Automatic backup is disabled");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _config.AutoBackupIntervalHours));
        _logger.LogInformation(
            "Automatic backup enabled, running every {Hours} hours", interval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await _backupService.CreateBackupAsync("auto", stoppingToken);
                await _backupService.CleanupOldBackupsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic backup failed");
            }
        }

        _logger.LogInformation("Backup background service stopped");
    }
}
