using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>
/// A named, reusable BQL query that persists across restarts
/// </summary>
public sealed class SavedQuery
{
    /// <summary>Unique ULID identifier</summary>
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>Human-readable name</summary>
    public required string Name { get; set; }

    /// <summary>The BQL/SPL query string</summary>
    public required string Query { get; set; }

    /// <summary>Optional description of what this query does</summary>
    public string? Description { get; set; }

    /// <summary>Principal who created this query</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When the query was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the query was last executed</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Number of times this query has been executed. Use IncrementUsageCount() for thread safety.</summary>
    private int _usageCount;

    /// <summary>Number of times this query has been executed (property for serialization).</summary>
    public int UsageCount
    {
        get => _usageCount;
        set => _usageCount = value;
    }

    /// <summary>Thread-safe increment of usage count</summary>
    public void IncrementUsageCount() => Interlocked.Increment(ref _usageCount);
}
