using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cabinet.CLI.Services;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.CLI;

public sealed class RemoteServerTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void APinnedCertificate_IsTrusted_AndNoOther()
    {
        // As the agents do: a server's own self-signed certificate, trusted by fingerprint
        using var server = SelfSigned("cabinet");
        using var impostor = SelfSigned("cabinet");
        var pin = string.Join(':', Convert.ToHexString(SHA256.HashData(server.RawData)).Chunk(2).Select(c => new string(c)));

        var validate = RemoteServer.Validator(pin)!;

        validate(this, server, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        validate(this, impostor, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        validate(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Fact]
    public void WithoutAPin_CertificatesAreCheckedNormally()
    {
        RemoteServer.Validator(null).Should().BeNull();
        RemoteServer.Validator(" ").Should().BeNull();
    }

    [Fact]
    public void AMalformedPin_IsRefused_RatherThanIgnored()
    {
        var act = () => RemoteServer.Validator("not-a-fingerprint");

        act.Should().Throw<ArgumentException>();
    }
}
