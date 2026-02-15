using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.Parsers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCabinetParsers(this IServiceCollection services)
    {
        // Register built-in parsers (order matters — specific parsers before generic)
        services.AddSingleton<ILogParser, L4D2Parser>();
        services.AddSingleton<ILogParser, SourceModParser>();
        services.AddSingleton<ILogParser, SteamCMDParser>();
        services.AddSingleton<ILogParser, JsonLogParser>();
        services.AddSingleton<ILogParser, CsvLogParser>();
        services.AddSingleton<ILogParser, CefParser>();
        services.AddSingleton<ILogParser, ApacheLogParser>();
        services.AddSingleton<ILogParser, NginxLogParser>();
        services.AddSingleton<ILogParser, GenericParser>(); // Generic must be last (catches all)

        // Register parser registry and populate it
        services.AddSingleton<IParserRegistry>(provider =>
        {
            var registry = new ParserRegistry();
            var parsers = provider.GetServices<ILogParser>();
            foreach (var parser in parsers)
            {
                registry.Register(parser);
            }
            return registry;
        });

        return services;
    }
}