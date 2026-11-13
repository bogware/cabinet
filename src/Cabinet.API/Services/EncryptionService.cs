using System.Security.Cryptography;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>AES-256-GCM encryption service for data at rest</summary>
public sealed class EncryptionService : IDisposable
{
    private readonly EncryptionConfiguration _config;
    private readonly ILogger<EncryptionService> _logger;
    private byte[]? _key;
    private string _keyId = string.Empty;

    // Every key that may have encrypted stored data, by ID, the current one included
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private const string VersionedPrefix = "enc:v2:";
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32; // 256 bits

    // How long a key file another instance has just created may stay locked or short while it's written
    private static readonly TimeSpan KeyFileSettleTime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeyFileRetryDelay = TimeSpan.FromMilliseconds(20);
    private const string EncryptedPrefix = "enc:";

    public EncryptionService(EncryptionConfiguration config, ILogger<EncryptionService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Whether encryption is configured on.</summary>
    public bool Enabled => _config.Enabled;

    /// <summary>Initialize the encryption key from the configured provider</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Off means off: nothing to load, and no key file to leave behind
        if (_initialized || !_config.Enabled) return;

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

            _keyId = KeyId(_key);
            _keys[_keyId] = _key;
            foreach (var previous in LoadPreviousKeys())
            {
                _keys.TryAdd(KeyId(previous), previous);
            }

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

        // Names the key, so that data stays readable after the current key changes
        return $"{VersionedPrefix}{_keyId}:{Convert.ToBase64String(combined)}";
    }

    /// <summary>The ID of the key new data is encrypted with.</summary>
    public string CurrentKeyId
    {
        get
        {
            EnsureInitialized();
            return _keyId;
        }
    }

    /// <summary>A short public ID for a key, from its hash.</summary>
    private static string KeyId(byte[] key) => Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant();

    /// <summary>Keys from before a rotation, still needed to read what they encrypted.</summary>
    private IEnumerable<byte[]> LoadPreviousKeys()
    {
        foreach (var path in _config.PreviousKeyFilePaths)
        {
            var key = File.ReadAllBytes(path);
            if (key.Length != KeySize)
                throw new InvalidOperationException($"Previous key file '{path}' has invalid size ({key.Length} bytes, expected {KeySize})");
            yield return key;
        }

        var environment = Environment.GetEnvironmentVariable("CABINET_ENCRYPTION_PREVIOUS_KEYS");
        foreach (var encoded in (environment ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length != KeySize)
                throw new InvalidOperationException($"A key in CABINET_ENCRYPTION_PREVIOUS_KEYS has invalid size ({key.Length} bytes, expected {KeySize})");
            yield return key;
        }
    }

    /// <summary>Decrypt an encrypted string produced by Encrypt()</summary>
    public string Decrypt(string encrypted)
    {
        EnsureInitialized();

        if (!encrypted.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Value is not encrypted (missing 'enc:' prefix)");

        byte[] key;
        byte[] combined;
        if (encrypted.StartsWith(VersionedPrefix, StringComparison.Ordinal))
        {
            var separator = encrypted.IndexOf(':', VersionedPrefix.Length);
            if (separator < 0)
                throw new ArgumentException("Encrypted value has no key ID");
            var keyId = encrypted[VersionedPrefix.Length..separator];
            if (!_keys.TryGetValue(keyId, out key!))
                throw new CryptographicException($"Encrypted with key {keyId}, which isn't configured. Add it to Encryption:PreviousKeyFilePaths or CABINET_ENCRYPTION_PREVIOUS_KEYS.");
            combined = Convert.FromBase64String(encrypted[(separator + 1)..]);
        }
        else
        {
            // From before keys had IDs: it can only have been the key in use then
            combined = Convert.FromBase64String(encrypted[EncryptedPrefix.Length..]);
            key = TryLegacyKey(combined);
        }

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

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Finds which configured key encrypted a value written before keys had IDs.</summary>
    private byte[] TryLegacyKey(byte[] combined)
    {
        if (combined.Length < NonceSize + TagSize)
            return _key!;

        var length = combined.Length - NonceSize - TagSize;
        foreach (var candidate in _keys.Values)
        {
            try
            {
                using var aes = new AesGcm(candidate, TagSize);
                aes.Decrypt(combined.AsSpan(0, NonceSize), combined.AsSpan(NonceSize, length), combined.AsSpan(NonceSize + length, TagSize), new byte[length]);
                return candidate;
            }
            catch (CryptographicException)
            {
                // Not this key; the tag check says so
                continue;
            }
        }

        return _key!;
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
        foreach (var key in _keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        _keys.Clear();
        _key = null;
        _initLock.Dispose();
    }

    private async Task<byte[]> LoadOrGenerateLocalKeyAsync(CancellationToken cancellationToken)
    {
        var keyPath = _config.KeyFilePath ?? "cabinet-encryption.key";

        if (File.Exists(keyPath))
        {
            var existing = await ReadKeyFileAsync(keyPath, cancellationToken);
            _logger.LogInformation("Loaded encryption key from {KeyPath}", keyPath);
            return existing;
        }

        var newKey = new byte[KeySize];
        RandomNumberGenerator.Fill(newKey);
        try
        {
            // Creating with CreateNew is atomic on every platform: when instances start together,
            // exactly one creates the file and the others read its key. A move that refuses to
            // overwrite isn't atomic on Linux, so two instances could each keep their own key.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using var stream = new FileStream(keyPath, options);
            await stream.WriteAsync(newKey, cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            _logger.LogInformation("Another instance created the encryption key at {KeyPath}; using it", keyPath);
            return await ReadKeyFileAsync(keyPath, cancellationToken);
        }

        if (OperatingSystem.IsWindows())
        {
            _logger.LogWarning(
                "Running on Windows: encryption key file at {KeyPath} may have overly permissive ACLs. " +
                "Manually restrict access to the service account only.", keyPath);
        }

        _logger.LogWarning("Generated new encryption key at {KeyPath}. Back up this file securely!", keyPath);
        return newKey;
    }

    // Another instance may be part way through writing the key it just created, so a locked or
    // short file is retried for a moment before it counts as invalid.
    private static async Task<byte[]> ReadKeyFileAsync(string keyPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + KeyFileSettleTime;
        while (true)
        {
            byte[]? keyBytes = null;
            IOException? readError = null;
            try
            {
                keyBytes = await File.ReadAllBytesAsync(keyPath, cancellationToken);
                if (keyBytes.Length == KeySize)
                {
                    return keyBytes;
                }
            }
            catch (IOException exception)
            {
                readError = exception;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw readError is null
                    ? new InvalidOperationException($"Key file '{keyPath}' has invalid size ({keyBytes!.Length} bytes, expected {KeySize})")
                    : new InvalidOperationException($"Key file '{keyPath}' couldn't be read: {readError.Message}", readError);
            }

            await Task.Delay(KeyFileRetryDelay, cancellationToken);
        }
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
