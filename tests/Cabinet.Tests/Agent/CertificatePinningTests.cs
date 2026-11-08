using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cabinet.Agent.Forwarding;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Cabinet.Tests.Agent;

/// <summary>
/// The agent sending to a server with its own self-signed certificate, as the Windows installer
/// sets up, over real TLS.
/// </summary>
public sealed class CertificatePinningTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = CreateSelfSigned();
    private WebApplication _server = null!;
    private Uri _address = null!;

    private static X509Certificate2 CreateSelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=cabinet", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // A round trip through PKCS#12 gives Windows a key it can use for TLS
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    public async Task InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.UseHttps(_certificate)));
        _server = builder.Build();
        _server.MapGet("/", () => "ok");
        await _server.StartAsync();

        // The certificate names "cabinet", not the address used, as when reached by IP
        _address = new Uri($"https://127.0.0.1:{port}/");
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        _certificate.Dispose();
    }

    private async Task<string> GetAsync(string? pin, bool verify = true)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = CertificatePinning.Create(pin, verify);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync(_address);
    }

    [Fact]
    public async Task PinnedFingerprint_IsTrusted_ThoughSelfSignedAndNamedDifferently()
    {
        (await GetAsync(CertificatePinning.Fingerprint(_certificate))).Should().Be("ok");
    }

    [Fact]
    public async Task Fingerprint_WithColonsAndLowerCase_IsAccepted()
    {
        var hex = CertificatePinning.Fingerprint(_certificate).ToLowerInvariant();
        var colons = string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2)));

        (await GetAsync(colons)).Should().Be("ok");
    }

    [Fact]
    public async Task AnyOtherCertificate_IsRefused()
    {
        var act = () => GetAsync(new string('A', 64));

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task WithoutAPin_ASelfSignedCertificateIsRefused()
    {
        var act = () => GetAsync(null);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("not hex at all, not hex at all, not hex at all, not hex at all!!")]
    public void MalformedPin_IsRefusedAtStartup(string pin)
    {
        var act = () => CertificatePinning.Create(pin, verify: true);

        act.Should().Throw<ArgumentException>();
    }
}
