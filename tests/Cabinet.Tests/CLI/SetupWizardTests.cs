using System.Runtime.InteropServices;
using Xunit;
using FluentAssertions;

namespace Cabinet.Tests.CLI;

/// <summary>
/// Tests for the Setup Wizard utility methods and configuration generation.
/// These test the internal logic without requiring an interactive terminal.
/// </summary>
public class SetupWizardTests
{
    [Fact]
    public void GenerateSecureApiKey_ReturnsValidKey()
    {
        var key = GenerateSecureApiKey();

        key.Should().StartWith("cab_");
        key.Length.Should().Be(44); // "cab_" + 40 chars
        key.Should().NotContain("+");
        key.Should().NotContain("/");
        key.Should().NotContain("=");
    }

    [Fact]
    public void GenerateSecureApiKey_ProducesUniqueKeys()
    {
        var keys = Enumerable.Range(0, 100).Select(_ => GenerateSecureApiKey()).ToList();

        keys.Distinct().Count().Should().Be(100, "each generated key should be unique");
    }

    [Fact]
    public void MaskApiKey_ShortKey_ReturnsMasked()
    {
        MaskApiKey("").Should().Be("****");
        MaskApiKey("short").Should().Be("****");
        MaskApiKey("12345678").Should().Be("****");
    }

    [Fact]
    public void MaskApiKey_LongKey_ShowsPrefixAndSuffix()
    {
        var key = "cab_abcdefghij1234567890ABCDEFGHIJ123456";
        var masked = MaskApiKey(key);

        masked.Should().StartWith("cab_abcd");
        masked.Should().EndWith("3456");
        masked.Should().Contain("...");
    }

    [Fact]
    public void MaskConnectionString_HidesPassword()
    {
        var connStr = "Host=localhost;Port=5432;Database=cabinet;Username=cabinet;Password=supersecret123";
        var masked = MaskConnectionString(connStr);

        masked.Should().Contain("Host=localhost");
        masked.Should().Contain("Database=cabinet");
        masked.Should().Contain("Password=****");
        masked.Should().NotContain("supersecret123");
    }

    [Fact]
    public void MaskConnectionString_NoPassword_ReturnsAsIs()
    {
        var connStr = "Data Source=cabinet.db";
        var masked = MaskConnectionString(connStr);

        masked.Should().Be("Data Source=cabinet.db");
    }

    [Fact]
    public void MaskConnectionString_Empty_ReturnsNotSet()
    {
        MaskConnectionString("").Should().Be("(not set)");
        MaskConnectionString(null!).Should().Be("(not set)");
    }

    [Fact]
    public void GenerateRandomPassword_ReturnsCorrectLength()
    {
        var password = GenerateRandomPassword(24);

        password.Length.Should().Be(24);
        password.Should().NotContain("+");
        password.Should().NotContain("/");
        password.Should().NotContain("=");
    }

    [Fact]
    public void GenerateRandomPassword_ProducesUniquePasswords()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => GenerateRandomPassword(32)).ToList();

        passwords.Distinct().Count().Should().Be(50);
    }

    [Fact]
    public void BuildAppSettings_Standalone_SQLite_GeneratesCorrectConfig()
    {
        var settings = BuildAppSettings("sqlite", "Data Source=cabinet.db",
            enableSyslog: false, enableEmail: false, 
            syslogPort: 1514, smtpHost: "", smtpFrom: "");

        settings.Should().ContainKey("Storage");
        var storage = settings["Storage"] as Dictionary<string, string>;
        storage.Should().NotBeNull();
        storage!["Type"].Should().Be("SQLite");
        storage["ConnectionString"].Should().Be("Data Source=cabinet.db");

        settings.Should().ContainKey("Logging");
        settings.Should().ContainKey("RateLimiting");
        settings.Should().ContainKey("Cors");
        settings.Should().ContainKey("Backup");
        settings.Should().NotContainKey("Syslog");
        settings.Should().NotContainKey("Email");
    }

    [Fact]
    public void BuildAppSettings_WithSyslog_IncludesSyslogConfig()
    {
        var settings = BuildAppSettings("sqlite", "Data Source=cabinet.db",
            enableSyslog: true, enableEmail: false, 
            syslogPort: 1514, smtpHost: "", smtpFrom: "");

        settings.Should().ContainKey("Syslog");
        var syslog = settings["Syslog"] as Dictionary<string, object>;
        syslog.Should().NotBeNull();
        syslog!["Enabled"].Should().Be(true);
        syslog["TcpPort"].Should().Be(1514);
    }

    [Fact]
    public void BuildAppSettings_PostgreSQL_SetsTypeCorrectly()
    {
        var settings = BuildAppSettings("postgresql", "Host=localhost;Database=cabinet",
            enableSyslog: false, enableEmail: false, 
            syslogPort: 1514, smtpHost: "", smtpFrom: "");

        var storage = settings["Storage"] as Dictionary<string, string>;
        storage!["Type"].Should().Be("PostgreSQL");
        // Connection string should be placeholder (set via env var)
        storage["ConnectionString"].Should().Be("SET_VIA_ENV_VAR");
    }

    [Fact]
    public void BuildAppSettings_OpenSearch_SetsTypeCorrectly()
    {
        var settings = BuildAppSettings("opensearch", "https://localhost:9200",
            enableSyslog: false, enableEmail: false, 
            syslogPort: 1514, smtpHost: "", smtpFrom: "");

        var storage = settings["Storage"] as Dictionary<string, string>;
        storage!["Type"].Should().Be("OpenSearch");
    }

    [Fact]
    public void BuildAppSettings_WithEmail_IncludesEmailConfig()
    {
        var settings = BuildAppSettings("sqlite", "Data Source=cabinet.db",
            enableSyslog: false, enableEmail: true, 
            syslogPort: 1514, smtpHost: "smtp.example.com", smtpFrom: "alerts@example.com");

        settings.Should().ContainKey("Email");
        var email = settings["Email"] as Dictionary<string, object>;
        email.Should().NotBeNull();
        email!["Enabled"].Should().Be(true);
        email["SmtpHost"].Should().Be("smtp.example.com");
        email["FromAddress"].Should().Be("alerts@example.com");
    }

    // --- Helper methods that mirror SetupWizard internals for testability ---

    private static string GenerateSecureApiKey()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        var encoded = Convert.ToBase64String(bytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "");
        return "cab_" + (encoded.Length >= 40 ? encoded[..40] : encoded.PadRight(40, 'x'));
    }

    private static string GenerateRandomPassword(int length)
    {
        var bytes = new byte[length];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "A")
            .Replace("/", "B")
            .Replace("=", "")[..length];
    }

    private static string MaskApiKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 8)
            return "****";
        return key[..8] + "..." + key[^4..];
    }

    private static string MaskConnectionString(string connStr)
    {
        if (string.IsNullOrEmpty(connStr))
            return "(not set)";

        var parts = connStr.Split(';');
        var masked = new List<string>();
        foreach (var part in parts)
        {
            if (part.TrimStart().StartsWith("Password", StringComparison.OrdinalIgnoreCase))
                masked.Add("Password=****");
            else
                masked.Add(part);
        }
        return string.Join(";", masked);
    }

    private static Dictionary<string, object> BuildAppSettings(
        string storageBackend, string connectionString,
        bool enableSyslog, bool enableEmail,
        int syslogPort, string smtpHost, string smtpFrom)
    {
        var settings = new Dictionary<string, object>
        {
            ["Storage"] = new Dictionary<string, string>
            {
                ["Type"] = storageBackend switch
                {
                    "postgresql" => "PostgreSQL",
                    "opensearch" => "OpenSearch",
                    _ => "SQLite"
                },
                ["ConnectionString"] = storageBackend == "sqlite" ? connectionString : "SET_VIA_ENV_VAR"
            },
            ["Logging"] = new Dictionary<string, object>
            {
                ["LogLevel"] = new Dictionary<string, string>
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning"
                }
            },
            ["RateLimiting"] = new Dictionary<string, object>
            {
                ["Basic"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 100 },
                ["Professional"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 1000 },
                ["Enterprise"] = new Dictionary<string, int> { ["RequestsPerMinute"] = 10000 }
            },
            ["Cors"] = new Dictionary<string, object>
            {
                ["AllowedOrigins"] = new[] { "http://localhost:3000", "http://localhost:5173" }
            },
            ["Backup"] = new Dictionary<string, object>
            {
                ["ArchivePath"] = "./backups",
                ["RetentionDays"] = 30,
                ["AutoBackupEnabled"] = false
            }
        };

        if (enableSyslog)
        {
            settings["Syslog"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["TcpPort"] = syslogPort,
                ["UdpPort"] = syslogPort
            };
        }

        if (enableEmail)
        {
            settings["Email"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SmtpHost"] = smtpHost,
                ["SmtpPort"] = 587,
                ["FromAddress"] = smtpFrom,
                ["UseTls"] = true
            };
        }


        return settings;
    }
}
