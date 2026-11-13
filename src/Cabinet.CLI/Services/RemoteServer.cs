using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cabinet.CLI.Services;

/// <summary>
/// HTTP clients for talking to a Cabinet server. A server with its own self-signed certificate is
/// trusted by pinning that certificate's SHA-256 fingerprint (the installer prints it, and it's in
/// https-fingerprint.txt on the server), as agents do: <c>--pin</c> or <c>CABINET_CERT_SHA256</c>.
/// </summary>
public static class RemoteServer
{
    /// <summary>The environment variable holding a certificate fingerprint to pin.</summary>
    public const string PinVariable = "CABINET_CERT_SHA256";

    /// <summary>A client for the server, pinned to a certificate when given one.</summary>
    public static HttpClient Create(string server, TimeSpan timeout, string? pin = null)
    {
        var handler = new SocketsHttpHandler();
        if (Validator(pin ?? Environment.GetEnvironmentVariable(PinVariable)) is { } validate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = validate;
        }

        return new HttpClient(handler) { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = timeout };
    }

    /// <summary>
    /// The check for a pinned fingerprint (hex, colons and spaces allowed), or null for normal
    /// certificate validation. Only that certificate is accepted, whoever issued it.
    /// </summary>
    public static RemoteCertificateValidationCallback? Validator(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        var expected = new string(pin.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A certificate fingerprint is 64 hex digits (SHA-256), colons allowed.");
        }

        return (_, certificate, _, _) => certificate != null && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Fingerprint(certificate)), Convert.FromHexString(expected));
    }

    /// <summary>A certificate's SHA-256 fingerprint as upper-case hex.</summary>
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
}
