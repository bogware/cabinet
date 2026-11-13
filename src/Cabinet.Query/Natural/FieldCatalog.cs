namespace Cabinet.Query.Natural;

/// <summary>
/// What a reader knows about the data: field names, and common values of the fields worth
/// recognizing (hosts, source types, indexes, and any field with few values). Built from recent
/// events the caller may read, so it never names anything they couldn't see anyway.
/// </summary>
public sealed class FieldCatalog
{
    /// <summary>The fields every event has.</summary>
    public static readonly IReadOnlyList<string> Builtin = ["host", "sourcetype", "index", "severity", "source_file", "message", "_time", "_raw"];

    /// <summary>A catalog that knows only the built-in fields.</summary>
    public static FieldCatalog Empty { get; } = new([], new Dictionary<string, IReadOnlyCollection<string>>());

    private readonly Dictionary<string, string> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public FieldCatalog(IEnumerable<string> fields, IReadOnlyDictionary<string, IReadOnlyCollection<string>> values)
    {
        foreach (var field in Builtin.Concat(fields))
        {
            _fields.TryAdd(field, field);
        }

        foreach (var (field, known) in values)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in known.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                map.TryAdd(value, value);
            }

            _values[field] = map;
            _fields.TryAdd(field, field);
        }
    }

    /// <summary>Whether anything beyond the built-in fields is known, so absence means something.</summary>
    public bool HasData => _fields.Count > Builtin.Count || _values.Count > 0;

    /// <summary>Every known field name.</summary>
    public IEnumerable<string> Fields => _fields.Values;

    /// <summary>The stored spelling of a field, ignoring case, or null.</summary>
    public string? Field(string name) => _fields.GetValueOrDefault(name);

    /// <summary>Known values of a field, for suggestions.</summary>
    public IEnumerable<string> Values(string field) => _values.TryGetValue(field, out var map) ? map.Values : [];

    /// <summary>The stored spelling of a known value of a field, or null.</summary>
    public string? Value(string field, string value) =>
        _values.TryGetValue(field, out var map) ? map.GetValueOrDefault(value) : null;

    /// <summary>The fields that have this as a known value, built-in ones first.</summary>
    public IEnumerable<string> FieldsWithValue(string value) =>
        _values.Where(pair => pair.Value.ContainsKey(value)).Select(pair => pair.Key)
            .OrderBy(field => Builtin.Contains(field, StringComparer.OrdinalIgnoreCase) ? 0 : 1);
}
