namespace Cabinet.Core.Models;

/// <summary>
/// Predefined system roles with associated capabilities
/// </summary>
public enum SystemRole
{
    /// <summary>Read-only access to permitted indexes</summary>
    Viewer = 0,
    /// <summary>Read + write (ingest) access</summary>
    Analyst = 1,
    /// <summary>Full access to permitted indexes + management</summary>
    Admin = 2,
    /// <summary>Unrestricted access to all indexes and settings</summary>
    SuperAdmin = 3
}

/// <summary>
/// Granular capabilities that can be granted to roles or users
/// </summary>
[Flags]
public enum Capability
{
    None = 0,
    /// <summary>Search and read log events</summary>
    Search = 1 << 0,
    /// <summary>Ingest/write log events</summary>
    Ingest = 1 << 1,
    /// <summary>Create and manage saved queries</summary>
    SavedQueries = 1 << 2,
    /// <summary>Run detection suites</summary>
    Detection = 1 << 3,
    /// <summary>Export and archive logs</summary>
    Export = 1 << 4,
    /// <summary>Manage API keys and users</summary>
    UserManagement = 1 << 5,
    /// <summary>Manage indexes and storage settings</summary>
    IndexManagement = 1 << 6,
    /// <summary>View system health and stats</summary>
    SystemHealth = 1 << 7,
    /// <summary>Manage alerts and notifications</summary>
    Alerts = 1 << 8,
    /// <summary>All capabilities</summary>
    All = Search | Ingest | SavedQueries | Detection | Export | UserManagement | IndexManagement | SystemHealth | Alerts
}

/// <summary>
/// Per-index permission grant — controls which indexes a principal can access
/// </summary>
public sealed class IndexPermission
{
    /// <summary>
    /// Index name pattern (supports wildcards, e.g. "gameserver-*", "*" for all)
    /// </summary>
    public required string IndexPattern { get; set; }

    /// <summary>
    /// Capabilities granted for matching indexes
    /// </summary>
    public Capability Capabilities { get; set; } = Capability.Search;

    /// <summary>
    /// Check if this permission grants access to a specific index
    /// </summary>
    public bool MatchesIndex(string indexName) => Matches(IndexPattern, indexName);

    /// <summary>
    /// Check if a pattern (a name, a prefix ending in *, or * alone) matches an index
    /// </summary>
    public static bool Matches(string pattern, string indexName)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
        {
            return indexName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(pattern, indexName, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// A principal (user/API key) with role-based access control
/// </summary>
public sealed class RbacPrincipal
{
    /// <summary>Unique identifier for this principal</summary>
    public required string Id { get; set; }

    /// <summary>Capabilities that act on an index's data, so that grants can limit them to some indexes</summary>
    public const Capability IndexScopedCapabilities = Capability.Search | Capability.Ingest | Capability.Export | Capability.IndexManagement;

    /// <summary>Display name</summary>
    public string? DisplayName { get; set; }

    /// <summary>System role (provides baseline capabilities)</summary>
    public SystemRole Role { get; set; } = SystemRole.Viewer;

    /// <summary>
    /// Per-index permission grants. With none, the role's capabilities apply to every index. With
    /// any, capabilities that act on an index's data apply only to the indexes granted them.
    /// </summary>
    public List<IndexPermission> IndexPermissions { get; set; } = new();

    /// <summary>Additional capabilities beyond role defaults</summary>
    public Capability AdditionalCapabilities { get; set; } = Capability.None;

    /// <summary>
    /// Capabilities this principal never has, whatever its role grants: an ingest key keeps only
    /// what sending needs, not the role's view of system health.
    /// </summary>
    public Capability WithheldCapabilities { get; set; } = Capability.None;

    /// <summary>
    /// Get effective capabilities for a specific index, combining role defaults + index permissions + additional
    /// </summary>
    public Capability GetEffectiveCapabilities(string indexName)
    {
        if (Role == SystemRole.SuperAdmin)
            return Capability.All;

        var granted = GetRoleCapabilities(Role) | AdditionalCapabilities;
        if (IndexPermissions.Count == 0)
            return granted & ~WithheldCapabilities;

        // Grants decide data access, so that granting one index really does limit the principal to it
        var effective = (GetRoleCapabilities(Role) & ~IndexScopedCapabilities) | AdditionalCapabilities;
        foreach (var perm in IndexPermissions)
        {
            if (perm.MatchesIndex(indexName))
            {
                effective |= perm.Capabilities;
            }
        }

        return effective & ~WithheldCapabilities;
    }

    /// <summary>
    /// Index patterns where this principal has a capability, or null if it has it on every index.
    /// </summary>
    public IReadOnlyList<string>? IndexesWith(Capability capability)
    {
        if (Role != SystemRole.SuperAdmin && (WithheldCapabilities & capability) != 0)
            return Array.Empty<string>();

        if (Role == SystemRole.SuperAdmin || (AdditionalCapabilities & capability) == capability)
            return null;

        if (IndexPermissions.Count == 0)
            return (GetRoleCapabilities(Role) & capability) == capability ? null : Array.Empty<string>();

        var patterns = IndexPermissions
            .Where(perm => (perm.Capabilities & capability) == capability)
            .Select(perm => perm.IndexPattern)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return patterns.Contains("*") ? null : patterns;
    }

    /// <summary>
    /// Check if this principal has a capability on at least one index
    /// </summary>
    public bool HasCapabilityOnAnyIndex(Capability capability)
    {
        if ((capability & IndexScopedCapabilities) == 0)
            return HasCapability(capability);

        return IndexesWith(capability) is not { Count: 0 };
    }

    /// <summary>
    /// Check if this principal has a specific capability for an index
    /// </summary>
    public bool HasCapability(Capability capability, string indexName = "main")
    {
        return (GetEffectiveCapabilities(indexName) & capability) == capability;
    }

    /// <summary>
    /// Get default capabilities for a system role
    /// </summary>
    public static Capability GetRoleCapabilities(SystemRole role) => role switch
    {
        SystemRole.Viewer => Capability.Search | Capability.SystemHealth,
        SystemRole.Analyst => Capability.Search | Capability.Ingest | Capability.SavedQueries | Capability.Detection | Capability.SystemHealth,
        SystemRole.Admin => Capability.Search | Capability.Ingest | Capability.SavedQueries | Capability.Detection | Capability.Export | Capability.IndexManagement | Capability.Alerts | Capability.SystemHealth,
        SystemRole.SuperAdmin => Capability.All,
        _ => Capability.None
    };
}
