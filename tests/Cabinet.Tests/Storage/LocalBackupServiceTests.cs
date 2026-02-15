using Cabinet.API.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Storage;

public class LocalBackupServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _archiveDir;
    private readonly string _testDbPath;

    public LocalBackupServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "cabinet-backup-test-" + Guid.NewGuid().ToString("N")[..8]);
        _archiveDir = Path.Combine(_testDir, "backups");
        _testDbPath = Path.Combine(_testDir, "test.db");

        Directory.CreateDirectory(_testDir);

        // Create a dummy SQLite database file
        File.WriteAllText(_testDbPath, "SQLite format 3\0" + new string('X', 4096));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private LocalBackupService CreateService(int retentionDays = 30)
    {
        var config = new BackupConfiguration
        {
            ArchivePath = _archiveDir,
            RetentionDays = retentionDays
        };

        return new LocalBackupService(
            config,
            _testDbPath,
            NullLogger<LocalBackupService>.Instance);
    }

    [Fact]
    public async Task CreateBackupAsync_CreatesGzippedFile()
    {
        var service = CreateService();
        var entry = await service.CreateBackupAsync("full");

        entry.Should().NotBeNull();
        entry.FileName.Should().EndWith(".db.gz");
        entry.SizeBytes.Should().BeGreaterThan(0);
        entry.BackupType.Should().Be("full");
        entry.Checksum.Should().NotBeNullOrWhiteSpace();
        entry.SourceDatabase.Should().Be("test.db");
        File.Exists(entry.FilePath).Should().BeTrue();
    }

    [Fact]
    public async Task CreateBackupAsync_CompressedFileIsSmaller()
    {
        // A large enough file to benefit from compression
        File.WriteAllBytes(_testDbPath, new byte[10000]);

        var service = CreateService();
        var entry = await service.CreateBackupAsync();

        var originalSize = new FileInfo(_testDbPath).Length;
        entry.SizeBytes.Should().BeLessThan(originalSize);
    }

    [Fact]
    public async Task CreateBackupAsync_NonExistentDatabase_ThrowsFileNotFound()
    {
        var config = new BackupConfiguration { ArchivePath = _archiveDir };
        var service = new LocalBackupService(
            config,
            "/nonexistent/path/database.db",
            NullLogger<LocalBackupService>.Instance);

        var act = () => service.CreateBackupAsync();
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ListBackupsAsync_ReturnsAllCreatedBackups()
    {
        var service = CreateService();
        await service.CreateBackupAsync("full");
        await Task.Delay(50); // Ensure different timestamps
        await service.CreateBackupAsync("auto");

        var backups = await service.ListBackupsAsync();

        backups.Should().HaveCount(2);
        // Should be ordered by creation date descending (most recent first)
        backups[0].CreatedAtUtc.Should().BeOnOrAfter(backups[1].CreatedAtUtc);
    }

    [Fact]
    public async Task ListBackupsAsync_EmptyManifest_ReturnsEmptyList()
    {
        var service = CreateService();
        var backups = await service.ListBackupsAsync();
        backups.Should().BeEmpty();
    }

    [Fact]
    public async Task CleanupOldBackupsAsync_DeletesExpiredBackups()
    {
        var service = CreateService(retentionDays: 0);
        // RetentionDays=0 means unlimited, no cleanup
        var result = await service.CleanupOldBackupsAsync();
        result.Should().Be(0);
    }

    [Fact]
    public async Task CleanupOldBackupsAsync_NoExpiredBackups_ReturnsZero()
    {
        var service = CreateService(retentionDays: 30);
        await service.CreateBackupAsync();

        var deleted = await service.CleanupOldBackupsAsync();
        deleted.Should().Be(0);
    }

    [Fact]
    public async Task GetLastBackupTimeAsync_NoBackups_ReturnsNull()
    {
        var service = CreateService();
        var lastTime = await service.GetLastBackupTimeAsync();
        lastTime.Should().BeNull();
    }

    [Fact]
    public async Task GetLastBackupTimeAsync_AfterBackup_ReturnsTime()
    {
        var service = CreateService();
        var before = DateTime.UtcNow;
        await service.CreateBackupAsync();

        var lastTime = await service.GetLastBackupTimeAsync();
        lastTime.Should().NotBeNull();
        lastTime!.Value.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task VerifyBackupAsync_ValidBackup_ReturnsTrue()
    {
        var service = CreateService();
        var entry = await service.CreateBackupAsync();

        var isValid = await service.VerifyBackupAsync(entry.Id);
        isValid.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyBackupAsync_NonExistentId_ReturnsFalse()
    {
        var service = CreateService();
        var isValid = await service.VerifyBackupAsync("nonexistent-backup");
        isValid.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBackupAsync_MultipleBackups_UniqueIds()
    {
        var service = CreateService();
        var b1 = await service.CreateBackupAsync();
        var b2 = await service.CreateBackupAsync();
        var b3 = await service.CreateBackupAsync();

        b1.Id.Should().NotBe(b2.Id);
        b2.Id.Should().NotBe(b3.Id);
        b1.FileName.Should().NotBe(b2.FileName);
    }

    [Fact]
    public async Task ManifestPersistence_ServiceRecreated_BackupsStillVisible()
    {
        var config = new BackupConfiguration { ArchivePath = _archiveDir, RetentionDays = 30 };

        // Create backup with first instance
        var service1 = new LocalBackupService(
            config, _testDbPath, NullLogger<LocalBackupService>.Instance);
        var entry = await service1.CreateBackupAsync("full");

        // Create new service instance (simulating restart) reading same archive dir
        var service2 = new LocalBackupService(
            config, _testDbPath, NullLogger<LocalBackupService>.Instance);
        var backups = await service2.ListBackupsAsync();

        backups.Should().HaveCount(1);
        backups[0].Id.Should().Be(entry.Id);
        backups[0].Checksum.Should().Be(entry.Checksum);
    }

    [Fact]
    public void ExtractDatabasePath_StandardFormat_ExtractsPath()
    {
        var path = LocalBackupService.ExtractDatabasePath("Data Source=cabinet.db");
        path.Should().Be("cabinet.db");
    }

    [Fact]
    public void ExtractDatabasePath_WithOptions_ExtractsPath()
    {
        var path = LocalBackupService.ExtractDatabasePath("Data Source=./data/cabinet.db;Cache=Shared");
        path.Should().Be("./data/cabinet.db");
    }

    [Fact]
    public void ExtractDatabasePath_MissingDataSource_Throws()
    {
        var act = () => LocalBackupService.ExtractDatabasePath("SomeOtherFormat=value");
        act.Should().Throw<InvalidOperationException>();
    }
}
