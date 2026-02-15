using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.Query;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCabinetQuery(this IServiceCollection services)
    {
        services.AddSingleton<IQueryEngine, QueryEngine>();
        services.AddSingleton<IQueryParser, QueryParser>();
        return services;
    }
}