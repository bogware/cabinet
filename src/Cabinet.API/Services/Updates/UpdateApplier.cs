using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Cabinet.API.Services.Updates;

/// <summary>
/// Replaces this server with a downloaded release. A running program can't safely replace
/// itself, so it writes a script and starts it as a separate process, which stops the service,
/// backs up the program, copies the new one in (never the settings), starts the service, and
/// restores the backup if the new version doesn't become healthy.
/// </summary>
public sealed class UpdateApplier
{
    private readonly UpdateOptions _options;
    private readonly IServer _server;
    private readonly ILogger<UpdateApplier> _logger;

    public UpdateApplier(UpdateOptions options, IServer server, ILogger<UpdateApplier> logger)
    {
        _options = options;
        _server = server;
        _logger = logger;
    }

    private static bool IsWindowsService => OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    private static bool IsSystemdService => OperatingSystem.IsLinux() && Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService();

    /// <summary>Whether this server can update itself, and why not.</summary>
    public (bool CanApply, string? Reason) CanApply()
    {
        if (IsWindowsService)
        {
            return (true, null);
        }

        if (IsSystemdService)
        {
            return Environment.UserName == "root"
                ? (true, null)
                : (false, "The service must run as root to restart itself. Update it with the installer instead.");
        }

        return (false, "Only a server running as a Windows or systemd service can update itself. In Docker, pull the new image.");
    }

    /// <summary>
    /// Starts the updater process. This server is stopped by it shortly after.
    /// </summary>
    /// <param name="stagedDirectory">The unpacked, verified release.</param>
    /// <param name="fromVersion">The running version in full, such as 3.0.0-rc.4.</param>
    /// <param name="toTag">The release's tag, such as v3.0.1.</param>
    public void Launch(string stagedDirectory, string fromVersion, string toTag)
    {
        var start = PrepareStart(stagedDirectory, fromVersion, toTag);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the updater");
        _logger.LogInformation("Updater started; progress goes to {Log}", Path.GetFullPath(Path.Combine(stagedDirectory, "..", "..", "update.log")));
    }

    /// <summary>Writes the updater script into the staged release and says how to start it.</summary>
    internal ProcessStartInfo PrepareStart(string stagedDirectory, string fromVersion, string toTag)
    {
        var installDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var apiDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var log = Path.Combine(stagedDirectory, "..", "..", "update.log");
        var health = HealthUrl();

        ProcessStartInfo start;
        if (IsWindowsService)
        {
            var script = Path.Combine(stagedDirectory, "apply-update.ps1");
            File.WriteAllText(script, WindowsScript);
            start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                    "-ServiceName", string.IsNullOrWhiteSpace(_options.ServiceName) ? "CabinetAPI" : _options.ServiceName,
                    "-ApiDirectory", apiDirectory,
                    "-InstallDirectory", installDirectory,
                    "-Staged", stagedDirectory,
                    "-From", fromVersion,
                    "-Version", toTag,
                    "-HealthUrl", health,
                    "-Log", log,
                },
            };
        }
        else
        {
            var script = Path.Combine(stagedDirectory, "apply-update.sh");
            File.WriteAllText(script, LinuxScript.Replace("\r\n", "\n", StringComparison.Ordinal));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Its own unit, outside this service's, so stopping the service doesn't stop it
            start = new ProcessStartInfo("systemd-run")
            {
                ArgumentList =
                {
                    $"--unit=cabinet-update-{DateTime.UtcNow:yyyyMMddHHmmss}", "--collect", "/bin/bash", script,
                    string.IsNullOrWhiteSpace(_options.ServiceName) ? "cabinet-api" : _options.ServiceName,
                    apiDirectory, stagedDirectory, toTag, health, log, fromVersion,
                },
            };
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return start;
    }

    /// <summary>An address on this machine the updater can check health on.</summary>
    private string HealthUrl()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var http = addresses.FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.FirstOrDefault()
            ?? "http://localhost:8080";
        var uri = new UriBuilder(http.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal));
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
        {
            uri.Host = "localhost";
        }

        uri.Path = "/health/ready";
        return uri.Uri.ToString();
    }

    // Plain ASCII: Windows PowerShell reads a file without a BOM as the system code page
    private const string WindowsScript = """
        param(
            [string]$ServiceName, [string]$ApiDirectory, [string]$InstallDirectory, [string]$Staged,
            [string]$From, [string]$Version, [string]$HealthUrl, [string]$Log
        )
        $ErrorActionPreference = "Stop"
        function Say([string]$Text) { Add-Content -Path $Log -Value "$(Get-Date -Format o) $Text" }

        # Kept from the running install: settings and keys, and logs
        $keep = @("appsettings.Production.json", "logs")

        function Wait-Healthy {
            for ($i = 0; $i -lt 120; $i++) {
                try {
                    if ((Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { return $true }
                }
                catch { }
                Start-Sleep -Seconds 1
            }
            return $false
        }

        # A large single-file service can take longer than the 30-second service-control start
        # timeout to come up, so Start-Service may report a timeout though the process is starting.
        # Health is the real test: start it, ignore that timeout, and start once more if needed.
        function Start-AndWait {
            Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            if (Wait-Healthy) { return $true }
            Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
            return (Wait-Healthy)
        }

        function Copy-Program([string]$From, [string]$To) {
            Get-ChildItem $From | Where-Object { $keep -notcontains $_.Name } | Copy-Item -Destination $To -Recurse -Force
        }

        $backup = Join-Path $InstallDirectory "backup\api"
        try {
            Say "Updating from $From to $Version"
            Start-Sleep -Seconds 2
            Stop-Service -Name $ServiceName -Force
            (Get-Service -Name $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))

            if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Copy-Program $ApiDirectory $backup
            Say "Backed up the current program to $backup"

            Copy-Program (Join-Path $Staged "api") $ApiDirectory
            $cli = Join-Path $InstallDirectory "cli"
            if ((Test-Path (Join-Path $Staged "cli")) -and (Test-Path $cli)) { Copy-Item (Join-Path $Staged "cli\*") $cli -Recurse -Force }

            if (Start-AndWait) {
                Say "Updated to $Version"
                exit 0
            }
            throw "Version $Version didn't become healthy"
        }
        catch {
            Say "Update failed: $($_.Exception.Message). Restoring the previous version."
            try {
                Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
                (Get-Service -Name $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
                if (Test-Path $backup) { Copy-Program $backup $ApiDirectory }
                if (Start-AndWait) { Say "Restored the previous version" } else { Say "Restored the previous version, but it isn't healthy yet; check $ServiceName." }
            }
            catch {
                Say "Restoring failed too: $($_.Exception.Message). Copy $backup into $ApiDirectory and start $ServiceName."
            }
            exit 1
        }
        """;

    private const string LinuxScript = """
        #!/bin/bash
        # Arguments: service, program folder, staged release, release tag, health URL, log, running version
        service="$1"; api="$2"; staged="$3"; version="$4"; health="$5"; log="$6"; from="$7"
        say() { echo "$(date -Is) $*" >> "$log"; }
        copy_program() { (cd "$1" && find . -mindepth 1 -maxdepth 1 ! -name appsettings.Production.json ! -name logs -exec cp -a {} "$2/" \;); }
        healthy() { for _ in $(seq 1 90); do curl -fs "$health" >/dev/null 2>&1 && return 0; sleep 1; done; return 1; }

        backup="$(dirname "$api")/backup/api"
        say "Updating from $from to $version"
        sleep 2
        systemctl stop "$service"
        rm -rf "$backup"; mkdir -p "$backup"
        copy_program "$api" "$backup"
        say "Backed up the current program to $backup"
        copy_program "$staged/api" "$api"
        systemctl start "$service"
        if healthy; then
            say "Updated to $version"
            exit 0
        fi
        say "Version $version didn't become healthy. Restoring the previous version."
        systemctl stop "$service"
        copy_program "$backup" "$api"
        systemctl start "$service"
        say "Restored the previous version"
        exit 1
        """;
}
