using System.CommandLine;
using Cabinet.Core.Interfaces;
using Cabinet.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

public class QueryCommand : Command
{
    public QueryCommand() : base("query", "Execute queries against logs using Splunk-like syntax")
    {
        var queryArgument = new Argument<string>("query", "Query expression (e.g., 'error | stats count by source')");
        var modeOption = new Option<string>("--mode", () => "indexed", "Query mode: 'indexed' or 'direct'");
        var pathOption = new Option<string>("--path", "Path for direct mode queries");
        var formatOption = new Option<string>("--format", () => "table", "Output format: table, json, ndjson, csv");
        var limitOption = new Option<int>("--limit", () => 100, "Maximum results to return");
        var timeRangeOption = new Option<string>("--time", "Time range (e.g., '1h', '24h', '7d')");

        AddArgument(queryArgument);
        AddOption(modeOption);
        AddOption(pathOption);
        AddOption(formatOption);
        AddOption(limitOption);
        AddOption(timeRangeOption);

        this.SetHandler(HandleCommand, queryArgument, modeOption, pathOption, formatOption, limitOption, timeRangeOption);
    }

    private async Task<int> HandleCommand(
        string query,
        string mode,
        string? path,
        string format,
        int limit,
        string? timeRange)
    {
        if (mode == "direct" && string.IsNullOrEmpty(path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --path is required for direct mode");
            return 1;
        }

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

            var queryEngine = host.Services.GetRequiredService<IQueryEngine>();

            try
            {
                // Parse time range
                DateTime? startTime = null;
                if (!string.IsNullOrEmpty(timeRange))
                {
                    startTime = ParseTimeRange(timeRange);
                }

                var context = new QueryExecutionContext
                {
                    Query = query,
                    Mode = mode == "direct" ? QueryMode.Direct : QueryMode.Indexed,
                    Path = path,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    Limit = limit
                };

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Executing query...", async ctx =>
                    {
                        var results = await queryEngine.ExecuteAsync(context);

                        ctx.Status("Formatting results...");

                        switch (format.ToLower())
                        {
                            case "json":
                                OutputJson(results);
                                break;
                            case "ndjson":
                                OutputNdJson(results);
                                break;
                            case "csv":
                                OutputCsv(results);
                                break;
                            default:
                                OutputTable(results);
                                break;
                        }

                        // Display statistics
                        if (results.Statistics != null)
                        {
                            AnsiConsole.WriteLine();
                            var statsTable = new Table();
                            statsTable.AddColumn("Statistic");
                            statsTable.AddColumn("Value");

                            statsTable.AddRow("Events Scanned", results.Statistics.EventsScanned.ToString("N0"));
                            statsTable.AddRow("Events Matched", results.Statistics.EventsMatched.ToString("N0"));
                            statsTable.AddRow("Execution Time", $"{results.Statistics.ExecutionTime.TotalMilliseconds:N0} ms");

                            if (results.Statistics.FieldCardinality?.Any() == true)
                            {
                                foreach (var field in results.Statistics.FieldCardinality)
                                {
                                    statsTable.AddRow($"Unique {field.Key}", field.Value.ToString("N0"));
                                }
                            }

                            AnsiConsole.Write(statsTable);
                        }
                    });

                await host.StopAsync();
                return 0;
            }
            catch (QueryParseException ex)
            {
                AnsiConsole.MarkupLine($"[red]Query Parse Error:[/] {ex.Message}");
                if (!string.IsNullOrEmpty(ex.Position))
                {
                    AnsiConsole.MarkupLine($"[dim]Position: {ex.Position}[/]");
                }
                return 1;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                return 1;
            }
        }
    }

    private DateTime ParseTimeRange(string timeRange)
    {
        var now = DateTime.UtcNow;
        var lower = timeRange.ToLower();

        if (lower.EndsWith("m"))
        {
            if (!int.TryParse(lower[..^1], out var minutes))
                throw new ArgumentException($"Invalid minutes value in time range: {timeRange}");
            return now.AddMinutes(-minutes);
        }
        else if (lower.EndsWith("h"))
        {
            if (!int.TryParse(lower[..^1], out var hours))
                throw new ArgumentException($"Invalid hours value in time range: {timeRange}");
            return now.AddHours(-hours);
        }
        else if (lower.EndsWith("d"))
        {
            if (!int.TryParse(lower[..^1], out var days))
                throw new ArgumentException($"Invalid days value in time range: {timeRange}");
            return now.AddDays(-days);
        }
        else if (lower.EndsWith("w"))
        {
            if (!int.TryParse(lower[..^1], out var weeks))
                throw new ArgumentException($"Invalid weeks value in time range: {timeRange}");
            return now.AddDays(-weeks * 7);
        }

        throw new ArgumentException($"Invalid time range format: {timeRange}");
    }

    private void OutputTable(QueryResult results)
    {
        if (!results.Events.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No results found[/]");
            return;
        }

        var table = new Table();

        // Add columns based on the first event
        var firstEvent = results.Events.First();
        var columns = results.Columns ?? GetEventColumns(firstEvent);

        foreach (var column in columns)
        {
            table.AddColumn(new TableColumn(column));
        }

        // Add rows
        foreach (var evt in results.Events)
        {
            var values = new List<string>();
            foreach (var column in columns)
            {
                var value = GetEventValue(evt, column);
                values.Add(Markup.Escape(value ?? "-"));
            }
            table.AddRow(values.ToArray());
        }

        AnsiConsole.Write(table);
    }

    private void OutputJson(QueryResult results)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(results.Events, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        AnsiConsole.WriteLine(json);
    }

    private void OutputNdJson(QueryResult results)
    {
        foreach (var evt in results.Events)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(evt);
            AnsiConsole.WriteLine(json);
        }
    }

    private void OutputCsv(QueryResult results)
    {
        if (!results.Events.Any()) return;

        var columns = results.Columns ?? GetEventColumns(results.Events.First());

        // Header
        var headerValues = new List<string>();
        foreach (var col in columns)
        {
            headerValues.Add(EscapeCsv(col));
        }
        AnsiConsole.WriteLine(string.Join(",", headerValues));

        // Rows
        foreach (var evt in results.Events)
        {
            var values = new List<string>();
            foreach (var col in columns)
            {
                values.Add(EscapeCsv(GetEventValue(evt, col) ?? ""));
            }
            AnsiConsole.WriteLine(string.Join(",", values));
        }
    }

    private List<string> GetEventColumns(dynamic evt)
    {
        var columns = new List<string> { "Timestamp", "Severity", "Source" };

        if (evt is IDictionary<string, object> dict)
        {
            columns.AddRange(dict.Keys.Where(k => !columns.Contains(k)));
        }

        return columns;
    }

    private string? GetEventValue(dynamic evt, string column)
    {
        if (evt is IDictionary<string, object> dict)
        {
            if (dict.TryGetValue(column, out var value))
            {
                return value?.ToString();
            }
        }

        return column switch
        {
            "Timestamp" => evt.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss"),
            "Severity" => evt.Severity?.ToString(),
            "Source" => evt.SourceType?.ToString(),
            _ => null
        };
    }

    private string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}