using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Cabinet.API.Services.Settings;

/// <summary>
/// The JSON file settings changed in the dashboard are saved to. It's outside the program folder,
/// so neither reinstalling nor updating replaces it, and it's read after the appsettings files and
/// before environment variables.
/// </summary>
public sealed class SettingsOverrideStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SettingsOverrideStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>The file's full path.</summary>
    public string Path { get; }

    /// <summary>
    /// Where the file is: <c>Settings:OverridesFile</c>, else <c>CABINET_SETTINGS_FILE</c>, else a
    /// <c>settings</c> folder beside the program's folder.
    /// </summary>
    public static string ResolvePath(IConfiguration configuration) =>
        configuration["Settings:OverridesFile"] is { Length: > 0 } configured
            ? configured
            : Environment.GetEnvironmentVariable("CABINET_SETTINGS_FILE") is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "settings", "cabinet.settings.json"));

    /// <summary>
    /// Adds the file to configuration, after the appsettings files and before environment
    /// variables, so environment variables still win. Reloads when the file changes.
    /// </summary>
    public static SettingsOverrideStore AddTo(IConfigurationBuilder builder, string path)
    {
        var store = new SettingsOverrideStore(path);
        var directory = System.IO.Path.GetDirectoryName(store.Path)!;
        var created = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);

        // Settings made in the dashboard can include secrets (a client secret, a mail password), and
        // the default folder sits under Program Files, which every user can read. Only a folder of
        // its own is changed, never one it shares.
        if (created || System.IO.Path.GetFileName(directory).Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Cabinet.Core.Utilities.PrivateFolder.Secure(directory))
                {
                    Serilog.Log.Warning("Made {Path} readable only by this server's account, SYSTEM and Administrators", directory);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException)
            {
                Serilog.Log.Warning(ex, "Couldn't make {Path} private; other users on this machine may be able to read the settings saved there", directory);
            }
        }

        var source = new SettingsOverridesSource
        {
            Path = System.IO.Path.GetFileName(store.Path),
            Optional = true,
            ReloadOnChange = true,
            FileProvider = new PhysicalFileProvider(directory),
        };

        var sources = builder.Sources;
        var lastJson = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource)
            {
                lastJson = i;
            }
        }

        sources.Insert(lastJson + 1, source);
        return store;
    }

    /// <summary>The saved values, as written.</summary>
    public async Task<JsonObject> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new JsonObject();
        }

        await using var stream = File.OpenRead(Path);
        return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken) as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Applies changes and saves the file in one step: a null value removes that setting. Written
    /// to a temporary file first and moved into place, so a crash can't leave half a file.
    /// </summary>
    public async Task UpdateAsync(IReadOnlyDictionary<string, JsonNode?> changes, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var root = await ReadAsync(cancellationToken);
            foreach (var (key, value) in changes)
            {
                if (value == null)
                {
                    Remove(root, key.Split(':'));
                }
                else
                {
                    Set(root, key.Split(':'), value);
                }
            }

            var temporary = Path + ".tmp";
            await File.WriteAllTextAsync(temporary, string.Empty, cancellationToken);

            // It can hold database passwords and tokens: readable only by the server and administrators
            Protect(temporary);
            await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Limits a file to the account running Cabinet, SYSTEM and administrators.</summary>
    public static void Protect(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var owners = new List<System.Security.Principal.SecurityIdentifier>
            {
                new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            };
            if (System.Security.Principal.WindowsIdentity.GetCurrent().User is { } self)
            {
                owners.Add(self);
            }

            foreach (var owner in owners.Distinct())
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    owner, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            }

            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string? ExistingName(JsonObject node, string name) =>
        node.Select(property => property.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void Set(JsonObject root, string[] path, JsonNode value)
    {
        var node = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var name = ExistingName(node, path[i]) ?? path[i];
            if (node[name] is not JsonObject child)
            {
                child = new JsonObject();
                node[name] = child;
            }

            node = child;
        }

        var leaf = ExistingName(node, path[^1]) ?? path[^1];
        node[leaf] = value.DeepClone();
    }

    private static void Remove(JsonObject root, string[] path)
    {
        var trail = new List<(JsonObject Node, string Name)>();
        var node = root;
        foreach (var segment in path[..^1])
        {
            var name = ExistingName(node, segment);
            if (name == null || node[name] is not JsonObject child)
            {
                return;
            }

            trail.Add((node, name));
            node = child;
        }

        if (ExistingName(node, path[^1]) is { } leaf)
        {
            node.Remove(leaf);
        }

        // Drop sections the removal left empty
        for (var i = trail.Count - 1; i >= 0; i--)
        {
            var (parent, name) = trail[i];
            if (parent[name] is JsonObject { Count: 0 })
            {
                parent.Remove(name);
            }
        }
    }
}

/// <summary>The configuration source for the overrides file.</summary>
public sealed class SettingsOverridesSource : JsonConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new SettingsOverridesProvider(this);
    }
}

/// <summary>
/// Reads the overrides file. A list or structured setting saved here replaces the one in the
/// appsettings files entirely, rather than being merged item by item, which is how configuration
/// otherwise combines lists: saving one origin over three would have kept the other two.
/// </summary>
public sealed class SettingsOverridesProvider : JsonConfigurationProvider
{
    public SettingsOverridesProvider(JsonConfigurationSource source)
        : base(source)
    {
    }

    /// <summary>Whether this file sets a setting, or anything under it.</summary>
    public bool Sets(string key) =>
        Data.Keys.Any(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase)
            || existing.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));

    public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
    {
        if (parentPath != null && ReplacedRoot(parentPath) != null)
        {
            var prefix = parentPath + ":";
            return Data.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(key => key[prefix.Length..].Split(':')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, ConfigurationKeyComparer.Instance)
                .ToList();
        }

        return base.GetChildKeys(earlierKeys, parentPath);
    }

    /// <summary>The list or structured setting a path is in, if this file sets it.</summary>
    private string? ReplacedRoot(string path)
    {
        foreach (var key in SettingsCatalog.ReplacedAsAWhole)
        {
            if ((path.Equals(key, StringComparison.OrdinalIgnoreCase) || path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) && Sets(key))
            {
                return key;
            }
        }

        return null;
    }
}
