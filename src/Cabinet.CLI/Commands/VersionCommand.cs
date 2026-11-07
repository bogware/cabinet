using System.CommandLine;
using System.Reflection;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class VersionCommand : Command
{
    public VersionCommand() : base("version", "Display version information")
    {
        this.SetHandler(HandleCommand);
    }

    private Task<int> HandleCommand()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        var panel = new Panel(new Markup(
            $"[bold cyan]Cabinet[/] - High-performance log intelligence\n" +
            $"Version: [green]{version}[/]\n" +
            $"Framework: {framework}\n" +
            $"OS: {os}\n" +
            $"Architecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}")
        )
        {
            Header = new PanelHeader("Version Information"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
        return Task.FromResult(0);
    }
}

public class HelpCommand : Command
{
    public HelpCommand() : base("help", "Show help and usage examples")
    {
        this.SetHandler(HandleCommand);
    }

    private Task<int> HandleCommand()
    {
        AnsiConsole.Write(new FigletText("Cabinet").Color(Color.Cyan1));

        AnsiConsole.MarkupLine("[bold]High-performance log intelligence for game servers[/]\n");

        var table = new Table();
        table.AddColumn("[cyan]Command[/]");
        table.AddColumn("Description");

        table.AddRow("peek <path>", "Quickly view beginning or end of log files");
        table.AddRow("tail <path>", "Monitor log files in real-time");
        table.AddRow("ingest <path>", "Index logs for fast querying");
        table.AddRow("query <expression>", "Search and analyze logs");
        table.AddRow("report <suite>", "Run detection suites (scumscan, hackwatch, adminabuse)");
        table.AddRow("config init", "Initialize configuration");
        table.AddRow("plugins list", "List installed plugins");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[bold]Examples:[/]");
        AnsiConsole.MarkupLine("  [dim]# View last 100 lines of a log[/]");
        AnsiConsole.MarkupLine("  cabinet peek server.log --lines 100 --reverse\n");

        AnsiConsole.MarkupLine("  [dim]# Follow log file for errors[/]");
        AnsiConsole.MarkupLine("  cabinet tail server.log --follow --where \"error OR failed\"\n");

        AnsiConsole.MarkupLine("  [dim]# Ingest L4D2 server logs[/]");
        AnsiConsole.MarkupLine("  cabinet ingest /var/log/l4d2 --profile l4d2 --recursive\n");

        AnsiConsole.MarkupLine("  [dim]# Query for VAC bans in last 24 hours[/]");
        AnsiConsole.MarkupLine("  cabinet query \"vac banned | stats count by steamid\" --time 24h\n");

        AnsiConsole.MarkupLine("  [dim]# Run toxic behavior scan[/]");

        AnsiConsole.MarkupLine("\n[dim]For more information, visit: https://github.com/yourusername/cabinet[/]");

        return Task.FromResult(0);
    }
}