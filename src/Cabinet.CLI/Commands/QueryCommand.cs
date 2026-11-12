using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
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
        var serverOption = new Option<string?>("--server",
            "Run the query on a Cabinet server, such as https://cabinet.example.com, instead of local storage. Also CABINET_SERVER. The API key comes from CABINET_API_KEY.");

        AddArgument(queryArgument);
        AddOption(modeOption);
        AddOption(pathOption);
        AddOption(formatOption);
        AddOption(limitOption);
        AddOption(timeRangeOption);
        AddOption(serverOption);

        this.SetHandler(HandleCommand, queryArgument, modeOption, pathOption, formatOption, limitOption, timeRangeOption, serverOption);
    }

    private async Task<int> HandleCommand(
        string query,
        string mode,
        string? path,
        string format,
        int limit,
        string? timeRange,
        string? server)
    {
        if (mode == "direct" && string.IsNullOrEmpty(path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] --path is required for direct mode");
            return 1;
        }

        server ??= Environment.GetEnvironmentVariable("CABINET_SERVER");
        if (!string.IsNullOrWhiteSpace(server))
        {
            if (mode == "direct")
            {
                AnsiConsole.MarkupLine("[red]Error:[/] direct mode reads a local file, so it can't run on a server");
                return 1;
            }

            return await HandleRemoteAsync(server, query, format, limit, timeRange);
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
                        Output(results, format);
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

    private void Output(QueryResult results, string format)
    {
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

        // Statistics only with the table, so json, ndjson and csv stay machine-readable
        if (results.Statistics != null && format.Equals("table", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.WriteLine();
            var statsTable = new Table();
            statsTable.AddColumn("Statistic");
            statsTable.AddColumn("Value");

            statsTable.AddRow("Events Scanned", results.Statistics.EventsScanned.ToString("N0"));
            statsTable.AddRow("Events Matched", results.Statistics.EventsMatched.ToString("N0"));
            statsTable.AddRow("Execution Time", $"{results.Statistics.ExecutionTime.TotalMilliseconds:N0} ms");
            if (results.Statistics.Truncated)
            {
                statsTable.AddRow("Truncated", "yes: more events matched than were read");
            }

            if (results.Statistics.FieldCardinality?.Any() == true)
            {
                foreach (var field in results.Statistics.FieldCardinality)
                {
                    statsTable.AddRow($"Unique {field.Key}", field.Value.ToString("N0"));
                }
            }

            AnsiConsole.Write(statsTable);
        }
    }

    private async Task<int> HandleRemoteAsync(string server, string query, string format, int limit, string? timeRange)
    {
        var apiKey = Environment.GetEnvironmentVariable("CABINET_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] set CABINET_API_KEY to an API key for the server");
            return 1;
        }

        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            var request = new
            {
                query,
                limit,
                startTime = string.IsNullOrEmpty(timeRange) ? (DateTime?)null : ParseTimeRange(timeRange),
                endTime = string.IsNullOrEmpty(timeRange) ? (DateTime?)null : DateTime.UtcNow,
            };

            using var response = await http.PostAsJsonAsync("api/v1/query/execute", request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error ({(int)response.StatusCode}):[/] {Markup.Escape(RemoteError(body) ?? response.ReasonPhrase ?? "request failed")}");
                return 1;
            }

            Output(ParseRemoteResult(body), format);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or ArgumentException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>A server's answer as a local result, so it prints the same way.</summary>
    public static QueryResult ParseRemoteResult(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var rows = new List<dynamic>();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in results.EnumerateArray())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                if (item.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in item.EnumerateObject())
                    {
                        row[property.Name] = ToValue(property.Value);
                    }
                }
                else
                {
                    row["value"] = ToValue(item);
                }

                rows.Add(row);
            }
        }

        long Number(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
        return new QueryResult
        {
            Events = rows,
            Columns = rows.Cast<Dictionary<string, object?>>().SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Statistics = new QueryStatistics
            {
                EventsScanned = Number("eventsScanned"),
                EventsMatched = rows.Count,
                ExecutionTime = TimeSpan.FromMilliseconds(Number("executionTimeMs")),
                Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
            },
        };
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    private static string? RemoteError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var name in new[] { "detail", "message", "error", "title" })
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, such as a proxy's error page: show it as it is
            return body.Length > 300 ? body[..300] : body;
        }

        return null;
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