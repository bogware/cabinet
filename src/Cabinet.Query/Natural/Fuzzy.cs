namespace Cabinet.Query.Natural;

/// <summary>Closeness of words, for correcting typos.</summary>
public static class Fuzzy
{
    /// <summary>Optimal string alignment distance: edits, with a swap of neighbors counting as one.</summary>
    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (Math.Abs(a.Length - b.Length) > 3)
        {
            return 4;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
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
    public static string? Closest(string word, IEnumerable<string> candidates)
    {
        var allowed = Allowed(word.Length);
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
