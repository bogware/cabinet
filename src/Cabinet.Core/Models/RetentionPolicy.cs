using Cabinet.Core.Utilities;

namespace Cabinet.Core.Models;

/// <summary>Per-index retention policy with archive support</summary>
public sealed class RetentionPolicy
{
    public string Id { get; set; } = Ulid.NewUlid();

    /// <summary>Index pattern (supports wildcards, e.g. "gameserver-*")</summary>
    public required string IndexPattern { get; set; }

    /// <summary>Max event age in days (0 = unlimited)</summary>
    public int RetentionDays { get; set; }

    /// <summary>Max storage size in MB (0 = unlimited)</summary>
    public long MaxSizeMB { get; set; }

    /// <summary>Archive events before deletion</summary>
    public bool ArchiveBeforeDelete { get; set; }

    /// <summary>Archive path</summary>
    public string? ArchivePath { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastAppliedAt { get; set; }

    public long LastPurgedCount { get; set; }
}
