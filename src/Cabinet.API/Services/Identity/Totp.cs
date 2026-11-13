using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cabinet.API.Services.Identity;

/// <summary>
/// Time-based one-time passwords (RFC 6238) as authenticator apps make them: HMAC-SHA1, six
/// digits, a new code every 30 seconds. A code is accepted one step either side of now, to allow
/// for clocks that drift.
/// </summary>
public static class Totp
{
    /// <summary>Seconds each code lasts.</summary>
    public const int StepSeconds = 30;

    /// <summary>Digits in a code.</summary>
    public const int Digits = 6;

    /// <summary>Bytes in a new secret: 160 bits, as RFC 4226 recommends.</summary>
    public const int SecretBytes = 20;

    /// <summary>A new random secret, in base32 as authenticator apps take it.</summary>
    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>The time step a moment falls in.</summary>
    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code for a secret at a time step.</summary>
    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        // Dynamic truncation, RFC 4226 section 5.3
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step a code matches, within one step of now and after <paramref name="lastUsedStep"/>,
    /// or null. Each candidate is compared in constant time.
    /// </summary>
    public static long? Match(string base32Secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = new string((code ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (normalized.Length != Digits || !normalized.All(char.IsAsciiDigit) || !Base32.TryDecode(base32Secret, out var secret))
        {
            return null;
        }

        var wanted = Encoding.ASCII.GetBytes(normalized);
        long? matched = null;
        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (CryptographicOperations.FixedTimeEquals(wanted, Encoding.ASCII.GetBytes(Code(secret, step))) && step > lastUsedStep)
            {
                matched ??= step;
            }
        }

        CryptographicOperations.ZeroMemory(secret);
        return matched;
    }

    /// <summary>The otpauth:// URI an authenticator app reads, for a QR code or a link.</summary>
    public static string Uri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={base32Secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 base32, without padding, as authenticator secrets are written.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Encodes bytes.</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    /// <summary>Decodes text, ignoring case, spaces, dashes and padding.</summary>
    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '=')
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0)
            {
                return false;
            }

            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        data = bytes.ToArray();
        return data.Length > 0;
    }
}
