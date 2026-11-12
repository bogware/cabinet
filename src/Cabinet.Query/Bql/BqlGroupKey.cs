using System.Text;

namespace Cabinet.Query.Bql;

/// <summary>
/// Builds a key for grouping rows by several fields. Values are length-prefixed, so ("a|b", "c")
/// and ("a", "b|c") stay separate groups whatever characters the values contain.
/// </summary>
internal static class BqlGroupKey
{
    public static string For(IDictionary<string, object?> row, IEnumerable<string> fields)
    {
        var key = new StringBuilder();
        foreach (var field in fields)
        {
            var value = row.TryGetValue(field, out var raw) ? raw?.ToString() ?? "" : "";
            key.Append(value.Length).Append(':').Append(value);
        }

        return key.ToString();
    }

    /// <summary>Whether the row has a value for every field.</summary>
    public static bool HasAll(IDictionary<string, object?> row, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (!row.TryGetValue(field, out var value) || value == null)
            {
                return false;
            }
        }

        return true;
    }
}
