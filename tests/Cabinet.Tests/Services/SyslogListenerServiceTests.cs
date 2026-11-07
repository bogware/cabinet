using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Cabinet.API.Services;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class SyslogListenerServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cabinet-syslog-{Guid.NewGuid():N}");

    public SyslogListenerServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    }

    private static SyslogListenerService CreateService(CapturingStorage storage, SyslogConfiguration config)
    {
        var scopes = new ServiceCollection()
            .AddSingleton(storage.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        return new SyslogListenerService(scopes, config, NullLogger<SyslogListenerService>.Instance);
    }

    private static SyslogConfiguration Config() => new()
    {
        Enabled = true,
        BindAddress = "127.0.0.1",
        TcpPort = FreeTcpPort(),
        UdpPort = FreeUdpPort(),
    };

    private static async Task<List<LogEvent>> WaitForEventsAsync(CapturingStorage storage, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (storage.Stored.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        return storage.Stored.ToList();
    }

    [Fact]
    public async Task Tcp_NewlineAndOctetFramedMessages_AreBothStored()
    {
        var storage = new CapturingStorage();
        var config = Config();
        var service = CreateService(storage, config);
        await StartAsync(service);

        const string counted = "<34>1 2026-03-04T05:06:07Z web-1 app 1 - - counted\nwith newline";
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, config.TcpPort);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                $"<34>Mar  4 05:06:07 web-2 sshd[9]: lined\n{Encoding.UTF8.GetByteCount(counted)} {counted}<13>1 2026-03-04T05:06:08Z web-3 app 1 - - last\n"));
        }

        var events = await WaitForEventsAsync(storage, 3);
        await service.StopAsync(CancellationToken.None);

        events.Should().HaveCount(3);
        events.Select(e => e.Host).Should().Equal("web-2", "web-1", "web-3");
        events[1].GetField<string>("Message").Should().Be("counted\nwith newline");
        events.Should().OnlyContain(e => e.Index == "syslog" && e.SourceType == SourceTypes.Syslog);
    }

    [Fact]
    public async Task Udp_MessageWithoutAHostname_UsesTheSenderAddress()
    {
        var storage = new CapturingStorage();
        var config = Config();
        var service = CreateService(storage, config);
        await StartAsync(service);

        using (var client = new UdpClient())
        {
            var bytes = Encoding.UTF8.GetBytes("<34>not a real header\n");
            await client.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, config.UdpPort));
        }

        var events = await WaitForEventsAsync(storage, 1);
        await service.StopAsync(CancellationToken.None);

        events.Should().ContainSingle();
        events[0].Host.Should().Be("127.0.0.1");
        events[0].Severity.Should().Be(LogSeverity.Critical);
    }

    [Fact]
    public async Task Tls_MessageIsStored()
    {
        var path = Path.Combine(_directory, "syslog.pfx");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "secret"));
        }

        var storage = new CapturingStorage();
        var config = Config();
        config.TlsPort = FreeTcpPort();
        config.TlsCertificatePath = path;
        config.TlsCertificatePassword = "secret";
        var service = CreateService(storage, config);
        await StartAsync(service);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, config.TlsPort);
            await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("localhost");
            await ssl.WriteAsync(Encoding.UTF8.GetBytes("<34>1 2026-03-04T05:06:07Z secure-host app 1 - - encrypted\n"));
            await ssl.FlushAsync();
        }

        var events = await WaitForEventsAsync(storage, 1);
        await service.StopAsync(CancellationToken.None);

        events.Should().ContainSingle().Which.Host.Should().Be("secure-host");
    }

    [Fact]
    public async Task TlsPort_WithoutACertificate_FailsStartup()
    {
        var config = Config();
        config.TlsPort = FreeTcpPort();
        var service = CreateService(new CapturingStorage(), config);

        var start = () => RunUntilFaultedAsync(service);

        await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*TlsCertificatePath*");
    }

    [Fact]
    public async Task PortInUse_FailsStartup_InsteadOfOnlyLogging()
    {
        var config = Config();
        var taken = new TcpListener(IPAddress.Loopback, config.TcpPort);
        taken.Start();
        try
        {
            var service = CreateService(new CapturingStorage(), config);

            var start = () => RunUntilFaultedAsync(service);

            await start.Should().ThrowAsync<SocketException>();
        }
        finally
        {
            taken.Stop();
        }
    }

    [Fact]
    public async Task StorageFailure_RetriesTheBatch_InsteadOfLosingIt()
    {
        var storage = new CapturingStorage { FailuresLeft = 1 };
        var config = Config();
        var service = CreateService(storage, config);
        await StartAsync(service);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, config.TcpPort);
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("<34>Mar  4 05:06:07 web-1 app: kept\n"));
        }

        var events = await WaitForEventsAsync(storage, 1);
        await service.StopAsync(CancellationToken.None);

        events.Should().ContainSingle();
    }

    [Fact]
    public async Task OnStop_QueuedMessagesAreStored()
    {
        var storage = new CapturingStorage();
        var config = Config();
        var service = CreateService(storage, config);
        await StartAsync(service);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, config.TcpPort);
            var lines = string.Concat(Enumerable.Range(0, 2000).Select(i => $"<34>Mar  4 05:06:07 web-1 app: {i}\n"));
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(lines));
        }

        await WaitForEventsAsync(storage, 2000);
        await service.StopAsync(CancellationToken.None);

        storage.Stored.Should().HaveCount(2000);
    }

    [Fact]
    public async Task FrameReader_LongLine_IsCutOff_AndTheNextLineSurvives()
    {
        var input = new string('a', 100) + "\n<34>next\n";
        var reader = new SyslogFrameReader(new MemoryStream(Encoding.UTF8.GetBytes(input)), 10);

        (await reader.ReadAsync()).Should().Be(new string('a', 10));
        (await reader.ReadAsync()).Should().Be("<34>next");
        (await reader.ReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task FrameReader_LongCountedMessage_IsCutOff_AndTheNextSurvives()
    {
        var body = "<34>" + new string('a', 96);
        var input = $"100 {body}7 <34>abc";
        var reader = new SyslogFrameReader(new MemoryStream(Encoding.UTF8.GetBytes(input)), 10);

        (await reader.ReadAsync()).Should().Be(body[..10]);
        (await reader.ReadAsync()).Should().Be("<34>abc");
        (await reader.ReadAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("12345 is just a line\n", "12345 is just a line")]
    [InlineData("12345\n", "12345")]
    [InlineData("99999999999999 digits\n", "99999999999999 digits")]
    public async Task FrameReader_LeadingDigits_WithoutAFrame_AreALine(string input, string expected)
    {
        var reader = new SyslogFrameReader(new MemoryStream(Encoding.UTF8.GetBytes(input)), 1024);

        (await reader.ReadAsync()).Should().Be(expected);
    }

    private static async Task StartAsync(SyslogListenerService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.Listening.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task RunUntilFaultedAsync(SyslogListenerService service)
    {
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
    }

    private sealed class CapturingStorage
    {
        private readonly object _lock = new();
        private readonly List<LogEvent> _stored = new();

        public CapturingStorage()
        {
            var storage = new Mock<ILogStorage>();
            storage
                .Setup(s => s.StoreBatchAsync(It.IsAny<IEnumerable<LogEvent>>(), It.IsAny<CancellationToken>()))
                .Returns((IEnumerable<LogEvent> events, CancellationToken _) => Store(events));
            Object = storage.Object;
        }

        public ILogStorage Object { get; }

        public int FailuresLeft { get; set; }

        public IReadOnlyList<LogEvent> Stored
        {
            get
            {
                lock (_lock)
                {
                    return _stored.ToList();
                }
            }
        }

        private Task<int> Store(IEnumerable<LogEvent> events)
        {
            lock (_lock)
            {
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                    throw new IOException("storage is down");
                }

                var batch = events.ToList();
                _stored.AddRange(batch);
                return Task.FromResult(batch.Count);
            }
        }
    }
}
