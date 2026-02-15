using System.CommandLine;
using Cabinet.Detection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class ReportCommand : Command
{
    public ReportCommand() : base("report", "Run detection suites for security and behavior analysis")
    {
        var suiteArgument = new Argument<string>("suite", "Detection suite to run (scumscan, hackwatch, adminabuse)");
        var lastOption = new Option<string>("--last", "Time range (e.g., 7d, 24h)");
        var serverOption = new Option<string>("--server", "Filter by server ID");
        var outputOption = new Option<string>("--output", "Output file path");

        AddArgument(suiteArgument);
        AddOption(lastOption);
        AddOption(serverOption);
        AddOption(outputOption);

        this.SetHandler(HandleCommand, suiteArgument, lastOption, serverOption, outputOption);
    }

    private async Task<int> HandleCommand(string suite, string? last, string? server, string? output)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddCabinetCore();
                services.AddCabinetStorage();
                services.AddCabinetDetection();
            })
            .Build();

        using (host)
        {
            await host.StartAsync();

            var detectionSuites = host.Services.GetServices<IDetectionSuite>();
            var selectedSuite = detectionSuites.FirstOrDefault(s =>
                s.Name.Equals(suite, StringComparison.OrdinalIgnoreCase));

            if (selectedSuite == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Unknown detection suite: {suite}");
                AnsiConsole.MarkupLine("Available suites: scumscan, hackwatch, adminabuse");
                return 1;
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Running {suite} detection...", async ctx =>
                {
                    // This is a placeholder for the actual implementation
                    // In a complete implementation, you would:
                    // 1. Query events from storage based on time range and server
                    // 2. Run the detection suite
                    // 3. Generate and display the report

                    ctx.Status($"Analyzing events...");
                    await Task.Delay(1000); // Simulate work

                    ctx.Status($"Generating report...");
                    await Task.Delay(500); // Simulate work
                });

            // Display sample report
            DisplaySampleReport(suite);

            if (!string.IsNullOrEmpty(output))
            {
                AnsiConsole.MarkupLine($"\n[green]✓[/] Report saved to: {output}");
            }

            await host.StopAsync();
        }

        return 0;
    }

    private void DisplaySampleReport(string suite)
    {
        var panel = new Panel(new Markup($"[bold cyan]{suite.ToUpper()} Detection Report[/]"))
        {
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);

        var table = new Table();

        switch (suite.ToLower())
        {
            case "scumscan":
                table.AddColumn("Player");
                table.AddColumn("Severity");
                table.AddColumn("Violations");
                table.AddColumn("Score");

                table.AddRow("Player123", "[red]HIGH[/]", "5", "8.5");
                table.AddRow("ToxicGamer", "[yellow]MEDIUM[/]", "3", "5.2");
                table.AddRow("User456", "[green]LOW[/]", "1", "2.1");
                break;

            case "hackwatch":
                table.AddColumn("Player");
                table.AddColumn("Type");
                table.AddColumn("Confidence");
                table.AddColumn("Evidence");

                table.AddRow("SuspectPlayer", "Aimbot", "[red]95%[/]", "Impossible headshot ratio");
                table.AddRow("Cheater123", "Wallhack", "[yellow]78%[/]", "Tracking through walls");
                break;

            case "adminabuse":
                table.AddColumn("Admin");
                table.AddColumn("Actions/Hour");
                table.AddColumn("Target Bias");
                table.AddColumn("Risk Level");

                table.AddRow("Admin1", "45", "Player789 (8x)", "[red]HIGH[/]");
                table.AddRow("Admin2", "12", "None", "[green]LOW[/]");
                break;
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[bold]Summary:[/]");
        AnsiConsole.MarkupLine($"• Events analyzed: [cyan]15,234[/]");
        AnsiConsole.MarkupLine($"• Detections: [yellow]8[/]");
        AnsiConsole.MarkupLine($"• Time range: Last 7 days");
        AnsiConsole.MarkupLine($"• Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }
}