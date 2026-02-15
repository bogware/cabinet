using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Cabinet.Core.Remote;

/// <summary>
/// Handles access to network shares (SMB/CIFS)
/// </summary>
public class NetworkShareAccess : IRemoteLogAccess
{
    private readonly ILogger<NetworkShareAccess> _logger;
    private readonly Dictionary<string, RemoteConnection> _connections = new();

    public NetworkShareAccess(ILogger<NetworkShareAccess> logger)
    {
        _logger = logger;
    }

    public async Task<RemoteConnection> ConnectAsync(RemoteConnectionConfig config, CancellationToken cancellationToken = default)
    {
        if (config.Type != RemoteSourceType.SMB && config.Type != RemoteSourceType.NFS)
        {
            throw new ArgumentException($"Unsupported connection type: {config.Type}");
        }

        var connection = new RemoteConnection
        {
            Type = config.Type,
            Host = config.Host ?? "localhost",
            IsConnected = false
        };

        try
        {
            // Build UNC path
            var uncPath = BuildUncPath(config);

            // Attempt to connect with credentials if provided
            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await ConnectWindowsShareAsync(uncPath, config, cancellationToken);
                }
                else
                {
                    await ConnectLinuxShareAsync(uncPath, config, cancellationToken);
                }
            }

            // Test access
            if (Directory.Exists(uncPath))
            {
                connection.IsConnected = true;
                connection.NativeConnection = uncPath;
                _connections[connection.Id] = connection;
                _logger.LogInformation("Connected to network share: {Path}", uncPath);
            }
            else
            {
                throw new DirectoryNotFoundException($"Cannot access network path: {uncPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to network share: {Host}", config.Host);
            throw;
        }

        return connection;
    }

    public async Task<IEnumerable<RemoteLogFile>> ListLogsAsync(
        RemoteConnection connection,
        string path,
        string pattern = "*.log",
        CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || connection.NativeConnection == null)
        {
            throw new InvalidOperationException("Not connected to remote source");
        }

        var basePath = connection.NativeConnection.ToString();
        var fullPath = Path.Combine(basePath!, path);

        var files = new List<RemoteLogFile>();

        try
        {
            if (Directory.Exists(fullPath))
            {
                var dirInfo = new DirectoryInfo(fullPath);
                var fileInfos = dirInfo.GetFiles(pattern, SearchOption.AllDirectories);

                foreach (var file in fileInfos)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    files.Add(new RemoteLogFile
                    {
                        Path = file.FullName,
                        Name = file.Name,
                        Size = file.Length,
                        LastModified = file.LastWriteTimeUtc,
                        SourceType = connection.Type,
                        RemoteHost = connection.Host
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list logs from network share: {Path}", fullPath);
            throw;
        }

        return await Task.FromResult(files);
    }

    public async IAsyncEnumerable<string> StreamLogAsync(
        RemoteConnection connection,
        string filePath,
        StreamOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!connection.IsConnected || connection.NativeConnection == null)
        {
            throw new InvalidOperationException("Not connected to remote source");
        }

        var basePath = connection.NativeConnection.ToString();
        var fullPath = Path.IsPathRooted(filePath) ? filePath : Path.Combine(basePath!, filePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Remote file not found: {fullPath}");
        }

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        if (options.FromEnd)
        {
            fs.Seek(0, SeekOrigin.End);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();

            if (line != null)
            {
                yield return line;
            }
            else if (options.Follow)
            {
                await Task.Delay(options.PollIntervalMs, cancellationToken);
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
        if (!connection.IsConnected || connection.NativeConnection == null)
        {
            throw new InvalidOperationException("Not connected to remote source");
        }

        var basePath = connection.NativeConnection.ToString();
        var fullRemotePath = Path.Combine(basePath!, remotePath);

        if (!File.Exists(fullRemotePath))
        {
            throw new FileNotFoundException($"Remote file not found: {fullRemotePath}");
        }

        // Create cache directory
        var cacheDir = Path.Combine(Path.GetTempPath(), "cabinet", "cache", connection.Host.Replace('/', '_'));
        Directory.CreateDirectory(cacheDir);

        var fileName = Path.GetFileName(remotePath);
        var localPath = Path.Combine(cacheDir, $"{fileName}.{DateTime.UtcNow.Ticks}");

        try
        {
            await using var sourceStream = new FileStream(fullRemotePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destStream = new FileStream(localPath, FileMode.Create, FileAccess.Write);

            await sourceStream.CopyToAsync(destStream, cancellationToken);

            _logger.LogInformation("Cached remote file locally: {Remote} -> {Local}", remotePath, localPath);
            return localPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cache remote file: {Path}", remotePath);

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
        catch
        {
            return false;
        }
    }

    private string BuildUncPath(RemoteConnectionConfig config)
    {
        if (string.IsNullOrEmpty(config.Host))
        {
            throw new ArgumentException("Host is required for network share connection");
        }

        // Handle different path formats
        var host = config.Host.TrimStart('\\', '/');
        var path = config.Path?.TrimStart('\\', '/') ?? "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows UNC path: \\server\share
            return $@"\\{host}\{path}".TrimEnd('\\');
        }
        else
        {
            // Linux mount point or SMB URL
            if (config.Type == RemoteSourceType.SMB)
            {
                // Check if already mounted
                var mountPoint = $"/mnt/{host.Replace('.', '_')}/{path}".TrimEnd('/');
                if (Directory.Exists(mountPoint))
                {
                    return mountPoint;
                }

                // Otherwise return SMB URL for mounting
                return $"smb://{host}/{path}".TrimEnd('/');
            }
            else
            {
                // NFS or local mount
                return $"/mnt/{host}/{path}".TrimEnd('/');
            }
        }
    }

    private async Task ConnectWindowsShareAsync(string uncPath, RemoteConnectionConfig config, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            // Use WNetAddConnection2 for Windows
            var netResource = new NETRESOURCE
            {
                dwType = ResourceType.DISK,
                lpRemoteName = uncPath,
                lpProvider = null
            };

            var result = WNetAddConnection2(
                ref netResource,
                config.Password,
                !string.IsNullOrEmpty(config.Domain)
                    ? $@"{config.Domain}\{config.Username}"
                    : config.Username,
                0);

            if (result != 0 && result != 1219) // 1219 = ERROR_SESSION_CREDENTIAL_CONFLICT (already connected)
            {
                throw new UnauthorizedAccessException($"Failed to connect to network share. Error code: {result}");
            }
        }, cancellationToken);
    }

    private async Task ConnectLinuxShareAsync(string smbUrl, RemoteConnectionConfig config, CancellationToken cancellationToken)
    {
        // For Linux, we'll need to mount the share using mount.cifs or similar
        // This typically requires sudo privileges
        var mountPoint = $"/mnt/cabinet/{config.Host?.Replace('.', '_')}";

        _logger.LogInformation("Linux SMB mounting would require sudo privileges. Mount command:");
        _logger.LogInformation("sudo mount -t cifs {Url} {MountPoint} -o username={User},domain={Domain}",
            smbUrl, mountPoint, config.Username, config.Domain ?? "WORKGROUP");

        // In production, you might use a privileged helper service or pre-mounted shares
        await Task.CompletedTask;
    }

    // Windows P/Invoke declarations
    [DllImport("mpr.dll")]
    private static extern int WNetAddConnection2(
        ref NETRESOURCE netResource,
        string? password,
        string? username,
        int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public ResourceScope dwScope;
        public ResourceType dwType;
        public ResourceDisplayType dwDisplayType;
        public ResourceUsage dwUsage;
        public string? lpLocalName;
        public string lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    private enum ResourceScope
    {
        CONNECTED = 1,
        GLOBALNET = 2,
        REMEMBERED = 3
    }

    private enum ResourceType
    {
        ANY = 0,
        DISK = 1,
        PRINT = 2
    }

    private enum ResourceDisplayType
    {
        GENERIC = 0,
        DOMAIN = 1,
        SERVER = 2,
        SHARE = 3
    }

    private enum ResourceUsage
    {
        CONNECTABLE = 1,
        CONTAINER = 2
    }
}