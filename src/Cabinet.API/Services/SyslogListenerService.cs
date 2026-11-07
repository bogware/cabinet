using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Parsers;

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

    /// <summary>Port for syslog over TLS (RFC 5425, usually 6514). 0 turns it off.</summary>
    public int TlsPort { get; set; }

    /// <summary>A PEM certificate, or a PKCS#12 file (.pfx, .p12). Required when TlsPort is set.</summary>
    public string? TlsCertificatePath { get; set; }

    /// <summary>The private key, for a PEM certificate that doesn't include it.</summary>
    public string? TlsKeyPath { get; set; }

    /// <summary>The password of a PKCS#12 file.</summary>
    public string? TlsCertificatePassword { get; set; }

    /// <summary>Longest message kept, in bytes. Anything beyond it is cut off.</summary>
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    /// <summary>Open TCP and TLS connections allowed at once.</summary>
    public int MaxConnections { get; set; } = 1000;
}

/// <summary>
/// Receives syslog (RFC 3164 and RFC 5424) over UDP, TCP and optionally TLS, and stores it.
/// When storage falls behind, TCP and TLS senders wait. UDP has no way to wait, so datagrams are
/// dropped and counted.
/// </summary>
public sealed class SyslogListenerService : BackgroundService
{
    private const int BatchSize = 100;
    private const int ChannelCapacity = 10_000;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SyslogConfiguration _config;
    private readonly ILogger<SyslogListenerService> _logger;
    private readonly SyslogParser _parser = new();
    private readonly Channel<(string Message, string RemoteAddress)> _messages =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

    private long _droppedDatagrams;
    private long _lastDropWarningTicks;
    private int _openConnections;

    public SyslogListenerService(
        IServiceScopeFactory scopeFactory,
        SyslogConfiguration config,
        ILogger<SyslogListenerService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>UDP datagrams dropped because storage was behind.</summary>
    public long DroppedDatagrams => Interlocked.Read(ref _droppedDatagrams);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Syslog listener is disabled");
            return;
        }

        if (!IPAddress.TryParse(_config.BindAddress, out var address))
        {
            throw new InvalidOperationException($"Invalid Syslog:BindAddress: {_config.BindAddress}");
        }

        // Bind everything first. Someone who turned the listener on needs to know it isn't
        // listening, so these failures stop the server rather than leaving a line in a log.
        var certificate = _config.TlsPort > 0 ? LoadCertificate(_config) : null;
        using var udp = new UdpClient(new IPEndPoint(address, _config.UdpPort));
        var tcp = new TcpListener(address, _config.TcpPort);
        var tls = certificate == null ? null : new TcpListener(address, _config.TlsPort);

        try
        {
            tcp.Start();
            tls?.Start();
            _logger.LogInformation(
                "Syslog listening on {Address} (UDP:{UdpPort}, TCP:{TcpPort}, TLS:{TlsPort})",
                _config.BindAddress, _config.UdpPort, _config.TcpPort, _config.TlsPort);

            var processing = ProcessMessagesAsync(stoppingToken);
            try
            {
                var listeners = new List<Task>
                {
                    ReceiveDatagramsAsync(udp, stoppingToken),
                    AcceptConnectionsAsync(tcp, null, stoppingToken),
                };

                if (tls != null)
                {
                    listeners.Add(AcceptConnectionsAsync(tls, certificate, stoppingToken));
                }

                await Task.WhenAll(listeners);
            }
            finally
            {
                _messages.Writer.TryComplete();
                await processing;
            }
        }
        finally
        {
            tcp.Stop();
            tls?.Stop();
            certificate?.Dispose();
        }
    }

    private static X509Certificate2 LoadCertificate(SyslogConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.TlsCertificatePath))
        {
            throw new InvalidOperationException("Syslog:TlsPort is set, but Syslog:TlsCertificatePath isn't");
        }

        var path = config.TlsCertificatePath;
        var extension = Path.GetExtension(path);
        if (extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, config.TlsCertificatePassword);
        }

        using var pem = X509Certificate2.CreateFromPemFile(path, config.TlsKeyPath);

        // Windows can't use the ephemeral key that a PEM file loads with.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
    }

    private async Task ReceiveDatagramsAsync(UdpClient udp, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(stoppingToken);
                var length = Math.Min(result.Buffer.Length, _config.MaxMessageBytes);
                var message = Encoding.UTF8.GetString(result.Buffer, 0, length).TrimEnd('\r', '\n', '\0');
                if (message.Length > 0 && !_messages.Writer.TryWrite((message, result.RemoteEndPoint.Address.ToString())))
                {
                    RecordDroppedDatagram();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Windows reports an ICMP "port unreachable" from an earlier send here.
                _logger.LogDebug(ex, "Syslog UDP receive failed");
            }
        }
    }

    private void RecordDroppedDatagram()
    {
        var dropped = Interlocked.Increment(ref _droppedDatagrams);
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastDropWarningTicks);
        if (now - last >= DropWarningInterval.TotalMilliseconds
            && Interlocked.CompareExchange(ref _lastDropWarningTicks, now, last) == last)
        {
            _logger.LogWarning("Storage is behind: {Dropped} syslog UDP datagrams dropped so far. TCP doesn't lose messages this way.", dropped);
        }
    }

    private async Task AcceptConnectionsAsync(TcpListener listener, X509Certificate2? certificate, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Error accepting syslog connection");
                continue;
            }

            if (Interlocked.Increment(ref _openConnections) > _config.MaxConnections)
            {
                Interlocked.Decrement(ref _openConnections);
                _logger.LogWarning("Refused a syslog connection: {Max} are already open", _config.MaxConnections);
                client.Dispose();
                continue;
            }

            _ = HandleConnectionAsync(client, certificate, stoppingToken);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, X509Certificate2? certificate, CancellationToken stoppingToken)
    {
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

        try
        {
            using (client)
            await using (var network = client.GetStream())
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idle.CancelAfter(HandshakeTimeout);

                await using var ssl = certificate == null ? null : new SslStream(network, leaveInnerStreamOpen: true);
                if (ssl != null)
                {
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions { ServerCertificate = certificate },
                        idle.Token);
                }

                var reader = new SyslogFrameReader(ssl ?? (Stream)network, _config.MaxMessageBytes);
                while (true)
                {
                    idle.CancelAfter(IdleTimeout);
                    var message = await reader.ReadAsync(idle.Token);
                    if (message == null)
                    {
                        return;
                    }

                    if (message.Length > 0)
                    {
                        // Waits when full, which slows the sender rather than losing its messages.
                        await _messages.Writer.WriteAsync((message, remoteAddress), stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Closing syslog connection from {Address} for shutdown", remoteAddress);
        }
        catch (ChannelClosedException)
        {
            _logger.LogDebug("Closing syslog connection from {Address} for shutdown", remoteAddress);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Syslog connection from {Address} timed out", remoteAddress);
        }
        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            _logger.LogWarning(ex, "Syslog connection from {Address} failed", remoteAddress);
        }
        finally
        {
            Interlocked.Decrement(ref _openConnections);
        }
    }

    /// <summary>
    /// Stores what arrives, in batches, until the listeners stop and everything queued is stored.
    /// A failed batch is retried rather than lost, so senders wait while storage is down.
    /// </summary>
    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        using var deadline = new CancellationTokenSource();
        await using var registration = stoppingToken.Register(() => deadline.CancelAfter(DrainTimeout));

        var reader = _messages.Reader;
        var batch = new List<LogEvent>(BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(deadline.Token))
            {
                batch.Clear();
                while (batch.Count < BatchSize && reader.TryRead(out var item))
                {
                    batch.Add(await ParseAsync(item.Message, item.RemoteAddress, deadline.Token));
                }

                await StoreWithRetryAsync(batch, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogError(
                "Storage didn't take the queued syslog messages within {Timeout} of shutdown: {Lost} lost",
                DrainTimeout, batch.Count + reader.Count);
        }
    }

    private async Task StoreWithRetryAsync(List<LogEvent> batch, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var storage = scope.ServiceProvider.GetRequiredService<ILogStorage>();
                await storage.StoreBatchAsync(batch, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to store {Count} syslog messages; retrying in {Delay}", batch.Count, delay);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }

    private async Task<LogEvent> ParseAsync(string message, string remoteAddress, CancellationToken cancellationToken)
    {
        var logEvent = await _parser.ParseAsync(message, cancellationToken: cancellationToken)
            ?? new LogEvent { RawLine = message, SourceType = SourceTypes.Syslog };

        logEvent.Index = "syslog";
        if (string.IsNullOrEmpty(logEvent.Host))
        {
            logEvent.Host = remoteAddress;
        }

        if (logEvent.GetField<string>("app") is { } app)
        {
            logEvent.SourceFile = app;
        }

        if (!_parser.CanParse(message))
        {
            logEvent.SetField("raw_syslog", true);
        }

        return logEvent;
    }
}

/// <summary>
/// Splits a syslog stream into messages. Senders either prefix each message with its length
/// (RFC 6587 octet counting) or end it with a newline, and may do either on any message.
/// </summary>
internal sealed class SyslogFrameReader
{
    private const int MaxLengthDigits = 10;

    private readonly Stream _stream;
    private readonly int _maxMessageBytes;
    private readonly byte[] _buffer = new byte[8192];
    private int _position;
    private int _available;

    public SyslogFrameReader(Stream stream, int maxMessageBytes)
    {
        _stream = stream;
        _maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Reads the next message: null at the end of the stream, empty for a blank line.</summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var message = new List<byte>(256);

        var first = await ReadByteAsync(cancellationToken);
        if (first < 0)
        {
            return null;
        }

        // Digits then a space then "<" is a length prefix. Other digits are the start of a line.
        var current = first;
        var digits = 0;
        var length = 0L;
        while (current is >= '0' and <= '9' && digits < MaxLengthDigits)
        {
            message.Add((byte)current);
            length = (length * 10) + (current - '0');
            digits++;
            current = await ReadByteAsync(cancellationToken);
        }

        if (digits > 0 && current == ' ' && length > 0 && await PeekByteAsync(cancellationToken) == '<')
        {
            return await ReadCountedAsync(length, cancellationToken);
        }

        return await ReadLineAsync(message, current, cancellationToken);
    }

    private async Task<string> ReadCountedAsync(long length, CancellationToken cancellationToken)
    {
        var message = new List<byte>((int)Math.Min(length, _maxMessageBytes));
        for (var i = 0L; i < length; i++)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value < 0)
            {
                break;
            }

            if (message.Count < _maxMessageBytes)
            {
                message.Add((byte)value);
            }
        }

        return Decode(message);
    }

    private async Task<string> ReadLineAsync(List<byte> message, int current, CancellationToken cancellationToken)
    {
        while (current >= 0 && current != '\n')
        {
            if (message.Count < _maxMessageBytes)
            {
                message.Add((byte)current);
            }

            current = await ReadByteAsync(cancellationToken);
        }

        return Decode(message);
    }

    private static string Decode(List<byte> message) =>
        Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(message)).TrimEnd('\r', '\n', '\0');

    private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        var value = await PeekByteAsync(cancellationToken);
        if (value >= 0)
        {
            _position++;
        }

        return value;
    }

    private async ValueTask<int> PeekByteAsync(CancellationToken cancellationToken)
    {
        if (_position >= _available)
        {
            _available = await _stream.ReadAsync(_buffer, cancellationToken);
            _position = 0;
            if (_available <= 0)
            {
                _available = 0;
                return -1;
            }
        }

        return _buffer[_position];
    }
}
