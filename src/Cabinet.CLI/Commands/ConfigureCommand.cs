using System.CommandLine;
using Cabinet.CLI.Configuration;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Command for configuring Cabinet CLI
/// </summary>
public class ConfigureCommand : Command
{
    public ConfigureCommand() : base("configure", "Configure Cabinet CLI settings")
    {
        var initOption = new Option<bool>(
            "--init",
            "Initialize configuration file with defaults"
        );

        var modeOption = new Option<string>(
            "--mode",
            "Set operation mode (local or remote)"
        );

        var apiUrlOption = new Option<string>(
            "--api-url",
            "API URL for remote mode"
        );

        var apiKeyOption = new Option<string>(
            "--api-key",
            "API key for remote mode"
        );

        var showOption = new Option<bool>(
            "--show",
            "Show current configuration"
        );

        var testOption = new Option<bool>(
            "--test",
            "Test the current configuration"
        );

        AddOption(initOption);
        AddOption(modeOption);
        AddOption(apiUrlOption);
        AddOption(apiKeyOption);
        AddOption(showOption);
        AddOption(testOption);

        this.SetHandler(async (context) =>
        {
            var init = context.ParseResult.GetValueForOption(initOption);
            var mode = context.ParseResult.GetValueForOption(modeOption);
            var apiUrl = context.ParseResult.GetValueForOption(apiUrlOption);
            var apiKey = context.ParseResult.GetValueForOption(apiKeyOption);
            var show = context.ParseResult.GetValueForOption(showOption);
            var test = context.ParseResult.GetValueForOption(testOption);

            await ExecuteAsync(init, mode, apiUrl, apiKey, show, test);
        });
    }

    private static async Task ExecuteAsync(
        bool init,
        string? mode,
        string? apiUrl,
        string? apiKey,
        bool show,
        bool test)
    {
        if (init)
        {
            InitializeConfig();
            return;
        }

        var config = ConfigManager.Load();

        if (show)
        {
            ShowConfig(config);
            return;
        }

        if (test)
        {
            await TestConfig(config);
            return;
        }

        // Update configuration
        bool updated = false;

        if (!string.IsNullOrEmpty(mode))
        {
            if (mode.ToLower() != "local" && mode.ToLower() != "remote")
            {
                AnsiConsole.MarkupLine("[red]Invalid mode. Use 'local' or 'remote'[/]");
                return;
            }
            config.Mode = mode.ToLower();
            updated = true;
            AnsiConsole.MarkupLine($"[green]Mode set to: {mode}[/]");
        }

        if (!string.IsNullOrEmpty(apiUrl))
        {
            config.Remote ??= new RemoteConfig();
            config.Remote.ApiUrl = apiUrl;
            updated = true;
            AnsiConsole.MarkupLine($"[green]API URL set to: {apiUrl}[/]");
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            config.Remote ??= new RemoteConfig();
            config.Remote.ApiKey = apiKey;
            updated = true;
            AnsiConsole.MarkupLine("[green]API key updated[/]");
        }

        if (updated)
        {
            ConfigManager.Save(config);
            AnsiConsole.MarkupLine("[green]Configuration saved[/]");
        }
        else
        {
            ShowHelp();
        }
    }

    private static void InitializeConfig()
    {
        ConfigManager.Initialize();
        AnsiConsole.MarkupLine("[green]Configuration initialized with defaults[/]");
        AnsiConsole.MarkupLine("[dim]Edit ~/.cabinet/config.json to customize settings[/]");
    }

    private static void ShowConfig(CabinetConfig config)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("Cabinet Configuration")
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Mode", config.Mode);
        table.AddRow("Auto Watch", config.AutoWatch.ToString());
        table.AddRow("Max Results", config.MaxResults.ToString());

        if (config.Mode == "local")
        {
            table.AddRow("[dim]Local Storage[/]", config.Local.StorageType);
            table.AddRow("[dim]Connection String[/]", config.Local.ConnectionString);
            table.AddRow("[dim]Log Directory[/]", config.Local.LogDirectory);
        }
        else if (config.Remote != null)
        {
            table.AddRow("[dim]API URL[/]", config.Remote.ApiUrl);
            table.AddRow("[dim]API Key[/]", MaskApiKey(config.Remote.ApiKey));
            table.AddRow("[dim]Timeout[/]", $"{config.Remote.Timeout} seconds");
        }

        AnsiConsole.Write(table);
    }

    private static async Task TestConfig(CabinetConfig config)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing configuration...", async ctx =>
            {
                try
                {
                    var service = Services.CabinetServiceFactory.Create(config);
                    await service.InitializeAsync();

                    ctx.Status("Checking health...");
                    var healthy = await service.CheckHealthAsync();

                    if (healthy)
                    {
                        var version = await service.GetVersionAsync();
                        AnsiConsole.MarkupLine($"[green]✓ Configuration is valid[/]");
                        AnsiConsole.MarkupLine($"[dim]Version: {version}[/]");

                        if (config.Mode == "remote")
                        {
                            AnsiConsole.MarkupLine($"[dim]Connected to: {config.Remote?.ApiUrl}[/]");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[dim]Using local storage: {config.Local.StorageType}[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]✗ Service is not healthy[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Configuration test failed: {ex.Message}[/]");
                }
            });
    }

    private static void ShowHelp()
    {
        AnsiConsole.MarkupLine("[bold]Cabinet Configuration Help[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Initialize configuration:");
        AnsiConsole.MarkupLine("  [dim]cabinet configure --init[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Set to local mode:");
        AnsiConsole.MarkupLine("  [dim]cabinet configure --mode local[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Set to remote mode:");
        AnsiConsole.MarkupLine("  [dim]cabinet configure --mode remote --api-url http://api.cabinet.io --api-key YOUR_KEY[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Show current configuration:");
        AnsiConsole.MarkupLine("  [dim]cabinet configure --show[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Test configuration:");
        AnsiConsole.MarkupLine("  [dim]cabinet configure --test[/]");
    }

    private static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
            return "(not set)";

        if (apiKey.Length <= 8)
            return "****";

        return apiKey.Substring(0, 4) + "..." + apiKey.Substring(apiKey.Length - 4);
    }
}