namespace Cabinet.Core.Models;

/// <summary>
/// Represents an async search job for long-running queries.
/// Jobs are submitted, executed in the background, and results polled.
/// </summary>
public sealed class SearchJob
{
    /// <summary>Job identifier (ULID)</summary>
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();

    /// <summary>The BQL/SPL query to execute</summary>
    public required string Query { get; set; }

    /// <summary>Current job status</summary>
    public SearchJobStatus Status { get; set; } = SearchJobStatus.Queued;

    /// <summary>Progress percentage (0-100)</summary>
    public int Progress { get; set; }

    /// <summary>When the job was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When execution started</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the job completed (success or failure)</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Time-to-live: auto-delete results after this duration</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Error message if the job failed</summary>
    public string? Error { get; set; }

    /// <summary>Number of result events</summary>
    public int ResultCount { get; set; }

    /// <summary>Number of events scanned</summary>
    public long EventsScanned { get; set; }

    /// <summary>Execution time</summary>
    public TimeSpan? ExecutionTime { get; set; }

    /// <summary>Who submitted the job</summary>
    public string? Owner { get; set; }

    /// <summary>Optional index scope</summary>
    public string? Index { get; set; }

    /// <summary>Maximum results to return</summary>
    public int Limit { get; set; } = 50_000;

    /// <summary>Serialized results (JSON). Null until completed.</summary>
    public string? ResultsJson { get; set; }
}

/// <summary>
/// Search job lifecycle states
/// </summary>
public enum SearchJobStatus
{
    /// <summary>Job is queued waiting for execution</summary>
    Queued,

    /// <summary>Job is currently executing</summary>
    Running,

    /// <summary>Job completed successfully</summary>
    Completed,

    /// <summary>Job failed with an error</summary>
    Failed,

    /// <summary>Job was cancelled by the user</summary>
    Cancelled
}
