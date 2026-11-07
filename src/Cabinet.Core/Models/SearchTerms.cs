using System.Text;

namespace Cabinet.Core.Models;

/// <summary>
/// What a text search means, on every backend and in the query pipeline: the text splits into
/// terms at anything that isn't a letter or digit, matching ignores case, and every term has to
/// appear in the line as a whole word. <c>connection failed</c> finds "failed: Connection reset"
/// but not "reconnection failed". Text with no terms, like <c>*</c>, matches everything.
/// </summary>
public static class SearchTerms
{
    /// <summary>Splits text into lower-case terms.</summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            terms.Add(current.ToString());
        }

        return terms;
    }

    /// <summary>Whether a line contains every term.</summary>
    public static bool Matches(string? line, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (!ContainsWord(line, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsWord(string line, string term)
    {
        var start = 0;
        while (true)
        {
            var found = line.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var end = found + term.Length;
            if ((found == 0 || !char.IsLetterOrDigit(line[found - 1]))
                && (end == line.Length || !char.IsLetterOrDigit(line[end])))
            {
                return true;
            }

            start = found + 1;
        }
    }
}
