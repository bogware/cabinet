using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Cabinet.Agent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cabinet.Agent.Updating;

/// <summary>Settings for keeping the agent at the server's version (<c>Agent:Update:*</c>).</summary>
public sealed class UpdateConfiguration
{
    /// <summary>Whether to install the agent build the server offers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to ask.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;
}

/// <summary>What the server offers.</summary>
public sealed class UpdateOffer
{
    public string Version { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

/// <summary>
/// Keeps the agent at the server's version. When the server offers a newer build, it's
/// downloaded over the same connection as events (so the same key and pinned certificate),
/// checked against its SHA-256, swapped in for the running program, and the agent stops so its
/// service manager starts the new one. Reading resumes from the checkpoints.
/// </summary>
public sealed class AgentUpdater : BackgroundService
{
    private readonly HttpClient _http;
    private readonly AgentConfiguration _config;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentUpdater> _logger;
    private readonly string _programPath;

    public AgentUpdater(HttpClient http, AgentConfiguration config, IHostApplicationLifetime lifetime, ILogger<AgentUpdater> logger, string? programPath = null)
    {
        _http = http;
        _config = config;
        _lifetime = lifetime;
        _logger = logger;
        _programPath = programPath ?? Environment.ProcessPath ?? string.Empty;
    }

    /// <summary>This agent's version.</summary>
    public static Version CurrentVersion { get; } = typeof(AgentUpdater).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Update.Enabled || string.IsNullOrEmpty(_programPath))
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await CheckOnceAsync(stoppingToken))
                {
                    _lifetime.StopApplication();
                    return;
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.Update.CheckIntervalMinutes)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
            return;
        }
    }

    /// <summary>
    /// Asks once, and installs what's offered. True if a new build is in place and the agent
    /// should restart.
    /// </summary>
    public async Task<bool> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        var server = new Uri(_config.Output.ServerUrl.TrimEnd('/') + "/");
        var platform = RuntimeInformation.RuntimeIdentifier;
        var host = string.IsNullOrEmpty(_config.Hostname) ? Environment.MachineName : _config.Hostname;
        try
        {
            using var response = await _http.SendAsync(
                Request(new Uri(server, $"api/v1/updates/agent?platform={Uri.EscapeDataString(platform)}&version={CurrentVersion}&host={Uri.EscapeDataString(host)}")),
                cancellationToken);
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            var offer = await response.Content.ReadFromJsonAsync<UpdateOffer>(cancellationToken);
            if (offer == null || !Version.TryParse(offer.Version, out var offered) || offered <= CurrentVersion)
            {
                return false;
            }

            _logger.LogInformation("Server offers agent {Version}; downloading", offer.Version);
            var staged = _programPath + ".new";
            using (var binary = await _http.SendAsync(Request(new Uri(server, $"api/v1/updates/agent/{Uri.EscapeDataString(platform)}")), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                binary.EnsureSuccessStatusCode();
                await using var download = await binary.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(staged);
                await download.CopyToAsync(file, cancellationToken);
            }

            string actual;
            await using (var file = File.OpenRead(staged))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
            }

            if (!actual.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(staged);
                _logger.LogWarning("Downloaded agent {Version} doesn't match its checksum; keeping this version", offer.Version);
                return false;
            }

            Install(staged);
            _logger.LogWarning("Installed agent {Version}; restarting on it", offer.Version);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "Checking for an agent update failed");
            return false;
        }
    }

    /// <summary>A request with the agent's key, unless the forwarder already set it for every request.</summary>
    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!_http.DefaultRequestHeaders.Contains("X-API-Key"))
        {
            request.Headers.Add("X-API-Key", _config.Output.ApiKey);
        }

        return request;
    }

    /// <summary>
    /// Puts the new program in place of the running one. Both platforms allow renaming a running
    /// program; the old one is kept beside it until the next update.
    /// </summary>
    private void Install(string staged)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var previous = _programPath + ".old";
        File.Delete(previous);
        File.Move(_programPath, previous);
        File.Move(staged, _programPath);
    }
}
