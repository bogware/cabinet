using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services.Identity;

/// <summary>Registers the user directory, local accounts and SCIM provisioning.</summary>
public static class IdentityRegistration
{
    /// <summary>Adds the user directory and what's built on it.</summary>
    public static IServiceCollection AddCabinetIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var options = (configuration.GetSection("Accounts").Get<AccountOptions>() ?? new AccountOptions()).Normalized();
        services.AddSingleton(options);
        services.AddSingleton(sp => new UserDirectory(
            sp.GetRequiredService<IDocumentStore>(),
            options,
            sp.GetRequiredService<ILogger<UserDirectory>>()));
        return services;
    }
}
