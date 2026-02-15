using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cabinet.Tests.Agent;

public class FileStateRegistryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _registryPath;
    private readonly Mock<ILogger<FileStateRegistry>> _loggerMock = new();

    public FileStateRegistryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cabinet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _registryPath = Path.Combine(_tempDir, "registry.json");
    }

    [Fact]
    public void GetState_UnknownPath_ReturnsNull()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        var state = registry.GetState("/nonexistent/file.log");

        state.Should().BeNull();
    }

    [Fact]
    public void UpdateState_TracksOffsetAndLines()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.UpdateState("/var/log/test.log", 1024, 2048, 50);

        var state = registry.GetState("/var/log/test.log");
        state.Should().NotBeNull();
        state!.Offset.Should().Be(1024);
        state.LastKnownSize.Should().Be(2048);
        state.LinesRead.Should().Be(50);
    }

    [Fact]
    public void RegisterFile_SetsHeaderHash()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.RegisterFile("/var/log/test.log", 4096, "ABC123");

        var state = registry.GetState("/var/log/test.log");
        state.Should().NotBeNull();
        state!.HeaderHash.Should().Be("ABC123");
        state.LastKnownSize.Should().Be(4096);
    }

    [Fact]
    public void ResetOffset_SetsOffsetToZero()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.UpdateState("/var/log/test.log", 5000, 5000, 100);
        registry.ResetOffset("/var/log/test.log");

        var state = registry.GetState("/var/log/test.log");
        state.Should().NotBeNull();
        state!.Offset.Should().Be(0);
        state.LinesRead.Should().Be(0);
    }

    [Fact]
    public void Remove_DeletesState()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.UpdateState("/var/log/test.log", 100, 200, 10);
        registry.Remove("/var/log/test.log");

        registry.GetState("/var/log/test.log").Should().BeNull();
    }

    [Fact]
    public void GetTrackedPaths_ReturnsAllPaths()
    {
        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.UpdateState("/var/log/a.log", 100, 200, 5);
        registry.UpdateState("/var/log/b.log", 300, 400, 15);

        var paths = registry.GetTrackedPaths();
        paths.Should().HaveCount(2);
        paths.Should().Contain("/var/log/a.log");
        paths.Should().Contain("/var/log/b.log");
    }

    [Fact]
    public void SaveAndLoad_PersistsState()
    {
        // Save
        using (var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object))
        {
            registry.Initialize();
            registry.UpdateState("/var/log/test.log", 2048, 4096, 75);
            registry.RegisterFile("/var/log/test.log", 4096, "HASH123");
            registry.Save();
        }

        // Load in new instance
        using (var registry2 = new FileStateRegistry(_registryPath, 60, _loggerMock.Object))
        {
            registry2.Initialize();

            var state = registry2.GetState("/var/log/test.log");
            state.Should().NotBeNull();
            state!.Offset.Should().Be(2048);
            state.LinesRead.Should().Be(75);
            state.HeaderHash.Should().Be("HASH123");
        }
    }

    [Fact]
    public void ComputeHeaderHash_ReturnsConsistentHash()
    {
        var testFile = Path.Combine(_tempDir, "test.log");
        File.WriteAllText(testFile, "Hello, this is a test log file.\nLine 2\nLine 3\n");

        var hash1 = FileStateRegistry.ComputeHeaderHash(testFile);
        var hash2 = FileStateRegistry.ComputeHeaderHash(testFile);

        hash1.Should().NotBeNull();
        hash2.Should().NotBeNull();
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ComputeHeaderHash_DifferentContent_DifferentHash()
    {
        var file1 = Path.Combine(_tempDir, "test1.log");
        var file2 = Path.Combine(_tempDir, "test2.log");
        File.WriteAllText(file1, "Content A\n");
        File.WriteAllText(file2, "Content B\n");

        var hash1 = FileStateRegistry.ComputeHeaderHash(file1);
        var hash2 = FileStateRegistry.ComputeHeaderHash(file2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void ComputeHeaderHash_NonexistentFile_ReturnsNull()
    {
        var hash = FileStateRegistry.ComputeHeaderHash("/nonexistent/file.log");
        hash.Should().BeNull();
    }

    [Fact]
    public void Initialize_MissingRegistryFile_StartsEmpty()
    {
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent", "registry.json");
        using var registry = new FileStateRegistry(nonExistentPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.GetTrackedPaths().Should().BeEmpty();
    }

    [Fact]
    public void Initialize_CorruptRegistryFile_StartsEmpty()
    {
        File.WriteAllText(_registryPath, "not valid json{{{{");

        using var registry = new FileStateRegistry(_registryPath, 60, _loggerMock.Object);
        registry.Initialize();

        registry.GetTrackedPaths().Should().BeEmpty();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
