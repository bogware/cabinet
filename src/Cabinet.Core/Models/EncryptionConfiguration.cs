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
}

public enum KeyManagementProvider
{
    Local,
    AwsKms,
    AzureKeyVault,
    HashiCorpVault,
    Environment
}
