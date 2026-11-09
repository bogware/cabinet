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

    /// <summary>The running program's version.</summary>
    public static Version Current { get; } = Normalize(typeof(ReleaseVersions).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>Platforms releases are built for. Anything else asked for is refused.</summary>
    public static readonly IReadOnlySet<string> Platforms = new HashSet<string>(StringComparer.Ordinal)
    {
        "win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64",
    };

    /// <summary>The release archive for a platform.</summary>
    public static string ArchiveName(string platform) =>
        platform.StartsWith("win", StringComparison.Ordinal) ? $"cabinet-{platform}.zip" : $"cabinet-{platform}.tar.gz";
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

    public async Task<ReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default) =>
        await GetAsync($"repos/{_repository}/releases/latest", cancellationToken)
        ?? throw new InvalidOperationException($"No releases found for {_repository}. If the repository is private, set Updates:Token.");

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

    private async Task<ReleaseInfo?> GetAsync(string path, CancellationToken cancellationToken)
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
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var version = ReleaseVersions.FromTag(tag) ?? throw new InvalidOperationException($"Release tag '{tag}' isn't a version");
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(asset => new ReleaseAsset(asset.GetProperty("id").GetInt64(), asset.GetProperty("name").GetString() ?? string.Empty, asset.GetProperty("size").GetInt64()))
            .ToList();
        DateTimeOffset? published = root.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String ? at.GetDateTimeOffset() : null;
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null;
        return new ReleaseInfo(tag, version, notes, published, assets);
    }
}
