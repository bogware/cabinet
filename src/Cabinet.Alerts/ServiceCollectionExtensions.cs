using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cabinet.Alerts;

/// <summary>
/// Service collection extensions for Cabinet alerts system
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add Cabinet alert services
    /// </summary>
    public static IServiceCollection AddCabinetAlerts(this IServiceCollection services)
    {
        // Register alert service
        services.AddSingleton<IAlertService, AlertService>();

        // Register alert channels
        services.AddSingleton<IAlertChannel, ConsoleAlertChannel>();
        services.AddSingleton<IAlertChannel, FileAlertChannel>();

        // Register webhook channel (uses config-driven URL; no-ops when URL not configured)
        services.AddSingleton<IAlertChannel>(sp =>
        {
            var config = new WebhookConfiguration();
            var configuration = sp.GetService<IConfiguration>();
            configuration?.GetSection("Webhook").Bind(config);
            return new WebhookAlertChannel(
                new HttpClient(),
                config,
                sp.GetRequiredService<ILogger<WebhookAlertChannel>>());
        });

        return services;
    }
}