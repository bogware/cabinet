#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using Cabinet.Core.Models;
using Cabinet.Core.Interfaces;
using Cabinet.Database.Abstractions;
using Cabinet.Parsers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Command to centralize logs from various sources into a database
/// </summary>
public class CentralizeCommand : Command
{
    public CentralizeCommand() : base("centralize", "Centralize logs from various sources into a database")
    {
        var sourceArgument = new Argument<string[]>("sources", "Log sources (files, directories, or patterns)")
        {
            Arity = ArgumentArity.OneOrMore
        };

        var providerOption = new Option<string>(
            "--provider",
            () => "sqlite",
            "Database provider (sqlite, postgresql, mysql, dynamodb)");

        var connectionOption = new Option<string?>(
            "--connection",
            "Database connection string");

        var batchSizeOption = new Option<int>(
            "--batch-size",
            () => 1000,
            "Batch size for inserts");

        var deleteOption = new Option<bool>(
            "--delete-source",
            "Delete source files after successful import");

        var continuousOption = new Option<bool>(
            "--continuous",
            "Continuously monitor sources for new logs");

        var intervalOption = new Option<int>(
            "--interval",
            () => 60,
            "Check interval in seconds for continuous mode");

        AddArgument(sourceArgument);
        AddOption(providerOption);
        AddOption(connectionOption);
        AddOption(batchSizeOption);
        AddOption(deleteOption);
        AddOption(continuousOption);
        AddOption(intervalOption);

        this.SetHandler(async (context) =>
        {
            var sources = context.ParseResult.GetValueForArgument(sourceArgument) ?? Array.Empty<string>();
            var provider = context.ParseResult.GetValueForOption(providerOption) ?? "postgresql";
            var connection = context.ParseResult.GetValueForOption(connectionOption);
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var deleteAfter = context.ParseResult.GetValueForOption(deleteOption);
            var continuous = context.ParseResult.GetValueForOption(continuousOption);
            var interval = context.ParseResult.GetValueForOption(intervalOption);
            var host = context.BindingContext.GetService<IHost>();

            if (host == null)
            {
                Console.WriteLine("Error: Unable to access host services");
                context.ExitCode = 1;
                return;
            }

            context.ExitCode = await HandleCommand(sources, provider, connection, batchSize, deleteAfter, continuous, interval, host);
        });
    }

    private async Task<int> HandleCommand(
        string[] sources,
        string provider,
        string? connectionString,
        int batchSize,
        bool deleteSource,
        bool continuous,
        int interval,
        IHost host)
    {
        var logger = host.Services.GetRequiredService<ILogger<CentralizeCommand>>();
        var databaseProvider = host.Services.GetRequiredService<ILogDatabaseProvider>();
        var parserProvider = host.Services.GetRequiredService<ILogParserProvider>();

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Initializing database...", async ctx =>
                {
                    await databaseProvider.InitializeAsync();
                });

            if (continuous)
            {
                await RunContinuousMode(sources, databaseProvider, parserProvider, batchSize, deleteSource, interval, logger);
            }
            else
            {
                await RunOnceMode(sources, databaseProvider, parserProvider, batchSize, deleteSource, logger);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private async Task RunOnceMode(
        string[] sources,
        ILogDatabaseProvider databaseProvider,
        ILogParserProvider parserProvider,
        int batchSize,
        bool deleteSource,
        ILogger logger)
    {
        var files = GatherFiles(sources);
        var totalFiles = files.Count;
        var processedFiles = 0;
        var totalEvents = 0;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Centralizing logs[/]", maxValue: totalFiles);

                foreach (var file in files)
                {
                    task.Description = $"[green]Processing {Path.GetFileName(file)}[/]";

                    try
                    {
                        var events = await ProcessFile(file, parserProvider, logger);
                        if (events.Any())
                        {
                            var inserted = await databaseProvider.InsertBatchAsync(events);
                            totalEvents += inserted;

                            if (deleteSource)
                            {
                                File.Delete(file);
                                logger.LogInformation("Deleted source file: {File}", file);
                            }
                        }

                        processedFiles++;
                        task.Increment(1);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process file: {File}", file);
                    }
                }
            });

        AnsiConsole.MarkupLine($"[green]✓[/] Centralized [cyan]{totalEvents}[/] events from [cyan]{processedFiles}[/] files");
    }

    private async Task RunContinuousMode(
        string[] sources,
        ILogDatabaseProvider databaseProvider,
        ILogParserProvider parserProvider,
        int batchSize,
        bool deleteSource,
        int interval,
        ILogger logger)
    {
        AnsiConsole.MarkupLine("[yellow]Running in continuous mode. Press Ctrl+C to stop.[/]");

        var processedFiles = new HashSet<string>();
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.Token.IsCancellationRequested)
        {
            var files = GatherFiles(sources).Where(f => !processedFiles.Contains(f)).ToList();

            if (files.Any())
            {
                AnsiConsole.MarkupLine($"[cyan]Found {files.Count} new files to process[/]");

                foreach (var file in files)
                {
                    try
                    {
                        var events = await ProcessFile(file, parserProvider, logger);
                        if (events.Any())
                        {
                            var inserted = await databaseProvider.InsertBatchAsync(events);
                            AnsiConsole.MarkupLine($"[green]✓[/] Inserted [cyan]{inserted}[/] events from {Path.GetFileName(file)}");

                            if (deleteSource)
                            {
                                File.Delete(file);
                                logger.LogInformation("Deleted source file: {File}", file);
                            }
                        }

                        processedFiles.Add(file);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process file: {File}", file);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), cts.Token);
        }

        AnsiConsole.MarkupLine("[yellow]Continuous mode stopped[/]");
    }

    private List<string> GatherFiles(string[] sources)
    {
        var files = new List<string>();

        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                files.Add(source);
            }
            else if (Directory.Exists(source))
            {
                files.AddRange(Directory.GetFiles(source, "*.log", SearchOption.AllDirectories));
            }
            else if (source.Contains('*') || source.Contains('?'))
            {
                // Handle glob patterns
                var directory = Path.GetDirectoryName(source) ?? ".";
                var pattern = Path.GetFileName(source);
                files.AddRange(Directory.GetFiles(directory, pattern, SearchOption.AllDirectories));
            }
        }

        return files.Distinct().ToList();
    }

    private async Task<List<LogEvent>> ProcessFile(string file, ILogParserProvider parserProvider, ILogger logger)
    {
        logger.LogDebug("Processing file: {File}", file);

        var events = new List<LogEvent>();

        try
        {
            var lines = await File.ReadAllLinesAsync(file);
            var parser = parserProvider.GetParser(lines.FirstOrDefault() ?? string.Empty);

            if (parser == null)
            {
                logger.LogWarning("No suitable parser found for file: {File}", file);
                return events;
            }

            await foreach (var evt in parser.ParseBatchAsync(lines))
            {
                if (evt != null)
                {
                    events.Add(evt);
                }
            }

            logger.LogDebug("Extracted {Count} events from {File}", events.Count, file);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing file: {File}", file);
        }

        return events;
    }
}