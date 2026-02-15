using System.Text.Json;

namespace Cabinet.CLI.Configuration;

/// <summary>
/// Configuration for Cabinet CLI
/// </summary>
public class CabinetConfig
{
    public string Mode { get; set; } = "local"; // "local" or "remote"
    public RemoteConfig? Remote { get; set; }
    public LocalConfig Local { get; set; } = new();
    public string DefaultSource { get; set; } = "logs";
    public bool AutoWatch { get; set; } = false;
    public int MaxResults { get; set; } = 100;
}

public class RemoteConfig
{
    public string ApiUrl { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = string.Empty;
    public int Timeout { get; set; } = 30; // seconds
    public bool VerifySSL { get; set; } = true;
}

public class LocalConfig
{
    public string StorageType { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "Data Source=cabinet.db";
    public string LogDirectory { get; set; } = "logs";
}

/// <summary>
/// Configuration manager for Cabinet CLI
/// </summary>
public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cabinet",
        "config.json"
    );

    public static CabinetConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<CabinetConfig>(json) ?? new CabinetConfig();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to load config: {ex.Message}");
        }

        // Check environment variables for configuration
        var config = new CabinetConfig();

        var mode = Environment.GetEnvironmentVariable("CABINET_MODE");
        if (!string.IsNullOrEmpty(mode))
        {
            config.Mode = mode;
        }

        var apiUrl = Environment.GetEnvironmentVariable("CABINET_API_URL");
        var apiKey = Environment.GetEnvironmentVariable("CABINET_API_KEY");

        if (!string.IsNullOrEmpty(apiUrl) && !string.IsNullOrEmpty(apiKey))
        {
            config.Mode = "remote";
            config.Remote = new RemoteConfig
            {
                ApiUrl = apiUrl,
                ApiKey = apiKey
            };
        }

        return config;
    }

    public static void Save(CabinetConfig config)
    {
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save config: {ex.Message}");
        }
    }

    public static void Initialize()
    {
        var config = new CabinetConfig
        {
            Mode = "local",
            Local = new LocalConfig
            {
                StorageType = "sqlite",
                ConnectionString = "Data Source=cabinet.db",
                LogDirectory = "logs"
            },
            Remote = new RemoteConfig
            {
                ApiUrl = "http://localhost:5000",
                ApiKey = "your-api-key-here"
            }
        };

        Save(config);
        Console.WriteLine($"Configuration file created at: {ConfigPath}");
    }
}