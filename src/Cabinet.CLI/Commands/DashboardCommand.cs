using System.CommandLine;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Live dashboard showing real-time log activity and metrics
/// </summary>
public class DashboardCommand : Command
{
    public DashboardCommand() : base("dashboard", "Live dashboard showing real-time activity and metrics")
    {
        var pathOption = new Option<string>("--path", "Path to monitor (optional for indexed mode)");
        var refreshOption = new Option<int>("--refresh", () => 1000, "Refresh interval in milliseconds");
        var modeOption = new Option<string>("--mode", () => "indexed", "Mode: indexed or direct");

        AddOption(pathOption);
        AddOption(refreshOption);
        AddOption(modeOption);

        this.SetHandler(HandleCommand, pathOption, refreshOption, modeOption);
    }

    private async Task<int> HandleCommand(string? path, int refreshInterval, string mode)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddCabinetCore();
                services.AddCabinetParsers();
                services.AddCabinetStorage();
                services.AddCabinetQuery();
            })
            .Build();

        using (host)
        {
            await host.StartAsync();

            var storage = host.Services.GetRequiredService<ILogStorage>();
            var ingestionEngine = host.Services.GetRequiredService<IIngestionEngine>();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            await AnsiConsole.Live(GenerateInitialDashboard())
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async ctx =>
                {
                    var metrics = new DashboardMetrics();
                    var recentEvents = new Queue<LogEvent>(100);

                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Update metrics
                            await UpdateMetrics(metrics, storage, recentEvents, cts.Token);

                            // Generate and update dashboard
                            var dashboard = GenerateDashboard(metrics, recentEvents);
                            ctx.UpdateTarget(dashboard);

                            await Task.Delay(refreshInterval, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                });

            await host.StopAsync();
        }

        return 0;
    }

    private async Task UpdateMetrics(
        DashboardMetrics metrics,
        ILogStorage storage,
        Queue<LogEvent> recentEvents,
        CancellationToken cancellationToken)
    {
        // Get storage stats
        var stats = await storage.GetStatsAsync(cancellationToken);
        metrics.TotalEvents = stats.TotalEvents;
        metrics.StorageSize = stats.StorageSizeBytes;

        // Get recent events
        var context = new QueryContext
        {
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow,
            Limit = 100,
            SortDescending = true
        };

        await foreach (var evt in storage.QueryAsync(context, cancellationToken))
        {
            if (recentEvents.Count >= 100)
                recentEvents.Dequeue();
            recentEvents.Enqueue(evt);

            // Update severity counts
            metrics.SeverityCounts[evt.Severity] = metrics.SeverityCounts.GetValueOrDefault(evt.Severity) + 1;

            // Update source type counts
            metrics.SourceTypeCounts[evt.SourceType] = metrics.SourceTypeCounts.GetValueOrDefault(evt.SourceType) + 1;

            // Track event rate
            metrics.LastMinuteEvents++;
        }

        // Calculate rates
        metrics.EventsPerSecond = metrics.LastMinuteEvents / 60.0;
    }

    private IRenderable GenerateInitialDashboard()
    {
        return new Panel(new Text("Initializing dashboard..."))
        {
            Header = new PanelHeader("Cabinet Live Dashboard"),
            Border = BoxBorder.Rounded
        };
    }

    private IRenderable GenerateDashboard(DashboardMetrics metrics, Queue<LogEvent> recentEvents)
    {
        var layout = new Layout("Root")
            .SplitRows(
                new Layout("Header").Size(3),
                new Layout("Metrics").Size(10),
                new Layout("Activity").Size(15),
                new Layout("Footer").Size(3)
            );

        // Header
        layout["Header"].Update(
            new Panel(new Markup($"[bold cyan]Cabinet Live Dashboard[/] - {DateTime.Now:yyyy-MM-dd HH:mm:ss}"))
            {
                Border = BoxBorder.None
            }
        );

        // Metrics section
        var metricsLayout = new Layout()
            .SplitColumns(
                new Layout("Stats"),
                new Layout("Severity"),
                new Layout("Sources"),
                new Layout("Rates")
            );

        // Stats panel
        var statsTable = new Table().BorderStyle(Style.Plain).HideHeaders();
        statsTable.AddColumn("");
        statsTable.AddColumn("");
        statsTable.AddRow("[cyan]Total Events[/]", metrics.TotalEvents.ToString("N0"));
        statsTable.AddRow("[cyan]Storage Size[/]", FormatBytes(metrics.StorageSize));
        statsTable.AddRow("[cyan]Last 5 Min[/]", metrics.LastMinuteEvents.ToString("N0"));

        metricsLayout["Stats"].Update(new Panel(statsTable) { Header = new PanelHeader("Statistics") });

        // Severity breakdown
        var severityChart = new BarChart()
            .Width(20)
            .Label("[yellow]Severity Distribution[/]");

        foreach (var severity in Enum.GetValues<LogSeverity>())
        {
            var count = metrics.SeverityCounts.GetValueOrDefault(severity);
            var color = GetSeverityColor(severity);
            severityChart.AddItem(severity.ToString()[..3], count, Color.FromConsoleColor(color));
        }

        metricsLayout["Severity"].Update(new Panel(severityChart) { Header = new PanelHeader("Severity") });

        // Source types
        var sourceTable = new Table().BorderStyle(Style.Plain).HideHeaders();
        sourceTable.AddColumn("");
        sourceTable.AddColumn("");
        foreach (var source in metrics.SourceTypeCounts.OrderByDescending(s => s.Value).Take(3))
        {
            sourceTable.AddRow(source.Key.ToString(), source.Value.ToString("N0"));
        }

        metricsLayout["Sources"].Update(new Panel(sourceTable) { Header = new PanelHeader("Top Sources") });

        // Rates
        var ratesTable = new Table().BorderStyle(Style.Plain).HideHeaders();
        ratesTable.AddColumn("");
        ratesTable.AddColumn("");
        ratesTable.AddRow("[green]Events/sec[/]", metrics.EventsPerSecond.ToString("F2"));
        ratesTable.AddRow("[yellow]Errors/min[/]", metrics.ErrorsPerMinute.ToString("F1"));
        ratesTable.AddRow("[red]Alerts[/]", metrics.ActiveAlerts.ToString());

        metricsLayout["Rates"].Update(new Panel(ratesTable) { Header = new PanelHeader("Rates") });

        layout["Metrics"].Update(metricsLayout);

        // Activity feed
        var activityTable = new Table();
        activityTable.AddColumn("Time", column => column.Width(8));
        activityTable.AddColumn("Severity", column => column.Width(8));
        activityTable.AddColumn("Source", column => column.Width(10));
        activityTable.AddColumn("Message", column => column.Width(60));

        foreach (var evt in recentEvents.TakeLast(10).Reverse())
        {
            var severityColor = GetSeverityColorName(evt.Severity);
            var time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss");
            var message = evt.RawLine.Length > 60 ? evt.RawLine[..57] + "..." : evt.RawLine;

            activityTable.AddRow(
                time,
                $"[{severityColor}]{evt.Severity}[/]",
                evt.SourceType.ToString(),
                Markup.Escape(message)
            );
        }

        layout["Activity"].Update(new Panel(activityTable) { Header = new PanelHeader("Recent Activity") });

        // Footer
        layout["Footer"].Update(
            new Panel(new Markup("[dim]Press Ctrl+C to exit • Auto-refresh every 1s • Use 'cabinet query' for detailed analysis[/]"))
            {
                Border = BoxBorder.None
            }
        );

        return new Panel(layout)
        {
            Header = new PanelHeader("Live Monitoring"),
            Border = BoxBorder.Rounded
        };
    }

    private ConsoleColor GetSeverityColor(LogSeverity severity)
    {
        return severity switch
        {
            LogSeverity.Fatal or LogSeverity.Critical => ConsoleColor.Red,
            LogSeverity.Error => ConsoleColor.DarkRed,
            LogSeverity.Warning => ConsoleColor.Yellow,
            LogSeverity.Info => ConsoleColor.Cyan,
            LogSeverity.Debug or LogSeverity.Trace => ConsoleColor.Gray,
            _ => ConsoleColor.White
        };
    }

    private string GetSeverityColorName(LogSeverity severity)
    {
        return severity switch
        {
            LogSeverity.Fatal or LogSeverity.Critical => "red",
            LogSeverity.Error => "darkred",
            LogSeverity.Warning => "yellow",
            LogSeverity.Info => "cyan",
            LogSeverity.Debug or LogSeverity.Trace => "dim",
            _ => "white"
        };
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

    private class DashboardMetrics
    {
        public long TotalEvents { get; set; }
        public long StorageSize { get; set; }
        public int LastMinuteEvents { get; set; }
        public double EventsPerSecond { get; set; }
        public double ErrorsPerMinute { get; set; }
        public int ActiveAlerts { get; set; }
        public Dictionary<LogSeverity, int> SeverityCounts { get; set; } = new();
        public Dictionary<string, int> SourceTypeCounts { get; set; } = new();
    }
}