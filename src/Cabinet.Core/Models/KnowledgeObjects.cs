using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

public sealed class LookupTable
{
    public string Id { get; set; } = Ulid.NewUlid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<string> Columns { get; set; } = new();

    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

public sealed class QueryMacro
{
    public string Id { get; set; } = Ulid.NewUlid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string Definition { get; set; }

    public List<string> Parameters { get; set; } = new();

    public string? ValidationRegex { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class FieldAlias
{
    public string Id { get; set; } = Ulid.NewUlid();

    public string SourceType { get; set; } = "*";

    public required string OriginalField { get; set; }

    public required string AliasField { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
