namespace Cabinet.Query.Bql;

/// <summary>
/// One row flowing through a BQL pipeline. Field names match case-insensitively and through a
/// fixed set of aliases, so <c>host</c>, <c>Host</c> and <c>HOST</c> are the same field in every
/// stage, and <c>source_type</c> finds <c>SourceType</c>.
/// </summary>
public sealed class BqlRow : Dictionary<string, object?>
{
    public BqlRow()
        : base(FieldNameComparer.Instance)
    {
    }

    private sealed class FieldNameComparer : IEqualityComparer<string>
    {
        public static readonly FieldNameComparer Instance = new();

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["source_type"] = "SourceType",
            ["raw_line"] = "RawLine",
            ["source_file"] = "SourceFile",
            ["line_number"] = "LineNumber",
            ["level"] = "LogLevel",
            ["log_level"] = "LogLevel",
        };

        // Called for every field of every row. All aliases have one of these lengths, which saves
        // hashing most names twice.
        private static string Canonical(string name) =>
            name.Length is 5 or 8 or 9 or 11 && Aliases.TryGetValue(name, out var canonical) ? canonical : name;

        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null && y is null;
            }

            return StringComparer.OrdinalIgnoreCase.Equals(Canonical(x), Canonical(y));
        }

        public int GetHashCode(string name) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Canonical(name));
    }
}
