using System.CommandLine;
using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class TailCommand : Command
{
    public TailCommand() : base("tail", "Monitor log files in real-time")
    {
        var pathArgument = new Argument<string>("path", "Path to the log file");
        var followOption = new Option<bool>("--follow", () => true, "Follow file changes");
        var whereOption = new Option<string>("--where", "Filter expression");

        AddArgument(pathArgument);
        AddOption(followOption);
        AddOption(whereOption);

        this.SetHandler(HandleCommand, pathArgument, followOption, whereOption);
    }

    private async Task<int> HandleCommand(string path, bool follow, string? where)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {path}");
            return 1;
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddCabinetCore();
                services.AddCabinetParsers();
            })
            .Build();

        using (host)
        {
            await host.StartAsync();

            var ingestionEngine = host.Services.GetRequiredService<IIngestionEngine>();

            AnsiConsole.MarkupLine($"[cyan]Tailing[/] {path}");
            if (!string.IsNullOrEmpty(where))
            {
                AnsiConsole.MarkupLine($"[yellow]Filter:[/] {where}");
            }
            AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop[/]\n");

            var options = new StreamOptions
            {
                Follow = follow,
                FromEnd = true,
                Filter = where
            };

            await foreach (var evt in ingestionEngine.StreamFileAsync(path, options))
            {
                DisplayLogEvent(evt);
            }

            await host.StopAsync();
        }

        return 0;
    }

    private void DisplayLogEvent(Cabinet.Core.Models.LogEvent evt)
    {
        var severityColor = evt.Severity switch
        {
            Cabinet.Core.Models.LogSeverity.Fatal => "red",
            Cabinet.Core.Models.LogSeverity.Critical => "red",
            Cabinet.Core.Models.LogSeverity.Error => "red",
            Cabinet.Core.Models.LogSeverity.Warning => "yellow",
            Cabinet.Core.Models.LogSeverity.Debug => "dim",
            Cabinet.Core.Models.LogSeverity.Trace => "dim",
            _ => "white"
        };

        var timestamp = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        var severity = evt.Severity.ToString().PadRight(8);
        var source = evt.SourceType.PadRight(10);

        var line = evt.RawLine.Length > 120
            ? evt.RawLine.Substring(0, 117) + "..."
            : evt.RawLine;

        AnsiConsole.MarkupLine(
            $"[dim]{timestamp}[/] [{severityColor}]{severity}[/] [cyan]{source}[/] {Markup.Escape(line)}");

        // Show extracted fields if any
        if (evt.Fields.Any())
        {
            var fields = string.Join(", ", evt.Fields.Take(5).Select(f => $"[yellow]{f.Key}[/]={Markup.Escape(f.Value?.ToString() ?? "null")}"));
            AnsiConsole.MarkupLine($"  [dim]→ {fields}[/]");
        }
    }
}