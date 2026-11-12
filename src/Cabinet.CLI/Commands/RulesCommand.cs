using System.CommandLine;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cabinet.CLI.Services;
using Cabinet.Core.Models;
using Cabinet.Parsers;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// Detection rules as code: test rule files, push them to a server, or pull a server's rules into
/// files. The server and its key come from --server or CABINET_SERVER and CABINET_API_KEY.
/// </summary>
public sealed class RulesCommand : Command
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RulesCommand() : base("rules", "Detection rules as YAML files: test them, push them to a server, or pull a server's rules")
    {
        var pathArgument = new Argument<string>("path", "A rule file, or a directory of them");
        var serverOption = new Option<string?>("--server", "The Cabinet server, such as https://cabinet.example.com. Also CABINET_SERVER; the key comes from CABINET_API_KEY.");

        var test = new Command("test", "Check rule files and run the tests in them");
        test.AddArgument(pathArgument);
        test.SetHandler(async (string path) => Environment.ExitCode = await TestAsync(path) ? 0 : 1, pathArgument);

        var forceOption = new Option<bool>("--force", "Push even if tests fail");
        var push = new Command("push", "Test rule files, then add or change them on the server");
        push.AddArgument(pathArgument);
        push.AddOption(serverOption);
        push.AddOption(forceOption);
        push.SetHandler(async (string path, string? server, bool force) => Environment.ExitCode = await PushAsync(path, server, force), pathArgument, serverOption, forceOption);

        var directoryArgument = new Argument<string>("directory", "Where to write the rule files");
        var pull = new Command("pull", "Write the server's rules, other than built-in ones, as rule files");
        pull.AddArgument(directoryArgument);
        pull.AddOption(serverOption);
        pull.SetHandler(async (string directory, string? server) => Environment.ExitCode = await PullAsync(directory, server), directoryArgument, serverOption);

        AddCommand(test);
        AddCommand(push);
        AddCommand(pull);
    }

    private static LogLineParser Parser() => new ServiceCollection().AddLogging().AddCabinetParsers().BuildServiceProvider().GetRequiredService<LogLineParser>();

    private static async Task<bool> TestAsync(string path)
    {
        var results = await TestAllAsync(path);
        return results != null && results.All(r => r.Problems.Count == 0);
    }

    private static async Task<List<RuleFileResult>?> TestAllAsync(string path)
    {
        List<string> files;
        try
        {
            files = RuleFiles.Find(path).ToList();
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return null;
        }

        var parser = Parser();
        var results = new List<RuleFileResult>();
        foreach (var file in files)
        {
            var result = await RuleFiles.TestAsync(file, parser);
            results.Add(result);
            var label = Markup.Escape($"{result.Rule?.Id ?? Path.GetFileName(file)} ({result.TestsRun} tests)");
            if (result.Problems.Count == 0)
            {
                AnsiConsole.MarkupLine($"[green]PASS[/] {label}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]FAIL[/] {label} [dim]{Markup.Escape(file)}[/]");
                result.Problems.ForEach(problem => AnsiConsole.MarkupLine($"     {Markup.Escape(problem)}"));
            }
        }

        var ids = results.Where(r => r.Rule?.Id != null).GroupBy(r => r.Rule!.Id!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        foreach (var duplicate in ids)
        {
            AnsiConsole.MarkupLine($"[red]FAIL[/] the id {Markup.Escape(duplicate.Key)} is in {duplicate.Count()} files");
            duplicate.Skip(1).ToList().ForEach(r => r.Problems.Add("duplicate id"));
        }

        var failed = results.Count(r => r.Problems.Count > 0);
        AnsiConsole.MarkupLine(failed == 0 ? $"[green]{results.Count} rule file(s) passed[/]" : $"[red]{failed} of {results.Count} rule file(s) failed[/]");
        return results;
    }

    private static HttpClient? Client(string? server)
    {
        server ??= Environment.GetEnvironmentVariable("CABINET_SERVER");
        var key = Environment.GetEnvironmentVariable("CABINET_API_KEY");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] set --server (or CABINET_SERVER) and CABINET_API_KEY");
            return null;
        }

        var client = new HttpClient { BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(1) };
        client.DefaultRequestHeaders.Add("X-API-Key", key);
        return client;
    }

    private static async Task<int> PushAsync(string path, string? server, bool force)
    {
        var results = await TestAllAsync(path);
        if (results == null) return 1;
        if (results.Any(r => r.Problems.Count > 0) && !force)
        {
            AnsiConsole.MarkupLine("[red]Not pushed: fix the failures, or push with --force.[/]");
            return 1;
        }

        using var client = Client(server);
        if (client == null) return 1;

        var failures = 0;
        foreach (var file in results.Where(r => r.Rule?.Id != null && r.Rule.Name != null))
        {
            var rule = file.Rule!.ToRule();
            try
            {
                // Actions are set up in the dashboard; a file without any keeps the server's
                using (var current = await client.GetAsync($"api/v1/rules/{Uri.EscapeDataString(rule.Id)}"))
                {
                    if (current.StatusCode == HttpStatusCode.OK && await current.Content.ReadFromJsonAsync<DetectionRule>(Json) is { } existing)
                    {
                        rule.Actions = existing.Actions;
                    }
                }

                using var response = await client.PutAsJsonAsync($"api/v1/rules/{Uri.EscapeDataString(rule.Id)}", rule, Json);
                if (response.IsSuccessStatusCode)
                {
                    AnsiConsole.MarkupLine($"[green]pushed[/] {Markup.Escape(rule.Id)}");
                }
                else
                {
                    failures++;
                    AnsiConsole.MarkupLine($"[red]rejected[/] {Markup.Escape(rule.Id)}: {Markup.Escape(await response.Content.ReadAsStringAsync())}");
                }
            }
            catch (HttpRequestException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<int> PullAsync(string directory, string? server)
    {
        using var client = Client(server);
        if (client == null) return 1;
        try
        {
            var rules = await client.GetFromJsonAsync<List<DetectionRule>>("api/v1/rules", Json) ?? [];
            Directory.CreateDirectory(directory);
            var written = 0;
            foreach (var rule in rules.Where(r => !r.Builtin))
            {
                var file = RuleFile.From(rule);
                file.Tests = [new RuleTest { Line = "replace with a line this rule should match", Match = true }];
                await File.WriteAllTextAsync(Path.Combine(directory, $"{EventFileName(rule.Id)}.yaml"), RuleFiles.Serialize(file));
                written++;
            }

            AnsiConsole.MarkupLine($"[green]Wrote {written} rule file(s) to {Markup.Escape(directory)}[/]. Add real tests before pushing them back.");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static string EventFileName(string id) => new(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}
