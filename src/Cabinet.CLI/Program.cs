using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.Parsing;
using Cabinet.CLI.Commands;
using Cabinet.Core;
using Cabinet.Core.Configuration;
using Cabinet.Parsers;
using Cabinet.Storage;
using Cabinet.Query;
using Cabinet.Extras.Gaming;
using Cabinet.Alerts;
using Cabinet.AWS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Cabinet.CLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Create root command
        var rootCommand = new RootCommand("cabinet - log ingestion, search and analysis")
        {
            TreatUnmatchedTokensAsErrors = true
        };

        // Add commands
        rootCommand.AddCommand(new HelpCommand());
        rootCommand.AddCommand(new VersionCommand());
        rootCommand.AddCommand(new UpdateCommand());
        rootCommand.AddCommand(new PeekCommand());
        rootCommand.AddCommand(new TailCommand());
        rootCommand.AddCommand(new IngestCommand());
        rootCommand.AddCommand(new QueryCommand());
        rootCommand.AddCommand(new DashboardCommand());
        rootCommand.AddCommand(new AWSCommand());
        rootCommand.AddCommand(new SetupWizardCommand());

        // Build command line with host
        var builder = new CommandLineBuilder(rootCommand)
            .UseDefaults()
            .UseHost(_ => Host.CreateDefaultBuilder(args), builder =>
            {
                builder.ConfigureAppConfiguration((context, config) =>
                {
                    config.SetBasePath(Directory.GetCurrentDirectory());
                    config.AddJsonFile("cabinet.config.json", optional: true, reloadOnChange: true);
                    config.AddEnvironmentVariables("CABINET_");
                });

                builder.ConfigureServices((context, services) =>
                {
                    // Configure services
                    services.Configure<CabinetConfiguration>(context.Configuration);
                    services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);

                    // Add core services
                    services.AddCabinetCore();
                    services.AddCabinetParsers(GamingExtension.IsEnabled(context.Configuration) ? s => s.AddGamingParsers() : null);
                    services.AddCabinetStorage();
                    services.AddCabinetQuery();
                    services.AddCabinetAlerts();
                    services.AddCabinetAWS();
                });

                builder.ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    if (context.HostingEnvironment.IsDevelopment())
                    {
                        logging.AddConsole();
                        logging.SetMinimumLevel(LogLevel.Debug);
                    }
                });
            })
            .UseExceptionHandler((exception, context) =>
            {
                AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
                context.ExitCode = 1;
            });

        var parser = builder.Build();
        return await parser.InvokeAsync(args);
    }
}