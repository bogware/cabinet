#pragma warning disable CS0612, CS0618 // Type or member is obsolete
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Hosting;
using System.CommandLine.Invocation;
using Cabinet.Database.Abstractions;
using Cabinet.Database.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Command to backup and archive logs
/// </summary>
public class BackupCommand : Command
{
    public BackupCommand() : base("backup", "Backup and archive logs from database")
    {
        var startOption = new Option<DateTime?>(
            "--start",
            "Start date for backup (defaults to 7 days ago)");

        var endOption = new Option<DateTime?>(
            "--end",
            "End date for backup (defaults to now)");

        var pathOption = new Option<string?>(
            "--path",
            "Custom backup path (defaults to ./archives)");

        var formatOption = new Option<string>(
            "--format",
            () => "jsonl.gz",
            "Archive format (json, jsonl, csv, jsonl.gz)");

        var incrementalOption = new Option<bool>(
            "--incremental",
            "Perform incremental backup since last backup");

        var s3Option = new Option<string?>(
            "--s3-bucket",
            "Upload backup to S3 bucket");

        var deleteOption = new Option<bool>(
            "--delete-after-backup",
            "Delete logs after successful backup");

        var verifyOption = new Option<bool>(
            "--verify",
            "Verify backup integrity after creation");

        AddOption(startOption);
        AddOption(endOption);
        AddOption(pathOption);
        AddOption(formatOption);
        AddOption(incrementalOption);
        AddOption(s3Option);
        AddOption(deleteOption);
        AddOption(verifyOption);

        this.SetHandler(async (context) =>
        {
            var start = context.ParseResult.GetValueForOption(startOption);
            var end = context.ParseResult.GetValueForOption(endOption);
            var path = context.ParseResult.GetValueForOption(pathOption);
            var format = context.ParseResult.GetValueForOption(formatOption) ?? "zip";
            var incremental = context.ParseResult.GetValueForOption(incrementalOption);
            var s3Bucket = context.ParseResult.GetValueForOption(s3Option);
            var deleteAfterBackup = context.ParseResult.GetValueForOption(deleteOption);
            var verify = context.ParseResult.GetValueForOption(verifyOption);
            var host = context.BindingContext.GetService<IHost>();

            if (host == null)
            {
                Console.WriteLine("Error: Unable to access host services");
                context.ExitCode = 1;
                return;
            }

            context.ExitCode = await HandleCommand(start, end, path, format, incremental, s3Bucket, deleteAfterBackup, verify, host);
        });
    }

    private async Task<int> HandleCommand(
        DateTime? start,
        DateTime? end,
        string? path,
        string format,
        bool incremental,
        string? s3Bucket,
        bool deleteAfterBackup,
        bool verify,
        IHost host)
    {
        var logger = host.Services.GetRequiredService<ILogger<BackupCommand>>();
        var backupService = host.Services.GetRequiredService<IBackupService>();
        var databaseProvider = host.Services.GetRequiredService<ILogDatabaseProvider>();

        try
        {
            string backupPath = string.Empty;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Creating backup...", async ctx =>
                {
                    if (incremental)
                    {
                        ctx.Status = "Creating incremental backup...";
                        backupPath = await backupService.BackupIncrementalAsync();
                    }
                    else
                    {
                        ctx.Status = "Creating full backup...";
                        backupPath = await backupService.BackupAsync(start, end, path);
                    }
                });

            if (string.IsNullOrEmpty(backupPath))
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] No logs found to backup");
                return 0;
            }

            // Display backup info
            var fileInfo = new FileInfo(backupPath);
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Property")
                .AddColumn("Value");

            table.AddRow("Backup Path", backupPath);
            table.AddRow("Size", $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB");
            table.AddRow("Created", fileInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"));

            AnsiConsole.Write(table);

            // Verify backup if requested
            if (verify)
            {
                await VerifyBackup(backupPath, logger);
            }

            // Upload to S3 if specified
            if (!string.IsNullOrEmpty(s3Bucket))
            {
                await UploadToS3(backupPath, s3Bucket, logger);
            }

            // Delete logs if requested
            if (deleteAfterBackup)
            {
                await DeleteBackedUpLogs(databaseProvider, start, end, logger);
            }

            AnsiConsole.MarkupLine("[green]✓[/] Backup completed successfully");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private async Task VerifyBackup(string backupPath, ILogger logger)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Verifying backup integrity...", async ctx =>
            {
                try
                {
                    var lineCount = 0;
                    var errors = 0;

                    if (backupPath.EndsWith(".gz"))
                    {
                        await using var fileStream = File.OpenRead(backupPath);
                        await using var gzipStream = new System.IO.Compression.GZipStream(fileStream, System.IO.Compression.CompressionMode.Decompress);
                        using var reader = new StreamReader(gzipStream);

                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            lineCount++;
                            try
                            {
                                // Try to parse as JSON to verify integrity
                                System.Text.Json.JsonSerializer.Deserialize<object>(line);
                            }
                            catch
                            {
                                errors++;
                            }
                        }
                    }
                    else
                    {
                        var lines = await File.ReadAllLinesAsync(backupPath);
                        lineCount = lines.Length;

                        foreach (var line in lines)
                        {
                            try
                            {
                                System.Text.Json.JsonSerializer.Deserialize<object>(line);
                            }
                            catch
                            {
                                errors++;
                            }
                        }
                    }

                    if (errors > 0)
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠[/] Backup verification completed with [red]{errors}[/] errors out of [cyan]{lineCount}[/] lines");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] Backup verified successfully: [cyan]{lineCount}[/] lines");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to verify backup");
                    AnsiConsole.MarkupLine("[red]✗[/] Backup verification failed");
                }
            });
    }

    private async Task UploadToS3(string backupPath, string s3Bucket, ILogger logger)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Uploading to S3 bucket {s3Bucket}...", async ctx =>
            {
                try
                {
                    var s3Client = new Amazon.S3.AmazonS3Client();
                    var archiveService = new Cabinet.AWS.Services.S3ArchiveService(
                        s3Client,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<Cabinet.AWS.Services.S3ArchiveService>.Instance,
                        s3Bucket);

                    var result = await archiveService.UploadAsync(backupPath, s3Bucket);
                    if (result.Success)
                    {
                        AnsiConsole.MarkupLine($"[green]✓[/] Uploaded to s3://{result.Bucket}/{result.Key} ({result.SizeBytes / (1024.0 * 1024.0):F2} MB)");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] S3 upload failed: {result.Error}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to upload to S3");
                    AnsiConsole.MarkupLine($"[red]✗[/] S3 upload failed: {ex.Message}");
                }
            });
    }

    private async Task DeleteBackedUpLogs(
        ILogDatabaseProvider databaseProvider,
        DateTime? start,
        DateTime? end,
        ILogger logger)
    {
        var confirmDelete = AnsiConsole.Confirm("Are you sure you want to delete the backed up logs from the database?");

        if (!confirmDelete)
        {
            AnsiConsole.MarkupLine("[yellow]Skipping deletion[/]");
            return;
        }

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Deleting backed up logs...", async ctx =>
            {
                try
                {
                    var cutoffDate = end ?? DateTime.UtcNow;
                    var deleted = await databaseProvider.DeleteOlderThanAsync(cutoffDate);
                    AnsiConsole.MarkupLine($"[green]✓[/] Deleted [cyan]{deleted}[/] logs from database");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to delete logs");
                    AnsiConsole.MarkupLine("[red]✗[/] Failed to delete logs");
                }
            });
    }
}