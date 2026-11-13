using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Cabinet.API.Services.Settings;

/// <summary>A setting as the settings screen shows it.</summary>
public sealed class SettingView
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required SettingType Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Options { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Min { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Max { get; init; }

    public object? Default { get; init; }

    /// <summary>The value in effect. Always null for secrets.</summary>
    public object? Value { get; init; }

    /// <summary>For secrets, whether one is set.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsSet { get; init; }

    /// <summary>default, appsettings, settings-file or environment.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Where exactly: the appsettings file's name (such as appsettings.Production.json), the
    /// overrides file's name, or the environment variable. Null for the default.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceName { get; init; }

    /// <summary>False when an environment variable sets it, which wins over the dashboard.</summary>
    public required bool Editable { get; init; }

    public required bool RequiresRestart { get; init; }

    /// <summary>Changed since this server started, and waiting for a restart to apply.</summary>
    public required bool PendingRestart { get; init; }
}

/// <summary>A group of settings as the settings screen shows it.</summary>
public sealed record SettingGroupView(string Name, string Description, IReadOnlyList<SettingView> Settings);

/// <summary>Everything the settings screen shows.</summary>
public sealed class SettingsView
{
    public required IReadOnlyList<SettingGroupView> Groups { get; init; }
    public required bool RestartPending { get; init; }
    public required bool CanRestart { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CannotRestartReason { get; init; }

    /// <summary>The file dashboard changes are saved in.</summary>
    public required string OverridesFile { get; init; }

    /// <summary>The addresses the server is listening on. Change them in the installer.</summary>
    public required IReadOnlyList<string> ListeningAddresses { get; init; }
}

/// <summary>Settings that couldn't be saved, and why.</summary>
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IReadOnlyDictionary<string, string> errors)
        : base("Some settings aren't valid")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string> Errors { get; }
}

/// <summary>
/// Reads settings with where each value comes from, and saves changes to the overrides file after
/// checking them all against the catalog. Every change is audited, without secret values.
/// </summary>
public sealed class SettingsService
{
    private readonly IConfigurationRoot _configuration;
    private readonly SettingsOverrideStore _store;
    private readonly ServiceRestarter _restarter;
    private readonly IAuditLogger? _audit;
    private readonly Dictionary<string, string?> _atStartup;

    public SettingsService(IConfiguration configuration, SettingsOverrideStore store, ServiceRestarter restarter, IAuditLogger? audit = null)
    {
        _configuration = configuration as IConfigurationRoot
            ?? throw new ArgumentException("Settings need the configuration root", nameof(configuration));
        _store = store;
        _restarter = restarter;
        _audit = audit;
        _atStartup = SettingsCatalog.All.ToDictionary(setting => setting.Key, Fingerprint, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The overrides file.</summary>
    public string OverridesFile => _store.Path;

    /// <summary>Every setting, by group, with its value and source.</summary>
    public SettingsView GetView(IReadOnlyList<string> listeningAddresses)
    {
        var views = SettingsCatalog.All.Select(View).ToList();
        var (canRestart, reason) = _restarter.CanRestart();
        return new SettingsView
        {
            Groups = SettingsCatalog.Groups
                .Select(group => new SettingGroupView(group.Name, group.Description, views.Where(view => SettingsCatalog.Find(view.Key)!.Group == group.Name).ToList()))
                .Where(group => group.Settings.Count > 0)
                .ToList(),
            RestartPending = views.Any(view => view.PendingRestart),
            CanRestart = canRestart,
            CannotRestartReason = reason,
            OverridesFile = _store.Path,
            ListeningAddresses = listeningAddresses,
        };
    }

    private SettingView View(SettingDefinition setting)
    {
        var source = SourceOf(setting.Key);
        return new SettingView
        {
            Key = setting.Key,
            Label = setting.Label,
            Description = setting.Description,
            Type = setting.Type,
            Options = setting.Options,
            Unit = setting.Unit,
            Min = setting.Min,
            Max = setting.Max,
            Default = setting.Sensitive ? null : setting.Default,
            Value = setting.Sensitive ? null : ValueOf(setting),
            IsSet = setting.Sensitive ? !string.IsNullOrEmpty(_configuration[setting.Key]) : null,
            Source = source,
            SourceName = SourceNameOf(setting.Key),
            Editable = source != "environment",
            RequiresRestart = setting.RequiresRestart,
            PendingRestart = setting.RequiresRestart && !string.Equals(_atStartup[setting.Key], Fingerprint(setting), StringComparison.Ordinal),
        };
    }

    /// <summary>The value in effect, typed, or the default when nothing sets it.</summary>
    public object? ValueOf(SettingDefinition setting)
    {
        var section = _configuration.GetSection(setting.Key);
        switch (setting.Type)
        {
            case SettingType.List:
                return section.Exists() ? section.GetChildren().Select(child => child.Value).Where(value => value != null).ToList() : setting.Default;
            case SettingType.Json:
                return section.Exists() ? ToJson(section) : setting.Default;
        }

        var raw = section.Value;
        if (string.IsNullOrEmpty(raw))
        {
            return setting.Default;
        }

        return setting.Type switch
        {
            SettingType.Bool => bool.TryParse(raw, out var b) ? b : setting.Default,
            SettingType.Int => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : setting.Default,
            SettingType.Long => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : setting.Default,
            SettingType.Double => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : setting.Default,
            _ => raw,
        };
    }

    /// <summary>A section as JSON: arrays where every child is numbered, objects otherwise.</summary>
    private static JsonNode? ToJson(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
        {
            return section.Value is null ? null : JsonValue.Create(section.Value);
        }

        if (children.All(child => int.TryParse(child.Key, out _)))
        {
            var array = new JsonArray();
            foreach (var child in children.OrderBy(child => int.Parse(child.Key, CultureInfo.InvariantCulture)))
            {
                array.Add(ToJson(child));
            }

            return array;
        }

        var obj = new JsonObject();
        foreach (var child in children)
        {
            obj[child.Key] = ToJson(child);
        }

        return obj;
    }

    /// <summary>What a value looks like now, to notice changes that wait for a restart.</summary>
    private string? Fingerprint(SettingDefinition setting) =>
        setting.Type is SettingType.List or SettingType.Json
            ? ToJson(_configuration.GetSection(setting.Key))?.ToJsonString()
            : _configuration[setting.Key];

    /// <summary>Where a value comes from: the last source that sets it.</summary>
    public string SourceOf(string key)
    {
        var providers = _configuration.Providers.ToList();
        for (var i = providers.Count - 1; i >= 0; i--)
        {
            if (Provides(providers[i], key))
            {
                return providers[i] switch
                {
                    EnvironmentVariablesConfigurationProvider => "environment",
                    Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider => "environment",
                    SettingsOverridesProvider => "settings-file",
                    _ => "appsettings",
                };
            }
        }

        return "default";
    }

    /// <summary>
    /// The file or variable the value comes from: the JSON file's name, or the environment
    /// variable as it's written (Storage__Type). Null when nothing sets it.
    /// </summary>
    public string? SourceNameOf(string key)
    {
        var providers = _configuration.Providers.ToList();
        for (var i = providers.Count - 1; i >= 0; i--)
        {
            if (Provides(providers[i], key))
            {
                return providers[i] switch
                {
                    EnvironmentVariablesConfigurationProvider => key.Replace(":", "__", StringComparison.Ordinal),
                    Microsoft.Extensions.Configuration.CommandLine.CommandLineConfigurationProvider => $"--{key}",
                    SettingsOverridesProvider => Path.GetFileName(_store.Path),
                    FileConfigurationProvider file when !string.IsNullOrEmpty(file.Source.Path) => Path.GetFileName(file.Source.Path),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static bool Provides(IConfigurationProvider provider, string key)
    {
        if (provider is SettingsOverridesProvider overrides)
        {
            return overrides.Sets(key);
        }

        if (provider.TryGet(key, out _))
        {
            return true;
        }

        return provider.GetChildKeys([], key).Any();
    }

    /// <summary>
    /// Checks every change, then saves them together, so nothing is saved if anything is wrong. A
    /// null value removes the dashboard's value, returning the setting to its file or default.
    /// </summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, JsonElement> values, string principal, string? sourceIp, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var setting = SettingsCatalog.Find(key);
            if (setting == null)
            {
                errors[key] = "There's no such setting";
                continue;
            }

            if (SourceOf(setting.Key) == "environment")
            {
                errors[setting.Key] = "An environment variable sets this, and it takes precedence. Change it there.";
                continue;
            }

            if (Validate(setting, value) is { } error)
            {
                errors[setting.Key] = error;
                continue;
            }

            changes[setting.Key] = value.ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(value.GetRawText());
        }

        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        var before = changes.Keys.ToDictionary(key => key, key => Describe(SettingsCatalog.Find(key)!), StringComparer.OrdinalIgnoreCase);
        await _store.UpdateAsync(changes, cancellationToken);
        _configuration.Reload();

        foreach (var key in changes.Keys)
        {
            var setting = SettingsCatalog.Find(key)!;
            await AuditAsync(principal, sourceIp, setting, changes[key] == null
                ? $"Reverted {setting.Key} (was {before[key]}, now {Describe(setting)})"
                : $"Changed {setting.Key} from {before[key]} to {Describe(setting)}");
        }
    }

    /// <summary>Removes the dashboard's value for a setting.</summary>
    public async Task<bool> RevertAsync(string key, string principal, string? sourceIp, CancellationToken cancellationToken = default)
    {
        var setting = SettingsCatalog.Find(key);
        if (setting == null)
        {
            return false;
        }

        using var nothing = JsonDocument.Parse("null");
        await SaveAsync(new Dictionary<string, JsonElement> { [setting.Key] = nothing.RootElement.Clone() }, principal, sourceIp, cancellationToken);
        return true;
    }

    /// <summary>A value for the audit log: never a secret's.</summary>
    private string Describe(SettingDefinition setting) =>
        setting.Sensitive
            ? (string.IsNullOrEmpty(_configuration[setting.Key]) ? "not set" : "a hidden value")
            : JsonSerializer.Serialize(ValueOf(setting));

    private Task AuditAsync(string principal, string? sourceIp, SettingDefinition setting, string description) =>
        _audit?.LogAsync(new AuditEvent
        {
            PrincipalId = principal,
            Action = AuditAction.Update,
            ResourceType = "Setting",
            ResourceId = setting.Key,
            Description = description,
            SourceIp = sourceIp,
        }) ?? Task.CompletedTask;

    /// <summary>Why a value doesn't suit a setting, or null if it does.</summary>
    public static string? Validate(SettingDefinition setting, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        switch (setting.Type)
        {
            case SettingType.Bool:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Must be true or false";

            case SettingType.Int:
            case SettingType.Long:
            case SettingType.Double:
                if (!TryNumber(value, out var number))
                {
                    return "Must be a number";
                }

                if (setting.Type != SettingType.Double && number != Math.Floor(number))
                {
                    return "Must be a whole number";
                }

                if (setting.Type == SettingType.Int && number is > int.MaxValue or < int.MinValue)
                {
                    return "Too large";
                }

                if (setting.Min is { } min && number < min)
                {
                    return $"Must be at least {min.ToString("N0", CultureInfo.InvariantCulture)}{UnitSuffix(setting)}";
                }

                if (setting.Max is { } max && number > max)
                {
                    return $"Must be at most {max.ToString("N0", CultureInfo.InvariantCulture)}{UnitSuffix(setting)}";
                }

                return null;

            case SettingType.Enum:
                return value.ValueKind == JsonValueKind.String && setting.Options!.Contains(value.GetString()!, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : $"Must be one of: {string.Join(", ", setting.Options!)}";

            case SettingType.List:
                return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                    ? null
                    : "Must be a list of text values";

            case SettingType.Json:
                return value.ValueKind is JsonValueKind.Array or JsonValueKind.Object ? null : "Must be a JSON list or object";

            default:
                return value.ValueKind == JsonValueKind.String
                    ? (value.GetString()!.Length > 8192 ? "Too long" : null)
                    : "Must be text";
        }
    }

    private static string UnitSuffix(SettingDefinition setting) => setting.Unit is { } unit ? $" {unit}" : string.Empty;

    private static bool TryNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            number = value.GetDouble();
            return true;
        }

        number = 0;
        return value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
