namespace Cabinet.Core.Models;

/// <summary>
/// What happens when a <see cref="DetectionRule"/> fires. A rule can carry several actions, run in
/// order. A rule with no actions notifies through every configured channel, as rules always did.
/// </summary>
/// <remarks>
/// Text fields fill in placeholders from the firing: <c>{rule}</c>, <c>{group}</c>, <c>{count}</c>,
/// <c>{window}</c>, <c>{severity}</c>, <c>{host}</c>, <c>{index}</c>, <c>{event_id}</c>,
/// <c>{technique}</c>, <c>{line}</c> and <c>{time}</c>.
/// </remarks>
public sealed class DetectionAction
{
    /// <summary>
    /// What this action does:
    /// <list type="bullet">
    /// <item><c>notify</c> — send through every configured alert channel (email, webhook, console, file).</item>
    /// <item><c>event</c> — write a new event into an index, searchable like any log and able to feed dashboards.</item>
    /// <item><c>webhook</c> — POST the firing as JSON to <see cref="Target"/>.</item>
    /// </list>
    /// </summary>
    public string Type { get; set; } = "notify";

    /// <summary>
    /// For <c>webhook</c>, the URL to POST to. For <c>event</c>, the index to write to
    /// (default <c>detections</c>). Ignored for <c>notify</c>.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>Overrides the rule's severity for this action. One of Info, Warning, Error, Critical.</summary>
    public string? Severity { get; set; }

    /// <summary>
    /// The alert title, event line, or webhook message, with placeholders filled in. When empty, a
    /// sensible default is built from the rule, group and count.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// For an <c>event</c> action, extra fields to set on the written event. Each value has the same
    /// placeholders filled in, so <c>offender={group}</c> records the Steam ID that matched.
    /// </summary>
    public Dictionary<string, string>? Fields { get; set; }
}
