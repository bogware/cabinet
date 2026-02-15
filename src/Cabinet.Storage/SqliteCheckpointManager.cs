using Cabinet.Core.Interfaces;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Cabinet.Storage;

/// <summary>
/// SQLite-based checkpoint manager for incremental processing
/// </summary>
public class SqliteCheckpointManager : ICheckpointManager
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteCheckpointManager> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteCheckpointManager(string connectionString, ILogger<SqliteCheckpointManager> logger)
    {
        _connectionString = connectionString ?? "Data Source=checkpoints.db";
        _logger = logger;
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

    private async Task InitializeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        var sql = @"
            CREATE TABLE IF NOT EXISTS checkpoints (
                file_path TEXT PRIMARY KEY,
                file_size INTEGER NOT NULL,
                last_modified INTEGER NOT NULL,
                last_position INTEGER NOT NULL,
                lines_processed INTEGER NOT NULL,
                file_hash TEXT,
                checkpoint_time INTEGER NOT NULL
            );
        ";

        await connection.ExecuteAsync(sql);
    }

    public async Task<FileCheckpoint?> GetCheckpointAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = "SELECT * FROM checkpoints WHERE file_path = @FilePath LIMIT 1";
        var result = await connection.QuerySingleOrDefaultAsync<dynamic>(sql, new { FilePath = filePath });

        if (result == null) return null;

        return new FileCheckpoint
        {
            FilePath = result.file_path,
            FileSize = result.file_size,
            LastModified = new DateTime(result.last_modified),
            LastPosition = result.last_position,
            LinesProcessed = result.lines_processed,
            FileHash = result.file_hash,
            CheckpointTime = new DateTime(result.checkpoint_time)
        };
    }

    public async Task SaveCheckpointAsync(FileCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = @"
            INSERT OR REPLACE INTO checkpoints (
                file_path, file_size, last_modified, last_position,
                lines_processed, file_hash, checkpoint_time
            ) VALUES (
                @FilePath, @FileSize, @LastModified, @LastPosition,
                @LinesProcessed, @FileHash, @CheckpointTime
            )";

        await connection.ExecuteAsync(sql, new
        {
            checkpoint.FilePath,
            checkpoint.FileSize,
            LastModified = checkpoint.LastModified.Ticks,
            checkpoint.LastPosition,
            checkpoint.LinesProcessed,
            checkpoint.FileHash,
            CheckpointTime = checkpoint.CheckpointTime.Ticks
        });

        _logger.LogDebug("Saved checkpoint for {FilePath} at position {Position}",
            checkpoint.FilePath, checkpoint.LastPosition);
    }

    public async Task DeleteCheckpointAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = "DELETE FROM checkpoints WHERE file_path = @FilePath";
        await connection.ExecuteAsync(sql, new { FilePath = filePath });
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await connection.ExecuteAsync("DELETE FROM checkpoints");
        _logger.LogInformation("Cleared all checkpoints");
    }
}