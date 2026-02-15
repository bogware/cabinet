using Microsoft.Extensions.DependencyInjection;

namespace Cabinet.Detection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCabinetDetection(this IServiceCollection services)
    {
        services.AddSingleton<IDetectionSuite, ScumscanDetector>();
        services.AddSingleton<IDetectionSuite, HackwatchDetector>();
        services.AddSingleton<IDetectionSuite, AdminabuseDetector>();
        return services;
    }
}