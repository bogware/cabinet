using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cabinet.API.Controllers;
using Cabinet.API.Infrastructure;
using Cabinet.API.Services;
using Cabinet.API.Services.Settings;
using Cabinet.API.Services.Updates;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class SettingsCatalogTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Cabinet.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Can't find the repository");
    }

    // Read by the code, but deliberately not on the settings screen
    private static readonly HashSet<string> NotSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiKeys:Keys",             // Keys have their own screen and API, and are never shown
        "Settings:OverridesFile",   // Where this screen saves to, so it's read before the file is
        "Storage:MaxEvents",        // In the shared configuration model, but nothing enforces it
    };

    private static readonly Regex[] KeyPatterns =
    [
        new("""\w+\["(?<key>[A-Za-z][A-Za-z0-9]*:[A-Za-z0-9:]+)"\]"""),
        new("""GetSection\("(?<key>[A-Za-z][A-Za-z0-9:]*)"\)"""),
        new("""(?<![A-Za-z])GetValue(?:<[^>]+>)?\(\$?"(?<key>[A-Za-z][A-Za-z0-9:{}]*)"""),
    ];

    private static bool Covered(string key)
    {
        if (NotSettings.Contains(key))
        {
            return true;
        }

        // An interpolated key such as RateLimiting:Tiers:{tier} is a family of settings
        var prefix = key.Contains('{') ? key[..key.IndexOf('{')] : null;
        return SettingsCatalog.All.Any(setting =>
            setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            || setting.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)
            || (prefix != null && setting.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void EveryKeyTheServerReads_IsInTheCatalog()
    {
        var root = RepositoryRoot();
        var files = new[] { "Cabinet.API", "Cabinet.Alerts", "Cabinet.Extras.Gaming" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var keys = files
            .SelectMany(file => KeyPatterns.SelectMany(pattern => pattern.Matches(File.ReadAllText(file)).Select(match => match.Groups["key"].Value)))
            .Where(key => !key.StartsWith("ASPNETCORE", StringComparison.Ordinal) && !key.StartsWith("DOTNET", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        keys.Should().NotBeEmpty();
        keys.Where(key => !Covered(key)).Should().BeEmpty("every setting the server reads belongs on the settings screen, or in NotSettings with a reason");
        Covered(Cabinet.Extras.Gaming.GamingExtension.EnabledKey).Should().BeTrue();
    }

    // Each section bound to a class: every property is a setting, or excluded with a reason
    public static TheoryData<string, Type, string[]> BoundSections => new()
    {
        { "Limits", typeof(ServerLimits), [] },
        { "Query:Limits", typeof(Cabinet.Query.Bql.QueryLimits), [] },
        { "Query:Concurrency", typeof(Cabinet.API.Middleware.ConcurrencyOptions), [] },
        { "Backup", typeof(BackupConfiguration), [] },
        { "Email", typeof(Cabinet.Alerts.EmailAlertConfiguration), [] },
        { "Syslog", typeof(SyslogConfiguration), [] },
        { "Webhook", typeof(Cabinet.Alerts.WebhookConfiguration), [] },
        { "Updates", typeof(UpdateOptions), [] },
        // Algorithm is fixed; the cloud key providers and rotation days aren't implemented
        { "Encryption", typeof(EncryptionConfiguration), ["Algorithm", "AwsKmsKeyArn", "AzureKeyVaultUrl", "KeyRotationDays"] },
        // SAML and LDAP aren't implemented; Oidc is described setting by setting
        { "SSO", typeof(SsoConfiguration), ["Saml", "Ldap", "Oidc"] },
        // JWT bearer validation uses the authority and client ID only
        { "SSO:Oidc", typeof(OidcSettings), ["ClientSecret", "Scopes", "CallbackPath", "GroupsClaim"] },
    };

    [Theory]
    [MemberData(nameof(BoundSections))]
    public void EveryPropertyOfABoundSection_IsInTheCatalog(string section, Type type, string[] excluded)
    {
        var missing = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite && !excluded.Contains(property.Name))
            .Select(property => $"{section}:{property.Name}")
            .Where(key => SettingsCatalog.Find(key) == null);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void Catalog_IsConsistent()
    {
        SettingsCatalog.All.Select(setting => setting.Key).Should().OnlyHaveUniqueItems();
        SettingsCatalog.All.Select(setting => setting.Group).Distinct().Should().BeSubsetOf(SettingsCatalog.Groups.Select(group => group.Name));
        foreach (var setting in SettingsCatalog.All)
        {
            setting.Description.Should().NotBeNullOrWhiteSpace(setting.Key);
            if (setting.Type == SettingType.Enum)
            {
                setting.Options.Should().NotBeNullOrEmpty(setting.Key);
                setting.Options.Should().Contain((string)setting.Default!, setting.Key);
            }

            if (setting.Default is int or long && Convert.ToDouble(setting.Default) is var number)
            {
                if (setting.Min is { } min)
                {
                    number.Should().BeGreaterThanOrEqualTo(min, setting.Key);
                }

                if (setting.Max is { } max)
                {
                    number.Should().BeLessThanOrEqualTo(max, setting.Key);
                }
            }
        }
    }

    [Fact]
    public void LimitDefaults_AreTheValuesTheCodeAlwaysHad()
    {
        var limits = new ServerLimits();
        limits.MaxRequestBodyMB.Should().Be(50);
        limits.HecMaxRequestMB.Should().Be(5);
        limits.OtlpMaxDecompressedMB.Should().Be(64);
        limits.QueryTimeoutSeconds.Should().Be(60);
        limits.BackupMinimumIntervalMinutes.Should().Be(5);
        limits.FieldExtractionRegexTimeoutMs.Should().Be(100);
        limits.FieldExtractionReloadSeconds.Should().Be(30);
        limits.DetectionRegexTimeoutMs.Should().Be(250);
        limits.AlertHistoryMax.Should().Be(DocumentAlertHistoryStorage.MaxAlerts);
        limits.BacktestMaxHours.Should().Be(24 * 90);
        limits.BacktestMaxEvents.Should().Be(10_000_000);

        foreach (var property in typeof(ServerLimits).GetProperties())
        {
            SettingsCatalog.Find($"Limits:{property.Name}")!.Default.Should().Be(property.GetValue(limits), property.Name);
        }
    }
}

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-settings-").FullName;
    private readonly string _environmentPrefix = $"CABINET_SETTINGS_TEST_{Guid.NewGuid():N}_";

    public void Dispose()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(name => name.StartsWith(_environmentPrefix, StringComparison.Ordinal)))
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Directory.Delete(_directory, recursive: true);
    }

    private string OverridesFile => Path.Combine(_directory, "settings", "cabinet.settings.json");

    /// <summary>appsettings, then the overrides file, then environment variables, as the server has them.</summary>
    private (IConfigurationRoot Configuration, SettingsService Service) Build(string appsettings)
    {
        var appsettingsPath = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(appsettingsPath, appsettings);
        var builder = new ConfigurationBuilder().AddJsonFile(appsettingsPath);
        var store = SettingsOverrideStore.AddTo(builder, OverridesFile);
        builder.AddEnvironmentVariables(_environmentPrefix);
        var configuration = builder.Build();
        var restarter = new ServiceRestarter(new UpdateOptions(), Mock.Of<IHostApplicationLifetime>(), NullLogger<ServiceRestarter>.Instance);
        return (configuration, new SettingsService(configuration, store, restarter));
    }

    private static Dictionary<string, JsonElement> Values(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static SettingView Find(SettingsView view, string key) =>
        view.Groups.SelectMany(group => group.Settings).Single(setting => setting.Key == key);

    [Fact]
    public async Task Saved_IsWrittenAtomically_TakesEffect_AndCanBeReverted()
    {
        var (configuration, service) = Build("""{"Storage":{"RetentionDays":"90"}}""");
        Find(service.GetView([]), "Storage:RetentionDays").Source.Should().Be("appsettings");

        await service.SaveAsync(Values("""{"Storage:RetentionDays": 30}"""), "admin", null);

        configuration["Storage:RetentionDays"].Should().Be("30");
        File.Exists(OverridesFile).Should().BeTrue();
        Directory.GetFiles(Path.GetDirectoryName(OverridesFile)!, "*.tmp").Should().BeEmpty();
        var view = Find(service.GetView([]), "Storage:RetentionDays");
        view.Source.Should().Be("settings-file");
        view.Value.Should().Be(30);
        view.PendingRestart.Should().BeTrue();

        (await service.RevertAsync("Storage:RetentionDays", "admin", null)).Should().BeTrue();

        configuration["Storage:RetentionDays"].Should().Be("90");
        Find(service.GetView([]), "Storage:RetentionDays").Source.Should().Be("appsettings");
        Find(service.GetView([]), "Storage:RetentionDays").PendingRestart.Should().BeFalse();
    }

    [Fact]
    public async Task SavedList_ReplacesTheWholeList()
    {
        var (configuration, service) = Build("""{"Cors":{"AllowedOrigins":["https://a","https://b","https://c"]},"Masking":{"Rules":[{"FieldPattern":"password"},{"FieldPattern":"ssn"}]}}""");

        await service.SaveAsync(Values("""{"Cors:AllowedOrigins": ["https://only"], "Masking:Rules": [{"FieldPattern": "token", "Strategy": "Hash"}]}"""), "admin", null);

        configuration.GetSection("Cors:AllowedOrigins").Get<string[]>().Should().Equal("https://only");
        var rules = configuration.GetSection("Masking:Rules").Get<List<FieldMaskingRule>>()!;
        rules.Should().ContainSingle().Which.FieldPattern.Should().Be("token");

        await service.SaveAsync(Values("""{"Cors:AllowedOrigins": []}"""), "admin", null);

        (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).Should().BeEmpty();
        Find(service.GetView([]), "Cors:AllowedOrigins").Value.Should().BeEquivalentTo(Array.Empty<string>());
    }

    [Fact]
    public async Task Secrets_AreNeverReturned()
    {
        var (_, service) = Build("""{"Email":{"Password":"hunter2-secret"}}""");
        await service.SaveAsync(Values("""{"Updates:Token": "ghp_also-secret"}"""), "admin", null);

        var view = service.GetView([]);
        var json = JsonSerializer.Serialize(view);

        json.Should().NotContain("hunter2-secret").And.NotContain("ghp_also-secret");
        Find(view, "Email:Password").IsSet.Should().BeTrue();
        Find(view, "Email:Password").Value.Should().BeNull();
        Find(view, "Updates:Token").IsSet.Should().BeTrue();
        Find(view, "Webhook:Url").IsSet.Should().BeFalse();
    }

    [Fact]
    public async Task EnvironmentVariables_Win_AndCantBeChangedHere()
    {
        Environment.SetEnvironmentVariable($"{_environmentPrefix}Query__CacheTtlSeconds", "5");
        var (_, service) = Build("{}");

        var view = Find(service.GetView([]), "Query:CacheTtlSeconds");
        view.Source.Should().Be("environment");
        view.Editable.Should().BeFalse();
        view.Value.Should().Be(5);

        var act = () => service.SaveAsync(Values("""{"Query:CacheTtlSeconds": 60}"""), "admin", null);
        (await act.Should().ThrowAsync<SettingsValidationException>()).Which.Errors.Should().ContainKey("Query:CacheTtlSeconds");
    }

    [Theory]
    [InlineData("""{"Storage:RetentionDays": -1}""", "Storage:RetentionDays", "at least")]
    [InlineData("""{"Storage:RetentionDays": 1.5}""", "Storage:RetentionDays", "whole")]
    [InlineData("""{"Storage:RetentionDays": "soon"}""", "Storage:RetentionDays", "number")]
    [InlineData("""{"Limits:QueryTimeoutSeconds": 999999}""", "Limits:QueryTimeoutSeconds", "at most")]
    [InlineData("""{"Email:Enabled": "yes"}""", "Email:Enabled", "true or false")]
    [InlineData("""{"Storage:Type": "Oracle"}""", "Storage:Type", "one of")]
    [InlineData("""{"Cors:AllowedOrigins": "https://a"}""", "Cors:AllowedOrigins", "list")]
    [InlineData("""{"Masking:Rules": "redact everything"}""", "Masking:Rules", "JSON")]
    [InlineData("""{"No:Such": 1}""", "No:Such", "no such")]
    public async Task InvalidValues_AreRefused_AndNothingIsSaved(string values, string key, string message)
    {
        var (configuration, service) = Build("{}");

        var act = () => service.SaveAsync(Values(values.Replace("}", ", \"Gaming:Enabled\": true}", StringComparison.Ordinal)), "admin", null);

        (await act.Should().ThrowAsync<SettingsValidationException>()).Which.Errors[key].Should().ContainEquivalentOf(message);
        configuration["Gaming:Enabled"].Should().BeNull("one bad value stops the whole save");
        File.Exists(OverridesFile).Should().BeFalse();
    }

    [Fact]
    public void OutsideAService_TheServerCantRestartItself()
    {
        var (_, service) = Build("{}");

        var view = service.GetView([]);

        view.CanRestart.Should().BeFalse();
        view.CannotRestartReason.Should().Contain("service");
    }

    [Fact]
    public async Task RestartEndpoint_ExplainsWhyNot_OutsideAService()
    {
        var (_, service) = Build("{}");
        var restarter = new ServiceRestarter(new UpdateOptions(), Mock.Of<IHostApplicationLifetime>(), NullLogger<ServiceRestarter>.Instance);
        var server = Mock.Of<IServer>(s => s.Features == new FeatureCollection());
        var controller = new SettingsController(service, restarter, server) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.Restart(Mock.Of<IAuditLogger>());

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(409);
    }
}

public sealed class StorageInfoTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-storageinfo-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Sqlite_ReportsItsFile_Size_Events_AndDisk()
    {
        var database = Path.Combine(_directory, "cabinet.db");
        await File.WriteAllBytesAsync(database, new byte[4096]);
        await File.WriteAllBytesAsync(database + "-wal", new byte[1024]);
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync(
        [
            new LogEvent { RawLine = "one", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new LogEvent { RawLine = "two", Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
        ]);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Type"] = "SQLite",
            ["Storage:ConnectionString"] = $"Data Source={database}",
            ["Storage:RetentionDays"] = "90",
        }).Build();

        var info = await StorageInfoService.DescribeAsync(configuration, storage);

        info.Type.Should().Be("SQLite");
        info.Location.Should().Be(Path.GetFullPath(database));
        info.SizeBytes.Should().Be(5120);
        info.EventsStored.Should().Be(2);
        info.RetentionDays.Should().Be(90);
        info.DiskFreeBytes.Should().BeGreaterThan(0);
        info.DiskTotalBytes.Should().BeGreaterThanOrEqualTo(info.DiskFreeBytes!.Value);
    }

    [Fact]
    public async Task Postgres_ShowsHostAndDatabase_NeverCredentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Type"] = "PostgreSQL",
            ["Storage:ConnectionString"] = "Host=db.internal;Port=5433;Database=logs;Username=cabinet;Password=very-secret",
        }).Build();

        var info = await StorageInfoService.DescribeAsync(configuration, new InMemoryStorage());

        info.Location.Should().Be("PostgreSQL db.internal:5433, database logs");
        JsonSerializer.Serialize(info).Should().NotContain("very-secret").And.NotContain("Username");
    }
}

public sealed class ManagedApiKeyTests
{
    private readonly InMemoryDocumentStore _documents = new();

    private static IConfiguration Settings(params (string Key, string Tier)[] keys) => new ConfigurationBuilder()
        .AddInMemoryCollection(keys.SelectMany((key, i) => new[]
        {
            new KeyValuePair<string, string?>($"ApiKeys:Keys:{i}:Key", key.Key),
            new KeyValuePair<string, string?>($"ApiKeys:Keys:{i}:Tier", key.Tier),
            new KeyValuePair<string, string?>($"ApiKeys:Keys:{i}:Name", $"settings-{i}"),
        }))
        .Build();

    private ApiKeyService Service(IConfiguration? configuration = null) =>
        new(configuration ?? Settings(), NullLogger<ApiKeyService>.Instance, _documents);

    [Fact]
    public async Task MadeKey_Works_SurvivesARestart_AndIsNeverListed()
    {
        var (key, info) = await Service().CreateAsync("game server", "ingest", DateTime.UtcNow.AddDays(30));

        info.Tier.Should().Be("Ingest");
        info.Source.Should().Be("dashboard");
        key.Should().StartWith(info.Prefix!);

        var afterRestart = Service();
        (await afterRestart.ValidateKeyAsync(key)).IsValid.Should().BeTrue();
        var listed = (await afterRestart.ListAsync()).Single(k => k.Id == info.Id);
        listed.ClientName.Should().Be("game server");
        JsonSerializer.Serialize(listed).Should().NotContain(key);
        (await _documents.ListAsync<StoredApiKey>(ApiKeyService.Collection)).Single().KeyHash.Should().NotBe(key);
    }

    [Fact]
    public async Task RevokedKey_StopsWorking_HereAndAfterARestart()
    {
        var service = Service();
        var (key, info) = await service.CreateAsync("old agent", "Ingest", null);

        (await service.RevokeAsync(info.Id)).Should().NotBeNull();

        (await service.ValidateKeyAsync(key)).IsValid.Should().BeFalse();
        (await Service().ValidateKeyAsync(key)).IsValid.Should().BeFalse();
        (await Service().ListAsync()).Single(k => k.Id == info.Id).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task KeyFromSettings_CanBeRevoked_AndStaysRevoked()
    {
        var configuration = Settings(("settings-key-0123456789", "Admin"));
        var service = Service(configuration);
        var id = (await service.ListAsync()).Single().Id;

        await service.RevokeAsync(id);

        (await Service(configuration).ValidateKeyAsync("settings-key-0123456789")).IsValid.Should().BeFalse();
        (await Service(configuration).ListAsync()).Single().Source.Should().Be("settings");
    }

    [Fact]
    public async Task UnknownTier_IsRefused()
    {
        var act = () => Service().CreateAsync("x", "Root", null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WithoutADatabase_SettingsKeysStillWork()
    {
        var store = new Mock<IDocumentStore>();
        store.Setup(s => s.InitializeAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("database is down"));
        var service = new ApiKeyService(Settings(("settings-key-0123456789", "Admin")), NullLogger<ApiKeyService>.Instance, store.Object);

        (await service.ValidateKeyAsync("settings-key-0123456789")).IsValid.Should().BeTrue();
    }
}
