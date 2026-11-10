using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Monitoring;

namespace Cabinet.Agent.Importing;

/// <summary>
/// <c>cabinet-agent import</c>: sends files that already exist, once. With no path, it imports the
/// history the running agent skips (files older than its inputs' IgnoreOlderThanHours). With a
/// folder, file or glob, it imports that, from any machine.
/// </summary>
public static class ImportCommand
{
    public const string Usage = """
        Usage:
          cabinet-agent import                     Import the history of every configured input:
                                                   the files the running agent leaves out as too old.
          cabinet-agent import <folder|file|glob>  Import those files.

        Options:
          --sourcetype <name>    How the server parses the lines (L4D2, SourceMod, syslog, json, ...).
                                 Default: generic. Ignored without a path.
          --index <name>         Index to store them in. Default: main. Ignored without a path.
          --host <name>          Host name to give the events. Default: the agent's, or this machine's.
          --tz <zone>            Time zone the files' times are in, such as America/Chicago.
                                 Default: the agent's, or this machine's.
          --server <url>         Cabinet server. Default: the agent's Output:ServerUrl.
          --certificate-sha256 <hex>  Accept only this server certificate. Default: the agent's.
          --rate <events/s>      Most events per second. Default: 2000. 0 for no limit.
          --top-only             With a folder, leave out its subfolders.
          --dry-run              List what would be imported, and send nothing.

        The API key comes from the agent's settings (Agent:Output:ApiKey) or the CABINET_API_KEY
        environment variable, never the command line, where other users could see it.

        Importing the same files again is safe: each event's ID comes from the host, the file's name
        and first line, and the line's position, so the server stores each line once. Files the running
        agent reads are left out, so nothing is stored under two IDs.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "help"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        Dictionary<string, string> options;
        List<string> paths;
        try
        {
            (options, paths) = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var config = new AgentConfiguration();
        configuration.GetSection("Agent").Bind(config);

        if (options.TryGetValue("--server", out var server)) config.Output.ServerUrl = server;
        if (options.TryGetValue("--certificate-sha256", out var pin)) config.Output.CertificateSha256 = pin;
        if (Environment.GetEnvironmentVariable("CABINET_API_KEY") is { Length: > 0 } key) config.Output.ApiKey = key;

        if (string.IsNullOrWhiteSpace(config.Output.ServerUrl) || string.IsNullOrWhiteSpace(config.Output.ApiKey))
        {
            Console.Error.WriteLine("Needs a server and an API key: run it where the agent is installed, or give --server and set CABINET_API_KEY.");
            return 1;
        }

        List<ImportSource> sources;
        if (paths.Count == 0)
        {
            if (config.Inputs.Count == 0)
            {
                Console.Error.WriteLine("No inputs are configured. Give a folder, file or glob to import.");
                return 1;
            }

            if (config.Inputs.All(input => input.IgnoreOlderThanHours <= 0))
            {
                Console.Error.WriteLine("The agent already reads every file its inputs match (IgnoreOlderThanHours is 0), so there's no history to import.");
                return 1;
            }

            sources = [.. config.Inputs
                .Where(input => input.IgnoreOlderThanHours > 0)
                .SelectMany(input => input.Paths.Select(path => new ImportSource
                {
                    Path = path,
                    SourceType = input.SourceType,
                    Index = input.Index,
                    Recursive = input.Recursive,
                    ExcludePatterns = input.ExcludePatterns,
                }))];
        }
        else
        {
            sources = [.. paths.Select(path => new ImportSource
            {
                Path = Path.GetFullPath(path),
                SourceType = options.GetValueOrDefault("--sourcetype", "generic"),
                Index = options.GetValueOrDefault("--index", "main"),
                Recursive = !options.ContainsKey("--top-only"),
            })];
        }

        int rate;
        if (!options.TryGetValue("--rate", out var rateText))
        {
            rate = 2000;
        }
        else if (!int.TryParse(rateText, out rate) || rate < 0)
        {
            Console.Error.WriteLine("--rate must be a whole number of events per second, or 0.");
            return 1;
        }

        var registryPath = Path.IsPathRooted(config.Registry.Path)
            ? config.Registry.Path
            : Path.Combine(AppContext.BaseDirectory, config.Registry.Path);
        var importOptions = new ImportOptions
        {
            Sources = sources,
            Hostname = options.GetValueOrDefault("--host") ?? (string.IsNullOrEmpty(config.Hostname) ? Environment.MachineName : config.Hostname),
            TimeZone = options.GetValueOrDefault("--tz") ?? (string.IsNullOrEmpty(config.TimeZone) ? FileMonitorService.LocalTimeZoneName() : config.TimeZone),
            SkipPaths = FolderImporter.AgentFiles(config, registryPath, DateTime.UtcNow),
            MaxEventsPerSecond = rate,
            MaxLineBytes = config.MaxLineBytes,
        };

        var (files, skipped) = FolderImporter.Plan(importOptions);
        var totalBytes = files.Sum(f => f.Length);
        Console.WriteLine($"{files.Count:N0} files, {totalBytes / 1024.0 / 1024:N1} MB, to {config.Output.ServerUrl} as host {importOptions.Hostname} ({importOptions.TimeZone})");
        if (skipped > 0)
        {
            Console.WriteLine($"Leaving out {skipped:N0} files the running agent reads.");
        }

        if (options.ContainsKey("--dry-run"))
        {
            foreach (var group in files.GroupBy(f => (f.Source.Index, f.Source.SourceType)))
            {
                Console.WriteLine($"  {group.Key.Index} / {group.Key.SourceType}: {group.Count():N0} files, {group.Sum(f => f.Length) / 1024.0 / 1024:N1} MB, {group.First().Path} .. {group.Last().Path}");
            }

            return 0;
        }

        if (files.Count == 0)
        {
            return 0;
        }

        // Its own spool, so nothing it leaves behind mixes with the running agent's
        config.Output.SpillDirectory = Path.Combine(Path.GetTempPath(), $"cabinet-import-{Environment.ProcessId}");
        config.Output.BatchSize = Math.Max(config.Output.BatchSize, 500);

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.SslOptions.RemoteCertificateValidationCallback = CertificatePinning.Create(config.Output.CertificateSha256, config.Output.VerifySsl);
        using var httpClient = new HttpClient(handler);
        await using var forwarder = new LogForwarder(config.Output, httpClient, loggerFactory.CreateLogger<LogForwarder>());

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var importer = new FolderImporter(forwarder, loggerFactory.CreateLogger<FolderImporter>());
        var lastShown = DateTime.MinValue;
        var progress = new Progress<ImportProgress>(p =>
        {
            if (p.CurrentFile != null && DateTime.UtcNow - lastShown < TimeSpan.FromSeconds(10))
            {
                return;
            }

            lastShown = DateTime.UtcNow;
            var share = p.BytesTotal == 0 ? 100 : 100.0 * p.BytesRead / p.BytesTotal;
            Console.WriteLine($"  {share,5:N1}%  {p.FilesDone:N0} of {p.FilesTotal:N0} files, {p.EventsRead:N0} events");
        });

        try
        {
            var result = await importer.ImportAsync(importOptions, progress, stop.Token);
            Console.WriteLine($"Done in {result.Took:hh\\:mm\\:ss}: {result.FilesImported:N0} files, {result.EventsSent:N0} of {result.EventsRead:N0} events delivered.");
            if (result.EventsNotDelivered > 0)
            {
                Console.WriteLine($"{result.EventsNotDelivered:N0} events weren't delivered. Run the same import again; what's already stored is skipped.");
                return 2;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped. Run the same import again to finish; what's already stored is skipped.");
            return 3;
        }
        finally
        {
            try
            {
                Directory.Delete(config.Output.SpillDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was spooled
            }
        }
    }

    private static readonly HashSet<string> Flags = ["--top-only", "--dry-run"];

    private static readonly HashSet<string> Valued = ["--sourcetype", "--index", "--host", "--tz", "--server", "--certificate-sha256", "--rate"];

    private static (Dictionary<string, string> Options, List<string> Paths) Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (Flags.Contains(arg))
            {
                options[arg] = "true";
            }
            else if (Valued.Contains(arg))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{arg} needs a value.");
                }

                options[arg] = args[++i];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown option {arg}.");
            }
            else
            {
                paths.Add(arg);
            }
        }

        return (options, paths);
    }
}
