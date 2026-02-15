using System.Security.Cryptography;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>AES-256-GCM encryption service for data at rest</summary>
public sealed class EncryptionService : IDisposable
{
    private readonly EncryptionConfiguration _config;
    private readonly ILogger<EncryptionService> _logger;
    private byte[]? _key;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bits
    private const string EncryptedPrefix = "enc:";

    public EncryptionService(EncryptionConfiguration config, ILogger<EncryptionService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Initialize the encryption key from the configured provider</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            _key = _config.KeyProvider switch
            {
                KeyManagementProvider.Local => await LoadOrGenerateLocalKeyAsync(cancellationToken),
                KeyManagementProvider.Environment => LoadEnvironmentKey(),
                _ => throw new NotSupportedException(
                    $"Key provider '{_config.KeyProvider}' requires external integration. " +
                    "Use Local or Environment for built-in support.")
            };

            _initialized = true;
            _logger.LogInformation("Encryption service initialized with {Provider} key provider", _config.KeyProvider);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Encrypt a plaintext string. Returns "enc:base64(nonce+ciphertext+tag)"</summary>
    public string Encrypt(string plaintext)
    {
        EnsureInitialized();

        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key!, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Format: nonce (12) + ciphertext (N) + tag (16)
        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        return EncryptedPrefix + Convert.ToBase64String(combined);
    }

    /// <summary>Decrypt an encrypted string produced by Encrypt()</summary>
    public string Decrypt(string encrypted)
    {
        EnsureInitialized();

        if (!encrypted.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Value is not encrypted (missing 'enc:' prefix)");

        var combined = Convert.FromBase64String(encrypted[EncryptedPrefix.Length..]);

        if (combined.Length < NonceSize + TagSize)
            throw new ArgumentException("Encrypted data is too short");

        var nonce = new byte[NonceSize];
        var ciphertextLength = combined.Length - NonceSize - TagSize;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[TagSize];

        Buffer.BlockCopy(combined, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(combined, NonceSize, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(combined, NonceSize + ciphertextLength, tag, 0, TagSize);

        var plaintext = new byte[ciphertextLength];

        using var aes = new AesGcm(_key!, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Check if a field should be encrypted based on configuration</summary>
    public bool ShouldEncryptField(string fieldName)
    {
        if (!_config.Enabled) return false;

        foreach (var pattern in _config.EncryptedFields)
        {
            if (pattern == "*") return true;
            if (pattern.EndsWith('*') &&
                fieldName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(pattern, fieldName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_key != null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
        _initLock.Dispose();
    }

    private async Task<byte[]> LoadOrGenerateLocalKeyAsync(CancellationToken cancellationToken)
    {
        var keyPath = _config.KeyFilePath ?? "cabinet-encryption.key";

        if (File.Exists(keyPath))
        {
            var keyBytes = await File.ReadAllBytesAsync(keyPath, cancellationToken);
            if (keyBytes.Length != KeySize)
                throw new InvalidOperationException($"Key file '{keyPath}' has invalid size ({keyBytes.Length} bytes, expected {KeySize})");

            _logger.LogInformation("Loaded encryption key from {KeyPath}", keyPath);
            return keyBytes;
        }

        // Generate a new key — write atomically (temp + rename) to prevent corruption on crash
        var newKey = new byte[KeySize];
        RandomNumberGenerator.Fill(newKey);
        var tempPath = keyPath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, newKey, cancellationToken);
        File.Move(tempPath, keyPath, overwrite: true);

        // Set restrictive file permissions (owner-only read/write)
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else
        {
            _logger.LogWarning(
                "Running on Windows: encryption key file at {KeyPath} may have overly permissive ACLs. " +
                "Manually restrict access to the service account only.", keyPath);
        }

        _logger.LogWarning("Generated new encryption key at {KeyPath}. Back up this file securely!", keyPath);
        return newKey;
    }

    private byte[] LoadEnvironmentKey()
    {
        var keyBase64 = Environment.GetEnvironmentVariable("CABINET_ENCRYPTION_KEY");
        if (string.IsNullOrEmpty(keyBase64))
            throw new InvalidOperationException("Environment variable 'CABINET_ENCRYPTION_KEY' is not set");

        var key = Convert.FromBase64String(keyBase64);
        if (key.Length != KeySize)
            throw new InvalidOperationException($"Environment encryption key has invalid size ({key.Length} bytes, expected {KeySize})");

        _logger.LogInformation("Loaded encryption key from environment variable");
        return key;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("EncryptionService has not been initialized. Call InitializeAsync() first.");
    }
}
