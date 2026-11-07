using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Agent;

public sealed class FileStateRegistryTests : IDisposable
{
    private const string Fingerprint = "FINGERPRINT";

    private readonly string _tempDir = Directory.CreateTempSubdirectory("cabinet-registry-").FullName;

    private string RegistryPath => Path.Combine(_tempDir, "registry.json");

    private FileStateRegistry Create()
    {
        var registry = new FileStateRegistry(RegistryPath, 60, NullLogger<FileStateRegistry>.Instance);
        registry.Initialize();
        return registry;
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GetState_UnknownPath_ReturnsNull()
    {
        using var registry = Create();

        registry.GetState("/nonexistent/file.log").Should().BeNull();
    }

    [Fact]
    public void Resolve_NewFile_StartsFromZeroWithItsOwnKey()
    {
        using var registry = Create();

        var first = registry.Resolve("/logs/a.log", Fingerprint, 100);
        var second = registry.Resolve("/logs/b.log", "OTHER", 100);

        first.Offset.Should().Be(0);
        first.FileKey.Should().NotBeEmpty();
        second.FileKey.Should().NotBe(first.FileKey);
    }

    [Fact]
    public void Resolve_SameFileGrown_Continues()
    {
        using var registry = Create();
        var state = registry.Resolve("/logs/a.log", Fingerprint, 100);
        registry.Commit(state.FileKey, 100, 5);

        var resumed = registry.Resolve("/logs/a.log", Fingerprint, 200);

        resumed.FileKey.Should().Be(state.FileKey);
        resumed.Offset.Should().Be(100);
    }

    [Fact]
    public void Resolve_DifferentFirstLine_StartsOver()
    {
        using var registry = Create();
        var state = registry.Resolve("/logs/a.log", Fingerprint, 100);
        registry.Commit(state.FileKey, 100, 5);

        var rotated = registry.Resolve("/logs/a.log", "OTHER", 500);

        rotated.FileKey.Should().NotBe(state.FileKey);
        rotated.Offset.Should().Be(0);
    }

    [Fact]
    public void Resolve_SameFirstLineButSmaller_StartsOver()
    {
        using var registry = Create();
        var state = registry.Resolve("/logs/a.log", Fingerprint, 100);
        registry.Commit(state.FileKey, 100, 5);

        var replaced = registry.Resolve("/logs/a.log", Fingerprint, 10);

        replaced.FileKey.Should().NotBe(state.FileKey);
        replaced.Offset.Should().Be(0);
    }

    [Fact]
    public void Resolve_RenamedFile_KeepsItsPosition()
    {
        using var registry = Create();
        var original = WriteFile("app.log", "first line\nsecond\n");
        var fingerprint = FileStateRegistry.ComputeFingerprint(original)!;
        var state = registry.Resolve(original, fingerprint, 18);
        registry.Commit(state.FileKey, 18, 2);

        var renamed = Path.Combine(_tempDir, "app.log.1");
        File.Move(original, renamed);
        var resolved = registry.Resolve(renamed, fingerprint, 18);

        resolved.FileKey.Should().Be(state.FileKey);
        resolved.Offset.Should().Be(18);
        registry.GetState(original).Should().BeNull();
    }

    [Fact]
    public void Resolve_RotatedFileFoundAfterNewFileStarted_KeepsTheNewFilesState()
    {
        // Found by chaos testing: picking up the rotated file removed whatever state was at its
        // old path, which by then was the new file's. The new file then lost its checkpoints.
        using var registry = Create();
        var logPath = Path.Combine(_tempDir, "app.log");
        var rotated = WriteFile("app.log.1", "old first\nold second\n");
        var oldFingerprint = FileStateRegistry.ComputeFingerprint(rotated)!;
        var old = registry.Resolve(logPath, oldFingerprint, 21);
        registry.Commit(old.FileKey, 10, 1);

        var current = registry.Resolve(logPath, "NEW FILE", 5);
        var adopted = registry.Resolve(rotated, oldFingerprint, 21);

        adopted.FileKey.Should().Be(old.FileKey);
        registry.GetState(logPath).Should().BeSameAs(current);

        registry.Commit(current.FileKey, 5, 1);
        current.Offset.Should().Be(5);
    }

    [Fact]
    public void Resolve_OtherFileStillHoldingTheSameFirstLine_IsNotARename()
    {
        using var registry = Create();
        var original = WriteFile("a.log", "# header\none\n");
        var copy = WriteFile("b.log", "# header\ntwo\n");
        var fingerprint = FileStateRegistry.ComputeFingerprint(original)!;
        var state = registry.Resolve(original, fingerprint, 13);
        registry.Commit(state.FileKey, 13, 2);

        var resolved = registry.Resolve(copy, fingerprint, 13);

        resolved.FileKey.Should().NotBe(state.FileKey);
        resolved.Offset.Should().Be(0);
    }

    [Fact]
    public void Commit_AfterRotation_IsIgnored()
    {
        using var registry = Create();
        var old = registry.Resolve("/logs/a.log", Fingerprint, 100);
        var current = registry.Resolve("/logs/a.log", "OTHER", 100);

        registry.Commit(old.FileKey, 100, 5);

        current.Offset.Should().Be(0, "that offset belongs to the old contents");
    }

    [Fact]
    public void Commit_AfterRotation_StillAppliesToTheOldContents()
    {
        // Events read before a rotation can be delivered after it. If the old file turns up under
        // a new name, reading must continue after them.
        using var registry = Create();
        var renamed = WriteFile("app.log.1", "first\nsecond\n");
        var fingerprint = FileStateRegistry.ComputeFingerprint(renamed)!;
        var old = registry.Resolve("/logs/app.log", fingerprint, 13);
        registry.Resolve("/logs/app.log", "OTHER", 5);

        registry.Commit(old.FileKey, 6, 1);
        var resolved = registry.Resolve(renamed, fingerprint, 13);

        resolved.FileKey.Should().Be(old.FileKey);
        resolved.Offset.Should().Be(6);
    }

    [Fact]
    public void Commit_NeverMovesBackwards()
    {
        using var registry = Create();
        var state = registry.Resolve("/logs/a.log", Fingerprint, 100);

        registry.Commit(state.FileKey, 100, 5);
        registry.Commit(state.FileKey, 50, 2);

        state.Offset.Should().Be(100);
    }

    [Fact]
    public void Remove_DeletesState()
    {
        using var registry = Create();
        registry.Resolve("/logs/a.log", Fingerprint, 100);

        registry.Remove("/logs/a.log");

        registry.GetState("/logs/a.log").Should().BeNull();
    }

    [Fact]
    public void SaveAndLoad_PersistsState()
    {
        string key;
        using (var registry = Create())
        {
            var state = registry.Resolve("/logs/a.log", Fingerprint, 4096);
            key = state.FileKey;
            registry.Commit(key, 2048, 75);
            registry.Save();
        }

        using var loaded = Create();
        var restored = loaded.GetState("/logs/a.log")!;

        restored.FileKey.Should().Be(key);
        restored.Offset.Should().Be(2048);
        restored.LinesRead.Should().Be(75);
        restored.Fingerprint.Should().Be(Fingerprint);
    }

    [Fact]
    public void ComputeFingerprint_DoesNotChangeAsTheFileGrows()
    {
        var path = WriteFile("grow.log", "first line\n");
        var before = FileStateRegistry.ComputeFingerprint(path);

        File.AppendAllText(path, "second\n");
        File.AppendAllText(path, new string('x', 5000) + "\n");

        before.Should().NotBeNull();
        FileStateRegistry.ComputeFingerprint(path).Should().Be(before);
    }

    [Fact]
    public void ComputeFingerprint_DiffersByFirstLine()
    {
        FileStateRegistry.ComputeFingerprint(WriteFile("a.log", "Content A\n"))
            .Should().NotBe(FileStateRegistry.ComputeFingerprint(WriteFile("b.log", "Content B\n")));
    }

    [Fact]
    public void ComputeFingerprint_WithoutCompleteFirstLine_IsNull()
    {
        FileStateRegistry.ComputeFingerprint(WriteFile("partial.log", "no newline yet")).Should().BeNull();
        FileStateRegistry.ComputeFingerprint(WriteFile("empty.log", string.Empty)).Should().BeNull();
    }

    [Fact]
    public void ComputeFingerprint_VeryLongFirstLine_UsesItsStart()
    {
        var path = WriteFile("long.log", new string('x', 2000));
        var before = FileStateRegistry.ComputeFingerprint(path);

        File.AppendAllText(path, new string('y', 2000) + "\n");

        before.Should().NotBeNull();
        FileStateRegistry.ComputeFingerprint(path).Should().Be(before);
    }

    [Fact]
    public void ComputeFingerprint_NonexistentFile_ReturnsNull()
    {
        FileStateRegistry.ComputeFingerprint(Path.Combine(_tempDir, "missing.log")).Should().BeNull();
    }

    [Fact]
    public void Initialize_MissingRegistryFile_StartsEmpty()
    {
        using var registry = Create();

        registry.GetTrackedPaths().Should().BeEmpty();
    }

    [Fact]
    public void Initialize_CorruptRegistryFile_StartsEmpty()
    {
        File.WriteAllText(RegistryPath, "not valid json{{{{");

        using var registry = Create();

        registry.GetTrackedPaths().Should().BeEmpty();
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }
}
