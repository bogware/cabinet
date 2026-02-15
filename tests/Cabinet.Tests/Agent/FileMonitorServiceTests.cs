using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Xunit;

namespace Cabinet.Tests.Agent;

public class FileMonitorServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FileMonitorServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cabinet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ExpandGlob_DirectFilePath_ReturnsFileIfExists()
    {
        var testFile = Path.Combine(_tempDir, "test.log");
        File.WriteAllText(testFile, "line1");

        var result = FileMonitorService.ExpandGlob(testFile, recursive: false);

        result.Should().HaveCount(1);
        result[0].Should().Be(Path.GetFullPath(testFile));
    }

    [Fact]
    public void ExpandGlob_DirectFilePath_ReturnsEmptyIfNotExists()
    {
        var result = FileMonitorService.ExpandGlob(
            Path.Combine(_tempDir, "nonexistent.log"), recursive: false);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExpandGlob_WildcardPattern_FindsMatchingFiles()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "error.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "data");

        var pattern = Path.Combine(_tempDir, "*.log");
        var result = FileMonitorService.ExpandGlob(pattern, recursive: false);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(f => f.EndsWith(".log"));
    }

    [Fact]
    public void ExpandGlob_RecursivePattern_FindsNestedFiles()
    {
        var subDir = Path.Combine(_tempDir, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(_tempDir, "root.log"), "data");
        File.WriteAllText(Path.Combine(subDir, "nested.log"), "data");

        var pattern = Path.Combine(_tempDir, "*.log");
        var result = FileMonitorService.ExpandGlob(pattern, recursive: true);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ExpandGlob_NonexistentDirectory_ReturnsEmpty()
    {
        var pattern = Path.Combine(_tempDir, "nonexistent", "*.log");
        var result = FileMonitorService.ExpandGlob(pattern, recursive: false);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExpandGlob_QuestionMarkWildcard_MatchesSingleChar()
    {
        File.WriteAllText(Path.Combine(_tempDir, "app1.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "app2.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "app10.log"), "data");

        var pattern = Path.Combine(_tempDir, "app?.log");
        var result = FileMonitorService.ExpandGlob(pattern, recursive: false);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void ExpandGlob_ResultsSortedAlphabetically()
    {
        File.WriteAllText(Path.Combine(_tempDir, "charlie.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "alpha.log"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "bravo.log"), "data");

        var pattern = Path.Combine(_tempDir, "*.log");
        var result = FileMonitorService.ExpandGlob(pattern, recursive: false);

        result.Should().HaveCount(3);
        Path.GetFileName(result[0]).Should().Be("alpha.log");
        Path.GetFileName(result[1]).Should().Be("bravo.log");
        Path.GetFileName(result[2]).Should().Be("charlie.log");
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
