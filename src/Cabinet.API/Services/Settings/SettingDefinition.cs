using System.Text.Json.Serialization;

namespace Cabinet.API.Services.Settings;

/// <summary>What kind of value a setting holds, which decides how it's edited and checked.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SettingType>))]
public enum SettingType
{
    /// <summary>On or off.</summary>
    Bool,

    /// <summary>A whole number.</summary>
    Int,

    /// <summary>A large whole number.</summary>
    Long,

    /// <summary>A number with a fraction.</summary>
    Double,

    /// <summary>Text.</summary>
    String,

    /// <summary>One of <see cref="SettingDefinition.Options"/>.</summary>
    Enum,

    /// <summary>A list of text values.</summary>
    List,

    /// <summary>Text that's never shown back, such as a password or token.</summary>
    Secret,

    /// <summary>Structured JSON, such as a list of rules.</summary>
    Json,
}

/// <summary>
/// One setting in the catalog: what it's called, what it does, and what values it takes. The
/// dashboard builds its settings screen from these.
/// </summary>
public sealed class SettingDefinition
{
    /// <summary>The configuration path, such as <c>Storage:RetentionDays</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The group it's shown in.</summary>
    public required string Group { get; init; }

    /// <summary>A short name.</summary>
    public required string Label { get; init; }

    /// <summary>What it does and when to change it.</summary>
    public required string Description { get; init; }

    /// <summary>The kind of value.</summary>
    public required SettingType Type { get; init; }

    /// <summary>The value when nothing sets it. Null for secrets and empty text.</summary>
    public object? Default { get; init; }

    /// <summary>The unit, such as seconds or MB.</summary>
    public string? Unit { get; init; }

    /// <summary>Smallest allowed number.</summary>
    public double? Min { get; init; }

    /// <summary>Largest allowed number.</summary>
    public double? Max { get; init; }

    /// <summary>Allowed values for an enum.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>Whether a change takes effect only after a restart.</summary>
    public bool RequiresRestart { get; init; } = true;

    /// <summary>Whether its value is never returned.</summary>
    public bool Sensitive => Type == SettingType.Secret;
}

/// <summary>A group of settings, in display order.</summary>
public sealed record SettingGroup(string Name, string Description);
