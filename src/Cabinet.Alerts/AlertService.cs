using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cabinet.Alerts;

/// <summary>
/// Sends alerts through every channel. Throttling belongs to whatever decides to fire.
/// </summary>
public class AlertService : IAlertService
{
    private readonly ILogger<AlertService> _logger;
    private readonly object _channelsLock = new();

    // Replaced, never changed, so a send can go through it while a channel is registered
    private IAlertChannel[] _channels;

    public AlertService(ILogger<AlertService> logger, IEnumerable<IAlertChannel> channels)
    {
        _logger = logger;
        _channels = channels.ToArray();
    }

    public async Task SendAlertAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Sending alert: {Title} (Severity: {Severity})", alert.Title, alert.Severity);

        var tasks = _channels.Select(channel => SendToChannelAsync(channel, alert, cancellationToken));
        await Task.WhenAll(tasks);
    }

    public void RegisterChannel(IAlertChannel channel)
    {
        lock (_channelsLock)
        {
            if (_channels.Any(c => c.Name == channel.Name))
            {
                return;
            }

            _channels = [.. _channels, channel];
        }

        _logger.LogInformation("Registered alert channel: {Channel}", channel.Name);
    }

    public IEnumerable<IAlertChannel> GetChannels()
    {
        return _channels.AsEnumerable();
    }

    private async Task SendToChannelAsync(IAlertChannel channel, Alert alert, CancellationToken cancellationToken)
    {
        try
        {
            await channel.SendAsync(alert, cancellationToken);
            _logger.LogDebug("Alert sent to channel {Channel}", channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send alert to channel {Channel}", channel.Name);
        }
    }

}

/// <summary>
/// Console alert channel - outputs alerts to console
/// </summary>
public class ConsoleAlertChannel : IAlertChannel
{
    public string Name => "Console";

    public Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        var color = alert.Severity switch
        {
            AlertSeverity.Critical => ConsoleColor.Red,
            AlertSeverity.Error => ConsoleColor.DarkRed,
            AlertSeverity.Warning => ConsoleColor.Yellow,
            _ => ConsoleColor.White
        };

        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;

            Console.WriteLine($"[{alert.Timestamp:yyyy-MM-dd HH:mm:ss}] [{alert.Severity}] {alert.Title}");
            Console.WriteLine($"  {alert.Message}");

            if (alert.Metadata.Any())
            {
                Console.WriteLine("  Metadata:");
                foreach (var kvp in alert.Metadata)
                {
                    Console.WriteLine($"    {kvp.Key}: {kvp.Value}");
                }
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// File alert channel - writes alerts to a file
/// </summary>
public class FileAlertChannel : IAlertChannel
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string Name => "File";

    public FileAlertChannel(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cabinet",
            "alerts.log"
        );

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var lines = new List<string>
            {
                $"[{alert.Timestamp:yyyy-MM-dd HH:mm:ss}] [{alert.Severity}] {alert.Title}",
                $"  {alert.Message}",
                $"  Source: {alert.Source}"
            };

            if (alert.Metadata.Any())
            {
                lines.Add("  Metadata:");
                foreach (var kvp in alert.Metadata)
                {
                    lines.Add($"    {kvp.Key}: {kvp.Value}");
                }
            }

            lines.Add(""); // Empty line separator

            await File.AppendAllLinesAsync(_filePath, lines, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
