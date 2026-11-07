using Cabinet.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cabinet.Parsers;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in parsers. A line goes to the first parser that accepts it, so strict
    /// formats come first and the catch-all generic parser last.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="addParsers">Registers optional parsers, which run before the generic parser.</param>
    public static IServiceCollection AddCabinetParsers(
        this IServiceCollection services,
        Action<IServiceCollection>? addParsers = null)
    {
        services.AddSingleton<ILogParser, JsonLogParser>();
        services.AddSingleton<ILogParser, CefParser>();
        services.AddSingleton<ILogParser, ApacheLogParser>();
        services.AddSingleton<ILogParser, NginxLogParser>();
        services.AddSingleton<ILogParser, CsvLogParser>();
        addParsers?.Invoke(services);
        services.AddSingleton<ILogParser, GenericParser>();

        services.TryAddSingleton<ILogParserProvider, LogParserProvider>();

        services.AddSingleton<IParserRegistry>(provider =>
        {
            var registry = new ParserRegistry();
            foreach (var parser in provider.GetServices<ILogParser>())
            {
                registry.Register(parser);
            }

            return registry;
        });

        return services;
    }
}
