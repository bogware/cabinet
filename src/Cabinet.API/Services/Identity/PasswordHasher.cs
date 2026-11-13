using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cabinet.API.Services.Identity;

/// <summary>
/// Password hashes: PBKDF2-HMAC-SHA256 with a random 16-byte salt. The stored form names its
/// version and iteration count, <c>$pbkdf2-sha256$v1$600000$salt$hash</c>, so the count can rise
/// later and older hashes still check; <see cref="NeedsRehash"/> says when to replace one.
/// </summary>
public static class PasswordHasher
{
    /// <summary>The fewest iterations used for a new hash.</summary>
    public const int MinimumIterations = 600_000;

    /// <summary>The longest password checked, so a huge one can't tie up the server.</summary>
    public const int MaxPasswordLength = 1024;

    private const string Prefix = "$pbkdf2-sha256$v1$";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // Checked when there's no user, so a wrong user name takes as long as a wrong password
    private static readonly Lazy<string> Decoy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), MinimumIterations));

    /// <summary>Hashes a password.</summary>
    public static string Hash(string password, int iterations = MinimumIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        iterations = Math.Max(MinimumIterations, iterations);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, iterations);
        return $"{Prefix}{iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Whether a password matches a stored hash, compared in constant time. A missing or
    /// malformed hash still costs a full derivation, then fails.
    /// </summary>
    public static bool Verify(string? password, string? stored)
    {
        password ??= string.Empty;
        if (password.Length > MaxPasswordLength || !TryParse(stored, out var iterations, out var salt, out var expected))
        {
            Verify(password.Length > MaxPasswordLength ? string.Empty : password, Decoy.Value);
            return false;
        }

        var actual = Derive(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spends the time a check takes, for when there's nothing to check against.</summary>
    public static void VerifyDecoy(string? password) => Verify(password, Decoy.Value);

    /// <summary>Whether a hash uses fewer iterations than wanted now, or an older format.</summary>
    public static bool NeedsRehash(string? stored, int iterations) =>
        !TryParse(stored, out var used, out _, out _) || used < Math.Max(MinimumIterations, iterations);

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (stored == null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = stored[Prefix.Length..].Split('$');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations < 1 || iterations > 100_000_000)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            hash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= SaltBytes && hash.Length == HashBytes;
    }
}
