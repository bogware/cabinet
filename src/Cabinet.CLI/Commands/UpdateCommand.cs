using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using Cabinet.CLI.Configuration;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// <c>cabinet update [check|apply]</c>: a server's version and updates, through its API.
/// </summary>
public class UpdateCommand : Command
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public UpdateCommand() : base("update", "Show a Cabinet server's version and update it from the latest release")
    {
        var serverOption = new Option<string?>("--server", "Server URL (default: the remote server in your config, or http://localhost:8080). CABINET_CERT_SHA256 pins a self-signed certificate.");
        var keyOption = new Option<string?>("--key", "Admin API key (default: CABINET_API_KEY, or the key in your config)");
        AddGlobalOption(serverOption);
        AddGlobalOption(keyOption);

        this.SetHandler((server, key) => RunAsync("status", server, key), serverOption, keyOption);

        var check = new Command("check", "Look for a new release now");
        check.SetHandler((server, key) => RunAsync("check", server, key), serverOption, keyOption);
        AddCommand(check);

        var apply = new Command("apply", "Install the newest release. The server restarts on it, and goes back if it doesn't start.");
        apply.SetHandler((server, key) => RunAsync("apply", server, key), serverOption, keyOption);
        AddCommand(apply);
    }

    private static async Task<int> RunAsync(string action, string? server, string? key)
    {
        var config = ConfigManager.Load();
        server ??= config.Remote?.ApiUrl ?? "http://localhost:8080";
        key ??= Environment.GetEnvironmentVariable("CABINET_API_KEY") ?? config.Remote?.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine("[red]An admin API key is needed: --key, CABINET_API_KEY, or 'cabinet config'.[/]");
            return 1;
        }

        using var http = Services.RemoteServer.Create(server, TimeSpan.FromMinutes(10));
        http.DefaultRequestHeaders.Add("X-API-Key", key);

        try
        {
            using var response = action switch
            {
                "check" => await http.PostAsync("api/v1/updates/check", null),
                "apply" => await AnsiConsole.Status().StartAsync("Downloading and verifying the release...", _ => http.PostAsync("api/v1/updates/apply", null)),
                _ => await http.GetAsync("api/v1/updates"),
            };

            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]{(int)response.StatusCode}:[/] {Markup.Escape(Detail(problem))}");
                return 1;
            }

            var status = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            Show(status);
            if (action == "apply")
            {
                AnsiConsole.MarkupLine("[green]The server is restarting on the new version.[/] Run [bold]cabinet update[/] in a minute to confirm.");
            }

            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Couldn't reach {Markup.Escape(server)}:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string Detail(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? body : body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value.ToString()
            : "-";

    private static void Show(JsonElement status)
    {
        var table = new Table().AddColumn("").AddColumn("");
        table.HideHeaders();
        table.AddRow("Running", Text(status, "current"));
        table.AddRow("Newest release", Text(status, "latest"));
        table.AddRow("Update available", Text(status, "available"));
        table.AddRow("State", $"{Text(status, "state")} {(Text(status, "message") is var m && m != "-" ? m : string.Empty)}");
        if (Text(status, "checkError") is var error && error != "-")
        {
            table.AddRow("Last check failed", error);
        }

        if (Text(status, "cannotApplyReason") is var reason && reason != "-")
        {
            table.AddRow("Can't update itself", reason);
        }

        AnsiConsole.Write(table);

        if (status.TryGetProperty("agents", out var agents) && agents.GetArrayLength() > 0)
        {
            var list = new Table().AddColumn("Agent").AddColumn("Platform").AddColumn("Version").AddColumn("Last seen");
            foreach (var agent in agents.EnumerateArray())
            {
                list.AddRow(Text(agent, "host"), Text(agent, "platform"), Text(agent, "version"), Text(agent, "lastSeen"));
            }

            AnsiConsole.Write(list);
        }
    }
}
