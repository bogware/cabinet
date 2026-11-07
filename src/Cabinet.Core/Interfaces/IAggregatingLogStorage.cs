namespace Cabinet.Core.Interfaces;

/// <summary>
/// Storage that can compute an aggregate itself, rather than sending every event to the query
/// engine. It must give exactly the answer the engine would, or return null so the engine
/// computes it instead. <c>AggregatePushdownTests</c> compares the two on every backend.
/// </summary>
public interface IAggregatingLogStorage
{
    /// <summary>Computes the aggregate, or returns null if this storage can't match the engine's answer.</summary>
    Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What to aggregate: <c>stats ... by ...</c>, or <c>timechart</c> when <see cref="Bucket"/> is set.</summary>
public sealed class AggregateRequest
{
    /// <summary>Which events, with the filters the optimizer could push down.</summary>
    public required QueryContext Filter { get; init; }

    /// <summary>Fields to group by, as written in the query.</summary>
    public IReadOnlyList<string> GroupBy { get; init; } = [];

    /// <summary>For a timechart, the width of each time bucket, starting from the start of year 1.</summary>
    public TimeSpan? Bucket { get; init; }

    /// <summary>The values to compute for each group.</summary>
    public required IReadOnlyList<AggregateSpec> Aggregates { get; init; }

    /// <summary>More groups than this fail the query, so storage can stop at one more.</summary>
    public int MaxGroups { get; init; } = 100_000;
}

/// <summary>Functions storage can be asked for.</summary>
public enum AggregateKind
{
    Count,
    DistinctCount,
    Sum,
    Average,
    Min,
    Max,
    Percentile,
    SampleDeviation,
    PopulationDeviation,
    SampleVariance,
    PopulationVariance,
}

/// <summary>One value to compute for each group.</summary>
/// <param name="Kind">The function.</param>
/// <param name="Field">Its field, or null for counting events.</param>
/// <param name="Percentile">For <see cref="AggregateKind.Percentile"/>, from 0 to 100.</param>
/// <param name="OutputName">The column it goes in.</param>
public sealed record AggregateSpec(AggregateKind Kind, string? Field, double Percentile, string OutputName);

/// <summary>The groups, in the order the engine would produce them.</summary>
public sealed class AggregateResult
{
    /// <summary>
    /// One row per group: the group values under their names (and <c>_time</c> for a bucket), then
    /// each aggregate under its output name.
    /// </summary>
    public required List<Dictionary<string, object?>> Rows { get; init; }

    /// <summary>Events the aggregate covered.</summary>
    public long EventsScanned { get; init; }

    /// <summary>Whether some value was estimated.</summary>
    public bool Approximate { get; init; }
}

/// <summary>
/// The fields events have as columns, which aggregate pushdown reads directly. Anything else is a
/// custom field, matched by name without regard to case, as the query engine does.
/// </summary>
public static class AggregateFields
{
    /// <summary>The column for a built-in field, or null for a custom one.</summary>
    public static string? Builtin(string name) => name.ToLowerInvariant() switch
    {
        "host" => "host",
        "sourcetype" or "source_type" => "source_type",
        "index" => "index",
        "severity" => "severity",
        "sourcefile" or "source_file" => "source_file",
        _ => null,
    };

    /// <summary>
    /// Whether a field can be pushed down. Time, the raw line and other derived fields can't.
    /// </summary>
    public static bool CanPush(string name) => name.ToLowerInvariant() is not
        ("timestamp" or "_time" or "_indextime" or "rawline" or "raw_line" or "_raw" or "message" or "id" or "linenumber" or "line_number" or "tags" or "parsername");

    /// <summary>The stored name for a custom field, after the engine's aliases.</summary>
    public static string CustomName(string name) => name.ToLowerInvariant() switch
    {
        "level" or "log_level" => "LogLevel",
        _ => name,
    };

    /// <summary>The start of the bucket a time falls in, counting from the start of year 1 as the engine does.</summary>
    public static DateTime BucketStart(DateTime time, TimeSpan bucket) =>
        new(time.Ticks / bucket.Ticks * bucket.Ticks, DateTimeKind.Utc);

    /// <summary>Seconds between the start of year 1 and the Unix epoch.</summary>
    public const long EpochOffsetSeconds = 62_135_596_800;
}
