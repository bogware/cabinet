using System.Diagnostics;
using Cabinet.API.Services.Updates;

namespace Cabinet.API.Services.Settings;

/// <summary>
/// Restarts the server so settings that are read at startup take effect. Only a server run as a
/// service can do this, because something has to start it again.
/// </summary>
public sealed class ServiceRestarter
{
    private readonly UpdateOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServiceRestarter> _logger;

    public ServiceRestarter(UpdateOptions options, IHostApplicationLifetime lifetime, ILogger<ServiceRestarter> logger)
    {
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    private static bool IsWindowsService => OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();

    private static bool IsSystemdService => OperatingSystem.IsLinux() && Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService();

    /// <summary>Whether this server can restart itself, and why not.</summary>
    public (bool CanRestart, string? Reason) CanRestart() =>
        IsWindowsService || IsSystemdService
            ? (true, null)
            : (false, "Only a server running as a Windows or systemd service can restart itself. Restart it the way it was started.");

    /// <summary>
    /// Starts the restart a moment from now, so the request that asked for it gets its answer. A
    /// Windows service is restarted by a separate process, which stops it cleanly and starts it
    /// again. Under systemd, stopping with a failure code lets its restart policy start it again.
    /// </summary>
    public void Restart(string requestedBy)
    {
        var (canRestart, reason) = CanRestart();
        if (!canRestart)
        {
            throw new InvalidOperationException(reason);
        }

        _logger.LogWarning("Restarting, as asked by {Principal}", requestedBy);
        if (IsWindowsService)
        {
            var service = string.IsNullOrWhiteSpace(_options.ServiceName) ? "CabinetAPI" : _options.ServiceName;
            var start = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds 2; Restart-Service -Name '{service.Replace("'", "''", StringComparison.Ordinal)}' -Force" },
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start the restart");
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Environment.ExitCode = 1;
            _lifetime.StopApplication();
        });
    }
}
