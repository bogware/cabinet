namespace Cabinet.API.Authentication;

/// <summary>
/// Makes a name someone typed safe to put in the audit record and the logs: no control or
/// line-breaking characters that could forge or hide a record, and not too long.
/// </summary>
public static class AuditName
{
    /// <summary>The longest name recorded.</summary>
    public const int MaxLength = 128;

    /// <summary>The name without control characters, cut to <see cref="MaxLength"/>.</summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "unknown";
        }

        var clean = new string(name
            .Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Format))
            .ToArray()).Trim();
        if (clean.Length > MaxLength)
        {
            clean = clean[..MaxLength] + "...";
        }

        return clean.Length == 0 ? "unknown" : clean;
    }
}
