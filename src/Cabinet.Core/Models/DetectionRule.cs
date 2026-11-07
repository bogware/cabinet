namespace Cabinet.Core.Models;

/// <summary>
/// A rule checked against each event as it's ingested. An event matches if it satisfies both
/// <see cref="Pattern"/> and <see cref="Condition"/>, whichever are set. The rule fires when
/// <see cref="Threshold"/> events match within <see cref="WindowSeconds"/>, counted separately for
/// each value of <see cref="GroupBy"/>.
/// </summary>
public sealed class DetectionRule
{
    /// <summary>Identifies the rule.</summary>
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();

    /// <summary>Shown in alerts.</summary>
    public required string Name { get; set; }

    /// <summary>What a match means, and what to check.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the rule runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether this is a built-in rule, which can be turned on and off but not changed.</summary>
    public bool Builtin { get; set; }

    /// <summary>The alert's severity: Info, Warning, Error or Critical.</summary>
    public string Severity { get; set; } = "Warning";

    /// <summary>A case-insensitive regular expression that the raw line has to match.</summary>
    public string? Pattern { get; set; }

    /// <summary>A BQL <c>where</c> expression the event has to satisfy, such as <c>severity >= error and host = "db1"</c>.</summary>
    public string? Condition { get; set; }

    /// <summary>A regular expression run on the raw line of a match. Its named groups become fields.</summary>
    public string? Extract { get; set; }

    /// <summary>The field to count by, such as a source address. Empty counts all matches together.</summary>
    public string? GroupBy { get; set; }

    /// <summary>Matches needed to fire.</summary>
    public int Threshold { get; set; } = 1;

    /// <summary>How close together, by event time, the matches have to be.</summary>
    public int WindowSeconds { get; set; } = 300;

    /// <summary>After firing, how long to wait before firing again for the same group.</summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>The MITRE ATT&amp;CK technique this relates to, such as T1110.</summary>
    public string? Technique { get; set; }
}
