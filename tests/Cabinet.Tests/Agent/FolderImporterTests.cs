using System.Text;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Importing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Agent;

public sealed class FolderImporterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-import-").FullName;
    private readonly FakeCollector _collector = new();

    private string Folder(string name)
    {
        var path = Path.Combine(_directory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    private async Task<ImportResult> ImportAsync(string path, IReadOnlySet<string>? skip = null, string host = "game-1")
    {
        var output = new OutputConfiguration
        {
            ServerUrl = "http://collector.invalid",
            ApiKey = "test",
            SpillDirectory = Path.Combine(_directory, "spool"),
            BatchSize = 50,
            FlushIntervalSeconds = 1,
            MaxRetries = 1,
            RetryDelayMilliseconds = 1,
            ShutdownFlushSeconds = 5,
        };
        await using var forwarder = new LogForwarder(output, new HttpClient(_collector, disposeHandler: false), NullLogger<LogForwarder>.Instance);
        var importer = new FolderImporter(forwarder, NullLogger.Instance);
        return await importer.ImportAsync(new ImportOptions
        {
            Sources = [new ImportSource { Path = path, SourceType = "L4D2", Index = "l4d2" }],
            Hostname = host,
            TimeZone = "America/Chicago",
            SkipPaths = skip ?? new HashSet<string>(),
        });
    }

    [Fact]
    public async Task Folder_IsImportedWhole_IncludingSubfoldersAndAnUnfinishedLastLine()
    {
        var logs = Folder("logs");
        Write(Path.Combine(logs, "a.log"), "one\r\ntwo\n\nthree");
        Directory.CreateDirectory(Path.Combine(logs, "old"));
        Write(Path.Combine(logs, "old", "b.log"), "four\n");

        var result = await ImportAsync(logs);

        result.FilesImported.Should().Be(2);
        result.EventsSent.Should().Be(4);
        result.EventsNotDelivered.Should().Be(0);
        _collector.Stored.Should().BeEquivalentTo("one", "two", "three", "four");
    }

    [Fact]
    public async Task ImportingAgain_StoresNothingTwice_EvenFromAnotherFolderOrAfterTheFileGrew()
    {
        var first = Folder("first");
        Write(Path.Combine(first, "map.log"), "L 11/09/2026 - 07:12:05: Log file started\nsay hello\n");
        await ImportAsync(first);

        // The same file copied elsewhere, and grown since
        var second = Folder("second");
        Write(Path.Combine(second, "map.log"), "L 11/09/2026 - 07:12:05: Log file started\nsay hello\nsay bye\n");
        await ImportAsync(second);

        _collector.Stored.Should().BeEquivalentTo("L 11/09/2026 - 07:12:05: Log file started", "say hello", "say bye");
    }

    [Fact]
    public async Task DifferentFiles_WithTheSameFirstLine_AreBothKept()
    {
        var logs = Folder("logs");
        Write(Path.Combine(logs, "app.log"), "starting\nsame\n");
        Write(Path.Combine(logs, "app.1.log"), "starting\nsame\n");

        await ImportAsync(logs);

        _collector.Stored.Should().HaveCount(4, "two files with the same lines are still two files");
    }

    [Fact]
    public async Task SameFile_FromAnotherHost_IsKept()
    {
        var logs = Folder("logs");
        Write(Path.Combine(logs, "map.log"), "starting\n");

        await ImportAsync(logs, host: "dev");
        await ImportAsync(logs, host: "prod");

        _collector.Stored.Should().HaveCount(2);
    }

    [Fact]
    public async Task FilesTheAgentReads_AreLeftOut()
    {
        var logs = Folder("logs");
        Write(Path.Combine(logs, "old.log"), "history\n");
        Write(Path.Combine(logs, "current.log"), "live\n");

        var result = await ImportAsync(logs, new HashSet<string> { Path.Combine(logs, "current.log") });

        result.FilesSkipped.Should().Be(1);
        _collector.Stored.Should().BeEquivalentTo("history");
    }

    [Fact]
    public void AgentFiles_AreItsRegistryAndRecentFilesItsInputsMatch()
    {
        var logs = Folder("logs");
        var old = Path.Combine(logs, "old.log");
        var recent = Path.Combine(logs, "recent.log");
        var tracked = Path.Combine(logs, "tracked.log");
        var excluded = Path.Combine(logs, "recent.gz");
        foreach (var file in new[] { old, recent, tracked, excluded })
        {
            Write(file, "x\n");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(tracked, DateTime.UtcNow.AddDays(-3));
        var registry = Path.Combine(_directory, "registry.json");
        File.WriteAllText(registry, $$"""[{"path": {{System.Text.Json.JsonSerializer.Serialize(tracked)}}, "offset": 2}]""");

        var config = new AgentConfiguration
        {
            Inputs = [new InputConfiguration { Paths = [Path.Combine(logs, "*")], IgnoreOlderThanHours = 24, ExcludePatterns = ["*.gz"] }],
        };

        FolderImporter.AgentFiles(config, registry, DateTime.UtcNow).Should().BeEquivalentTo(recent, tracked);
    }

    [Fact]
    public void FileKey_DependsOnHostNameAndFirstLine_NotWhatFollows()
    {
        var a = FolderImporter.FileKey("dev", "/x/map.log", "first\nsecond\n"u8);
        FolderImporter.FileKey("dev", "/y/map.log", "first\nother\n"u8).Should().Be(a);
        FolderImporter.FileKey("prod", "/x/map.log", "first\n"u8).Should().NotBe(a);
        FolderImporter.FileKey("dev", "/x/map2.log", "first\n"u8).Should().NotBe(a);
        FolderImporter.FileKey("dev", "/x/map.log", "First\n"u8).Should().NotBe(a);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
