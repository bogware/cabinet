using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Cabinet.Agent.Forwarding;

/// <summary>
/// Decides which server certificates the agent accepts. With a pinned SHA-256 fingerprint, only
/// that certificate, whoever issued it and whatever name it carries: a server's own self-signed
/// certificate is as safe as one from a certificate authority, without needing a domain name.
/// </summary>
public static class CertificatePinning
{
    /// <summary>
    /// The validation callback for a pin (hex, with or without colons or spaces), or for normal
    /// validation. Turning verification off accepts anything, and is only for testing.
    /// </summary>
    public static RemoteCertificateValidationCallback? Create(string? pinnedSha256, bool verify)
    {
        var pin = Normalize(pinnedSha256);
        if (pin != null)
        {
            if (pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            {
                throw new ArgumentException("Output:CertificateSha256 must be a SHA-256 fingerprint: 64 hex digits, colons allowed.");
            }

            return (_, certificate, _, _) => certificate != null && Fingerprint(certificate) == pin;
        }

        return verify ? null : (_, _, _, _) => true;
    }

    /// <summary>A certificate's SHA-256 fingerprint as upper-case hex.</summary>
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    private static string? Normalize(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return null;
        }

        return new string(pin.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
    }
}
