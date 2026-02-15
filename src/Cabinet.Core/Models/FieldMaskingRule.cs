namespace Cabinet.Core.Models;

/// <summary>Rule for masking PII/sensitive fields at query time (original data unchanged)</summary>
public sealed class FieldMaskingRule
{
    public required string FieldPattern { get; set; }

    public MaskingStrategy Strategy { get; set; } = MaskingStrategy.Redact;

    public string? ValuePattern { get; set; }

    public string Replacement { get; set; } = "***REDACTED***";

    public List<SystemRole> ExemptRoles { get; set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>Higher priority rules are applied first (default 0). Use to control ordering when multiple rules match.</summary>
    public int Priority { get; set; }
}

public enum MaskingStrategy
{
    Redact,
    Hash,
    Partial,
    Pattern,
    Remove
}
