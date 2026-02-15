namespace Cabinet.Core.Models;

/// <summary>
/// A data model defines pre-aggregated summaries for accelerated queries (tstats-like).
/// Summary tables are periodically rebuilt by a background service.
/// </summary>
public sealed class DataModel
{
    /// <summary>Unique identifier</summary>
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();

    /// <summary>Human-readable name</summary>
    public required string Name { get; set; }

    /// <summary>Optional description</summary>
    public string? Description { get; set; }

    /// <summary>BQL query that defines the events in this data model</summary>
    public required string BaseQuery { get; set; }

    /// <summary>Fields to track in the summary</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>Time bucket span for pre-aggregation</summary>
    public TimeSpan BucketSpan { get; set; } = TimeSpan.FromHours(1);

    /// <summary>When the model was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the summary was last rebuilt</summary>
    public DateTime? LastRebuiltAt { get; set; }

    /// <summary>Whether acceleration is enabled</summary>
    public bool AccelerationEnabled { get; set; } = true;

    /// <summary>How far back to keep summaries</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// A single row in a pre-aggregated summary table.
/// Stores (time_bucket, field_values) → count for fast tstats queries.
/// </summary>
public sealed class DataModelSummary
{
    /// <summary>Time bucket start</summary>
    public DateTime TimeBucket { get; set; }

    /// <summary>Field values for this bucket (field_name → value)</summary>
    public Dictionary<string, string> FieldValues { get; set; } = new();

    /// <summary>Event count in this bucket</summary>
    public long Count { get; set; }
}
