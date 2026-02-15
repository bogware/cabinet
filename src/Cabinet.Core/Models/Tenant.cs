using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>Tenant for multi-tenant data isolation via index prefix scoping</summary>
public sealed class Tenant
{
    public string Id { get; set; } = Ulid.NewUlid();

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Index prefix for isolation (e.g., "acme-" means indexes start with "acme-").
    /// Must be non-empty and end with a separator character to prevent prefix collision.</summary>
    private string _indexPrefix = string.Empty;
    public required string IndexPrefix
    {
        get => _indexPrefix;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("IndexPrefix must not be empty — empty prefix bypasses tenant isolation.", nameof(IndexPrefix));
            _indexPrefix = value;
        }
    }

    public long MaxStorageMB { get; set; }

    public long MaxEventsPerDay { get; set; }

    public int DefaultRetentionDays { get; set; } = 90;

    public List<string> ApiKeyHashes { get; set; } = new();
}
