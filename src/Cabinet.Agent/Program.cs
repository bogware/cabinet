using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Monitoring;

namespace Cabinet.Agent;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Handle --version flag
        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            Console.WriteLine($"cabinet-agent {GetVersion()}");
            return 0;
        }

        if (args.Length > 0 && args[0] is "import")
        {
            return await Importing.ImportCommand.RunAsync(args[1..]);
        }

        // Handle --validate flag (check config and exit)
        var validate = args.Length > 0 && args[0] is "--validate";

        var builder = Host.CreateApplicationBuilder(args);

        // Bind configuration
        var agentConfig = new AgentConfiguration();
        builder.Configuration.GetSection("Agent").Bind(agentConfig);

        // Validate configuration
        var errors = ValidateConfiguration(agentConfig);
        if (errors.Count > 0)
        {
            Console.Error.WriteLine("Configuration errors:");
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        if (validate)
        {
            Console.WriteLine("Configuration is valid.");
            Console.WriteLine($"  Inputs: {agentConfig.Inputs.Count}");
            var totalPaths = agentConfig.Inputs.Sum(i => i.Paths.Count);
            Console.WriteLine($"  Path patterns: {totalPaths}");
            Console.WriteLine($"  Output: {agentConfig.Output.ServerUrl}");
            Console.WriteLine($"  Batch size: {agentConfig.Output.BatchSize}");
            Console.WriteLine($"  Registry: {agentConfig.Registry.Path}");
            return 0;
        }

        // Register services
        builder.Services.AddSingleton(agentConfig);

        // File state registry
        builder.Services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FileStateRegistry>>();
            return new FileStateRegistry(
                agentConfig.Registry.Path,
                agentConfig.Registry.SaveIntervalSeconds,
                logger,
                agentConfig.MaxLineBytes);
        });

        // HTTP client for forwarding (with SSL verification toggle)
        builder.Services.AddSingleton(sp =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            handler.SslOptions.RemoteCertificateValidationCallback =
                Cabinet.Agent.Forwarding.CertificatePinning.Create(agentConfig.Output.CertificateSha256, agentConfig.Output.VerifySsl);

            return new HttpClient(handler);
        });

        // Log forwarder
        builder.Services.AddSingleton(sp =>
        {
            var httpClient = sp.GetRequiredService<HttpClient>();
            var logger = sp.GetRequiredService<ILogger<LogForwarder>>();
            return new LogForwarder(agentConfig.Output, httpClient, logger);
        });

        // File monitor (background service)
        builder.Services.AddHostedService(sp =>
        {
            var registry = sp.GetRequiredService<FileStateRegistry>();
            var forwarder = sp.GetRequiredService<LogForwarder>();
            var logger = sp.GetRequiredService<ILogger<FileMonitorService>>();
            return new FileMonitorService(agentConfig, registry, forwarder, logger);
        });

        // The journal and Windows event logs, each checkpointed in its own file next to the registry
        builder.Services.AddSingleton(sp => new Cabinet.Agent.Monitoring.InputCheckpoints(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(agentConfig.Registry.Path)) ?? ".", "checkpoints.json"),
            sp.GetRequiredService<ILogger<Cabinet.Agent.Monitoring.InputCheckpoints>>()));
        foreach (var input in agentConfig.Inputs.Where(i => i.Type.Equals("journald", StringComparison.OrdinalIgnoreCase)))
        {
            builder.Services.AddSingleton<IHostedService>(sp => new Cabinet.Agent.Monitoring.JournaldInput(
                input, agentConfig, sp.GetRequiredService<LogForwarder>(), sp.GetRequiredService<Cabinet.Agent.Monitoring.InputCheckpoints>(),
                sp.GetRequiredService<ILogger<Cabinet.Agent.Monitoring.JournaldInput>>()));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var input in agentConfig.Inputs.Where(i => i.Type.Equals("wineventlog", StringComparison.OrdinalIgnoreCase)))
            {
                AddWindowsEventLog(builder.Services, input, agentConfig);
            }
        }

        // Keeps the agent at the server's version
        builder.Services.AddHostedService(sp => new Cabinet.Agent.Updating.AgentUpdater(
            sp.GetRequiredService<HttpClient>(),
            agentConfig,
            sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<ILogger<Cabinet.Agent.Updating.AgentUpdater>>()));

        // Stopping delivers what's queued, so allow for that plus some margin
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(agentConfig.Output.ShutdownFlushSeconds + 15));

        // Support systemd on Linux and Windows Service
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "CabinetAgent";
        });

        var host = builder.Build();

        var appLogger = host.Services.GetRequiredService<ILogger<FileMonitorService>>();
        appLogger.LogInformation("Cabinet Agent {Version} starting", GetVersion());

        await host.RunAsync();
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsEventLog(IServiceCollection services, InputConfiguration input, AgentConfiguration agentConfig) =>
        services.AddSingleton<IHostedService>(sp => new Cabinet.Agent.Monitoring.WindowsEventLogInput(
            input, agentConfig, sp.GetRequiredService<LogForwarder>(), sp.GetRequiredService<Cabinet.Agent.Monitoring.InputCheckpoints>(),
            sp.GetRequiredService<ILogger<Cabinet.Agent.Monitoring.WindowsEventLogInput>>()));

    private static List<string> ValidateConfiguration(AgentConfiguration config)
    {
        var errors = new List<string>();

        if (config.Inputs.Count == 0)
        {
            errors.Add("No inputs configured. Add at least one input with file paths to monitor.");
        }

        foreach (var input in config.Inputs)
        {
            if (input.Type.ToLowerInvariant() is not ("file" or "kubernetes" or "journald" or "wineventlog"))
            {
                errors.Add($"Input type '{input.Type}' isn't one of file, kubernetes, journald or wineventlog.");
            }
            else if (input.IsKubernetes && input.Paths.Count == 0)
            {
                // Where the kubelet links every container's log
                input.Paths.Add("/var/log/containers/*.log");
            }
            else if (input.IsFileInput && input.Paths.Count == 0)
            {
                errors.Add("A file input has no paths configured.");
            }
            else if (input.Type.Equals("journald", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsLinux())
            {
                errors.Add("journald inputs only run on Linux.");
            }
            else if (input.Type.Equals("wineventlog", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsWindows())
            {
                errors.Add("wineventlog inputs only run on Windows.");
            }
        }

        if (string.IsNullOrWhiteSpace(config.Output.ServerUrl))
        {
            errors.Add("Output.ServerUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(config.Output.ApiKey))
        {
            errors.Add("Output.ApiKey is required. Set Agent:Output:ApiKey or Agent__Output__ApiKey environment variable.");
        }

        if (config.Output.BatchSize < 1 || config.Output.BatchSize > 10000)
        {
            errors.Add("Output.BatchSize must be between 1 and 10000.");
        }

        if (config.Output.FlushIntervalSeconds < 1 || config.Output.FlushIntervalSeconds > 300)
        {
            errors.Add("Output.FlushIntervalSeconds must be between 1 and 300.");
        }

        if (config.ScanIntervalSeconds < 1 || config.ScanIntervalSeconds > 3600)
        {
            errors.Add("ScanIntervalSeconds must be between 1 and 3600.");
        }

        return errors;
    }

    private static string GetVersion()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "0.0.1";
    }
}
