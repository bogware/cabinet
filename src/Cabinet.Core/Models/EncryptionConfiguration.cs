namespace Cabinet.Core.Models;

public sealed class EncryptionConfiguration
{
    public bool Enabled { get; set; }

    public string Algorithm { get; set; } = "AES-256-GCM";

    public KeyManagementProvider KeyProvider { get; set; } = KeyManagementProvider.Local;

    public string? KeyFilePath { get; set; }

    public string? AwsKmsKeyArn { get; set; }

    public string? AzureKeyVaultUrl { get; set; }

    public List<string> EncryptedFields { get; set; } = new();

    public int KeyRotationDays { get; set; }

    public bool EncryptRawLine { get; set; }

    /// <summary>
    /// Key files from before a rotation. New data uses the current key, and these keep older
    /// data readable. Remove one only once nothing it encrypted is still stored.
    /// </summary>
    public List<string> PreviousKeyFilePaths { get; set; } = new();
}

public enum KeyManagementProvider
{
    Local,
    AwsKms,
    AzureKeyVault,
    HashiCorpVault,
    Environment
}
