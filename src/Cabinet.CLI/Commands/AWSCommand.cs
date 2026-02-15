using System.CommandLine;
using Cabinet.AWS;
using Cabinet.AWS.Parsers;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Cabinet.CLI.Commands;

/// <summary>
/// AWS integration commands for Cabinet
/// </summary>
public class AWSCommand : Command
{
    public AWSCommand() : base("aws", "AWS CloudWatch and CloudTrail log integration")
    {
        // CloudWatch subcommand
        var cloudwatchCommand = new Command("cloudwatch", "Access CloudWatch logs");
        AddCloudWatchCommands(cloudwatchCommand);
        AddCommand(cloudwatchCommand);

        // CloudTrail subcommand
        var cloudtrailCommand = new Command("cloudtrail", "Access CloudTrail audit logs");
        AddCloudTrailCommands(cloudtrailCommand);
        AddCommand(cloudtrailCommand);

        // Configure AWS credentials
        var configureCommand = new Command("configure", "Configure AWS credentials");
        AddConfigureCommand(configureCommand);
        AddCommand(configureCommand);
    }

    private void AddCloudWatchCommands(Command parent)
    {
        // List log groups
        var listCommand = new Command("list", "List CloudWatch log groups and streams");
        var regionOption = new Option<string>("--region", () => "us-east-1", "AWS region");
        var profileOption = new Option<string>("--profile", "AWS profile name");
        var prefixOption = new Option<string>("--prefix", "Log group name prefix");

        listCommand.AddOption(regionOption);
        listCommand.AddOption(profileOption);
        listCommand.AddOption(prefixOption);

        listCommand.SetHandler(async (string region, string? profile, string? prefix) =>
        {
            await HandleCloudWatchList(region, profile, prefix);
        }, regionOption, profileOption, prefixOption);

        parent.AddCommand(listCommand);

        // Stream logs
        var streamCommand = new Command("stream", "Stream CloudWatch logs in real-time");
        var logGroupArg = new Argument<string>("log-group", "Log group name");
        var logStreamArg = new Argument<string>("log-stream", "Log stream name");
        var followOption = new Option<bool>("--follow", "Follow log stream");

        streamCommand.AddArgument(logGroupArg);
        streamCommand.AddArgument(logStreamArg);
        streamCommand.AddOption(regionOption);
        streamCommand.AddOption(profileOption);
        streamCommand.AddOption(followOption);

        streamCommand.SetHandler(async (string logGroup, string logStream, string region, string? profile, bool follow) =>
        {
            await HandleCloudWatchStream(logGroup, logStream, region, profile, follow);
        }, logGroupArg, logStreamArg, regionOption, profileOption, followOption);

        parent.AddCommand(streamCommand);

        // Query with CloudWatch Insights
        var queryCommand = new Command("query", "Run CloudWatch Insights query");
        var logGroupQueryArg = new Argument<string>("log-group", "Log group name");
        var queryArg = new Argument<string>("query", "CloudWatch Insights query");
        var timeRangeOption = new Option<string>("--time", () => "1h", "Time range (e.g., 1h, 24h, 7d)");

        queryCommand.AddArgument(logGroupQueryArg);
        queryCommand.AddArgument(queryArg);
        queryCommand.AddOption(regionOption);
        queryCommand.AddOption(profileOption);
        queryCommand.AddOption(timeRangeOption);

        queryCommand.SetHandler(async (string logGroup, string query, string region, string? profile, string timeRange) =>
        {
            await HandleCloudWatchQuery(logGroup, query, region, profile, timeRange);
        }, logGroupQueryArg, queryArg, regionOption, profileOption, timeRangeOption);

        parent.AddCommand(queryCommand);

        // Ingest CloudWatch logs
        var ingestCommand = new Command("ingest", "Ingest CloudWatch logs into Cabinet");
        var ingestLogGroupArg = new Argument<string>("log-group", "Log group name");

        ingestCommand.AddArgument(ingestLogGroupArg);
        ingestCommand.AddOption(regionOption);
        ingestCommand.AddOption(profileOption);
        ingestCommand.AddOption(timeRangeOption);

        ingestCommand.SetHandler(async (string logGroup, string region, string? profile, string timeRange) =>
        {
            await HandleCloudWatchIngest(logGroup, region, profile, timeRange);
        }, ingestLogGroupArg, regionOption, profileOption, timeRangeOption);

        parent.AddCommand(ingestCommand);
    }

    private void AddCloudTrailCommands(Command parent)
    {
        // List trails
        var listCommand = new Command("list", "List CloudTrail trails and recent events");
        var regionOption = new Option<string>("--region", () => "us-east-1", "AWS region");
        var profileOption = new Option<string>("--profile", "AWS profile name");

        listCommand.AddOption(regionOption);
        listCommand.AddOption(profileOption);

        listCommand.SetHandler(async (string region, string? profile) =>
        {
            await HandleCloudTrailList(region, profile);
        }, regionOption, profileOption);

        parent.AddCommand(listCommand);

        // Lookup events
        var lookupCommand = new Command("lookup", "Lookup CloudTrail events");
        var eventNameOption = new Option<string>("--event-name", "Event name to search for");
        var userNameOption = new Option<string>("--username", "Username to search for");
        var timeRangeOption = new Option<string>("--time", () => "24h", "Time range");

        lookupCommand.AddOption(eventNameOption);
        lookupCommand.AddOption(userNameOption);
        lookupCommand.AddOption(regionOption);
        lookupCommand.AddOption(profileOption);
        lookupCommand.AddOption(timeRangeOption);

        lookupCommand.SetHandler(async (string? eventName, string? username, string region, string? profile, string timeRange) =>
        {
            await HandleCloudTrailLookup(eventName, username, region, profile, timeRange);
        }, eventNameOption, userNameOption, regionOption, profileOption, timeRangeOption);

        parent.AddCommand(lookupCommand);

        // Ingest CloudTrail logs
        var ingestCommand = new Command("ingest", "Ingest CloudTrail logs into Cabinet");
        ingestCommand.AddOption(regionOption);
        ingestCommand.AddOption(profileOption);
        ingestCommand.AddOption(timeRangeOption);

        ingestCommand.SetHandler(async (string region, string? profile, string timeRange) =>
        {
            await HandleCloudTrailIngest(region, profile, timeRange);
        }, regionOption, profileOption, timeRangeOption);

        parent.AddCommand(ingestCommand);
    }

    private void AddConfigureCommand(Command parent)
    {
        var profileOption = new Option<string>("--profile", () => "default", "AWS profile name");
        parent.AddOption(profileOption);

        parent.SetHandler(async (string profile) =>
        {
            await ConfigureAWSCredentials(profile);
        }, profileOption);
    }

    private async Task HandleCloudWatchList(string region, string? profile, string? prefix)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Connecting to CloudWatch...", async ctx =>
            {
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IRemoteLogAccess, CloudWatchLogAccess>();
                        services.AddLogging();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudWatch = host.Services.GetRequiredService<IRemoteLogAccess>();

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudWatch,
                        CustomSettings = new Dictionary<string, string>
                        {
                            ["Region"] = region
                        }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    ctx.Status("Fetching log groups...");

                    var connection = await cloudWatch.ConnectAsync(config);
                    var logs = await cloudWatch.ListLogsAsync(connection, prefix ?? "", "*.log");

                    var logList = logs.ToList();

                    if (!logList.Any())
                    {
                        AnsiConsole.MarkupLine("[yellow]No log groups found[/]");
                        return;
                    }

                    var table = new Table();
                    table.AddColumn("Log Group / Stream");
                    table.AddColumn("Size");
                    table.AddColumn("Last Modified");

                    var groups = logList.GroupBy(l => l.Path.Split('/')[0]);

                    foreach (var group in groups.Take(20))
                    {
                        table.AddRow(
                            $"[cyan]{group.Key}[/]",
                            "",
                            ""
                        );

                        foreach (var stream in group.Take(5))
                        {
                            table.AddRow(
                                $"  └─ {stream.Name}",
                                FormatBytes(stream.Size),
                                stream.LastModified.ToString("yyyy-MM-dd HH:mm:ss")
                            );
                        }

                        if (group.Count() > 5)
                        {
                            table.AddRow($"  └─ [dim]... and {group.Count() - 5} more streams[/]", "", "");
                        }
                    }

                    AnsiConsole.Write(table);

                    if (groups.Count() > 20)
                    {
                        AnsiConsole.MarkupLine($"\n[dim]... and {groups.Count() - 20} more log groups[/]");
                    }
                }
            });
    }

    private async Task HandleCloudWatchStream(string logGroup, string logStream, string region, string? profile, bool follow)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IRemoteLogAccess, CloudWatchLogAccess>();
                services.AddLogging();
            })
            .Build();

        using (host)
        {
            await host.StartAsync();

            var cloudWatch = host.Services.GetRequiredService<IRemoteLogAccess>();

            var config = new RemoteConnectionConfig
            {
                Type = RemoteSourceType.CloudWatch,
                CustomSettings = new Dictionary<string, string> { ["Region"] = region }
            };

            if (!string.IsNullOrEmpty(profile))
            {
                config.CustomSettings["Profile"] = profile;
            }

            try
            {
                var connection = await cloudWatch.ConnectAsync(config);

                AnsiConsole.MarkupLine($"[cyan]Streaming logs from:[/] {logGroup}/{logStream}");
                if (follow)
                {
                    AnsiConsole.MarkupLine("[yellow]Following log stream... Press Ctrl+C to stop[/]\n");
                }

                var options = new Cabinet.Core.Remote.StreamOptions
                {
                    Follow = follow,
                    FromEnd = follow,
                    PollIntervalMs = 2000
                };

                await foreach (var line in cloudWatch.StreamLogAsync(connection, $"{logGroup}/{logStream}", options))
                {
                    // Parse and colorize
                    if (line.Contains("ERROR") || line.Contains("Exception"))
                    {
                        AnsiConsole.MarkupLine($"[red]{Markup.Escape(line)}[/]");
                    }
                    else if (line.Contains("WARN"))
                    {
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(line)}[/]");
                    }
                    else if (line.Contains("DEBUG"))
                    {
                        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(line)}[/]");
                    }
                    else
                    {
                        AnsiConsole.WriteLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            }
        }
    }

    private async Task HandleCloudWatchQuery(string logGroup, string query, string region, string? profile, string timeRange)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Running CloudWatch Insights query...", async ctx =>
            {
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<CloudWatchLogAccess>();
                        services.AddLogging();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudWatch = host.Services.GetRequiredService<CloudWatchLogAccess>();

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudWatch,
                        CustomSettings = new Dictionary<string, string> { ["Region"] = region }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    var connection = await cloudWatch.ConnectAsync(config);

                    var endTime = DateTime.UtcNow;
                    var startTime = ParseTimeRange(timeRange, endTime);

                    var result = await cloudWatch.RunInsightsQueryAsync(
                        connection,
                        logGroup,
                        query,
                        startTime,
                        endTime);

                    ctx.Status("Formatting results...");

                    if (!result.Results.Any())
                    {
                        AnsiConsole.MarkupLine("[yellow]No results found[/]");
                        return;
                    }

                    // Display results in a table
                    var table = new Table();

                    // Add columns from first result
                    var firstResult = result.Results.First();
                    foreach (var field in firstResult)
                    {
                        table.AddColumn(field.Field);
                    }

                    // Add rows
                    foreach (var row in result.Results.Take(100))
                    {
                        var values = row.Select(f => Markup.Escape(f.Value ?? "")).ToArray();
                        table.AddRow(values);
                    }

                    AnsiConsole.Write(table);

                    // Show statistics
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[dim]Records scanned: {result.Statistics.RecordsScanned:N0}[/]");
                    AnsiConsole.MarkupLine($"[dim]Records matched: {result.Statistics.RecordsMatched:N0}[/]");
                    AnsiConsole.MarkupLine($"[dim]Bytes scanned: {FormatBytes((long)result.Statistics.BytesScanned)}[/]");

                    if (result.Results.Count > 100)
                    {
                        AnsiConsole.MarkupLine($"\n[dim]Showing first 100 of {result.Results.Count} results[/]");
                    }
                }
            });
    }

    private async Task HandleCloudWatchIngest(string logGroup, string region, string? profile, string timeRange)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]Ingesting CloudWatch logs from {logGroup}[/]", maxValue: 100);

                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddCabinetCore();
                        services.AddCabinetParsers();
                        services.AddCabinetStorage();
                        services.AddSingleton<IRemoteLogAccess, CloudWatchLogAccess>();
                        services.AddSingleton<ILogParser, CloudWatchParser>();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudWatch = host.Services.GetRequiredService<IRemoteLogAccess>();
                    var ingestion = host.Services.GetRequiredService<IIngestionEngine>();
                    var parserRegistry = host.Services.GetRequiredService<IParserRegistry>();

                    // Register CloudWatch parser
                    var cwParser = host.Services.GetRequiredService<ILogParser>();
                    parserRegistry.Register(cwParser);

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudWatch,
                        CustomSettings = new Dictionary<string, string> { ["Region"] = region }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    task.Value = 10;
                    var connection = await cloudWatch.ConnectAsync(config);

                    task.Value = 20;
                    task.Description = "[green]Listing log streams...[/]";
                    var logs = await cloudWatch.ListLogsAsync(connection, logGroup);
                    var logList = logs.ToList();

                    task.Value = 30;
                    task.Description = "[green]Caching and ingesting logs...[/]";

                    var totalIngested = 0;
                    var streamCount = 0;

                    foreach (var log in logList.Take(10)) // Limit to 10 streams for demo
                    {
                        streamCount++;
                        task.Description = $"[green]Ingesting stream {streamCount}/{Math.Min(logList.Count, 10)}[/]";

                        var localPath = await cloudWatch.CacheLogLocallyAsync(connection, log.Path);

                        var result = await ingestion.IngestFileAsync(localPath, new IngestionOptions
                        {
                            Profile = "CloudWatch",
                            Host = $"aws-{region}"
                        });

                        totalIngested += (int)result.EventsStored;
                        task.Value = 30 + (streamCount * 60 / Math.Min(logList.Count, 10));
                    }

                    task.Value = 100;
                    task.Description = "[green]Ingestion complete![/]";

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[green]✓[/] Ingested {totalIngested:N0} events from {streamCount} log streams");
                }
            });
    }

    private async Task HandleCloudTrailList(string region, string? profile)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Connecting to CloudTrail...", async ctx =>
            {
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IRemoteLogAccess, CloudTrailLogAccess>();
                        services.AddLogging();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudTrail = host.Services.GetRequiredService<IRemoteLogAccess>();

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudTrail,
                        CustomSettings = new Dictionary<string, string> { ["Region"] = region }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    ctx.Status("Fetching trails and events...");

                    var connection = await cloudTrail.ConnectAsync(config);
                    var logs = await cloudTrail.ListLogsAsync(connection, "");

                    var logList = logs.ToList();

                    if (!logList.Any())
                    {
                        AnsiConsole.MarkupLine("[yellow]No CloudTrail logs found[/]");
                        return;
                    }

                    var table = new Table();
                    table.AddColumn("Trail / Log File");
                    table.AddColumn("Size");
                    table.AddColumn("Last Modified");

                    foreach (var log in logList.Take(20))
                    {
                        table.AddRow(
                            log.Name,
                            FormatBytes(log.Size),
                            log.LastModified.ToString("yyyy-MM-dd HH:mm:ss")
                        );
                    }

                    AnsiConsole.Write(table);

                    if (logList.Count > 20)
                    {
                        AnsiConsole.MarkupLine($"\n[dim]Showing first 20 of {logList.Count} logs[/]");
                    }
                }
            });
    }

    private async Task HandleCloudTrailLookup(string? eventName, string? username, string region, string? profile, string timeRange)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Looking up CloudTrail events...", async ctx =>
            {
                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<CloudTrailLogAccess>();
                        services.AddLogging();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudTrail = host.Services.GetRequiredService<CloudTrailLogAccess>();

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudTrail,
                        CustomSettings = new Dictionary<string, string> { ["Region"] = region }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    var connection = await cloudTrail.ConnectAsync(config);

                    var endTime = DateTime.UtcNow;
                    var startTime = ParseTimeRange(timeRange, endTime);

                    var request = new Amazon.CloudTrail.Model.LookupEventsRequest
                    {
                        StartTime = startTime,
                        EndTime = endTime,
                        MaxResults = 50
                    };

                    if (!string.IsNullOrEmpty(eventName))
                    {
                        request.LookupAttributes.Add(new Amazon.CloudTrail.Model.LookupAttribute
                        {
                            AttributeKey = Amazon.CloudTrail.LookupAttributeKey.EventName,
                            AttributeValue = eventName
                        });
                    }

                    if (!string.IsNullOrEmpty(username))
                    {
                        request.LookupAttributes.Add(new Amazon.CloudTrail.Model.LookupAttribute
                        {
                            AttributeKey = Amazon.CloudTrail.LookupAttributeKey.Username,
                            AttributeValue = username
                        });
                    }

                    var events = await cloudTrail.LookupEventsAsync(connection, request);

                    if (!events.Any())
                    {
                        AnsiConsole.MarkupLine("[yellow]No events found matching criteria[/]");
                        return;
                    }

                    var table = new Table();
                    table.AddColumn("Time");
                    table.AddColumn("Event");
                    table.AddColumn("User");
                    table.AddColumn("Source");
                    table.AddColumn("IP");

                    foreach (var evt in events.Take(50))
                    {
                        table.AddRow(
                            evt.EventTime.ToString("HH:mm:ss"),
                            evt.EventName ?? "",
                            evt.Username ?? "N/A",
                            evt.EventSource ?? "",
                            evt.SourceIPAddress ?? ""
                        );
                    }

                    AnsiConsole.Write(table);

                    if (events.Count > 50)
                    {
                        AnsiConsole.MarkupLine($"\n[dim]Showing first 50 of {events.Count} events[/]");
                    }
                }
            });
    }

    private async Task HandleCloudTrailIngest(string region, string? profile, string timeRange)
    {
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            })
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Ingesting CloudTrail events[/]", maxValue: 100);

                var host = Host.CreateDefaultBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddCabinetCore();
                        services.AddCabinetParsers();
                        services.AddCabinetStorage();
                        services.AddSingleton<CloudTrailLogAccess>();
                        services.AddSingleton<ILogParser, CloudTrailParser>();
                    })
                    .Build();

                using (host)
                {
                    await host.StartAsync();

                    var cloudTrail = host.Services.GetRequiredService<CloudTrailLogAccess>();
                    var ingestion = host.Services.GetRequiredService<IIngestionEngine>();
                    var parserRegistry = host.Services.GetRequiredService<IParserRegistry>();

                    // Register CloudTrail parser
                    var ctParser = host.Services.GetRequiredService<ILogParser>();
                    parserRegistry.Register(ctParser);

                    var config = new RemoteConnectionConfig
                    {
                        Type = RemoteSourceType.CloudTrail,
                        CustomSettings = new Dictionary<string, string> { ["Region"] = region }
                    };

                    if (!string.IsNullOrEmpty(profile))
                    {
                        config.CustomSettings["Profile"] = profile;
                    }

                    task.Value = 20;
                    var connection = await cloudTrail.ConnectAsync(config);

                    task.Value = 40;
                    task.Description = "[green]Caching recent events...[/]";
                    var localPath = await cloudTrail.CacheLogLocallyAsync(connection, "recent-events");

                    task.Value = 60;
                    task.Description = "[green]Ingesting events...[/]";

                    var result = await ingestion.IngestFileAsync(localPath, new IngestionOptions
                    {
                        Profile = "CloudTrail",
                        Host = $"aws-{region}"
                    });

                    task.Value = 100;
                    task.Description = "[green]Ingestion complete![/]";

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[green]✓[/] Ingested {result.EventsStored:N0} CloudTrail events");
                }
            });
    }

    private async Task ConfigureAWSCredentials(string profile)
    {
        AnsiConsole.MarkupLine($"[cyan]Configuring AWS credentials for profile: {profile}[/]");
        AnsiConsole.MarkupLine("[dim]Note: You can also configure credentials using the AWS CLI or by editing ~/.aws/credentials[/]");

        var accessKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter AWS Access Key ID:")
                .PromptStyle("yellow")
                .Secret());

        var secretKey = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter AWS Secret Access Key:")
                .PromptStyle("yellow")
                .Secret());

        var region = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter default region:")
                .DefaultValue("us-east-1")
                .PromptStyle("yellow"));

        // Save to AWS credentials file
        var credentialsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aws",
            "credentials"
        );

        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aws",
            "config"
        );

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(credentialsPath)!);

            // Write credentials
            var credentialsContent = $@"[{profile}]
aws_access_key_id = {accessKey}
aws_secret_access_key = {secretKey}
";
            await File.AppendAllTextAsync(credentialsPath, credentialsContent);

            // Write config
            var configContent = $@"[profile {profile}]
region = {region}
output = json
";
            await File.AppendAllTextAsync(configPath, configContent);

            AnsiConsole.MarkupLine($"[green]✓[/] AWS credentials configured for profile: {profile}");
            AnsiConsole.MarkupLine($"[dim]Credentials saved to: {credentialsPath}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error saving credentials:[/] {ex.Message}");
        }
    }

    private DateTime ParseTimeRange(string timeRange, DateTime endTime)
    {
        var lower = timeRange.ToLower();

        if (lower.EndsWith("m"))
        {
            var minutes = int.Parse(lower[..^1]);
            return endTime.AddMinutes(-minutes);
        }
        else if (lower.EndsWith("h"))
        {
            var hours = int.Parse(lower[..^1]);
            return endTime.AddHours(-hours);
        }
        else if (lower.EndsWith("d"))
        {
            var days = int.Parse(lower[..^1]);
            return endTime.AddDays(-days);
        }
        else if (lower.EndsWith("w"))
        {
            var weeks = int.Parse(lower[..^1]);
            return endTime.AddDays(-weeks * 7);
        }

        return endTime.AddHours(-1); // Default to 1 hour
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
}