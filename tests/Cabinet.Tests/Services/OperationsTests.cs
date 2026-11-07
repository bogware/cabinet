using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cabinet.API.Controllers;
using Cabinet.API.Services;
using Cabinet.Core.Configuration;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Storage;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cabinet.Tests.Services;

public sealed class FieldMaskingEventTests
{
    private static FieldMaskingService Service(params FieldMaskingRule[] rules)
    {
        var service = new FieldMaskingService();
        foreach (var rule in rules)
        {
            service.AddRule(rule);
        }

        return service;
    }

    private static LogEvent Event()
    {
        var logEvent = new LogEvent { RawLine = "user=alice card=4111111111111111 action=pay", Index = "main" };
        logEvent.SetField("user", "alice");
        logEvent.SetField("card", "4111111111111111");
        return logEvent;
    }

    [Fact]
    public void MaskedField_IsMaskedInTheRawLineToo()
    {
        var service = Service(new FieldMaskingRule { FieldPattern = "card", Strategy = MaskingStrategy.Redact });

        var masked = service.MaskEvent(Event(), SystemRole.Viewer);

        masked.GetField<string>("card").Should().Be("***REDACTED***");
        masked.RawLine.Should().NotContain("4111111111111111").And.Contain("alice");
        masked.GetField<string>("user").Should().Be("alice");
    }

    [Fact]
    public void ExemptRole_SeesTheOriginal()
    {
        var service = Service(new FieldMaskingRule { FieldPattern = "card", ExemptRoles = [SystemRole.Admin] });

        service.MaskEvent(Event(), SystemRole.Admin).RawLine.Should().Contain("4111111111111111");
        service.MaskEvent(Event(), SystemRole.Viewer).RawLine.Should().NotContain("4111111111111111");
    }

    [Fact]
    public void ValuePattern_MasksWhatNoParserExtracted()
    {
        var service = Service(new FieldMaskingRule
        {
            FieldPattern = "nothing-matches-this",
            Strategy = MaskingStrategy.Pattern,
            ValuePattern = @"\b\d{3}-\d{2}-\d{4}\b",
            Replacement = "[ssn]",
        });
        var logEvent = new LogEvent { RawLine = "applicant 123-45-6789 approved" };

        service.MaskEvent(logEvent, SystemRole.Viewer).RawLine.Should().Be("applicant [ssn] approved");
    }

    [Fact]
    public void Masking_DoesNotChangeTheStoredEvent()
    {
        var original = Event();
        Service(new FieldMaskingRule { FieldPattern = "card" }).MaskEvent(original, SystemRole.Viewer);

        original.RawLine.Should().Contain("4111111111111111");
        original.GetField<string>("card").Should().Be("4111111111111111");
    }
}

public sealed class EncryptingLogStorageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-encryption-").FullName;

    [Fact]
    public async Task EncryptionOff_LeavesNoKeyFileBehind()
    {
        var keyPath = Path.Combine(_directory, "unused.key");
        var encryption = new EncryptionService(
            new EncryptionConfiguration { Enabled = false, KeyFilePath = keyPath },
            NullLogger<EncryptionService>.Instance);

        await encryption.InitializeAsync();

        File.Exists(keyPath).Should().BeFalse();
    }

    [Fact]
    public async Task InstancesStartingTogether_AgreeOnOneKey()
    {
        var keyPath = Path.Combine(_directory, "shared.key");
        EncryptionService Create() => new(
            new EncryptionConfiguration { Enabled = true, KeyFilePath = keyPath },
            NullLogger<EncryptionService>.Instance);
        var services = Enumerable.Range(0, 16).Select(_ => Create()).ToList();

        await Task.WhenAll(services.Select(service => Task.Run(() => service.InitializeAsync())));

        var encrypted = services[0].Encrypt("secret");
        services.Should().OnlyContain(service => service.Decrypt(encrypted) == "secret");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<(ILogStorage Storage, string Path)> CreateAsync(bool encryptRawLine = false)
    {
        var path = Path.Combine(_directory, "events.db");
        var configuration = new CabinetConfiguration();
        configuration.Storage.ConnectionString = $"Data Source={path}";
        var inner = new SqliteLogStorage(Options.Create(configuration), NullLogger<SqliteLogStorage>.Instance);
        var encryption = new EncryptionService(
            new EncryptionConfiguration
            {
                Enabled = true,
                KeyProvider = KeyManagementProvider.Local,
                KeyFilePath = Path.Combine(_directory, "key.bin"),
                EncryptedFields = ["card", "amount"],
            },
            NullLogger<EncryptionService>.Instance);
        var storage = new EncryptingLogStorage(inner, encryption, encryptRawLine);
        await storage.InitializeAsync();
        return (storage, path);
    }

    private static LogEvent Event()
    {
        var logEvent = new LogEvent { RawLine = "paid card=4111111111111111 amount=1250", Index = "main", Timestamp = DateTime.UtcNow };
        logEvent.SetField("card", "4111111111111111");
        logEvent.SetField("amount", 1250L);
        logEvent.SetField("user", "alice");
        return logEvent;
    }

    private static async Task<byte[]> ReadDatabaseAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(path);
        var wal = $"{path}-wal";
        return File.Exists(wal) ? bytes.Concat(await File.ReadAllBytesAsync(wal)).ToArray() : bytes;
    }

    private static bool Contains(byte[] haystack, string text) =>
        Encoding.UTF8.GetString(haystack).Contains(text, StringComparison.Ordinal);

    [Fact]
    public async Task EncryptedField_IsNotOnDisk_InTheFieldOrTheLine_ButReadsBackIntact()
    {
        var (storage, path) = await CreateAsync();
        var stored = Event();
        await storage.StoreBatchAsync([stored]);

        Contains(await ReadDatabaseAsync(path), "4111111111111111").Should().BeFalse();

        var read = await storage.GetByIdAsync(stored.Id);
        read!.GetField<string>("card").Should().Be("4111111111111111");
        read.Fields["amount"].Should().BeAssignableTo<IConvertible>().And.NotBeOfType<string>("the type survives");
        Convert.ToInt64(read.Fields["amount"]).Should().Be(1250);
        read.GetField<string>("user").Should().Be("alice");
        read.RawLine.Should().Be("paid card=4111111111111111 amount=1250");
    }

    [Fact]
    public async Task StoringAnEvent_DoesNotChangeTheCallersCopy()
    {
        var (storage, _) = await CreateAsync();
        var stored = Event();

        await storage.StoreBatchAsync([stored]);

        stored.GetField<string>("card").Should().Be("4111111111111111");
    }

    [Fact]
    public async Task EncryptRawLine_HidesTheWholeLine()
    {
        var (storage, path) = await CreateAsync(encryptRawLine: true);
        var stored = Event();
        stored.RawLine = "a sensitive sentence appears here";
        await storage.StoreAsync(stored);

        Contains(await ReadDatabaseAsync(path), "sensitive sentence").Should().BeFalse();

        (await storage.GetByIdAsync(stored.Id))!.RawLine.Should().Be("a sensitive sentence appears here");
    }
}

public sealed class PerIndexRetentionTests
{
    private static async Task<RetentionService> ServiceAsync(ILogStorage storage, IDocumentStore documents, int globalDays, params (string Index, int Days)[] policies)
    {
        foreach (var (index, days) in policies)
        {
            await documents.PutAsync(RetentionService.PoliciesCollection, index, new RetentionPolicyDto { Index = index, MaxAgeDays = days });
        }

        var configuration = new CabinetConfiguration();
        configuration.Storage.RetentionDays = globalDays;
        return new RetentionService(NullLogger<RetentionService>.Instance, storage, documents, Options.Create(configuration));
    }

    private static LogEvent Event(string index, int daysOld) => new()
    {
        RawLine = $"{index} {daysOld}",
        Index = index,
        Timestamp = DateTime.UtcNow.AddDays(-daysOld),
    };

    private static async Task<List<string>> LinesAsync(ILogStorage storage)
    {
        var lines = new List<string>();
        await foreach (var logEvent in storage.QueryAsync(new QueryContext()))
        {
            lines.Add(logEvent.RawLine);
        }

        return lines;
    }

    [Fact]
    public async Task EachIndex_FollowsItsPolicy_AndOthersTheGlobalSetting()
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync([
            Event("short", 2), Event("short", 20),
            Event("long", 20), Event("long", 200),
            Event("other", 20), Event("other", 40),
        ]);
        var service = await ServiceAsync(storage, new InMemoryDocumentStore(), 30, ("short", 7), ("long", 90));

        await service.ApplyRetentionPoliciesAsync();

        (await LinesAsync(storage)).Should().BeEquivalentTo("short 2", "long 20", "other 20");
    }

    [Fact]
    public async Task NothingConfigured_DeletesNothing()
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync([Event("main", 5000)]);
        var service = await ServiceAsync(storage, new InMemoryDocumentStore(), 0);

        await service.ApplyRetentionPoliciesAsync();

        (await LinesAsync(storage)).Should().ContainSingle();
    }

    [Fact]
    public async Task APolicy_LongerThanTheGlobalSetting_KeepsEventsTheGlobalSettingWouldDelete()
    {
        var storage = new InMemoryStorage();
        await storage.StoreBatchAsync([Event("audit", 60)]);
        var service = await ServiceAsync(storage, new InMemoryDocumentStore(), 30, ("audit", 365));

        await service.ApplyRetentionPoliciesAsync();

        (await LinesAsync(storage)).Should().ContainSingle();
    }
}

public sealed class AuditChainTests
{
    private readonly InMemoryDocumentStore _documents = new();

    private TamperEvidentAuditLogger CreateLogger() =>
        new(Mock.Of<IAuditLogger>(), _documents, NullLogger<TamperEvidentAuditLogger>.Instance);

    private static AuditEvent Event(string description) => new()
    {
        PrincipalId = "someone",
        Action = AuditAction.Search,
        ResourceType = "Query",
        Description = description,
        SourceIp = "10.0.0.1",
    };

    private async Task<(TamperEvidentAuditLogger Logger, string Chain)> WriteAsync(int count)
    {
        var logger = CreateLogger();
        for (var i = 0; i < count; i++)
        {
            await logger.LogAsync(Event($"event {i}"));
        }

        (await logger.VerifyAsync()).Valid.Should().BeTrue();
        var chains = await _documents.ListAsync<AuditChainInfo>("audit-chains");
        return (logger, chains.Single().Id);
    }

    [Fact]
    public async Task IntactChain_Verifies()
    {
        var (logger, _) = await WriteAsync(5);

        var result = await logger.VerifyAsync();

        result.Valid.Should().BeTrue();
        result.RecordsChecked.Should().Be(5);
    }

    [Fact]
    public async Task ChangingAnyField_IsDetected()
    {
        var (logger, chain) = await WriteAsync(5);
        var collection = $"audit-records-{chain}";
        var record = await _documents.GetAsync<AuditRecord>(collection, "000000000000000002");

        // Fields the old hash left out, which could be changed unnoticed
        record!.Event.SourceIp = "1.2.3.4";
        await _documents.PutAsync(collection, "000000000000000002", record);

        var result = await logger.VerifyAsync();

        result.Valid.Should().BeFalse();
        result.Problems.Should().ContainSingle().Which.Should().Contain("record 2 was changed");
    }

    [Fact]
    public async Task KeyedChain_RewrittenWithoutTheKey_IsDetected()
    {
        const string key = "a-key-of-at-least-thirty-two-characters";
        var logger = new TamperEvidentAuditLogger(Mock.Of<IAuditLogger>(), _documents, NullLogger<TamperEvidentAuditLogger>.Instance, key);
        for (var i = 0; i < 3; i++)
        {
            await logger.LogAsync(Event($"event {i}"));
        }

        (await logger.VerifyAsync()).Valid.Should().BeTrue();
        var chain = (await _documents.ListAsync<AuditChainInfo>("audit-chains")).Single();
        var collection = $"audit-records-{chain.Id}";

        // Someone with database access changes a record and recomputes every hash after it,
        // which is all an unkeyed chain needs
        var previous = TamperEvidentAuditLogger.Genesis;
        foreach (var record in (await _documents.ListAsync<AuditRecord>(collection)).OrderBy(record => record.Sequence))
        {
            record.Event.Description = "nothing to see";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previous + "|" + JsonSerializer.Serialize(record.Event)))).ToLowerInvariant();
            await _documents.PutAsync(collection, record.Sequence.ToString("D18"), new AuditRecord { Sequence = record.Sequence, PreviousHash = previous, Hash = hash, Event = record.Event });
            previous = hash;
        }

        chain.HeadHash = previous;
        await _documents.PutAsync("audit-chains", chain.Id, chain);

        (await logger.VerifyAsync()).Problems.Should().Contain(problem => problem.Contains("was changed"));
    }

    [Fact]
    public async Task KeyedChain_UnderAnotherKey_IsReported()
    {
        var writer = new TamperEvidentAuditLogger(Mock.Of<IAuditLogger>(), _documents, NullLogger<TamperEvidentAuditLogger>.Instance, new string('a', 32));
        await writer.LogAsync(Event("x"));
        (await writer.VerifyAsync()).Valid.Should().BeTrue();

        var reader = new TamperEvidentAuditLogger(Mock.Of<IAuditLogger>(), _documents, NullLogger<TamperEvidentAuditLogger>.Instance, new string('b', 32));

        (await reader.VerifyAsync()).Problems.Should().Contain(problem => problem.Contains("isn't the configured"));
    }

    [Fact]
    public async Task RemovingFromTheMiddle_IsDetected()
    {
        var (logger, chain) = await WriteAsync(5);

        await _documents.DeleteAsync($"audit-records-{chain}", "000000000000000002");

        (await logger.VerifyAsync()).Problems.Should().Contain(problem => problem.Contains("missing"));
    }

    [Fact]
    public async Task RemovingFromTheEnd_IsDetected()
    {
        var (logger, chain) = await WriteAsync(5);

        await _documents.DeleteAsync($"audit-records-{chain}", "000000000000000004");

        (await logger.VerifyAsync()).Problems.Should().Contain(problem => problem.Contains("missing from the end"));
    }

    [Fact]
    public async Task RemovingFromTheStart_IsDetected()
    {
        var (logger, chain) = await WriteAsync(5);

        await _documents.DeleteAsync($"audit-records-{chain}", "000000000000000000");

        (await logger.VerifyAsync()).Valid.Should().BeFalse();
    }

    [Fact]
    public async Task Pruning_IsNotMistakenForTampering()
    {
        var (logger, _) = await WriteAsync(5);

        var removed = await logger.PruneAsync(DateTime.UtcNow.AddDays(1));

        removed.Should().Be(5);
        await logger.LogAsync(Event("after pruning"));
        (await logger.VerifyAsync()).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task EveryInstance_ReadsEveryChain()
    {
        var first = CreateLogger();
        var second = CreateLogger();
        await first.LogAsync(Event("from the first"));
        await second.LogAsync(Event("from the second"));
        await first.VerifyAsync();
        var result = await second.VerifyAsync();

        result.Valid.Should().BeTrue();
        result.ChainsChecked.Should().Be(2);
        (await first.QueryAsync()).Select(e => e.Description).Should().BeEquivalentTo("from the first", "from the second");
    }
}

public sealed class LocalBackupTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-backup-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private string DatabasePath => Path.Combine(_directory, "events.db");

    private LocalBackupService CreateService() => new(
        new BackupConfiguration { ArchivePath = Path.Combine(_directory, "backups") },
        DatabasePath,
        NullLogger<LocalBackupService>.Instance);

    private async Task<SqliteLogStorage> CreateStorageAsync()
    {
        var configuration = new CabinetConfiguration();
        configuration.Storage.ConnectionString = $"Data Source={DatabasePath}";
        var storage = new SqliteLogStorage(Options.Create(configuration), NullLogger<SqliteLogStorage>.Instance);
        await storage.InitializeAsync();
        return storage;
    }

    private static LogEvent Event(string line) => new() { RawLine = line, Timestamp = DateTime.UtcNow };

    [Fact]
    public async Task Backup_IncludesEventsStillInTheWal_AndRestoresThem()
    {
        var storage = await CreateStorageAsync();
        await storage.StoreBatchAsync([Event("first"), Event("second")]);

        var backup = await CreateService().CreateBackupAsync();
        (await CreateService().VerifyBackupAsync(backup.Id)).Should().BeTrue();

        await storage.StoreBatchAsync([Event("after the backup")]);
        SqliteConnection.ClearAllPools();

        await LocalBackupService.RestoreAsync(backup.FilePath, DatabasePath, overwrite: true);

        var restored = await CreateStorageAsync();
        (await restored.GetCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Restore_WontReplaceADatabase_UnlessToldTo()
    {
        var storage = await CreateStorageAsync();
        await storage.StoreBatchAsync([Event("kept")]);
        var backup = await CreateService().CreateBackupAsync();
        SqliteConnection.ClearAllPools();

        var restore = () => LocalBackupService.RestoreAsync(backup.FilePath, DatabasePath, overwrite: false);

        await restore.Should().ThrowAsync<InvalidOperationException>().WithMessage("*--force*");
    }

    [Fact]
    public async Task CorruptBackup_FailsVerification_AndLeavesTheDatabaseAlone()
    {
        var storage = await CreateStorageAsync();
        await storage.StoreBatchAsync([Event("kept")]);
        var backup = await CreateService().CreateBackupAsync();
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(backup.FilePath);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(backup.FilePath, bytes);

        (await CreateService().VerifyBackupAsync(backup.Id)).Should().BeFalse();

        var restore = () => LocalBackupService.RestoreAsync(backup.FilePath, DatabasePath, overwrite: true);
        await restore.Should().ThrowAsync<Exception>();

        var after = await CreateStorageAsync();
        (await after.GetCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CancelledBackup_LeavesNoFileBehind()
    {
        var storage = await CreateStorageAsync();
        await storage.StoreBatchAsync([Event("kept")]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var create = () => CreateService().CreateBackupAsync(cancellationToken: cancelled.Token);

        await create.Should().ThrowAsync<OperationCanceledException>();
        var directory = Path.Combine(_directory, "backups");
        (Directory.Exists(directory) ? Directory.GetFiles(directory, "*.gz*") : []).Should().BeEmpty();
    }
}

/// <summary>Operations through the real API process.</summary>
public sealed class OperationsApiTests : IDisposable
{
    private const string AdminKey = "ops-admin-key";
    private const string BasicKey = "ops-basic-key";

    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-ops-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 9)
            {
                // Windows holds files briefly after a process is killed
                Task.Delay(500).Wait();
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private ProcessStartInfo CreateStartInfo(int port, Dictionary<string, string>? settings)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(LogsController).Assembly.Location);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["Storage__Type"] = "SQLite";
        startInfo.Environment["Storage__ConnectionString"] = $"Data Source={Path.Combine(_directory, "logs.db")}";
        startInfo.Environment["Storage__RetentionDays"] = "0";
        startInfo.Environment["Backup__ArchivePath"] = Path.Combine(_directory, "backups");
        startInfo.Environment["ApiKeys__Keys__0__Key"] = AdminKey;
        startInfo.Environment["ApiKeys__Keys__0__Tier"] = "Admin";
        startInfo.Environment["ApiKeys__Keys__1__Key"] = BasicKey;
        startInfo.Environment["ApiKeys__Keys__1__Tier"] = "Basic";
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    private async Task<(Process Process, HttpClient Client)> StartAsync(Dictionary<string, string>? settings = null)
    {
        var port = FreePort();
        var process = Process.Start(CreateStartInfo(port, settings))!;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            process.HasExited.Should().BeFalse("the API should keep running");
            try
            {
                using var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return (process, client);
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The API didn't become ready");
    }

    private static async Task StopAsync(Process process, HttpClient client)
    {
        client.Dispose();
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-API-Key", key);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, string key, object? body = null)
    {
        using var response = await client.SendAsync(Request(method, path, key, body));
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{method} {path} returned {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<int> CountAsync(HttpClient client)
    {
        var result = await SendAsync(client, HttpMethod.Post, "/api/v1/query/execute", AdminKey, new { query = "find * | stats count" });
        return result.GetProperty("results")[0].GetProperty("count").GetInt32();
    }

    [Fact]
    public async Task BackupDrill_RestoresEverythingToAFreshInstall()
    {
        var (first, client) = await StartAsync();
        string backupFile;
        try
        {
            for (var i = 0; i < 25; i++)
            {
                await SendAsync(client, HttpMethod.Post, "/services/collector/event", AdminKey, new { @event = $"event {i}" });
            }

            // State other than events has to come back as well
            await SendAsync(client, HttpMethod.Put, "/api/v1/retention/main", AdminKey, new { maxAgeDays = 42 });

            var backup = await SendAsync(client, HttpMethod.Post, "/api/v1/backups", AdminKey);
            var verified = await SendAsync(client, HttpMethod.Post, $"/api/v1/backups/{backup.GetProperty("id").GetString()}/verify", AdminKey);
            verified.GetProperty("valid").GetBoolean().Should().BeTrue();
            backupFile = backup.GetProperty("filePath").GetString()!;
        }
        finally
        {
            await StopAsync(first, client);
        }

        // Lose everything
        SqliteConnection.ClearAllPools();
        await Task.Delay(500);
        foreach (var file in Directory.GetFiles(_directory, "logs.db*"))
        {
            File.Delete(file);
        }

        var restoreInfo = CreateStartInfo(FreePort(), null);
        restoreInfo.ArgumentList.Add("--restore");
        restoreInfo.ArgumentList.Add(backupFile);
        using (var restore = Process.Start(restoreInfo)!)
        {
            var output = restore.StandardOutput.ReadToEndAsync();
            var errors = restore.StandardError.ReadToEndAsync();
            await restore.WaitForExitAsync();
            restore.ExitCode.Should().Be(0, await output + await errors);
        }

        var (second, restored) = await StartAsync();
        try
        {
            (await CountAsync(restored)).Should().Be(25);
            var policy = await SendAsync(restored, HttpMethod.Get, "/api/v1/retention/main", AdminKey);
            policy.GetProperty("maxAgeDays").GetInt32().Should().Be(42);
        }
        finally
        {
            await StopAsync(second, restored);
        }
    }

    [Fact]
    public async Task MaskingRules_ApplyToEveryRead_ExceptForExemptRoles()
    {
        var (process, client) = await StartAsync(new Dictionary<string, string>
        {
            ["Masking__Rules__0__FieldPattern"] = "card",
            ["Masking__Rules__0__Strategy"] = "Redact",
            ["Masking__Rules__0__ExemptRoles__0"] = "SuperAdmin",
        });
        try
        {
            await SendAsync(client, HttpMethod.Post, "/services/collector/event", AdminKey, new { @event = "paid card=4111111111111111 amount=5" });

            var viewer = await SendAsync(client, HttpMethod.Post, "/api/v1/query/execute", BasicKey, new { query = "find *" });
            viewer.ToString().Should().NotContain("4111111111111111").And.Contain("REDACTED");

            var logs = await SendAsync(client, HttpMethod.Get, "/api/v1/logs?count=5", BasicKey);
            logs.ToString().Should().NotContain("4111111111111111");

            var admin = await SendAsync(client, HttpMethod.Post, "/api/v1/query/execute", AdminKey, new { query = "find *" });
            admin.ToString().Should().Contain("4111111111111111");
        }
        finally
        {
            await StopAsync(process, client);
        }
    }

    [Fact]
    public async Task AuditLog_RecordsActionsAcrossARestart_AndVerifies()
    {
        var (first, client) = await StartAsync();
        try
        {
            await SendAsync(client, HttpMethod.Post, "/api/v1/query/execute", BasicKey, new { query = "find *" });
            await SendAsync(client, HttpMethod.Get, "/api/v1/logs?count=1", BasicKey);
            (await SendAsync(client, HttpMethod.Get, "/api/v1/audit/verify", AdminKey)).GetProperty("valid").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await StopAsync(first, client);
        }

        var (second, restarted) = await StartAsync();
        try
        {
            var events = await SendAsync(restarted, HttpMethod.Get, "/api/v1/audit?limit=100", AdminKey);
            events.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);

            var result = await SendAsync(restarted, HttpMethod.Get, "/api/v1/audit/verify", AdminKey);
            result.GetProperty("valid").GetBoolean().Should().BeTrue(result.ToString());
            result.GetProperty("recordsChecked").GetInt64().Should().BeGreaterThanOrEqualTo(2);

            using var denied = await restarted.SendAsync(Request(HttpMethod.Get, "/api/v1/audit", BasicKey));
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await StopAsync(second, restarted);
        }
    }

    [Fact]
    public async Task Traces_ReachAnOtlpReceiver()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stopping = new CancellationTokenSource();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = ReceiveSpansAsync(listener, received, stopping.Token);

        var (process, client) = await StartAsync(new Dictionary<string, string>
        {
            ["Telemetry__OtlpEndpoint"] = $"http://127.0.0.1:{port}/v1/traces",
            ["Telemetry__OtlpProtocol"] = "http",
            ["OTEL_BSP_SCHEDULE_DELAY"] = "100",
        });
        try
        {
            await SendAsync(client, HttpMethod.Post, "/api/v1/query/execute", AdminKey, new { query = "find * | head 1" });

            var body = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            body.Should().Contain("query.execute").And.Contain("cabinet-api");
        }
        finally
        {
            await StopAsync(process, client);
            await stopping.CancelAsync();
            listener.Stop();
            await receiving;
        }
    }

    /// <summary>Accepts OTLP over HTTP until a batch mentions a query span. Protobuf keeps strings readable.</summary>
    private static async Task ReceiveSpansAsync(TcpListener listener, TaskCompletionSource<string> received, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
                var stream = connection.GetStream();
                var buffer = new byte[1 << 20];
                var total = 0;
                var contentLength = -1;
                var headerEnd = -1;
                while (headerEnd < 0 || total < headerEnd + contentLength)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    var text = Encoding.Latin1.GetString(buffer, 0, total);
                    if (headerEnd < 0 && text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var end and >= 0)
                    {
                        headerEnd = end + 4;
                        var match = System.Text.RegularExpressions.Regex.Match(text, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        contentLength = match.Success ? int.Parse(match.Groups[1].Value) : 0;
                    }
                }

                var body = Encoding.Latin1.GetString(buffer, 0, total);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellationToken);
                if (body.Contains("query.execute", StringComparison.Ordinal))
                {
                    received.TrySetResult(body);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            // Stopping, or the sender hung up
            return;
        }
    }
}
