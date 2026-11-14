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

/// <summary>
/// A verified agent build for agents to download, with the publisher's signature over it (base64
/// DER), which agents check themselves. Null only for an unsigned build that
/// <c>Updates:AllowUnsigned</c> let through.
/// </summary>
public sealed record AgentPackage(string Path, string Sha256, long Size, string Version, string? Signature = null);

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
    private readonly ConcurrentDictionary<string, DateTimeOffset> _agentStageFailed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _agentStageLock = new(1, 1);
    private readonly byte[] _releaseKeyInfo;
    private readonly bool _allowUnsigned;

    /// <summary>Written into a staged release once its archive's signature has been checked.</summary>
    private const string VerifiedMarker = ".verified";

    private ReleaseInfo? _latest;
    private DateTimeOffset? _lastChecked;
    private string? _checkError;
    private string _state = "Idle";
    private string? _message;

    /// <param name="releaseKey">The publisher's public key. Null, as in production, uses the one built in.</param>
    public UpdateService(IReleaseSource source, UpdateApplier applier, UpdateOptions options, ILogger<UpdateService> logger, ECDsa? releaseKey = null)
    {
        _source = source;
        _applier = applier;
        _logger = logger;

        // Kept as bytes, and loaded for each check: an agent's download and an update can verify at once
        using (var embedded = releaseKey == null ? ReleaseSignature.LoadEmbeddedKey() : null)
        {
            _releaseKeyInfo = (releaseKey ?? embedded!).ExportSubjectPublicKeyInfo();
        }

        _allowUnsigned = options.AllowUnsigned;
        _workDirectory = string.IsNullOrWhiteSpace(options.WorkDirectory)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "updates"))
            : options.WorkDirectory;
        if (_allowUnsigned)
        {
            _logger.LogWarning("Updates:AllowUnsigned is on: this server will install releases, and give agents builds, that have no publisher signature. Use it only with development builds, and turn it off afterwards.");
        }
    }

    /// <summary>This program's platform, as release archives name it.</summary>
    public static string Platform => RuntimeInformation.RuntimeIdentifier;

    /// <summary>Whether a release is newer than this build. A release of the same number as this pre-release is.</summary>
    private static bool IsNewer(ReleaseInfo release) =>
        ReleaseVersions.Compare((release.Version, ReleaseVersions.Parse(release.Tag)?.Pre), (ReleaseVersions.Current, ReleaseVersions.Parse(ReleaseVersions.CurrentFull)?.Pre)) > 0;

    public UpdateStatus GetStatus()
    {
        var latest = _latest;
        var (canApply, reason) = _applier.CanApply();
        return new UpdateStatus
        {
            Current = ReleaseVersions.CurrentFull,
            Latest = latest?.Version.ToString(),
            Available = latest != null && IsNewer(latest),
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
            if (!IsNewer(latest))
            {
                throw new InvalidOperationException($"Already on {ReleaseVersions.CurrentFull}, the newest release");
            }

            _state = "Downloading";
            _message = $"Downloading {latest.Tag}";
            var staged = await StageAsync(latest, Platform, cancellationToken);

            _state = "Applying";
            _message = $"Restarting on {latest.Tag}";
            _logger.LogWarning("Updating from {Current} to {Latest}; the server will restart", ReleaseVersions.CurrentFull, latest.Tag);
            _applier.Launch(staged, ReleaseVersions.CurrentFull, latest.Tag);
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

        // Any agent can ask, so one download at a time, and a failure isn't retried for a while
        await _agentStageLock.WaitAsync(cancellationToken);
        try
        {
            if (_agentPackages.TryGetValue(platform, out cached) && File.Exists(cached.Path))
            {
                return cached;
            }

            if (_agentStageFailed.TryGetValue(platform, out var failedAt) && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromMinutes(15))
            {
                return null;
            }

            _agentStageFailed[platform] = DateTimeOffset.UtcNow;
            var release = await _source.GetByTagAsync(ReleaseVersions.CurrentTag, cancellationToken);
            if (release == null)
            {
                return null;
            }

            string staged;
            try
            {
                // The archive's own signature is checked here, so the agent comes from a verified release
                staged = await StageAsync(release, platform, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning("Not offering agents the {Platform} build of {Tag}: {Message}", platform, release.Tag, ex.Message);
                return null;
            }

            var binary = Path.Combine(staged, ReleaseVersions.AgentBinaryPath(platform));
            if (!File.Exists(binary))
            {
                return null;
            }

            // Agents check the publisher's signature over the program itself, since they never see the archive
            string? signature = null;
            var signatureAsset = release.Asset(ReleaseVersions.AgentSignatureName(platform));
            if (signatureAsset != null)
            {
                signature = (await ReadSmallAssetAsync(signatureAsset, cancellationToken)).Trim();
                if (!VerifyFile(binary, signature))
                {
                    _logger.LogError("The {Platform} agent in {Tag} doesn't match its signature ({Asset}); not offering it to agents", platform, release.Tag, signatureAsset.Name);
                    return null;
                }
            }
            else if (_allowUnsigned)
            {
                _logger.LogWarning("Offering agents the unsigned {Platform} agent from {Tag} because Updates:AllowUnsigned is on; agents refuse it unless they allow unsigned builds too", platform, release.Tag);
            }
            else
            {
                _logger.LogWarning("{Tag} has no signature for the {Platform} agent ({Asset}); not offering it to agents", release.Tag, platform, ReleaseVersions.AgentSignatureName(platform));
                return null;
            }

            await using var stream = File.OpenRead(binary);
            var package = new AgentPackage(binary, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length, ReleaseVersions.CurrentFull, signature);
            _agentPackages[platform] = package;
            _agentStageFailed.TryRemove(platform, out _);
            return package;
        }
        finally
        {
            _agentStageLock.Release();
        }
    }

    /// <summary>Remembers an agent's version for the dashboard. Bounded, since agents report it.</summary>
    public void RecordAgent(string host, string platform, string version, string? sender = null)
    {
        // Kept apart by the key that reported it, so one key can't rewrite another's agents, and
        // each key holds at most a share of the list, so one can't crowd out the rest
        var entry = $"{sender ?? string.Empty}|{host}";
        var known = _agents.ContainsKey(entry);
        if (!known && (_agents.Count >= 10_000 || _agents.Keys.Count(key => key.StartsWith($"{sender ?? string.Empty}|", StringComparison.Ordinal)) >= MaxAgentsPerSender))
        {
            return;
        }

        _agents[entry] = new AgentVersion(host, platform, version, DateTimeOffset.UtcNow);
    }

    /// <summary>The most agents one key's reports are kept for.</summary>
    public const int MaxAgentsPerSender = 1_000;

    /// <summary>A release's folder under the work directory: by tag, not version, since 3.0.0-rc.1 and 3.0.0 share a version number but not a build.</summary>
    private static string FolderFor(string? tag, Version version) =>
        string.IsNullOrWhiteSpace(tag) ? $"v{version}"
            : new string(tag.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());

    /// <summary>
    /// Deletes downloads of releases other than this one and the running one, which agents are
    /// offered. Each is a few hundred megabytes, and they used to pile up with every update.
    /// </summary>
    private void RemoveOtherReleases(string keep)
    {
        var running = FolderFor(ReleaseVersions.CurrentTag, ReleaseVersions.Current);
        foreach (var directory in Directory.EnumerateDirectories(_workDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals(keep, StringComparison.OrdinalIgnoreCase) || name.Equals(running, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't remove the old update download {Folder}", directory);
            }
        }
    }

    /// <summary>Reads a checksum or signature asset: a few kilobytes at most.</summary>
    private async Task<string> ReadSmallAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        await using var stream = await _source.OpenAssetAsync(asset, cancellationToken);
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return new string(buffer, 0, read);
    }

    /// <summary>Whether a file carries the publisher's signature.</summary>
    private bool VerifyFile(string path, string signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_releaseKeyInfo, out _);
        return ReleaseSignature.VerifyFile(path, signature, key);
    }

    /// <summary>Whether a staged release can be reused: signed, or unsigned while that's allowed.</summary>
    private bool IsAcceptable(string marker) =>
        marker.StartsWith("signed", StringComparison.Ordinal) || (_allowUnsigned && marker.StartsWith("unsigned", StringComparison.Ordinal));

    /// <summary>
    /// Downloads a release's archive for a platform, checks it against the release's SHA-256 and
    /// the publisher's signature (<c>&lt;archive&gt;.sig</c>), and only then unpacks it. A release
    /// without a signature is refused unless <c>Updates:AllowUnsigned</c> is on; a wrong signature
    /// always is. Reuses an earlier download that was verified the same way.
    /// </summary>
    public async Task<string> StageAsync(ReleaseInfo release, string platform, CancellationToken cancellationToken = default)
    {
        var folder = FolderFor(release.Tag, release.Version);
        var directory = Path.Combine(_workDirectory, folder, platform);

        // Not the ".complete" of releases before 3.1.0, which unpacked without checking a signature
        var verified = Path.Combine(directory, VerifiedMarker);
        if (File.Exists(verified) && IsAcceptable(await File.ReadAllTextAsync(verified, cancellationToken)))
        {
            return directory;
        }

        var archiveName = ReleaseVersions.ArchiveName(platform);
        var archive = release.Asset(archiveName) ?? throw new InvalidOperationException($"{release.Tag} has no {archiveName}");
        var checksum = release.Asset(archiveName + ".sha256") ?? throw new InvalidOperationException($"{release.Tag} has no checksum for {archiveName}");
        var signatureAsset = release.Asset(archiveName + ".sig");
        if (signatureAsset == null && !_allowUnsigned)
        {
            throw new InvalidOperationException(
                $"{release.Tag} has no signature for {archiveName} ({archiveName}.sig), so nothing shows it came from the Cabinet publisher. Nothing was changed. "
                + "Releases before 3.1.0 aren't signed: install those with the installer instead.");
        }

        var expected = (await ReadSmallAssetAsync(checksum, cancellationToken)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var signature = signatureAsset == null ? null : await ReadSmallAssetAsync(signatureAsset, cancellationToken);

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

            // Before unpacking: a forged archive must never reach the disk as files
            if (signature != null)
            {
                if (!VerifyFile(download, signature))
                {
                    throw new InvalidOperationException($"{archiveName} from {release.Tag} isn't signed by the Cabinet release key, so it may not come from the Cabinet publisher. Nothing was changed.");
                }
            }
            else
            {
                _logger.LogWarning("Accepting {Archive} from {Tag} without a signature because Updates:AllowUnsigned is on", archiveName, release.Tag);
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

            await File.WriteAllTextAsync(verified, $"{(signature != null ? "signed" : "unsigned")} {actual}", cancellationToken);
            _logger.LogInformation("Downloaded and verified {Archive} from {Tag} ({Signed})", archiveName, release.Tag, signature != null ? "signed" : "unsigned");
            RemoveOtherReleases(folder);
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
