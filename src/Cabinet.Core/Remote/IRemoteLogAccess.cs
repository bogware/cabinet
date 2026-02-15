using Cabinet.Core.Models;

namespace Cabinet.Core.Remote;

/// <summary>
/// Interface for accessing logs from remote sources
/// </summary>
public interface IRemoteLogAccess
{
    /// <summary>
    /// Connect to a remote log source
    /// </summary>
    Task<RemoteConnection> ConnectAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// List available log files from remote source
    /// </summary>
    Task<IEnumerable<RemoteLogFile>> ListLogsAsync(RemoteConnection connection, string path, string pattern = "*.log", CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream log content from remote source
    /// </summary>
    IAsyncEnumerable<string> StreamLogAsync(RemoteConnection connection, string filePath, StreamOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Download log for local processing (optional caching)
    /// </summary>
    Task<string> CacheLogLocallyAsync(RemoteConnection connection, string remotePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to remote source
    /// </summary>
    Task<bool> TestConnectionAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration for remote connections
/// </summary>
public class RemoteConnectionConfig
{
    public RemoteSourceType Type { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Path { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public bool UseCache { get; set; } = true;
    public int CacheExpirationMinutes { get; set; } = 60;
    public Dictionary<string, string> CustomSettings { get; set; } = new();
}

/// <summary>
/// Types of remote sources
/// </summary>
public enum RemoteSourceType
{
    Local,
    SMB,        // Windows shares
    NFS,        // Network File System
    SSH,        // SSH/SFTP
    S3,         // AWS S3
    CloudWatch, // AWS CloudWatch
    CloudTrail, // AWS CloudTrail
    Azure,      // Azure Monitor
    HTTP        // HTTP/REST endpoints
}

/// <summary>
/// Represents an active remote connection
/// </summary>
public class RemoteConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public RemoteSourceType Type { get; set; }
    public string Host { get; set; } = "";
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public bool IsConnected { get; set; }
    public object? NativeConnection { get; set; } // Platform-specific connection object
}

/// <summary>
/// Represents a remote log file
/// </summary>
public class RemoteLogFile
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public RemoteSourceType SourceType { get; set; }
    public string? RemoteHost { get; set; }
}

/// <summary>
/// Options for streaming logs
/// </summary>
public class StreamOptions
{
    public bool Follow { get; set; } = false;
    public bool FromEnd { get; set; } = false;
    public int PollIntervalMs { get; set; } = 1000;
    public int MaxLines { get; set; } = 0; // 0 = unlimited
    public string? Filter { get; set; }
}