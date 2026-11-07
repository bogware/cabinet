using System.Diagnostics;

namespace Cabinet.Core.Utilities;

/// <summary>
/// Where Cabinet's own trace spans come from. With no listener, starting a span costs almost
/// nothing and returns null.
/// </summary>
public static class CabinetDiagnostics
{
    /// <summary>The name to subscribe to.</summary>
    public const string SourceName = "Cabinet";

    /// <summary>Starts spans for queries, ingest and background work.</summary>
    public static readonly ActivitySource Source = new(SourceName);
}
