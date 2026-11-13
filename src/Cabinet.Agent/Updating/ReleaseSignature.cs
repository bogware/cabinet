using System.Security.Cryptography;

namespace Cabinet.Agent.Updating;

/// <summary>
/// Checks the publisher's signature over an agent build: ECDSA P-256 over SHA-256, DER-encoded
/// and then base64, as in the release's <c>cabinet-agent-&lt;platform&gt;.sig</c>. The server
/// passes it with its offer; the key it's checked against is built into this agent, so a
/// server, or anyone who has taken one over, can't hand agents a program the publisher didn't sign.
/// </summary>
/// <remarks>A copy of the server's <c>ReleaseSignature</c>: the agent references no other project.</remarks>
public static class ReleaseSignature
{
    /// <summary>The embedded public key's resource name, from build/release-signing.pub.pem.</summary>
    public const string ResourceName = "Cabinet.ReleaseSigning.pub.pem";

    /// <summary>The OID of the NIST P-256 curve.</summary>
    private const string P256 = "1.2.840.10045.3.1.7";

    /// <summary>A DER P-256 signature is at most 72 bytes; anything much longer isn't one.</summary>
    private const int MaxSignatureText = 512;

    /// <summary>The publisher's public key, as built into this program.</summary>
    public static string EmbeddedPublicKeyPem { get; } = ReadEmbedded();

    private static string ReadEmbedded()
    {
        using var stream = typeof(ReleaseSignature).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The release signing key ({ResourceName}) isn't embedded in this build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The embedded public key, loaded.</summary>
    public static ECDsa LoadEmbeddedKey() => LoadPublicKey(EmbeddedPublicKeyPem);

    /// <summary>A P-256 public key from PEM. Refuses any other curve.</summary>
    public static ECDsa LoadPublicKey(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.ExportParameters(false).Curve.Oid?.Value != P256)
            {
                throw new CryptographicException("The release signing key isn't a P-256 key");
            }

            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="signatureText"/> (base64 of a DER signature) is the key's signature
    /// over the stream's bytes. Anything malformed is false.
    /// </summary>
    public static bool Verify(Stream data, string? signatureText, ECDsa publicKey)
    {
        var text = (signatureText ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > MaxSignatureText)
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifies a file. See <see cref="Verify(Stream, string?, ECDsa)"/>.</summary>
    public static bool VerifyFile(string path, string? signatureText, ECDsa publicKey)
    {
        using var file = File.OpenRead(path);
        return Verify(file, signatureText, publicKey);
    }
}
