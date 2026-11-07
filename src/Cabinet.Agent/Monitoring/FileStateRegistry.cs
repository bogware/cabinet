using System.Security.Cryptography;
using System.Text.Json;

namespace Cabinet.Agent.Monitoring;

/// <summary>
/// Persists how far each file has been delivered, so the agent can resume after a restart.
/// Thread-safe for concurrent access from multiple harvesters.
/// </summary>
public sealed class FileStateRegistry : IDisposable
{
    private const int FingerprintBytes = 1024;
    private const int MaxDisplacedStates = 256;
    private static readonly TimeSpan DisplacedLifetime = TimeSpan.FromDays(7);

    /// <summary>Path comparison matching the platform: Linux paths are case-sensitive.</summary>
    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _registryPath;
    private readonly TimeSpan _saveInterval;
    private readonly Dictionary<string, FileState> _states = new(PathComparer);
    private readonly List<FileState> _displaced = [];
    private readonly int _maxLineBytes;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly ILogger<FileStateRegistry> _logger;
    private bool _dirty;
    private bool _hasUnsavedKey;
    private Timer? _saveTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileStateRegistry(string registryPath, int saveIntervalSeconds, ILogger<FileStateRegistry> logger, int maxLineBytes = 1024 * 1024)
    {
        _maxLineBytes = maxLineBytes;
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
    /// Works out which contents a file holds and where to read from: the same file continues, a
    /// file renamed from a tracked path keeps its position, and anything else starts from zero.
    /// </summary>
    public FileState Resolve(string path, string fingerprint, long fileSize)
    {
        lock (_lock)
        {
            _states.TryGetValue(path, out var existing);
            if (existing != null && IsSameContents(existing, path, fingerprint, fileSize))
            {
                return existing;
            }

            var renamed =
                _displaced.FirstOrDefault(state => IsSameContents(state, path, fingerprint, fileSize))
                ?? _states.Values.FirstOrDefault(state =>
                    !PathComparer.Equals(state.Path, path)
                    && IsSameContents(state, path, fingerprint, fileSize)
                    && NoLongerHolds(state));

            if (existing != null)
            {
                Displace(existing);
            }

            if (renamed == null)
            {
                return StartNew(path, fingerprint);
            }

            _logger.LogInformation("File {Old} is now {New}, continuing from offset {Offset}", renamed.Path, path, renamed.Offset);
            _displaced.Remove(renamed);
            // The old path may already hold a newer file's state, which has to stay.
            if (_states.TryGetValue(renamed.Path, out var atOldPath) && ReferenceEquals(atOldPath, renamed))
            {
                _states.Remove(renamed.Path);
            }
            renamed.Path = path;
            renamed.Displaced = false;
            _states[path] = renamed;
            _dirty = true;
            return renamed;
        }
    }

    /// <summary>
    /// Records that everything before an offset has been delivered. Ignored if the file has since
    /// been rotated, because the offset then belongs to the old contents.
    /// </summary>
    public void Commit(string fileKey, long offset, long linesRead, long anchorStart = 0, string? anchorHash = null)
    {
        lock (_lock)
        {
            // Events from before a rotation can arrive after it, and still count for those contents.
            var state = _states.Values.FirstOrDefault(s => s.FileKey == fileKey)
                ?? _displaced.FirstOrDefault(s => s.FileKey == fileKey);
            if (state == null || offset <= state.Offset)
            {
                return;
            }

            state.Offset = offset;
            state.LinesRead = linesRead;
            state.AnchorStart = anchorStart;
            state.AnchorHash = anchorHash;
            state.LastUpdated = DateTime.UtcNow;
            _dirty = true;
        }
    }

    /// <summary>
    /// Stops tracking a path that no longer exists. The state is kept for a while, in case its
    /// contents reappear under a new name.
    /// </summary>
    public void Remove(string path)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(path, out var state))
            {
                Displace(state);
            }
        }
    }

    /// <summary>Hash stored with a checkpoint to recognize the event text at that position.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

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
    /// Identifies a file's contents by its first line, or its first 1 KB if that line is longer.
    /// This doesn't change as the file grows, unlike a hash of however many bytes it has so far.
    /// Returns null until the file has a complete first line, or if it can't be read.
    /// </summary>
    public static string? ComputeFingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[FingerprintBytes];
            var count = stream.ReadAtLeast(buffer, FingerprintBytes, throwOnEndOfStream: false);

            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            if (newline < 0 && count < FingerprintBytes)
            {
                return null;
            }

            var length = newline >= 0 ? newline + 1 : FingerprintBytes;
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves now if a new file key has been created. Event IDs derive from the key, so it must be
    /// on disk before events using it are sent: a crash that forgot it would re-read those lines
    /// under new IDs, and the server would store them twice.
    /// </summary>
    public void SaveNewKeys()
    {
        bool needed;
        lock (_lock)
        {
            needed = _hasUnsavedKey;
            _hasUnsavedKey = false;
        }

        if (needed)
        {
            Save();
        }
    }

    /// <summary>
    /// Force save to disk immediately.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            List<FileState> snapshot;
            lock (_lock)
            {
                PruneDisplaced();
                snapshot = [.. _states.Values, .. _displaced];
                // Clear before writing, so an update during the write marks it dirty again.
                _dirty = false;
            }

            try
            {
                var dir = Path.GetDirectoryName(_registryPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Reach the disk before the rename, so a crash can't leave a truncated registry.
                var tempPath = _registryPath + ".tmp";
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    lock (_lock)
                    {
                        JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                    }

                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _registryPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save file state registry to {Path}", _registryPath);
                lock (_lock)
                {
                    _dirty = true;
                }
            }
        }
    }

    private FileState StartNew(string path, string fingerprint)
    {
        var state = new FileState
        {
            Path = path,
            FileKey = Guid.NewGuid().ToString("N"),
            Fingerprint = fingerprint,
        };
        _states[path] = state;
        _dirty = true;
        _hasUnsavedKey = true;
        return state;
    }

    private void Displace(FileState state)
    {
        _states.Remove(state.Path);
        state.Displaced = true;
        state.LastUpdated = DateTime.UtcNow;
        _displaced.Add(state);
        PruneDisplaced();
        _dirty = true;
    }

    private void PruneDisplaced()
    {
        var cutoff = DateTime.UtcNow - DisplacedLifetime;
        _displaced.RemoveAll(state => state.LastUpdated < cutoff);
        if (_displaced.Count > MaxDisplacedStates)
        {
            _displaced.RemoveRange(0, _displaced.Count - MaxDisplacedStates);
        }
    }

    /// <summary>
    /// Whether a file holds a state's contents. Files often share a first line, such as a header,
    /// so this also checks that the last delivered event is still at its recorded position.
    /// Guessing wrong here would skip the start of a new file.
    /// </summary>
    private bool IsSameContents(FileState state, string path, string fingerprint, long fileSize)
    {
        if (state.Fingerprint != fingerprint || fileSize < state.Offset)
        {
            return false;
        }

        if (state.AnchorHash == null || state.Offset == 0)
        {
            return true;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = LineReader.ReadRange(stream, state.AnchorStart, state.Offset, _maxLineBytes);
            return HashText(text) == state.AnchorHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Can't verify {Path} against its checkpoint", path);
            return false;
        }
    }

    private static bool NoLongerHolds(FileState state)
    {
        var info = new FileInfo(state.Path);
        return !info.Exists
            || info.Length < state.Offset
            || ComputeFingerprint(state.Path) != state.Fingerprint;
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
                        if (state.Displaced)
                        {
                            _displaced.Add(state);
                        }
                        else
                        {
                            _states[state.Path] = state;
                        }
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
