using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Cabinet.Core.Models;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;

namespace Cabinet.API.Authentication;

/// <summary>
/// SAML 2.0 sign-in: Cabinet is the service provider, and the identity provider's metadata says
/// where to send people and which certificates sign what comes back.
/// </summary>
public sealed class SamlSignIn
{
    private readonly SamlSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SamlSignIn> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Saml2Configuration? _configuration;

    public SamlSignIn(SamlSettings settings, IHttpClientFactory http, ILogger<SamlSignIn> logger)
    {
        _settings = settings;
        _http = http;
        _logger = logger;
    }

    /// <summary>The configuration, reading the identity provider's metadata the first time.</summary>
    public async Task<Saml2Configuration> ConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (_configuration is { } loaded)
        {
            return loaded;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_configuration is { } again)
            {
                return again;
            }

            var configuration = new Saml2Configuration
            {
                Issuer = _settings.EntityId,

                // The certificates are pinned from metadata fetched over HTTPS, or configured;
                // identity providers usually sign with a self-signed certificate.
                CertificateValidationMode = System.ServiceModel.Security.X509CertificateValidationMode.None,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            configuration.AllowedAudienceUris.Add(_settings.EntityId);

            var metadata = new EntityDescriptor();
            await metadata.ReadIdPSsoDescriptorFromUrlAsync(_http, new Uri(_settings.MetadataUrl), cancellationToken);
            if (metadata.IdPSsoDescriptor == null)
            {
                throw new InvalidOperationException($"{_settings.MetadataUrl} doesn't describe a SAML identity provider");
            }

            configuration.AllowedIssuer = metadata.EntityId;
            configuration.SingleSignOnDestination = metadata.IdPSsoDescriptor.SingleSignOnServices.First().Location;
            configuration.SignatureValidationCertificates.AddRange(metadata.IdPSsoDescriptor.SigningCertificates);
            if (!string.IsNullOrWhiteSpace(_settings.Certificate))
            {
                configuration.SignatureValidationCertificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(_settings.Certificate)));
            }

            if (configuration.SignatureValidationCertificates.Count == 0)
            {
                throw new InvalidOperationException("The SAML identity provider's metadata has no signing certificate, so its responses can't be checked");
            }

            _logger.LogInformation("SAML identity provider {Issuer} loaded", configuration.AllowedIssuer);
            _configuration = configuration;
            return configuration;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>The identity from a SAML response, which must be signed by the identity provider.</summary>
    public static ClaimsIdentity IdentityFrom(Saml2AuthnResponse response)
    {
        var identity = new ClaimsIdentity("saml");
        foreach (var claim in response.ClaimsIdentity.Claims)
        {
            // Attribute names are often URIs; role mappings can use either form
            identity.AddClaim(new Claim(claim.Type, claim.Value));
            var shortName = claim.Type.Contains('/') ? claim.Type[(claim.Type.LastIndexOf('/') + 1)..] : null;
            if (!string.IsNullOrEmpty(shortName) && shortName != claim.Type)
            {
                identity.AddClaim(new Claim(shortName, claim.Value));
            }
        }

        if (identity.FindFirst(ClaimTypes.NameIdentifier) == null && response.NameId?.Value is { } nameId)
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, nameId));
        }

        return identity;
    }
}
