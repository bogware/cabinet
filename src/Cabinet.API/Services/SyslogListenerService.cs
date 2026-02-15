using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;

namespace Cabinet.API.Services;

/// <summary>
/// Configuration for the syslog listener
/// </summary>
public sealed class SyslogConfiguration
{
    public bool Enabled { get; set; }
    public string BindAddress { get; set; } = "0.0.0.0";
    public int UdpPort { get; set; } = 1514;
    public int TcpPort { get; set; } = 1514;
}

/// <summary>
/// BackgroundService that listens on UDP and TCP for syslog messages (RFC 3164).
/// Parses syslog priority, timestamp, hostname, app, message and stores as LogEvent.
/// Only starts if SyslogConfiguration.Enabled is true.
/// </summary>
public sealed class SyslogListenerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SyslogConfiguration _config;
    private readonly ILogger<SyslogListenerService> _logger;
    private readonly System.Threading.Channels.Channel<(string Message, string RemoteAddress)> _messageChannel;

    private const int BatchSize = 100;
    private const int FlushIntervalMs = 200;

    // RFC 3164 syslog pattern: <priority>timestamp hostname app[pid]: message
    // Example: <34>Oct 11 22:14:15 mymachine su[12345]: 'su root' failed for lonvick
    private static readonly Regex SyslogPattern = new(
        @"^<(\d{1,3})>" +                                     // Priority
        @"(\w{3}\s+\d{1,2}\s+\d{2}:\d{2}:\d{2})?\s*" +      // Timestamp (optional)
        @"(\S+)?\s*" +                                         // Hostname (optional)
        @"(\S+?)(?:\[(\d+)\])?:\s*" +                         // App[PID]:
        @"(.*)$",                                              // Message
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Facility names for syslog facility codes
    private static readonly string[] FacilityNames =
    {
        "kern", "user", "mail", "daemon", "auth", "syslog", "lpr", "news",
        "uucp", "cron", "authpriv", "ftp", "ntp", "audit", "alert", "clock",
        "local0", "local1", "local2", "local3", "local4", "local5", "local6", "local7"
    };

    public SyslogListenerService(
        IServiceScopeFactory scopeFactory,
        SyslogConfiguration config,
        ILogger<SyslogListenerService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        _messageChannel = System.Threading.Channels.Channel.CreateBounded<(string, string)>(
            new System.Threading.Channels.BoundedChannelOptions(10_000)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Syslog listener is disabled");
            return;
        }

        _logger.LogInformation(
            "Starting syslog listener on {Address} (UDP:{UdpPort}, TCP:{TcpPort})",
            _config.BindAddress, _config.UdpPort, _config.TcpPort);

        var udpTask = ListenUdpAsync(stoppingToken);
        var tcpTask = ListenTcpAsync(stoppingToken);
        var batchTask = BatchProcessorAsync(stoppingToken);

        await Task.WhenAll(udpTask, tcpTask, batchTask);
    }

    private async Task ListenUdpAsync(CancellationToken stoppingToken)
    {
        UdpClient? udpClient = null;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(_config.BindAddress), _config.UdpPort);
            udpClient = new UdpClient(endpoint);
            _logger.LogInformation("Syslog UDP listener started on {Endpoint}", endpoint);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(stoppingToken);
                    var message = Encoding.UTF8.GetString(result.Buffer);
                    await EnqueueMessageAsync(message, result.RemoteEndPoint.Address.ToString(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error receiving syslog UDP message");
                }
            }
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Failed to start syslog UDP listener on port {Port}", _config.UdpPort);
        }
        finally
        {
            udpClient?.Dispose();
        }
    }

    private async Task ListenTcpAsync(CancellationToken stoppingToken)
    {
        TcpListener? tcpListener = null;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(_config.BindAddress), _config.TcpPort);
            tcpListener = new TcpListener(endpoint);
            tcpListener.Start();
            _logger.LogInformation("Syslog TCP listener started on {Endpoint}", endpoint);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var client = await tcpListener.AcceptTcpClientAsync(stoppingToken);
                    // Handle each TCP connection in the background
                    _ = HandleTcpClientAsync(client, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error accepting syslog TCP connection");
                }
            }
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "Failed to start syslog TCP listener on port {Port}", _config.TcpPort);
        }
        finally
        {
            tcpListener?.Stop();
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

        try
        {
            // Per-connection idle timeout to prevent slowloris attacks
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            connectionCts.CancelAfter(TimeSpan.FromMinutes(5));

            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (!connectionCts.IsCancellationRequested && !reader.EndOfStream)
                {
                    // Reset idle timeout on each successful read
                    connectionCts.CancelAfter(TimeSpan.FromMinutes(5));
                    var line = await reader.ReadLineAsync(connectionCts.Token);
                    if (!string.IsNullOrEmpty(line))
                    {
                        await EnqueueMessageAsync(line, remoteAddress, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Syslog TCP connection from {Address} timed out due to inactivity", remoteAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling syslog TCP client from {Address}", remoteAddress);
        }
    }

    private async Task EnqueueMessageAsync(string message, string remoteAddress, CancellationToken cancellationToken)
    {
        if (!_messageChannel.Writer.TryWrite((message, remoteAddress)))
        {
            _logger.LogWarning("Syslog channel full, dropping message from {Address}", remoteAddress);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Batch processor: reads from the channel and writes to storage in batches.
    /// Uses a single DI scope per batch instead of per-message.
    /// </summary>
    private async Task BatchProcessorAsync(CancellationToken stoppingToken)
    {
        var batch = new List<LogEvent>(BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                // Wait for at least one message
                if (await _messageChannel.Reader.WaitToReadAsync(stoppingToken))
                {
                    // Drain up to BatchSize messages, with a timeout for flushing
                    using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    flushCts.CancelAfter(FlushIntervalMs);

                    while (batch.Count < BatchSize)
                    {
                        try
                        {
                            if (_messageChannel.Reader.TryRead(out var item))
                            {
                                var logEvent = ParseSyslogMessage(item.Message, item.RemoteAddress);
                                batch.Add(logEvent);
                            }
                            else
                            {
                                // No more messages available right now, flush what we have
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse syslog message");
                        }
                    }

                    if (batch.Count > 0)
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var storage = scope.ServiceProvider.GetRequiredService<ILogStorage>();
                        await storage.StoreBatchAsync(batch, stoppingToken);
                        _logger.LogDebug("Syslog batch stored {Count} events", batch.Count);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in syslog batch processor");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private static LogEvent ParseSyslogMessage(string message, string remoteAddress)
    {
        var logEvent = new LogEvent
        {
            Id = Ulid.NewUlid(),
            RawLine = message,
            Timestamp = DateTime.UtcNow,
            IndexTime = DateTime.UtcNow,
            SourceType = "syslog",
            Index = "syslog",
            Host = remoteAddress,
            Severity = LogSeverity.Info
        };

        var match = SyslogPattern.Match(message);
        if (!match.Success)
        {
            // Not a valid syslog format; store as raw
            logEvent.SetField("raw_syslog", true);
            return logEvent;
        }

        // Parse priority (facility * 8 + severity)
        if (int.TryParse(match.Groups[1].Value, out var priority))
        {
            var facility = priority / 8;
            var syslogSeverity = priority % 8;

            logEvent.SetField("facility", facility);
            logEvent.SetField("syslog_severity", syslogSeverity);

            if (facility >= 0 && facility < FacilityNames.Length)
            {
                logEvent.SetField("facility_name", FacilityNames[facility]);
            }

            // Map syslog severity (0=emergency..7=debug) to LogSeverity
            logEvent.Severity = syslogSeverity switch
            {
                0 => LogSeverity.Fatal,    // Emergency
                1 => LogSeverity.Fatal,    // Alert
                2 => LogSeverity.Critical, // Critical
                3 => LogSeverity.Error,    // Error
                4 => LogSeverity.Warning,  // Warning
                5 => LogSeverity.Info,     // Notice
                6 => LogSeverity.Info,     // Informational
                7 => LogSeverity.Debug,    // Debug
                _ => LogSeverity.Info
            };
        }

        // Parse timestamp
        if (match.Groups[2].Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value))
        {
            var timestampStr = match.Groups[2].Value.Trim();
            // RFC 3164 timestamp doesn't include year, assume current year
            var withYear = $"{DateTime.UtcNow.Year} {timestampStr}";
            if (DateTime.TryParse(withYear, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                logEvent.Timestamp = timestamp.ToUniversalTime();
            }
        }

        // Hostname
        if (match.Groups[3].Success && !string.IsNullOrWhiteSpace(match.Groups[3].Value))
        {
            logEvent.Host = match.Groups[3].Value;
            logEvent.SetField("hostname", match.Groups[3].Value);
        }

        // Application name
        if (match.Groups[4].Success)
        {
            logEvent.SetField("app", match.Groups[4].Value);
            logEvent.SourceFile = match.Groups[4].Value;
        }

        // Process ID
        if (match.Groups[5].Success)
        {
            logEvent.SetField("pid", match.Groups[5].Value);
        }

        // Message body
        if (match.Groups[6].Success)
        {
            logEvent.SetField("message", match.Groups[6].Value);
        }

        return logEvent;
    }
}
