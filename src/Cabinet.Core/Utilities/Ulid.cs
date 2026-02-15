using System.Security.Cryptography;

namespace Cabinet.Core.Utilities;

/// <summary>
/// ULID (Universally Unique Lexicographically Sortable Identifier) generator.
/// ULIDs are 128-bit identifiers that encode a millisecond-precision timestamp
/// followed by cryptographic randomness, making them time-sortable and unique.
/// Format: 26-character Crockford Base32 string (e.g., "01ARZ3NDEKTSV4RRFFQ69G5FAV").
/// </summary>
public static class Ulid
{
    private static readonly char[] CrockfordBase32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

    /// <summary>
    /// Generate a new ULID for the current UTC time.
    /// Thread-safe: uses RandomNumberGenerator for cryptographic randomness.
    /// </summary>
    public static string NewUlid()
    {
        return NewUlid(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Generate a ULID for a specific timestamp.
    /// Useful for importing historical events with time-ordered IDs.
    /// </summary>
    public static string NewUlid(DateTimeOffset timestamp)
    {
        Span<char> result = stackalloc char[26];

        // Timestamp component: 48-bit milliseconds since Unix epoch (10 Crockford Base32 chars)
        var ms = timestamp.ToUnixTimeMilliseconds();
        for (var i = 9; i >= 0; i--)
        {
            result[i] = CrockfordBase32[ms & 0x1F];
            ms >>= 5;
        }

        // Random component: 80 bits of cryptographic randomness (16 Crockford Base32 chars)
        Span<byte> randomBytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomBytes);

        // Encode 10 bytes (80 bits) into 16 base32 characters
        // Process 5 bits at a time
        var bitBuffer = 0L;
        var bitsInBuffer = 0;
        var charIndex = 26;

        for (var i = 9; i >= 0; i--)
        {
            bitBuffer |= (long)randomBytes[i] << bitsInBuffer;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 5)
            {
                charIndex--;
                result[charIndex] = CrockfordBase32[bitBuffer & 0x1F];
                bitBuffer >>= 5;
                bitsInBuffer -= 5;
            }
        }

        return new string(result);
    }

    /// <summary>
    /// Extract the timestamp from a ULID string.
    /// Returns null if the string is not a valid ULID.
    /// </summary>
    public static DateTimeOffset? ExtractTimestamp(string ulid)
    {
        if (string.IsNullOrEmpty(ulid) || ulid.Length != 26)
            return null;

        try
        {
            long ms = 0;
            for (var i = 0; i < 10; i++)
            {
                var idx = Array.IndexOf(CrockfordBase32, char.ToUpperInvariant(ulid[i]));
                if (idx < 0) return null;
                ms = (ms << 5) | (uint)idx;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch
        {
            return null;
        }
    }
}
