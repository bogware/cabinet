using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cabinet.API.Services.Updates;

/// <summary>Settings for updates (<c>Updates:*</c>).</summary>
public sealed class UpdateOptions
{
    /// <summary>GitHub repository releases come from.</summary>
    public string Repository { get; set; } = "bogware/cabinet";

    /// <summary>
    /// GitHub token with read access to the repository's contents, needed while it's private. Or
    /// the <c>CABINET_UPDATE_TOKEN</c> environment variable.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>How often to look for a new release. 0 checks only when asked.</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>Where downloads are kept. Empty = an <c>updates</c> folder beside the program's.</summary>
    public string? WorkDirectory { get; set; }

    /// <summary>The service to restart when updating. Empty = CabinetAPI on Windows, cabinet-api elsewhere.</summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Agents that take this server's agent build, by the host name they report, with * as a
    /// wildcard: a staged rollout, such as a test machine first and the rest after. Empty, the
    /// default, means every agent.
    /// </summary>
    public List<string> AgentHosts { get; set; } = [];

    /// <summary>
    /// For development builds only: installs a release, and serves agents a build, that has no
    /// publisher signature (releases before 3.0.1, or archives you built yourself). A signature
    /// that is present but wrong is refused whatever this says. Off by default, and logged as a
    /// warning whenever it's on, since it lets whoever controls the release source choose what
    /// this server runs.
    /// </summary>
    public bool AllowUnsigned { get; set; }

    /// <summary>Whether an agent reporting this host name takes this server's agent build.</summary>
    public bool OffersAgentTo(string host) =>
        AgentHosts.Count == 0 || AgentHosts.Any(pattern => System.Text.RegularExpressions.Regex.IsMatch(
            host,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100)));
}

/// <summary>A published release.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string? Notes, DateTimeOffset? PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>The asset with this name, if the release has one.</summary>
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(asset => asset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(long Id, string Name, long Size);

/// <summary>Where releases come from.</summary>
public interface IReleaseSource
{
    /// <summary>The newest release.</summary>
    Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default);

    /// <summary>The release with a tag, or null if there isn't one.</summary>
    Task<ReleaseInfo?> GetByTagAsync(string tag, CancellationToken cancellationToken = default);

    /// <summary>Opens an asset's contents.</summary>
    Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken = default);
}

/// <summary>Versions as release tags write them (v2.0.1).</summary>
public static partial class ReleaseVersions
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex TagPattern();

    /// <summary>The version a tag names, or null for anything that isn't a plain release tag.</summary>
    public static Version? FromTag(string? tag)
    {
        var match = TagPattern().Match(tag ?? string.Empty);
        return match.Success
            ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value))
            : null;
    }

    /// <summary>Major, minor and patch only, so 2.0.1.0 and 2.0.1 compare equal.</summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>The running program's version, without any pre-release label.</summary>
    public static Version Current { get; } = Normalize(typeof(ReleaseVersions).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// The running program's full version, such as 3.0.0 or 3.0.0-rc.1, from the version it was
    /// built with. A pre-release counts as just below the release of the same number.
    /// </summary>
    public static string CurrentFull { get; } = FullVersion(
        typeof(ReleaseVersions).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
        Current);

    /// <summary>Whether this build is a pre-release, such as a release candidate.</summary>
    public static bool CurrentIsPrerelease => CurrentFull.Contains('-', StringComparison.Ordinal);

    /// <summary>The tag this build was released under.</summary>
    public static string CurrentTag => "v" + CurrentFull;

    /// <summary>A built version's text without build metadata, or the plain version when it doesn't match it.</summary>
    public static string FullVersion(string? informational, Version core)
    {
        var text = (informational ?? string.Empty).Split('+')[0];
        return PrereleasePattern().Match(text) is { Success: true } match && FromTag(match.Groups["core"].Value) == core ? text : core.ToString();
    }

    [GeneratedRegex(@"^v?(?<core>\d+\.\d+\.\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?$")]
    private static partial Regex PrereleasePattern();

    /// <summary>The version and pre-release label in a tag like v3.0.0-rc.1, or null.</summary>
    public static (Version Core, string? Pre)? Parse(string? text)
    {
        var match = PrereleasePattern().Match((text ?? string.Empty).Split('+')[0]);
        return match.Success && FromTag(match.Groups["core"].Value) is { } core
            ? (core, match.Groups["pre"].Success ? match.Groups["pre"].Value : null)
            : null;
    }

    /// <summary>
    /// Orders two versions as semantic versioning does: by number, then a pre-release below the
    /// release, then pre-release labels part by part, numbers as numbers.
    /// </summary>
    public static int Compare((Version Core, string? Pre) a, (Version Core, string? Pre) b)
    {
        var byCore = a.Core.CompareTo(b.Core);
        if (byCore != 0)
        {
            return byCore;
        }

        if (a.Pre == null || b.Pre == null)
        {
            return a.Pre == null ? (b.Pre == null ? 0 : 1) : -1;
        }

        var left = a.Pre.Split('.');
        var right = b.Pre.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var numbers = long.TryParse(left[i], out var x) & long.TryParse(right[i], out var y);
            var order = numbers ? x.CompareTo(y) : string.CompareOrdinal(left[i], right[i]);
            if (order != 0)
            {
                return order;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Platforms releases are built for. Anything else asked for is refused.</summary>
    public static readonly IReadOnlySet<string> Platforms = new HashSet<string>(StringComparer.Ordinal)
    {
        "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64",
    };

    /// <summary>The release archive for a platform.</summary>
    public static string ArchiveName(string platform) =>
        platform.StartsWith("win", StringComparison.Ordinal) ? $"cabinet-{platform}.zip" : $"cabinet-{platform}.tar.gz";

    /// <summary>The release asset holding the signature of the agent program inside a platform's archive.</summary>
    public static string AgentSignatureName(string platform) => $"cabinet-agent-{platform}.sig";

    /// <summary>The agent program's path inside an unpacked release archive.</summary>
    public static string AgentBinaryPath(string platform) =>
        Path.Combine("agent", platform.StartsWith("win", StringComparison.Ordinal) ? "cabinet-agent.exe" : "cabinet-agent");
}

/// <summary>Releases from the GitHub API, with a token for private repositories.</summary>
public sealed class GitHubReleaseSource : IReleaseSource
{
    private readonly HttpClient _http;
    private readonly string _repository;

    public GitHubReleaseSource(HttpClient http, UpdateOptions options)
    {
        _http = http;
        _repository = options.Repository;
        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cabinet-updater");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = string.IsNullOrWhiteSpace(options.Token) ? Environment.GetEnvironmentVariable("CABINET_UPDATE_TOKEN") : options.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            // Sent to GitHub only: .NET drops it when following the download's redirect
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        // GitHub's latest release is never a pre-release. A server running one is testing the next
        // version, so it's offered the newest of all, which may be the next candidate or the release.
        var latest = ReleaseVersions.CurrentIsPrerelease
            ? Newest(await GetListAsync($"repos/{_repository}/releases?per_page=30", cancellationToken))
            : await GetAsync($"repos/{_repository}/releases/latest", cancellationToken);
        return latest ?? throw new InvalidOperationException($"No releases found for {_repository}. If the repository is private, set Updates:Token.");
    }

    /// <summary>The highest version among published releases, pre-releases included.</summary>
    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases) =>
        releases
            .Select(release => (Release: release, Version: ReleaseVersions.Parse(release.Tag)))
            .Where(item => item.Version != null && item.Release.Assets.Count > 0)
            .OrderByDescending(item => item.Version!.Value, Comparer<(Version Core, string? Pre)>.Create(ReleaseVersions.Compare))
            .Select(item => item.Release)
            .FirstOrDefault();

    public Task<ReleaseInfo?> GetByTagAsync(string tag, CancellationToken cancellationToken = default) =>
        GetAsync($"repos/{_repository}/releases/tags/{Uri.EscapeDataString(tag)}", cancellationToken);

    public async Task<Stream> OpenAssetAsync(ReleaseAsset asset, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{_repository}/releases/assets/{asset.Id}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    private async Task<List<ReleaseInfo>> GetListAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(path, cancellationToken);
        return document == null ? [] : document.RootElement.EnumerateArray()
            .Where(release => !(release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True))
            .Select(Read)
            .OfType<ReleaseInfo>()
            .ToList();
    }

    private async Task<ReleaseInfo?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(path, cancellationToken);
        return document == null ? null
            : Read(document.RootElement) ?? throw new InvalidOperationException($"Release tag '{document.RootElement.GetProperty("tag_name").GetString()}' isn't a version");
    }

    private async Task<JsonDocument?> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException($"GitHub refused the request ({(int)response.StatusCode}). Check Updates:Token.");
        }

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>A release from GitHub's JSON, or null if its tag isn't a version.</summary>
    private static ReleaseInfo? Read(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (ReleaseVersions.Parse(tag)?.Core is not { } version)
        {
            return null;
        }

        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(asset => new ReleaseAsset(asset.GetProperty("id").GetInt64(), asset.GetProperty("name").GetString() ?? string.Empty, asset.GetProperty("size").GetInt64()))
            .ToList();
        DateTimeOffset? published = root.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : null;
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
        return new ReleaseInfo(tag, version, notes, published, assets);
    }
}
