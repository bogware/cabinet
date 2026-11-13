namespace Cabinet.API.Services.Identity;

/// <summary>
/// What a local account's password must be: long enough, not built from the user's own name or
/// email, and not one of the passwords attackers try first.
/// </summary>
public static class PasswordPolicy
{
    // The long passwords that top breach lists, and the patterns people reach for to pass a length rule
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012", "1234567890123", "12345678901234", "123456789012345", "1234567890123456",
        "1q2w3e4r5t6y", "1q2w3e4r5t6y7u", "1qaz2wsx3edc", "1qaz2wsx3edc4rfv", "qwertyuiop12", "qwertyuiop123",
        "qwertyuiopasdf", "qwertyuiopasdfgh", "asdfghjkl123", "zxcvbnm12345", "abcdefghijkl", "abcdefgh1234",
        "abc123abc123", "a1b2c3d4e5f6", "password1234", "password12345", "password123456", "passwordpassword",
        "password123!", "password1234!", "p@ssw0rd1234", "p@ssword1234", "passw0rd1234", "welcome12345",
        "welcome123456", "welcome2024!", "welcome2025!", "welcome2026!", "letmein12345", "iloveyou1234",
        "changeme1234", "changeme123!", "administrator", "administrator1", "admin1234567", "adminadmin12",
        "superman1234", "football1234", "baseball1234", "monkey123456", "dragon123456", "sunshine1234",
        "princess1234", "trustno11234", "starwars1234", "computer1234", "internet1234", "cabinet12345",
        "cabinetcabinet", "summer2024!!", "summer2025!!", "summer2026!!", "winter2024!!", "winter2025!!",
        "winter2026!!", "spring2026!!", "autumn2026!!", "qazwsxedcrfv", "1qazxsw23edc", "zaq12wsxcde3",
        "111111111111", "000000000000", "123123123123", "121212121212", "987654321098", "098765432109",
    };

    /// <summary>Why a password isn't allowed, or null if it is.</summary>
    public static string? Check(string? password, int minLength, string? userName, string? email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            return $"Use at least {minLength} characters.";
        }

        if (password.Length > PasswordHasher.MaxPasswordLength)
        {
            return $"Use at most {PasswordHasher.MaxPasswordLength} characters.";
        }

        if (password.Distinct().Count() <= 2)
        {
            return "That password repeats too few characters to be hard to guess.";
        }

        if (Common.Contains(password))
        {
            return "That's one of the passwords attackers try first. Choose another.";
        }

        foreach (var part in PersonalParts(userName, email))
        {
            if (password.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                return "Don't use your user name or email in your password.";
            }
        }

        return null;
    }

    private static IEnumerable<string> PersonalParts(string? userName, string? email)
    {
        foreach (var value in new[] { userName, email })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.Length >= 3)
            {
                yield return trimmed;
            }

            // The part before @ of an email, or of a user name written like one
            var at = trimmed.IndexOf('@');
            if (at >= 3)
            {
                yield return trimmed[..at];
            }
        }
    }
}
