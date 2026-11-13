namespace Cabinet.Query.Natural;

/// <summary>Closeness of words, for correcting typos.</summary>
public static class Fuzzy
{
    /// <summary>
    /// Longest word worth correcting. Words and field names come from people and from logs, so
    /// either can be huge, and comparing two takes time in proportion to both lengths.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Optimal string alignment distance: edits, with a swap of neighbors counting as one. Words
    /// whose lengths differ by more than 3, or longer than <see cref="MaxLength"/>, are never close,
    /// and give 4 or more without being compared.
    /// </summary>
    public static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3 || a.Length > MaxLength || b.Length > MaxLength)
        {
            return Math.Max(4, Math.Abs(a.Length - b.Length));
        }

        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        // Only the last two rows are needed: the one before, and the one before that for swaps
        var older = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    current[j] = Math.Min(current[j], older[j - 2] + 1);
                }
            }

            (older, previous, current) = (previous, current, older);
        }

        return previous[b.Length];
    }

    /// <summary>Most edits a word of this length may be from what it's corrected to.</summary>
    public static int Allowed(int length) => length switch
    {
        < 4 => 0,
        < 7 => 1,
        _ => 2,
    };

    /// <summary>
    /// The one candidate closest to a word, within the edits its length allows. Null when none is
    /// close enough, or two are equally close, since then a guess could be wrong either way.
    /// </summary>
    public static string? Closest(string word, IEnumerable<string> candidates, int leastAllowed = 0)
    {
        var allowed = Math.Max(leastAllowed, Allowed(word.Length));
        if (allowed == 0)
        {
            return null;
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        var tied = false;
        foreach (var candidate in candidates)
        {
            if (candidate.Length < 3)
            {
                continue;
            }

            var distance = Distance(word, candidate);
            if (distance > allowed || distance > bestDistance)
            {
                continue;
            }

            if (distance == bestDistance)
            {
                tied |= !string.Equals(best, candidate, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            best = candidate;
            bestDistance = distance;
            tied = false;
        }

        return tied || bestDistance == 0 ? null : best;
    }
}
