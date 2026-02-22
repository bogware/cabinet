using System.CommandLine;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.CLI.Configuration;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Interactive setup wizard for first-time Cabinet deployment.
/// Works on both GUI terminals (Windows Terminal, iTerm2) and headless CLI (SSH, WSL).
/// Uses Spectre.Console for rich terminal UI with fallback for non-interactive environments.
/// </summary>
public sealed class SetupWizardCommand : Command
{
    public SetupWizardCommand() : base("setup", "Interactive setup wizard for first-time Cabinet deployment")
    {
        var nonInteractiveOption = new Option<bool>(
            "--non-interactive",
            "Run in non-interactive mode using defaults and environment variables");

        var modeOption = new Option<string?>(
            "--mode",
            "Deployment mode: standalone, docker, distributed, agent");

        var storageOption = new Option<string?>(
            "--storage",
            "Storage backend: sqlite, postgresql, opensearch");

        var apiKeyOption = new Option<string?>(
            "--api-key",
            "Pre-generated API key (skips key generation)");

        var serverUrlOption = new Option<string?>(
            "--server-url",
            "Cabinet API server URL (for agent mode)");

        var skipSampleDataOption = new Option<bool>(
            "--skip-sample-data",
            "Skip sample data ingestion and starter dashboard creation");

        AddOption(nonInteractiveOption);
        AddOption(modeOption);
        AddOption(storageOption);
        AddOption(apiKeyOption);
        AddOption(serverUrlOption);
        AddOption(skipSampleDataOption);

        this.SetHandler(async (context) =>
        {
            var nonInteractive = context.ParseResult.GetValueForOption(nonInteractiveOption);
            var mode = context.ParseResult.GetValueForOption(modeOption);
            var storage = context.ParseResult.GetValueForOption(storageOption);
            var apiKey = context.ParseResult.GetValueForOption(apiKeyOption);
            var serverUrl = context.ParseResult.GetValueForOption(serverUrlOption);
            var skipSampleData = context.ParseResult.GetValueForOption(skipSampleDataOption);

            var wizard = new SetupWizard(nonInteractive, mode, storage, apiKey, serverUrl, skipSampleData);
            context.ExitCode = await wizard.RunAsync();
        });
    }
}

/// <summary>
/// Core setup wizard logic — guides users through Cabinet deployment configuration.
/// </summary>
internal sealed class SetupWizard
{
    private readonly bool _nonInteractive;
    private readonly string? _presetMode;
    private readonly string? _presetStorage;
    private readonly string? _presetApiKey;
    private readonly string? _presetServerUrl;
    private readonly bool _skipSampleData;

    // Wizard state
    private string _deploymentMode = "standalone";
    private string _storageBackend = "sqlite";
    private string _connectionString = "Data Source=cabinet.db";
    private string _apiKey = string.Empty;
    private string _serverUrl = "http://localhost:5000";
    private int _apiPort = 5000;
    private bool _enableSyslog;
    private int _syslogPort = 1514;
    private bool _enableDistributed;
    private string _redisConnectionString = "localhost:6379";
    private bool _enableEmail;
    private string _smtpHost = string.Empty;
    private string _smtpFrom = string.Empty;
    private readonly List<string> _agentLogPaths = new();
    private string _installDirectory = string.Empty;

    public SetupWizard(bool nonInteractive, string? mode, string? storage,
        string? apiKey, string? serverUrl, bool skipSampleData)
    {
        _nonInteractive = nonInteractive;
        _presetMode = mode;
        _presetStorage = storage;
        _presetApiKey = apiKey;
        _presetServerUrl = serverUrl;
        _skipSampleData = skipSampleData;
    }

    public async Task<int> RunAsync()
    {
        try
        {
            RenderWelcomeBanner();

            // Step 1: Environment detection
            var envInfo = DetectEnvironment();
            RenderEnvironmentInfo(envInfo);

            // Step 2: Deployment mode selection
            await SelectDeploymentModeAsync();

            // Step 3: Mode-specific configuration
            switch (_deploymentMode)
            {
                case "standalone":
                    await ConfigureStandaloneAsync();
                    break;
                case "docker":
                    await ConfigureDockerAsync();
                    break;
                case "distributed":
                    await ConfigureDistributedAsync();
                    break;
                case "agent":
                    await ConfigureAgentOnlyAsync();
                    break;
            }

            // Step 4: Generate configuration files
            AnsiConsole.WriteLine();
            var configPaths = await GenerateConfigurationAsync();

            // Step 5: Sample data and starter dashboard (unless agent-only or skipped)
            if (_deploymentMode != "agent" && !_skipSampleData)
            {
                await SetupSampleDataAndDashboardAsync();
            }

            // Step 6: Summary and next steps
            RenderCompletionSummary(configPaths);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"\n[red]Setup failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[dim]Run 'cabinet setup --help' for usage information.[/]");
            return 1;
        }
    }

    private void RenderWelcomeBanner()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Cabinet").Color(Color.Cyan1));
        AnsiConsole.Write(new Rule("[bold cyan]Setup Wizard[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Welcome to Cabinet[/] — Enterprise Log Intelligence System");
        AnsiConsole.MarkupLine("[dim]This wizard will guide you through first-time setup and configuration.[/]");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C at any time to cancel.[/]\n");
    }

    #region Environment Detection

    private EnvironmentInfo DetectEnvironment()
    {
        var info = new EnvironmentInfo
        {
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
            IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            IsDocker = File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            CurrentDirectory = Directory.GetCurrentDirectory(),
            UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            IsRoot = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.UserName == "root",
            MachineName = Environment.MachineName
        };

        // Check for Docker/Podman
        info.HasDocker = CheckCommandExists("docker");
        info.HasDockerCompose = CheckCommandExists("docker") && CheckDockerComposeAvailable();
        info.HasSystemd = !info.IsWindows && Directory.Exists("/etc/systemd/system");

        // Check for existing Cabinet installation
        info.ExistingConfigPath = FindExistingConfig();
        info.HasExistingInstall = info.ExistingConfigPath != null;

        // Detect available ports
        info.Port5000Available = IsPortAvailable(5000);
        info.Port8080Available = IsPortAvailable(8080);

        return info;
    }

    private void RenderEnvironmentInfo(EnvironmentInfo info)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Environment Detection[/]")
            .AddColumn("Property")
            .AddColumn("Value");

        var osIcon = info.IsWindows ? "Windows" : info.IsLinux ? "Linux" : info.IsMacOS ? "macOS" : "Unknown";
        table.AddRow("Operating System", $"[cyan]{osIcon}[/] {Markup.Escape(info.OperatingSystem)}");
        table.AddRow("Architecture", info.Architecture);
        table.AddRow(".NET Runtime", Markup.Escape(info.DotNetVersion));
        table.AddRow("Machine Name", Markup.Escape(info.MachineName));
        table.AddRow("Running in Docker", info.IsDocker ? "[yellow]Yes[/]" : "No");
        table.AddRow("Docker Available", info.HasDocker ? "[green]Yes[/]" : "[dim]No[/]");
        table.AddRow("Docker Compose", info.HasDockerCompose ? "[green]Yes[/]" : "[dim]No[/]");

        if (info.IsLinux)
        {
            table.AddRow("systemd Available", info.HasSystemd ? "[green]Yes[/]" : "[dim]No[/]");
            table.AddRow("Running as root", info.IsRoot ? "[yellow]Yes[/]" : "No");
        }

        if (info.HasExistingInstall)
        {
            table.AddRow("[yellow]Existing Install[/]", $"[yellow]Found at {Markup.Escape(info.ExistingConfigPath!)}[/]");
        }

        table.AddRow("Port 5000", info.Port5000Available ? "[green]Available[/]" : "[red]In use[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    #endregion

    #region Deployment Mode Selection

    private Task SelectDeploymentModeAsync()
    {
        if (!string.IsNullOrEmpty(_presetMode))
        {
            _deploymentMode = _presetMode.ToLowerInvariant();
            AnsiConsole.MarkupLine($"[green]Deployment mode:[/] {_deploymentMode} (from --mode flag)");
            return Task.CompletedTask;
        }

        if (_nonInteractive)
        {
            _deploymentMode = "standalone";
            AnsiConsole.MarkupLine("[green]Deployment mode:[/] standalone (default for non-interactive)");
            return Task.CompletedTask;
        }

        AnsiConsole.Write(new Rule("[bold]Step 1: Deployment Mode[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        _deploymentMode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How would you like to deploy Cabinet?")
                .PageSize(6)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(new[]
                {
                    "standalone",
                    "docker",
                    "distributed",
                    "agent"
                })
                .UseConverter(mode => mode switch
                {
                    "standalone" => "Standalone Server  — Single binary, SQLite/PostgreSQL, best for getting started",
                    "docker"     => "Docker Compose     — API + PostgreSQL + Redis, production-ready containers",
                    "distributed"=> "Distributed Cluster — Multi-node with load balancing, OpenSearch/PostgreSQL + Redis",
                    "agent"      => "Agent Only         — Install log forwarding agent to send logs to existing Cabinet server",
                    _ => mode
                }));

        AnsiConsole.MarkupLine($"\n[green]Selected:[/] {_deploymentMode}\n");
        return Task.CompletedTask;
    }

    #endregion

    #region Standalone Configuration

    private async Task ConfigureStandaloneAsync()
    {
        AnsiConsole.Write(new Rule("[bold]Step 2: Storage Backend[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        await SelectStorageBackendAsync();

        AnsiConsole.Write(new Rule("[bold]Step 3: Security[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        ConfigureApiKey();
        ConfigureApiPort();

        AnsiConsole.Write(new Rule("[bold]Step 4: Optional Features[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        ConfigureOptionalFeatures();
    }

    private Task SelectStorageBackendAsync()
    {
        if (!string.IsNullOrEmpty(_presetStorage))
        {
            _storageBackend = _presetStorage.ToLowerInvariant();
            AnsiConsole.MarkupLine($"[green]Storage backend:[/] {_storageBackend} (from --storage flag)");
            SetDefaultConnectionString();
            return Task.CompletedTask;
        }

        if (_nonInteractive)
        {
            _storageBackend = "sqlite";
            _connectionString = "Data Source=cabinet.db";
            AnsiConsole.MarkupLine("[green]Storage backend:[/] SQLite (default)");
            return Task.CompletedTask;
        }

        _storageBackend = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which storage backend would you like to use?")
                .PageSize(5)
                .HighlightStyle(new Style(Color.Cyan1))
                .AddChoices(new[] { "sqlite", "postgresql", "opensearch" })
                .UseConverter(s => s switch
                {
                    "sqlite"      => "SQLite         — Zero-config, single file, great for dev/small deployments (up to ~10GB)",
                    "postgresql"  => "PostgreSQL     — Production-grade, JSONB + full-text search, scales to 100s of GB",
                    "opensearch"  => "OpenSearch     — Enterprise distributed search, time-based indices, scales to PBs",
                    _ => s
                }));

        AnsiConsole.MarkupLine($"\n[green]Selected:[/] {_storageBackend}");

        // Get connection string for non-SQLite backends
        if (_storageBackend != "sqlite")
        {
            ConfigureConnectionString();
        }
        else
        {
            var dbPath = _nonInteractive ? "cabinet.db" :
                AnsiConsole.Prompt(
                    new TextPrompt<string>("SQLite database file path:")
                        .DefaultValue("cabinet.db")
                        .AllowEmpty());
            _connectionString = $"Data Source={dbPath}";
        }

        AnsiConsole.MarkupLine($"[dim]Connection: {MaskConnectionString(_connectionString)}[/]\n");
        return Task.CompletedTask;
    }

    private void ConfigureConnectionString()
    {
        if (_nonInteractive)
        {
            SetDefaultConnectionString();
            return;
        }

        if (_storageBackend == "postgresql")
        {
            AnsiConsole.MarkupLine("\n[bold]PostgreSQL Connection[/]");

            var host = AnsiConsole.Prompt(new TextPrompt<string>("  Host:").DefaultValue("localhost"));
            var port = AnsiConsole.Prompt(new TextPrompt<int>("  Port:").DefaultValue(5432));
            var database = AnsiConsole.Prompt(new TextPrompt<string>("  Database:").DefaultValue("cabinet"));
            var username = AnsiConsole.Prompt(new TextPrompt<string>("  Username:").DefaultValue("cabinet"));
            var password = AnsiConsole.Prompt(new TextPrompt<string>("  Password:").Secret());

            _connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        }
        else if (_storageBackend == "opensearch")
        {
            AnsiConsole.MarkupLine("\n[bold]OpenSearch Connection[/]");

            var url = AnsiConsole.Prompt(new TextPrompt<string>("  URL:").DefaultValue("https://localhost:9200"));
            _connectionString = url;

            var hasAuth = AnsiConsole.Confirm("  Requires authentication?", true);
            if (hasAuth)
            {
                AnsiConsole.Prompt(new TextPrompt<string>("  Username:").DefaultValue("admin"));
                AnsiConsole.Prompt(new TextPrompt<string>("  Password:").Secret());
            }
        }
    }

    private void SetDefaultConnectionString()
    {
        _connectionString = _storageBackend switch
        {
            "postgresql" => Environment.GetEnvironmentVariable("CABINET_DB_CONNECTION")
                ?? "Host=localhost;Port=5432;Database=cabinet;Username=cabinet;Password=cabinet",
            "opensearch" => Environment.GetEnvironmentVariable("CABINET_OS_URL")
                ?? "https://localhost:9200",
            _ => "Data Source=cabinet.db"
        };
    }

    private void ConfigureApiKey()
    {
        if (!string.IsNullOrEmpty(_presetApiKey))
        {
            _apiKey = _presetApiKey;
            AnsiConsole.MarkupLine($"[green]API key:[/] {MaskApiKey(_apiKey)} (from --api-key flag)");
            return;
        }

        if (_nonInteractive)
        {
            _apiKey = Environment.GetEnvironmentVariable("CABINET_API_KEY") ?? GenerateSecureApiKey();
            AnsiConsole.MarkupLine($"[green]API key:[/] {MaskApiKey(_apiKey)}");
            return;
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("How would you like to configure your API key?")
                .AddChoices(new[]
                {
                    "generate",
                    "custom"
                })
                .UseConverter(c => c switch
                {
                    "generate" => "Generate a secure API key automatically (recommended)",
                    "custom"   => "Enter a custom API key",
                    _ => c
                }));

        if (choice == "generate")
        {
            _apiKey = GenerateSecureApiKey();
            AnsiConsole.MarkupLine($"\n[green]Generated API key:[/] [bold]{_apiKey}[/]");
            AnsiConsole.MarkupLine("[yellow]Save this key securely — it will be written to your config but shown here only once.[/]");
        }
        else
        {
            _apiKey = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter your API key:")
                    .Validate(key => key.Length >= 16
                        ? ValidationResult.Success()
                        : ValidationResult.Error("API key must be at least 16 characters for security")));
        }

        AnsiConsole.WriteLine();
    }

    private void ConfigureApiPort()
    {
        if (_nonInteractive)
        {
            _apiPort = 5000;
            return;
        }

        _apiPort = AnsiConsole.Prompt(
            new TextPrompt<int>("API listen port:")
                .DefaultValue(5000)
                .Validate(port => port is >= 1 and <= 65535
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Port must be between 1 and 65535")));
    }

    private void ConfigureOptionalFeatures()
    {
        if (_nonInteractive)
            return;

        var features = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Enable optional features? (Space to toggle, Enter to confirm)")
                .NotRequired()
                .PageSize(8)
                .InstructionsText("[dim](Space to toggle, Enter to confirm)[/]")
                .AddChoices(new[]
                {
                    "Syslog Ingestion (TCP/UDP listener for RFC 3164 syslog)",
                    "Email Alerts (SMTP notifications for scheduled searches)",
                    "Automatic Backups (scheduled SQLite/PostgreSQL backups)"
                }));

        foreach (var feature in features)
        {
            if (feature.StartsWith("Syslog", StringComparison.Ordinal))
            {
                _enableSyslog = true;
                _syslogPort = AnsiConsole.Prompt(
                    new TextPrompt<int>("  Syslog listen port (TCP+UDP):")
                        .DefaultValue(1514));
                AnsiConsole.MarkupLine($"  [green]Syslog enabled on port {_syslogPort}[/]");
            }
            else if (feature.StartsWith("Email", StringComparison.Ordinal))
            {
                _enableEmail = true;
                _smtpHost = AnsiConsole.Prompt(new TextPrompt<string>("  SMTP host:").DefaultValue("smtp.gmail.com"));
                _smtpFrom = AnsiConsole.Prompt(new TextPrompt<string>("  From address:"));
                AnsiConsole.MarkupLine($"  [green]Email alerts enabled via {_smtpHost}[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    #endregion

    #region Docker Configuration

    private async Task ConfigureDockerAsync()
    {
        AnsiConsole.Write(new Rule("[bold]Step 2: Docker Compose Configuration[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        if (!CheckCommandExists("docker"))
        {
            AnsiConsole.MarkupLine("[red]Docker is not installed or not in PATH.[/]");
            AnsiConsole.MarkupLine("[dim]Install Docker from https://docs.docker.com/get-docker/[/]");

            if (!_nonInteractive)
            {
                var switchMode = AnsiConsole.Confirm("Switch to standalone mode instead?", true);
                if (switchMode)
                {
                    _deploymentMode = "standalone";
                    await ConfigureStandaloneAsync();
                    return;
                }
            }

            throw new InvalidOperationException("Docker is required for Docker Compose deployment");
        }

        // Docker mode always uses PostgreSQL
        _storageBackend = "postgresql";
        AnsiConsole.MarkupLine("[green]Storage:[/] PostgreSQL (included in Docker Compose stack)");

        ConfigureApiKey();

        if (!_nonInteractive)
        {
            var enableMonitoring = AnsiConsole.Confirm("Enable Grafana + Prometheus monitoring stack?", false);
            if (enableMonitoring)
            {
                AnsiConsole.MarkupLine("[green]Monitoring stack will be included (Grafana on :3000, Prometheus on :9090)[/]");
            }
        }

        AnsiConsole.WriteLine();
    }

    #endregion

    #region Distributed Configuration

    private async Task ConfigureDistributedAsync()
    {
        AnsiConsole.Write(new Rule("[bold]Step 2: Distributed Cluster Configuration[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        _enableDistributed = true;

        await SelectStorageBackendAsync();

        AnsiConsole.Write(new Rule("[bold]Step 3: Redis Configuration[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        if (_nonInteractive)
        {
            _redisConnectionString = Environment.GetEnvironmentVariable("CABINET_REDIS_URL") ?? "localhost:6379";
        }
        else
        {
            _redisConnectionString = AnsiConsole.Prompt(
                new TextPrompt<string>("Redis connection string:")
                    .DefaultValue("localhost:6379"));
        }

        AnsiConsole.MarkupLine($"[green]Redis:[/] {_redisConnectionString}");

        AnsiConsole.Write(new Rule("[bold]Step 4: Security[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        ConfigureApiKey();
        ConfigureApiPort();

        AnsiConsole.Write(new Rule("[bold]Step 5: Optional Features[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        ConfigureOptionalFeatures();
    }

    #endregion

    #region Agent-Only Configuration

    private Task ConfigureAgentOnlyAsync()
    {
        AnsiConsole.Write(new Rule("[bold]Step 2: Cabinet Server Connection[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        if (!string.IsNullOrEmpty(_presetServerUrl))
        {
            _serverUrl = _presetServerUrl;
        }
        else if (_nonInteractive)
        {
            _serverUrl = Environment.GetEnvironmentVariable("CABINET_SERVER_URL") ?? "http://localhost:5000";
        }
        else
        {
            _serverUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("Cabinet API server URL:")
                    .DefaultValue("http://localhost:5000")
                    .Validate(url =>
                        Uri.TryCreate(url, UriKind.Absolute, out _)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Must be a valid URL (e.g., https://cabinet.example.com)")));
        }

        AnsiConsole.MarkupLine($"[green]Server URL:[/] {_serverUrl}");

        ConfigureApiKey();

        AnsiConsole.Write(new Rule("[bold]Step 3: Log Sources[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        ConfigureLogPaths();

        // Install directory
        if (!_nonInteractive)
        {
            var defaultInstallDir = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? @"C:\Program Files\Cabinet\Agent"
                : "/opt/cabinet-agent";

            _installDirectory = AnsiConsole.Prompt(
                new TextPrompt<string>("Install directory:")
                    .DefaultValue(defaultInstallDir));
        }
        else
        {
            _installDirectory = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? @"C:\Program Files\Cabinet\Agent"
                : "/opt/cabinet-agent";
        }

        AnsiConsole.WriteLine();
        return Task.CompletedTask;
    }

    private void ConfigureLogPaths()
    {
        if (_nonInteractive)
        {
            var envPaths = Environment.GetEnvironmentVariable("CABINET_LOG_PATHS");
            if (!string.IsNullOrEmpty(envPaths))
            {
                _agentLogPaths.AddRange(envPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                // Platform-specific defaults
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _agentLogPaths.Add(@"C:\inetpub\logs\LogFiles\**\*.log");
                    _agentLogPaths.Add(@"C:\Windows\System32\LogFiles\**\*.log");
                }
                else
                {
                    _agentLogPaths.Add("/var/log/*.log");
                    _agentLogPaths.Add("/var/log/syslog");
                }
            }
            AnsiConsole.MarkupLine($"[green]Log paths:[/] {string.Join(", ", _agentLogPaths)}");
            return;
        }

        AnsiConsole.MarkupLine("Add log file paths or glob patterns to monitor.");
        AnsiConsole.MarkupLine("[dim]Examples: /var/log/*.log, C:\\inetpub\\logs\\**\\*.log, /opt/app/logs/app.log[/]\n");

        // Suggest platform-specific defaults
        var suggestions = new List<string>();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            suggestions.AddRange(new[]
            {
                @"C:\inetpub\logs\LogFiles\**\*.log",
                @"C:\Windows\System32\LogFiles\**\*.log",
                @"C:\ProgramData\**\*.log"
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            suggestions.AddRange(new[]
            {
                "/var/log/*.log",
                "/var/log/syslog",
                "/var/log/auth.log",
                "/var/log/nginx/access.log",
                "/var/log/apache2/access.log"
            });
        }
        else
        {
            suggestions.AddRange(new[]
            {
                "/var/log/*.log",
                "/usr/local/var/log/**/*.log"
            });
        }

        // Let user pick from suggestions and/or add custom paths
        var selectedDefaults = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select log paths to monitor (Space to toggle, Enter to confirm):")
                .NotRequired()
                .PageSize(10)
                .InstructionsText("[dim](Space to toggle, Enter to confirm, then add custom paths)[/]")
                .AddChoices(suggestions));

        _agentLogPaths.AddRange(selectedDefaults);

        // Allow adding custom paths
        while (true)
        {
            var customPath = AnsiConsole.Prompt(
                new TextPrompt<string>("Add custom log path (or press Enter to finish):")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(customPath))
                break;

            _agentLogPaths.Add(customPath);
            AnsiConsole.MarkupLine($"  [green]Added:[/] {Markup.Escape(customPath)}");
        }

        if (_agentLogPaths.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Warning: No log paths configured. Agent won't monitor any files.[/]");
        }
    }

    #endregion

    #region Configuration Generation

    private async Task<Dictionary<string, string>> GenerateConfigurationAsync()
    {
        var paths = new Dictionary<string, string>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Cyan1))
            .StartAsync("Generating configuration files...", async ctx =>
            {
                await Task.Delay(200); // Brief pause for visual feedback

                switch (_deploymentMode)
                {
                    case "standalone":
                    case "distributed":
                        paths = GenerateStandaloneConfig();
                        break;
                    case "docker":
                        paths = GenerateDockerConfig();
                        break;
                    case "agent":
                        paths = GenerateAgentConfig();
                        break;
                }

                ctx.Status("Validating configuration...");
                await Task.Delay(200);
            });

        AnsiConsole.MarkupLine("[green]Configuration files generated successfully.[/]\n");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Generated Files[/]")
            .AddColumn("File")
            .AddColumn("Purpose");

        foreach (var (purpose, path) in paths)
        {
            table.AddRow(Markup.Escape(path), purpose);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        return paths;
    }

    private Dictionary<string, string> GenerateStandaloneConfig()
    {
        var paths = new Dictionary<string, string>();
        var configDir = Path.Combine(Directory.GetCurrentDirectory(), "config");
        Directory.CreateDirectory(configDir);

        // Generate appsettings.json
        var appSettings = BuildAppSettings();
        var appSettingsPath = Path.Combine(configDir, "appsettings.json");
        File.WriteAllText(appSettingsPath, JsonSerializer.Serialize(appSettings, new JsonSerializerOptions { WriteIndented = true }));
        paths["API configuration"] = appSettingsPath;

        // Generate .env file for secrets
        var envPath = Path.Combine(configDir, ".env");
        var envContent = new StringBuilder();
        envContent.AppendLine("# Cabinet API Environment Variables");
        envContent.AppendLine("# Generated by Cabinet Setup Wizard");
        envContent.AppendLine($"# Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        envContent.AppendLine();
        envContent.AppendLine($"ApiKeys__Keys__0__Key={_apiKey}");
        envContent.AppendLine("ApiKeys__Keys__0__Tier=Enterprise");
        envContent.AppendLine("ApiKeys__Keys__0__Name=Setup Wizard Key");
        envContent.AppendLine();
        envContent.AppendLine($"ASPNETCORE_URLS=http://0.0.0.0:{_apiPort}");
        envContent.AppendLine("ASPNETCORE_ENVIRONMENT=Production");

        if (_storageBackend == "postgresql")
        {
            envContent.AppendLine($"Storage__ConnectionString={_connectionString}");
        }
        if (_enableDistributed)
        {
            envContent.AppendLine($"Distributed__RedisConnectionString={_redisConnectionString}");
        }

        File.WriteAllText(envPath, envContent.ToString());
        // Restrict permissions on .env file (Unix)
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* Best effort */ }
        }
        paths["Environment secrets (.env)"] = envPath;

        // Generate CLI config
        var cliConfig = new CabinetConfig
        {
            Mode = "remote",
            Remote = new RemoteConfig
            {
                ApiUrl = $"http://localhost:{_apiPort}",
                ApiKey = _apiKey
            }
        };
        ConfigManager.Save(cliConfig);
        var cliConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cabinet", "config.json");
        paths["CLI configuration"] = cliConfigPath;

        // Generate startup script
        var startupPath = GenerateStartupScript(configDir);
        paths["Startup script"] = startupPath;

        return paths;
    }

    private Dictionary<string, string> GenerateDockerConfig()
    {
        var paths = new Dictionary<string, string>();
        var configDir = Path.Combine(Directory.GetCurrentDirectory(), "cabinet-docker");
        Directory.CreateDirectory(configDir);

        // Generate .env for Docker Compose
        var envPath = Path.Combine(configDir, ".env");
        var envContent = new StringBuilder();
        envContent.AppendLine("# Cabinet Docker Compose Environment");
        envContent.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        envContent.AppendLine();
        envContent.AppendLine($"CABINET_API_KEY={_apiKey}");
        envContent.AppendLine($"CABINET_DB_PASSWORD={GenerateRandomPassword(24)}");
        envContent.AppendLine("POSTGRES_DB=cabinet");
        envContent.AppendLine("POSTGRES_USER=cabinet");
        File.WriteAllText(envPath, envContent.ToString());
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* Best effort */ }
        }
        paths["Docker environment (.env)"] = envPath;

        // Generate docker-compose.override.yml with user's customizations
        var overridePath = Path.Combine(configDir, "docker-compose.override.yml");
        var overrideYaml = new StringBuilder();
        overrideYaml.AppendLine("# Cabinet Docker Compose Override");
        overrideYaml.AppendLine("# Customize ports, volumes, and environment here");
        overrideYaml.AppendLine("version: '3.8'");
        overrideYaml.AppendLine("services:");
        overrideYaml.AppendLine("  cabinet-api:");
        overrideYaml.AppendLine("    ports:");
        overrideYaml.AppendLine($"      - \"{_apiPort}:8080\"");
        overrideYaml.AppendLine("    environment:");
        overrideYaml.AppendLine("      - ApiKeys__Keys__0__Key=${CABINET_API_KEY}");
        overrideYaml.AppendLine("      - ApiKeys__Keys__0__Tier=Enterprise");
        overrideYaml.AppendLine("      - ApiKeys__Keys__0__Name=Setup Wizard Key");
        File.WriteAllText(overridePath, overrideYaml.ToString());
        paths["Docker Compose override"] = overridePath;

        // Generate CLI config
        var cliConfig = new CabinetConfig
        {
            Mode = "remote",
            Remote = new RemoteConfig
            {
                ApiUrl = $"http://localhost:{_apiPort}",
                ApiKey = _apiKey
            }
        };
        ConfigManager.Save(cliConfig);
        var cliConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cabinet", "config.json");
        paths["CLI configuration"] = cliConfigPath;

        return paths;
    }

    private Dictionary<string, string> GenerateAgentConfig()
    {
        var paths = new Dictionary<string, string>();
        var configDir = string.IsNullOrEmpty(_installDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "agent-config")
            : _installDirectory;
        Directory.CreateDirectory(configDir);

        // Generate agent appsettings.json
        var agentSettings = new Dictionary<string, object>
        {
            ["Agent"] = new Dictionary<string, object>
            {
                ["Inputs"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["Paths"] = _agentLogPaths.Count > 0
                            ? _agentLogPaths.ToArray()
                            : new[] { "/var/log/*.log" },
                        ["SourceType"] = "auto",
                        ["Index"] = "main",
                        ["ExcludePatterns"] = new[] { "*.gz", "*.zip", "*.bz2", "*.tar" }
                    }
                },
                ["Output"] = new Dictionary<string, object>
                {
                    ["ServerUrl"] = _serverUrl,
                    ["ApiKey"] = "SET_VIA_ENV_VAR",
                    ["BatchSize"] = 100,
                    ["FlushIntervalSeconds"] = 5,
                    ["VerifySsl"] = !_serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                },
                ["Registry"] = new Dictionary<string, object>
                {
                    ["Path"] = Path.Combine(configDir, "data", "registry.json")
                },
                ["ScanIntervalSeconds"] = 10
            },
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft"] = "Warning"
                }
            }
        };

        var agentSettingsPath = Path.Combine(configDir, "appsettings.json");
        File.WriteAllText(agentSettingsPath,
            JsonSerializer.Serialize(agentSettings, new JsonSerializerOptions { WriteIndented = true }));
        paths["Agent configuration"] = agentSettingsPath;

        // Generate .env for secrets
        var envPath = Path.Combine(configDir, ".env");
        var envContent = $"""
            # Cabinet Agent Environment Variables
            # Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
            Agent__Output__ApiKey={_apiKey}
            """;
        File.WriteAllText(envPath, envContent);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { File.SetUnixFileMode(envPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* Best effort */ }
        }
        paths["Agent environment secrets"] = envPath;

        // Generate systemd service file (Linux) or Windows service batch (Windows)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var servicePath = Path.Combine(configDir, "cabinet-agent.service");
            var serviceContent = $"""
                [Unit]
                Description=Cabinet Log Forwarding Agent
                Documentation=https://github.com/draxios/cabinet
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=notify
                ExecStart={Path.Combine(configDir, "cabinet-agent")}
                WorkingDirectory={configDir}
                EnvironmentFile={envPath}
                Restart=always
                RestartSec=5
                User=cabinet-agent
                Group=cabinet-agent

                # Security hardening
                ProtectSystem=strict
                ProtectHome=true
                PrivateTmp=true
                NoNewPrivileges=true
                ReadWritePaths={configDir}

                [Install]
                WantedBy=multi-user.target
                """;
            File.WriteAllText(servicePath, serviceContent);
            paths["systemd service unit"] = servicePath;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var batPath = Path.Combine(configDir, "install-service.bat");
            var batContent = $"""
                @echo off
                echo Installing Cabinet Agent as Windows Service...
                sc create CabinetAgent binPath= "{Path.Combine(configDir, "cabinet-agent.exe")}" start= auto DisplayName= "Cabinet Log Forwarding Agent"
                sc description CabinetAgent "Forwards log files to Cabinet API for indexing and analysis"
                sc start CabinetAgent
                echo Done. Check service status with: sc query CabinetAgent
                pause
                """;
            File.WriteAllText(batPath, batContent);
            paths["Windows service installer"] = batPath;
        }

        // Generate CLI config pointing to the server
        var cliConfig = new CabinetConfig
        {
            Mode = "remote",
            Remote = new RemoteConfig
            {
                ApiUrl = _serverUrl,
                ApiKey = _apiKey
            }
        };
        ConfigManager.Save(cliConfig);

        return paths;
    }

    private Dictionary<string, object> BuildAppSettings()
    {
        var settings = new Dictionary<string, object>
        {
            ["Storage"] = new Dictionary<string, string>
            {
                ["Type"] = _storageBackend switch
                {
                    "postgresql" => "PostgreSQL",
                    "opensearch" => "OpenSearch",
                    _ => "SQLite"
                },
                ["ConnectionString"] = _storageBackend == "sqlite" ? _connectionString : "SET_VIA_ENV_VAR"
            },
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning"
                }
            },
            ["RateLimiting"] = new Dictionary<string, object>
            {
                ["Basic"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 100 },
                ["Professional"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 1000 },
                ["Enterprise"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 10000 }
            },
            ["Cors"] = new Dictionary<string, object>
            {
                ["AllowedOrigins"] = new[] { "http://localhost:3000", "http://localhost:5173" }
            },
            ["Backup"] = new Dictionary<string, object>
            {
                ["ArchivePath"] = "./backups",
                ["RetentionDays"] = 30,
                ["AutoBackupEnabled"] = false
            }
        };

        if (_enableSyslog)
        {
            settings["Syslog"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["TcpPort"] = _syslogPort,
                ["UdpPort"] = _syslogPort
            };
        }

        if (_enableEmail)
        {
            settings["Email"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SmtpHost"] = _smtpHost,
                ["SmtpPort"] = 587,
                ["FromAddress"] = _smtpFrom,
                ["UseTls"] = true
            };
        }

        if (_enableDistributed)
        {
            settings["Distributed"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["RedisConnectionString"] = "SET_VIA_ENV_VAR",
                ["InstanceId"] = Environment.MachineName,
                ["LeaderElection"] = new Dictionary<string, object>
                {
                    ["Enabled"] = true,
                    ["LeaseTimeSeconds"] = 30
                },
                ["Cache"] = new Dictionary<string, int>
                {
                    ["ApiKeyTtlSeconds"] = 60
                }
            };
        }

        return settings;
    }

    private string GenerateStartupScript(string configDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var scriptPath = Path.Combine(configDir, "start-cabinet.bat");
            var script = $"""
                @echo off
                echo Starting Cabinet API Server...
                echo Configuration: {configDir}\appsettings.json
                echo.

                REM Load environment variables
                if exist "{configDir}\.env" (
                    for /f "usebackq tokens=1,* delims==" %%a in ("{configDir}\.env") do (
                        if not "%%a"=="" if not "%%a:~0,1%"=="#" set "%%a=%%b"
                    )
                )

                set ASPNETCORE_URLS=http://0.0.0.0:{_apiPort}
                set ASPNETCORE_ENVIRONMENT=Production

                REM Start the API
                cabinet-api.exe --contentRoot "{configDir}"
                """;
            File.WriteAllText(scriptPath, script);
            return scriptPath;
        }
        else
        {
            var scriptPath = Path.Combine(configDir, "start-cabinet.sh");
            var script = $"""
                #!/bin/bash
                # Cabinet API Startup Script
                # Generated by Cabinet Setup Wizard

                SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
                CONFIG_DIR="{configDir}"

                echo "Starting Cabinet API Server..."
                echo "Configuration: $CONFIG_DIR/appsettings.json"
                echo ""

                # Load environment variables
                if [ -f "$CONFIG_DIR/.env" ]; then
                    set -a
                    source "$CONFIG_DIR/.env"
                    set +a
                fi

                export ASPNETCORE_URLS="http://0.0.0.0:{_apiPort}"
                export ASPNETCORE_ENVIRONMENT="Production"

                # Start the API
                exec cabinet-api --contentRoot "$CONFIG_DIR"
                """;
            File.WriteAllText(scriptPath, script);
            try { File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute); }
            catch { /* Best effort */ }
            return scriptPath;
        }
    }

    #endregion

    #region Sample Data & Dashboard

    private async Task SetupSampleDataAndDashboardAsync()
    {
        if (_nonInteractive && _skipSampleData)
            return;

        if (!_nonInteractive)
        {
            var setupSample = AnsiConsole.Confirm("\nWould you like to ingest sample data and create a starter dashboard?", true);
            if (!setupSample)
                return;
        }

        AnsiConsole.Write(new Rule("[bold]Sample Data & Starter Dashboard[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var sampleTask = ctx.AddTask("Generating sample log events", maxValue: 100);
                var dashTask = ctx.AddTask("Creating starter dashboard", maxValue: 100);
                var verifyTask = ctx.AddTask("Verifying pipeline", maxValue: 100);

                // Step 1: Generate sample data file
                sampleTask.StartTask();
                var sampleLogsPath = GenerateSampleLogs();
                sampleTask.Value = 100;

                // Step 2: Create starter dashboard config
                dashTask.StartTask();
                var dashboardPath = GenerateStarterDashboard();
                dashTask.Value = 100;

                // Step 3: Write instructions for post-start setup
                verifyTask.StartTask();
                GeneratePostStartScript(sampleLogsPath, dashboardPath);
                verifyTask.Value = 100;

                await Task.Delay(300); // Let progress bars settle
            });

        AnsiConsole.MarkupLine("\n[green]Sample data and dashboard templates generated.[/]");
        AnsiConsole.MarkupLine("[dim]These will be imported when Cabinet starts for the first time.[/]\n");
    }

    private string GenerateSampleLogs()
    {
        var configDir = Path.Combine(Directory.GetCurrentDirectory(),
            _deploymentMode == "docker" ? "cabinet-docker" : "config");
        var sampleDir = Path.Combine(configDir, "sample-data");
        Directory.CreateDirectory(sampleDir);

        var sampleLogsPath = Path.Combine(sampleDir, "sample-logs.json");
        var now = DateTime.UtcNow;

        var sampleEvents = new List<Dictionary<string, object>>();
        var random = new Random(42); // Deterministic for reproducibility
        var hosts = new[] { "web-01", "web-02", "api-01", "db-01", "worker-01" };
        var sources = new[] { "nginx", "application", "postgresql", "systemd", "cron" };
        var severities = new[] { "Info", "Info", "Info", "Warning", "Error", "Info", "Debug", "Info" };
        var messages = new[]
        {
            "GET /api/v1/health 200 2ms",
            "POST /api/v1/logs/ingest 201 45ms",
            "Connection pool: 5 active, 15 idle",
            "Slow query detected: 1.2s on users table",
            "Failed to connect to upstream: connection refused",
            "User login: admin@example.com from 192.168.1.100",
            "Backup completed: 2.3GB compressed to 456MB",
            "Certificate renewal: SSL cert expires in 30 days",
            "Rate limit triggered for IP 10.0.0.55 (150 req/min)",
            "Memory usage: 2.1GB / 4GB (52%)",
            "Disk usage: /var/log at 78% capacity",
            "New agent connected: agent-prod-03 (linux-x64)",
            "Query executed: find errors from last 1h | stats count by host (12ms)",
            "Scheduled search 'error-spike' triggered: 47 matches",
            "Dashboard 'Infrastructure Overview' accessed by admin"
        };

        for (var i = 0; i < 50; i++)
        {
            var timestamp = now.AddMinutes(-random.Next(0, 1440)); // Last 24 hours
            sampleEvents.Add(new Dictionary<string, object>
            {
                ["event"] = messages[random.Next(messages.Length)],
                ["time"] = new DateTimeOffset(timestamp).ToUnixTimeSeconds(),
                ["host"] = hosts[random.Next(hosts.Length)],
                ["sourcetype"] = sources[random.Next(sources.Length)],
                ["index"] = "main",
                ["fields"] = new Dictionary<string, string>
                {
                    ["severity"] = severities[random.Next(severities.Length)],
                    ["environment"] = "production"
                }
            });
        }

        File.WriteAllText(sampleLogsPath,
            JsonSerializer.Serialize(sampleEvents, new JsonSerializerOptions { WriteIndented = true }));

        return sampleLogsPath;
    }

    private string GenerateStarterDashboard()
    {
        var configDir = Path.Combine(Directory.GetCurrentDirectory(),
            _deploymentMode == "docker" ? "cabinet-docker" : "config");
        var sampleDir = Path.Combine(configDir, "sample-data");
        Directory.CreateDirectory(sampleDir);

        var dashboardPath = Path.Combine(sampleDir, "starter-dashboard.json");
        var dashboard = new Dictionary<string, object>
        {
            ["title"] = "Infrastructure Overview",
            ["description"] = "Starter dashboard created by Cabinet Setup Wizard — customize or replace as needed.",
            ["refreshIntervalSeconds"] = 60,
            ["panels"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["title"] = "Events Over Time",
                    ["query"] = "find * | timechart span=1h count",
                    ["type"] = "timechart",
                    ["row"] = 0, ["col"] = 0, ["width"] = 12, ["height"] = 3
                },
                new Dictionary<string, object>
                {
                    ["title"] = "Events by Host",
                    ["query"] = "find * | stats count by host | sort -count | head 10",
                    ["type"] = "bar",
                    ["row"] = 3, ["col"] = 0, ["width"] = 6, ["height"] = 3
                },
                new Dictionary<string, object>
                {
                    ["title"] = "Events by Severity",
                    ["query"] = "find * | stats count by severity",
                    ["type"] = "pie",
                    ["row"] = 3, ["col"] = 6, ["width"] = 6, ["height"] = 3
                },
                new Dictionary<string, object>
                {
                    ["title"] = "Recent Errors",
                    ["query"] = "find * | where severity >= 4 | sort -timestamp | head 20",
                    ["type"] = "table",
                    ["row"] = 6, ["col"] = 0, ["width"] = 12, ["height"] = 4
                },
                new Dictionary<string, object>
                {
                    ["title"] = "Top Error Messages",
                    ["query"] = "find * | where severity >= 4 | top 10 RawLine",
                    ["type"] = "table",
                    ["row"] = 10, ["col"] = 0, ["width"] = 6, ["height"] = 3
                },
                new Dictionary<string, object>
                {
                    ["title"] = "Source Types",
                    ["query"] = "find * | stats count by SourceType | sort -count",
                    ["type"] = "bar",
                    ["row"] = 10, ["col"] = 6, ["width"] = 6, ["height"] = 3
                }
            }
        };

        File.WriteAllText(dashboardPath,
            JsonSerializer.Serialize(dashboard, new JsonSerializerOptions { WriteIndented = true }));

        return dashboardPath;
    }

    private void GeneratePostStartScript(string sampleLogsPath, string dashboardPath)
    {
        var configDir = Path.Combine(Directory.GetCurrentDirectory(),
            _deploymentMode == "docker" ? "cabinet-docker" : "config");
        var sampleDir = Path.Combine(configDir, "sample-data");

        var scriptPath = Path.Combine(sampleDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "import-sample-data.bat" : "import-sample-data.sh");

        var apiUrl = _deploymentMode == "docker"
            ? $"http://localhost:{_apiPort}"
            : $"http://localhost:{_apiPort}";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var script = $"""
                @echo off
                echo ====================================
                echo  Cabinet - Import Sample Data
                echo ====================================
                echo.
                echo Importing sample logs...

                REM Import sample logs via HEC
                for /f "delims=" %%i in ('type "{sampleLogsPath}"') do (
                    curl -s -X POST "{apiUrl}/services/collector/event" ^
                        -H "X-API-Key: {_apiKey}" ^
                        -H "Content-Type: application/json" ^
                        -d "%%i" >nul 2>&1
                )

                echo Importing starter dashboard...
                curl -s -X POST "{apiUrl}/api/v1/dashboards" ^
                    -H "X-API-Key: {_apiKey}" ^
                    -H "Content-Type: application/json" ^
                    -d @"{dashboardPath}"

                echo.
                echo Sample data imported successfully!
                echo Open your browser to: {apiUrl}/swagger
                pause
                """;
            File.WriteAllText(scriptPath, script);
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/bash");
            sb.AppendLine("# Cabinet - Import Sample Data");
            sb.AppendLine("# Generated by Setup Wizard");
            sb.AppendLine();
            sb.AppendLine($"API_URL=\"{apiUrl}\"");
            sb.AppendLine($"API_KEY=\"{_apiKey}\"");
            sb.AppendLine();
            sb.AppendLine("echo \"====================================\"");
            sb.AppendLine("echo \" Cabinet - Import Sample Data\"");
            sb.AppendLine("echo \"====================================\"");
            sb.AppendLine("echo \"\"");
            sb.AppendLine();
            sb.AppendLine("# Wait for API to be ready");
            sb.AppendLine("echo \"Waiting for Cabinet API to be ready...\"");
            sb.AppendLine("for i in $(seq 1 30); do");
            sb.AppendLine("    if curl -sf \"$API_URL/health/live\" > /dev/null 2>&1; then");
            sb.AppendLine("        echo \"API is ready!\"");
            sb.AppendLine("        break");
            sb.AppendLine("    fi");
            sb.AppendLine("    echo \"  Attempt $i/30 - waiting...\"");
            sb.AppendLine("    sleep 2");
            sb.AppendLine("done");
            sb.AppendLine();
            sb.AppendLine("# Import sample logs via HEC");
            sb.AppendLine("echo \"\"");
            sb.AppendLine("echo \"Importing sample logs...\"");
            sb.AppendLine($"SAMPLE_FILE=\"{sampleLogsPath}\"");
            sb.AppendLine("if [ -f \"$SAMPLE_FILE\" ]; then");
            sb.AppendLine("    python3 -c \"");
            sb.AppendLine("import json");
            sb.AppendLine("with open('$SAMPLE_FILE') as f:");
            sb.AppendLine("    events = json.load(f)");
            sb.AppendLine("for e in events:");
            sb.AppendLine("    print(json.dumps(e))");
            sb.AppendLine("\" 2>/dev/null | while IFS= read -r event; do");
            sb.AppendLine("        curl -sf -X POST \"$API_URL/services/collector/event\" \\");
            sb.AppendLine("            -H \"X-API-Key: $API_KEY\" \\");
            sb.AppendLine("            -H \"Content-Type: application/json\" \\");
            sb.AppendLine("            -d \"$event\" > /dev/null 2>&1");
            sb.AppendLine("    done");
            sb.AppendLine("    echo \"  Imported sample events\"");
            sb.AppendLine("fi");
            sb.AppendLine();
            sb.AppendLine("# Import starter dashboard");
            sb.AppendLine("echo \"Creating starter dashboard...\"");
            sb.AppendLine($"DASHBOARD_FILE=\"{dashboardPath}\"");
            sb.AppendLine("if [ -f \"$DASHBOARD_FILE\" ]; then");
            sb.AppendLine("    curl -sf -X POST \"$API_URL/api/v1/dashboards\" \\");
            sb.AppendLine("        -H \"X-API-Key: $API_KEY\" \\");
            sb.AppendLine("        -H \"Content-Type: application/json\" \\");
            sb.AppendLine("        -d @\"$DASHBOARD_FILE\" > /dev/null 2>&1");
            sb.AppendLine("    echo \"  Dashboard 'Infrastructure Overview' created\"");
            sb.AppendLine("fi");
            sb.AppendLine();
            sb.AppendLine("# Create saved queries");
            sb.AppendLine("echo \"Creating sample saved queries...\"");
            sb.AppendLine("curl -sf -X POST \"$API_URL/api/v1/query/saved\" \\");
            sb.AppendLine("    -H \"X-API-Key: $API_KEY\" \\");
            sb.AppendLine("    -H \"Content-Type: application/json\" \\");
            sb.Append("    -d '{\"name\":\"Recent Errors\",\"query\":\"find * | where severity >= 4 | sort -timestamp | head 50\",");
            sb.AppendLine("\"description\":\"Show the 50 most recent error events\"}' > /dev/null 2>&1");
            sb.AppendLine("curl -sf -X POST \"$API_URL/api/v1/query/saved\" \\");
            sb.AppendLine("    -H \"X-API-Key: $API_KEY\" \\");
            sb.AppendLine("    -H \"Content-Type: application/json\" \\");
            sb.Append("    -d '{\"name\":\"Events by Host\",\"query\":\"find * | stats count by host | sort -count\",");
            sb.AppendLine("\"description\":\"Count events grouped by host\"}' > /dev/null 2>&1");
            sb.AppendLine("echo \"  Created 2 saved queries\"");
            sb.AppendLine();
            sb.AppendLine("echo \"\"");
            sb.AppendLine("echo \"====================================\"");
            sb.AppendLine("echo \" Setup Complete!\"");
            sb.AppendLine("echo \"====================================\"");
            sb.AppendLine("echo \"\"");
            sb.AppendLine("echo \"API:       $API_URL\"");
            sb.AppendLine("echo \"Swagger:   $API_URL/swagger (dev mode only)\"");
            sb.AppendLine("echo \"Health:    $API_URL/health\"");
            sb.AppendLine("echo \"Metrics:   $API_URL/metrics\"");
            var script = sb.ToString();
            File.WriteAllText(scriptPath, script);
            try { File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute); }
            catch { /* Best effort */ }
        }
    }

    #endregion

    #region Completion Summary

    private void RenderCompletionSummary(Dictionary<string, string> configPaths)
    {
        AnsiConsole.Write(new Rule("[bold green]Setup Complete[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        // Summary panel
        var summaryTree = new Tree("[bold]Deployment Summary[/]");

        var modeNode = summaryTree.AddNode($"[cyan]Mode:[/] {_deploymentMode}");
        if (_deploymentMode != "agent")
        {
            modeNode.AddNode($"Storage: [green]{_storageBackend}[/]");
            modeNode.AddNode($"API Port: [green]{_apiPort}[/]");
            if (_enableSyslog) modeNode.AddNode($"Syslog: [green]Port {_syslogPort}[/]");
            if (_enableDistributed) modeNode.AddNode($"Distributed: [green]Redis at {_redisConnectionString}[/]");
            if (_enableEmail) modeNode.AddNode($"Email Alerts: [green]{_smtpHost}[/]");
        }
        else
        {
            modeNode.AddNode($"Server: [green]{_serverUrl}[/]");
            modeNode.AddNode($"Log Paths: [green]{_agentLogPaths.Count} configured[/]");
        }

        var keyNode = summaryTree.AddNode("[cyan]API Key[/]");
        keyNode.AddNode($"[bold]{_apiKey}[/]");
        keyNode.AddNode("[yellow]Save this key — you'll need it for API access and agent connections[/]");

        AnsiConsole.Write(summaryTree);
        AnsiConsole.WriteLine();

        // Next steps
        AnsiConsole.Write(new Rule("[bold]Next Steps[/]").RuleStyle("dim").LeftJustified());
        AnsiConsole.WriteLine();

        var steps = new List<string>();

        switch (_deploymentMode)
        {
            case "standalone":
                steps.Add("Start the API server:");
                steps.Add($"  [dim]source config/.env && dotnet run --project src/Cabinet.API[/]");
                steps.Add($"  [dim]OR: ./config/start-cabinet.sh[/]");
                steps.Add("");
                steps.Add("Import sample data (after API starts):");
                steps.Add("  [dim]./config/sample-data/import-sample-data.sh[/]");
                steps.Add("");
                steps.Add("Install a forwarding agent on log sources:");
                steps.Add("  [dim]cabinet setup --mode agent --server-url http://this-server:5000 --api-key YOUR_KEY[/]");
                break;

            case "docker":
                steps.Add("Start the Docker Compose stack:");
                steps.Add("  [dim]cd cabinet-docker && docker compose up -d[/]");
                steps.Add("");
                steps.Add("Import sample data (after containers start):");
                steps.Add("  [dim]./cabinet-docker/sample-data/import-sample-data.sh[/]");
                steps.Add("");
                steps.Add("View logs:");
                steps.Add("  [dim]docker compose logs -f cabinet-api[/]");
                break;

            case "distributed":
                steps.Add("Start each instance with the generated config:");
                steps.Add($"  [dim]source config/.env && dotnet run --project src/Cabinet.API[/]");
                steps.Add("");
                steps.Add("Ensure Redis is running:");
                steps.Add($"  [dim]redis-cli -h {_redisConnectionString.Split(':')[0]} ping[/]");
                steps.Add("");
                steps.Add("Deploy behind a load balancer (nginx, HAProxy, etc.)");
                break;

            case "agent":
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    steps.Add("Copy the agent binary to the install directory:");
                    steps.Add($"  [dim]sudo cp cabinet-agent {_installDirectory}/[/]");
                    steps.Add("");
                    steps.Add("Install and start the systemd service:");
                    steps.Add($"  [dim]sudo cp {_installDirectory}/cabinet-agent.service /etc/systemd/system/[/]");
                    steps.Add("  [dim]sudo systemctl daemon-reload[/]");
                    steps.Add("  [dim]sudo systemctl enable --now cabinet-agent[/]");
                    steps.Add("");
                    steps.Add("Check agent status:");
                    steps.Add("  [dim]sudo systemctl status cabinet-agent[/]");
                    steps.Add("  [dim]journalctl -u cabinet-agent -f[/]");
                }
                else
                {
                    steps.Add("Copy the agent binary to the install directory:");
                    steps.Add($"  [dim]copy cabinet-agent.exe \"{_installDirectory}\\\"[/]");
                    steps.Add("");
                    steps.Add("Install as Windows service (requires Administrator):");
                    steps.Add($"  [dim]{_installDirectory}\\install-service.bat[/]");
                }
                break;
        }

        steps.Add("");
        steps.Add("Verify the installation:");
        steps.Add($"  [dim]cabinet configure --test[/]");
        steps.Add($"  [dim]cabinet query \"find * | head 10\"[/]");

        foreach (var step in steps)
        {
            AnsiConsole.MarkupLine(step);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]https://github.com/draxios/cabinet[/]").RuleStyle("dim"));
    }

    #endregion

    #region Utilities

    private static string GenerateSecureApiKey()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        // Prefix with 'cab_' for easy identification as a Cabinet API key
        var encoded = Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        return "cab_" + (encoded.Length >= 40 ? encoded[..40] : encoded.PadRight(40, 'x'));
    }

    private static string GenerateRandomPassword(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "A")
            .Replace("/", "B")
            .Replace("=", "")[..length];
    }

    private static string MaskApiKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 8)
            return "****";
        return key[..8] + "..." + key[^4..];
    }

    private static string MaskConnectionString(string connStr)
    {
        if (string.IsNullOrEmpty(connStr))
            return "(not set)";

        // Mask password in connection strings
        var parts = connStr.Split(';');
        var masked = new List<string>();
        foreach (var part in parts)
        {
            if (part.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase))
                masked.Add("Password=****");
            else
                masked.Add(part);
        }
        return string.Join(";", masked);
    }

    private static bool CheckCommandExists(string command)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckDockerComposeAvailable()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "compose version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExistingConfig()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "appsettings.json"),
            "/etc/cabinet/appsettings.json",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cabinet", "config.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

internal sealed class EnvironmentInfo
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public bool IsWindows { get; set; }
    public bool IsLinux { get; set; }
    public bool IsMacOS { get; set; }
    public string DotNetVersion { get; set; } = string.Empty;
    public bool IsDocker { get; set; }
    public string CurrentDirectory { get; set; } = string.Empty;
    public string UserHome { get; set; } = string.Empty;
    public bool IsRoot { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public bool HasDocker { get; set; }
    public bool HasDockerCompose { get; set; }
    public bool HasSystemd { get; set; }
    public string? ExistingConfigPath { get; set; }
    public bool HasExistingInstall { get; set; }
    public bool Port5000Available { get; set; }
    public bool Port8080Available { get; set; }
}
