using System.Security.Cryptography;
using System.Text.Json;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Persists file read positions to disk so the agent can resume after restart.
/// Thread-safe for concurrent access from multiple harvesters.
/// </summary>
public sealed class FileStateRegistry : IDisposable
{
    private readonly string _registryPath;
    private readonly TimeSpan _saveInterval;
    private readonly Dictionary<string, FileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly ILogger<FileStateRegistry> _logger;
    private bool _dirty;
    private Timer? _saveTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileStateRegistry(string registryPath, int saveIntervalSeconds, ILogger<FileStateRegistry> logger)
    {
        _registryPath = registryPath;
        _saveInterval = TimeSpan.FromSeconds(saveIntervalSeconds);
        _logger = logger;
    }

    /// <summary>
    /// Load saved state from disk and start periodic save timer.
    /// </summary>
    public void Initialize()
    {
        Load();
        _saveTimer = new Timer(_ => SaveIfDirty(), null, _saveInterval, _saveInterval);
    }

    /// <summary>
    /// Get the saved state for a file, or null if not tracked.
    /// </summary>
    public FileState? GetState(string path)
    {
        lock (_lock)
        {
            return _states.TryGetValue(path, out var state) ? state : null;
        }
    }

    /// <summary>
    /// Update the state for a file after reading new data.
    /// </summary>
    public void UpdateState(string path, long offset, long fileSize, long linesRead)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(path, out var state))
            {
                state = new FileState { Path = path };
                _states[path] = state;
            }

            state.Offset = offset;
            state.LastKnownSize = fileSize;
            state.LinesRead = linesRead;
            state.LastUpdated = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>
    /// Register a new file with its header hash for rotation detection.
    /// </summary>
    public void RegisterFile(string path, long fileSize, string? headerHash)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(path, out var state))
            {
                state = new FileState { Path = path };
                _states[path] = state;
            }

            state.LastKnownSize = fileSize;
            state.HeaderHash = headerHash;
            state.LastModified = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>
    /// Reset offset to 0 (e.g., after file rotation/truncation).
    /// </summary>
    public void ResetOffset(string path)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(path, out var state))
            {
                state.Offset = 0;
                state.LinesRead = 0;
                state.LastUpdated = DateTime.UtcNow;
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Remove tracking for a file (e.g., deleted).
    /// </summary>
    public void Remove(string path)
    {
        lock (_lock)
        {
            if (_states.Remove(path))
            {
                _dirty = true;
            }
        }
    }

    /// <summary>
    /// Get all tracked file paths.
    /// </summary>
    public IReadOnlyList<string> GetTrackedPaths()
    {
        lock (_lock)
        {
            return _states.Keys.ToList();
        }
    }

    /// <summary>
    /// Compute a hash of the first N bytes of a file for rotation detection.
    /// </summary>
    public static string? ComputeHeaderHash(string path, int headerSize = 256)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(headerSize, stream.Length)];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0) return null;

            var hash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Force save to disk immediately.
    /// </summary>
    public void Save()
    {
        List<FileState> snapshot;
        lock (_lock)
        {
            snapshot = _states.Values.ToList();
            // Don't clear _dirty until AFTER successful write —
            // if the write fails, or new updates arrive during write, we'll retry on next tick.
        }

        try
        {
            var dir = Path.GetDirectoryName(_registryPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = _registryPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _registryPath, overwrite: true);

            // Only clear dirty flag after successful write
            lock (_lock)
            {
                _dirty = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save file state registry to {Path}", _registryPath);
            // _dirty remains true, so we'll retry on the next timer tick
        }
    }

    private void Load()
    {
        if (!File.Exists(_registryPath))
        {
            _logger.LogInformation("No existing registry at {Path}, starting fresh", _registryPath);
            return;
        }

        try
        {
            var json = File.ReadAllText(_registryPath);
            var states = JsonSerializer.Deserialize<List<FileState>>(json, JsonOptions);
            if (states != null)
            {
                lock (_lock)
                {
                    foreach (var state in states)
                    {
                        _states[state.Path] = state;
                    }
                }

                _logger.LogInformation("Loaded {Count} file states from registry", states.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load registry from {Path}, starting fresh", _registryPath);
        }
    }

    private void SaveIfDirty()
    {
        bool isDirty;
        lock (_lock)
        {
            isDirty = _dirty;
        }

        if (isDirty)
        {
            Save();
        }
    }

    public void Dispose()
    {
        _saveTimer?.Dispose();
        SaveIfDirty();
    }
}
