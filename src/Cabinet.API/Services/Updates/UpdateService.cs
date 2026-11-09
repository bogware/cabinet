using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cabinet.API.Services.Updates;

/// <summary>Where updating stands, for the dashboard, CLI and API.</summary>
public sealed class UpdateStatus
{
    public required string Current { get; init; }
    public string? Latest { get; init; }
    public bool Available { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset? LastChecked { get; init; }
    public string? CheckError { get; init; }

    /// <summary>Idle, Downloading, Applying or Failed.</summary>
    public required string State { get; init; }
    public string? Message { get; init; }

    /// <summary>Whether this server can update itself as it's running.</summary>
    public bool CanApply { get; init; }
    public string? CannotApplyReason { get; init; }
    public required IReadOnlyList<AgentVersion> Agents { get; init; }
}

/// <summary>An agent that has checked for updates.</summary>
public sealed record AgentVersion(string Host, string Platform, string Version, DateTimeOffset LastSeen);

/// <summary>A verified agent build for agents to download.</summary>
public sealed record AgentPackage(string Path, string Sha256, long Size, string Version);

/// <summary>
/// Finds new releases, downloads and verifies them, applies them to this server on request, and
/// serves agents the build matching this server's version.
/// </summary>
public sealed class UpdateService
{
    private readonly IReleaseSource _source;
    private readonly UpdateApplier _applier;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _workDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, AgentVersion> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentPackage> _agentPackages = new(StringComparer.Ordinal);

    private ReleaseInfo? _latest;
    private DateTimeOffset? _lastChecked;
    private string? _checkError;
    private string _state = "Idle";
    private string? _message;

    public UpdateService(IReleaseSource source, UpdateApplier applier, UpdateOptions options, ILogger<UpdateService> logger)
    {
        _source = source;
        _applier = applier;
        _logger = logger;
        _workDirectory = string.IsNullOrWhiteSpace(options.WorkDirectory)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "updates"))
            : options.WorkDirectory;
    }

    /// <summary>This program's platform, as release archives name it.</summary>
    public static string Platform => RuntimeInformation.RuntimeIdentifier;

    public UpdateStatus GetStatus()
    {
        var latest = _latest;
        var (canApply, reason) = _applier.CanApply();
        return new UpdateStatus
        {
            Current = ReleaseVersions.Current.ToString(),
            Latest = latest?.Version.ToString(),
            Available = latest != null && latest.Version > ReleaseVersions.Current,
            Notes = latest?.Notes,
            LastChecked = _lastChecked,
            CheckError = _checkError,
            State = _state,
            Message = _message,
            CanApply = canApply,
            CannotApplyReason = reason,
            Agents = _agents.Values.OrderBy(agent => agent.Host, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Asks for the newest release.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _latest = await _source.GetLatestAsync(cancellationToken);
            _checkError = null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _checkError = ex.Message;
            _logger.LogWarning("Checking for updates failed: {Message}", ex.Message);
        }

        _lastChecked = DateTimeOffset.UtcNow;
        return GetStatus();
    }

    /// <summary>
    /// Downloads, verifies and unpacks the newest release, then hands it to the applier, which
    /// restarts this server on it. Returns once the restart has been started.
    /// </summary>
    public async Task<UpdateStatus> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var (canApply, reason) = _applier.CanApply();
        if (!canApply)
        {
            throw new InvalidOperationException(reason);
        }

        if (!await _lock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("An update is already in progress");
        }

        try
        {
            await CheckAsync(cancellationToken);
            var latest = _latest ?? throw new InvalidOperationException(_checkError ?? "No release found");
            if (latest.Version <= ReleaseVersions.Current)
            {
                throw new InvalidOperationException($"Already on {ReleaseVersions.Current}, the newest release");
            }

            _state = "Downloading";
            _message = $"Downloading {latest.Tag}";
            var staged = await StageAsync(latest, Platform, cancellationToken);

            _state = "Applying";
            _message = $"Restarting on {latest.Tag}";
            _logger.LogWarning("Updating from {Current} to {Latest}; the server will restart", ReleaseVersions.Current, latest.Version);
            _applier.Launch(staged, latest.Version);
            return GetStatus();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = "Failed";
            _message = ex.Message;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The agent build for a platform at this server's version, so agents follow the server.
    /// Null if the release has no build for it.
    /// </summary>
    public async Task<AgentPackage?> GetAgentPackageAsync(string platform, CancellationToken cancellationToken = default)
    {
        if (!ReleaseVersions.Platforms.Contains(platform))
        {
            return null;
        }

        if (_agentPackages.TryGetValue(platform, out var cached) && File.Exists(cached.Path))
        {
            return cached;
        }

        var release = await _source.GetByTagAsync($"v{ReleaseVersions.Current}", cancellationToken);
        if (release == null)
        {
            return null;
        }

        var staged = await StageAsync(release, platform, cancellationToken);
        var binary = Path.Combine(staged, "agent", platform.StartsWith("win", StringComparison.Ordinal) ? "cabinet-agent.exe" : "cabinet-agent");
        if (!File.Exists(binary))
        {
            return null;
        }

        await using var stream = File.OpenRead(binary);
        var package = new AgentPackage(binary, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length, release.Version.ToString());
        _agentPackages[platform] = package;
        return package;
    }

    /// <summary>Remembers an agent's version for the dashboard. Bounded, since agents report it.</summary>
    public void RecordAgent(string host, string platform, string version)
    {
        if (_agents.Count < 10_000 || _agents.ContainsKey(host))
        {
            _agents[host] = new AgentVersion(host, platform, version, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Downloads a release's archive for a platform, checks it against the release's SHA-256, and
    /// unpacks it. Reuses an earlier download.
    /// </summary>
    public async Task<string> StageAsync(ReleaseInfo release, string platform, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_workDirectory, $"v{release.Version}", platform);
        var complete = Path.Combine(directory, ".complete");
        if (File.Exists(complete))
        {
            return directory;
        }

        var archiveName = ReleaseVersions.ArchiveName(platform);
        var archive = release.Asset(archiveName) ?? throw new InvalidOperationException($"{release.Tag} has no {archiveName}");
        var checksum = release.Asset(archiveName + ".sha256") ?? throw new InvalidOperationException($"{release.Tag} has no checksum for {archiveName}");

        string expected;
        await using (var checksumStream = await _source.OpenAssetAsync(checksum, cancellationToken))
        using (var reader = new StreamReader(checksumStream))
        {
            expected = (await reader.ReadToEndAsync(cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        var download = Path.Combine(directory, "..", archiveName + ".download");
        try
        {
            await using (var source = await _source.OpenAssetAsync(archive, cancellationToken))
            await using (var file = File.Create(download))
            {
                await source.CopyToAsync(file, cancellationToken);
            }

            string actual;
            await using (var file = File.OpenRead(download))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{archiveName} doesn't match its checksum (expected {expected}, got {actual}). Nothing was changed.");
            }

            if (archiveName.EndsWith(".zip", StringComparison.Ordinal))
            {
                ZipFile.ExtractToDirectory(download, directory, overwriteFiles: true);
            }
            else
            {
                await using var file = File.OpenRead(download);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, directory, overwriteFiles: true, cancellationToken);
            }

            await File.WriteAllTextAsync(complete, actual, cancellationToken);
            _logger.LogInformation("Downloaded and verified {Archive} from {Tag}", archiveName, release.Tag);
            return directory;
        }
        finally
        {
            File.Delete(download);
        }
    }
}

/// <summary>Checks for new releases on a schedule. Never applies them.</summary>
public sealed class UpdateCheckService : BackgroundService
{
    private readonly UpdateService _updates;
    private readonly UpdateOptions _options;

    public UpdateCheckService(UpdateService updates, UpdateOptions options)
    {
        _updates = updates;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CheckIntervalHours <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await _updates.CheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(_options.CheckIntervalHours), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
            return;
        }
    }
}
