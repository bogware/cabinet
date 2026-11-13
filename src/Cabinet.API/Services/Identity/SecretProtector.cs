using System.Xml.Linq;
using Cabinet.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Cabinet.API.Services.Identity;

/// <summary>
/// Encrypts secrets Cabinet keeps for users, such as authenticator secrets. With encryption at
/// rest on (<c>Encryption:Enabled</c>), its key does it; otherwise ASP.NET Data Protection, whose
/// keys are kept in the document store so every instance can read what another wrote.
/// </summary>
public sealed class SecretProtector
{
    private const string DataProtectionPrefix = "dp:";
    private const string EncryptedPrefix = "enc:";

    private readonly EncryptionService? _encryption;
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider dataProtection, EncryptionService? encryption = null)
    {
        _encryption = encryption;
        _protector = dataProtection.CreateProtector("Cabinet.Identity.Secrets.v1");
    }

    /// <summary>Encrypts a secret.</summary>
    public async Task<string> ProtectAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (_encryption is { Enabled: true })
        {
            await _encryption.InitializeAsync(cancellationToken);
            return _encryption.Encrypt(secret);
        }

        return DataProtectionPrefix + _protector.Protect(secret);
    }

    /// <summary>Decrypts a secret made by <see cref="ProtectAsync"/>.</summary>
    public async Task<string> UnprotectAsync(string protectedSecret, CancellationToken cancellationToken = default)
    {
        if (protectedSecret.StartsWith(DataProtectionPrefix, StringComparison.Ordinal))
        {
            return _protector.Unprotect(protectedSecret[DataProtectionPrefix.Length..]);
        }

        if (protectedSecret.StartsWith(EncryptedPrefix, StringComparison.Ordinal) && _encryption != null)
        {
            await _encryption.InitializeAsync(cancellationToken);
            return _encryption.Decrypt(protectedSecret);
        }

        throw new InvalidOperationException("The secret was encrypted with encryption at rest, which is now off. Turn Encryption:Enabled back on with the same key.");
    }
}

/// <summary>
/// Keeps ASP.NET Data Protection's keys in the document store, so session cookies and the secrets
/// <see cref="SecretProtector"/> encrypts survive restarts and work on every instance.
/// </summary>
public sealed class DocumentStoreXmlRepository : IXmlRepository
{
    /// <summary>Where the keys are kept.</summary>
    public const string Collection = "data-protection-keys";

    private readonly IDocumentStore _documents;

    public DocumentStoreXmlRepository(IDocumentStore documents)
    {
        _documents = documents;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // Data Protection asks synchronously, and only when it loads or refreshes its key ring
        _documents.InitializeAsync().GetAwaiter().GetResult();
        return _documents.ListAsync<StoredKey>(Collection).GetAwaiter().GetResult()
            .Select(key => XElement.Parse(key.Xml))
            .ToList();
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        _documents.InitializeAsync().GetAwaiter().GetResult();
        var id = string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName;
        _documents.PutAsync(Collection, id, new StoredKey { Xml = element.ToString(SaveOptions.DisableFormatting) }).GetAwaiter().GetResult();
    }

    /// <summary>A key as stored.</summary>
    public sealed class StoredKey
    {
        public required string Xml { get; set; }
    }
}
