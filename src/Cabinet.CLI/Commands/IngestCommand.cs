using System.CommandLine;
using System.Diagnostics;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class IngestCommand : Command
{
    public IngestCommand() : base("ingest", "Ingest logs into the index for fast querying")
    {
        var pathArgument = new Argument<string>("path", "Path to log file or directory");
        var profileOption = new Option<string>("--profile", "Parser profile to use (steamcmd, sourcemod, l4d2, generic)");
        var recursiveOption = new Option<bool>("--recursive", () => true, "Recursively scan directories");
        var forceOption = new Option<bool>("--force", "Force re-ingestion of already processed files");
        var serverOption = new Option<string>("--server", "Server ID to assign to logs");
        var followOption = new Option<bool>("--follow", "Continue monitoring for new log entries");

        AddArgument(pathArgument);
        AddOption(profileOption);
        AddOption(recursiveOption);
        AddOption(forceOption);
        AddOption(serverOption);
        AddOption(followOption);

        this.SetHandler(HandleCommand, pathArgument, profileOption, recursiveOption, forceOption, serverOption, followOption);
    }

    private async Task<int> HandleCommand(
        string path,
        string? profile,
        bool recursive,
        bool force,
        string? serverId,
        bool follow)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddCabinetCore();
                services.AddCabinetParsers();
                services.AddCabinetStorage();
            })
            .Build();

        using (host)
        {
            await host.StartAsync();

            var ingestionEngine = host.Services.GetRequiredService<IIngestionEngine>();

            var options = new IngestionOptions
            {
                Profile = profile,
                Recursive = recursive,
                Force = force,
                Host = serverId,
                Incremental = !force
            };

            var stopwatch = Stopwatch.StartNew();

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn()
                })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Ingesting logs[/]", maxValue: 100);

                    var progress = new Progress<IngestionProgress>(p =>
                    {
                        task.Value = p.PercentComplete;
                        task.Description = $"[green]Ingesting:[/] {p.CurrentFile} ({p.FilesCompleted}/{p.TotalFiles} files)";
                    });

                    IngestionResult result;
                    if (Directory.Exists(path))
                    {
                        result = await ingestionEngine.IngestDirectoryAsync(path, options, progress);
                    }
                    else if (File.Exists(path))
                    {
                        result = await ingestionEngine.IngestFileAsync(path, options, progress);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {path}");
                        return;
                    }

                    task.Value = 100;
                    stopwatch.Stop();

                    // Display results
                    var table = new Table();
                    table.AddColumn("Metric");
                    table.AddColumn("Value");

                    table.AddRow("Files Processed", result.FilesProcessed.ToString("N0"));
                    table.AddRow("Lines Processed", result.LinesProcessed.ToString("N0"));
                    table.AddRow("Events Stored", result.EventsStored.ToString("N0"));
                    table.AddRow("Bytes Processed", FormatBytes(result.BytesProcessed));
                    table.AddRow("Duration", stopwatch.Elapsed.ToString(@"mm\:ss\.fff"));
                    table.AddRow("Lines/Second", result.LinesPerSecond.ToString("N0"));
                    table.AddRow("MB/Second", result.MBPerSecond.ToString("N2"));

                    if (result.Errors.Any())
                    {
                        table.AddRow("[red]Errors[/]", result.Errors.Count.ToString("N0"));
                    }

                    AnsiConsole.Write(table);

                    // Show errors if any
                    if (result.Errors.Any())
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Ingestion completed with errors:[/]");
                        foreach (var error in result.Errors.Take(10))
                        {
                            AnsiConsole.MarkupLine($"  [red]•[/] {error.File}:{error.LineNumber} - {error.Error}");
                        }
                        if (result.Errors.Count > 10)
                        {
                            AnsiConsole.MarkupLine($"  [dim]... and {result.Errors.Count - 10} more errors[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("\n[green]✓[/] Ingestion completed successfully!");
                    }

                    // Follow mode
                    if (follow && File.Exists(path))
                    {
                        AnsiConsole.MarkupLine("\n[yellow]Following file for new entries... Press Ctrl+C to stop[/]");

                        var streamOptions = new StreamOptions
                        {
                            Follow = true,
                            FromEnd = true
                        };

                        await foreach (var evt in ingestionEngine.StreamFileAsync(path, streamOptions))
                        {
                            AnsiConsole.MarkupLine($"[green]→[/] {evt.Timestamp:HH:mm:ss} [{evt.Severity}] {Markup.Escape(evt.RawLine.Substring(0, Math.Min(evt.RawLine.Length, 120)))}");
                        }
                    }
                });

            await host.StopAsync();
        }

        return 0;
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}