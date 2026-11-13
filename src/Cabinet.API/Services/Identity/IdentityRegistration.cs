using System.Security.Claims;
using Cabinet.API.Authentication;
using Cabinet.Core.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

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

        // Session cookies and users' encrypted secrets have to read the same on every instance and
        // after a restart, so Data Protection's keys live in the shared document store
        services.AddDataProtection().SetApplicationName("Cabinet");
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(keys =>
            keys.XmlRepository = new DocumentStoreXmlRepository(sp.GetRequiredService<IDocumentStore>())));

        services.AddSingleton(sp => new SecretProtector(sp.GetRequiredService<IDataProtectionProvider>(), sp.GetService<EncryptionService>()));
        services.AddSingleton(_ => new SignInThrottle());
        services.AddSingleton(sp => new LocalAccountService(
            sp.GetRequiredService<UserDirectory>(),
            sp.GetRequiredService<IDocumentStore>(),
            sp.GetRequiredService<SecretProtector>(),
            sp.GetRequiredService<SignInThrottle>(),
            sp.GetRequiredService<ILogger<LocalAccountService>>()));

        // SCIM's own tokens and scheme, used only on /scim
        services.AddSingleton(sp => new ScimTokenService(sp.GetRequiredService<IDocumentStore>()));
        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ScimAuthenticationHandler>(ScimAuthenticationHandler.SchemeName, null);

        // OIDC bearer tokens for a user the directory deactivated are refused. This wraps whatever
        // the bearer setup does when a token validates, so it runs after the claims are mapped.
        services.PostConfigure<JwtBearerOptions>("Bearer", bearer =>
        {
            bearer.Events ??= new JwtBearerEvents();
            var mapped = bearer.Events.OnTokenValidated;
            bearer.Events.OnTokenValidated = async context =>
            {
                await mapped(context);
                if (context.Result != null || context.Principal?.Identity is not ClaimsIdentity identity)
                {
                    return;
                }

                var directory = context.HttpContext.RequestServices.GetService<UserDirectory>();
                if (!await DirectoryClaims.LinkSsoAsync(identity, directory, session: false, context.HttpContext.RequestAborted))
                {
                    context.Fail("This account is deactivated in Cabinet");
                }
            };
        });

        return services;
    }
}
