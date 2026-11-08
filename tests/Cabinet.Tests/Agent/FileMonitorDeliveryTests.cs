using System.Net;
using System.Text;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using Cabinet.Agent.Monitoring;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Agent;

/// <summary>
/// Runs the real monitor over real files. Each test checks what matters to a user: every line
/// written ends up stored exactly once, whatever happened along the way.
/// </summary>
public sealed class FileMonitorDeliveryTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-monitor-").FullName;
    private readonly FakeCollector _collector = new();
    private readonly List<RunningAgent> _running = [];

    private string LogDirectory => Path.Combine(_directory, "logs");

    private string LogFile => Path.Combine(LogDirectory, "app.log");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(LogDirectory);
        return Task.CompletedTask;
    }

    private sealed class RunningAgent(FileMonitorService monitor, FileStateRegistry registry, LogForwarder forwarder)
    {
        public LogForwarder Forwarder => forwarder;

        public async Task StopAsync()
        {
            await monitor.StopAsync(CancellationToken.None);
            registry.Dispose();
            await forwarder.DisposeAsync();
        }
    }

    private async Task<RunningAgent> StartAgent(Action<InputConfiguration>? configureInput = null, string? pattern = null, int saveIntervalSeconds = 3600)
    {
        var input = new InputConfiguration { Paths = [pattern ?? Path.Combine(LogDirectory, "*.log")] };
        configureInput?.Invoke(input);

        var config = new AgentConfiguration
        {
            Inputs = [input],
            Hostname = "test-host",
            ScanIntervalSeconds = 1,
            PollIntervalMilliseconds = 20,
            Output = new OutputConfiguration
            {
                ServerUrl = "http://collector.invalid",
                SpillDirectory = Path.Combine(_directory, "spool"),
                BatchSize = 50,
                FlushIntervalSeconds = 1,
                MaxRetries = 1,
                RetryDelayMilliseconds = 1,
                SpoolRetryIntervalSeconds = 1,
                ShutdownFlushSeconds = 5,
            },
        };

        var registry = new FileStateRegistry(Path.Combine(_directory, "registry.json"), saveIntervalSeconds, NullLogger<FileStateRegistry>.Instance);
        var forwarder = new LogForwarder(config.Output, new HttpClient(_collector, disposeHandler: false), NullLogger<LogForwarder>.Instance);
        var monitor = new FileMonitorService(config, registry, forwarder, NullLogger<FileMonitorService>.Instance);

        await monitor.StartAsync(CancellationToken.None);

        var agent = new RunningAgent(monitor, registry, forwarder);
        _running.Add(agent);
        return agent;
    }

    private async Task StopAgent(RunningAgent agent)
    {
        _running.Remove(agent);
        await agent.StopAsync();
    }

    [Fact]
    public async Task OldHistory_IsSkipped_UntilWrittenAgain()
    {
        // A game server's log folder can hold years of files, one per map
        var old = Path.Combine(LogDirectory, "old.log");
        await Append(old, "from last year");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));
        await Append(LogFile, "today");

        await StartAgent(input => input.IgnoreOlderThanHours = 24);
        await WaitForStored(1);
        await Settle();
        StoredShouldBe("today");

        // Written to again, it's current, and read whole
        await Append(old, "again");
        await WaitForStored(3);
        await Settle();
        StoredShouldBe("today", "from last year", "again");
    }

    [Fact]
    public async Task QuietFile_IsClosed_AndResumesWhereItLeftOff()
    {
        await Append(LogFile, "one");
        await StartAgent(input => input.CloseInactiveSeconds = 1);
        await WaitForStored(1);

        // Long enough to close it, and for a scan to see it unchanged
        await Task.Delay(TimeSpan.FromSeconds(3));

        await Append(LogFile, "two");
        await WaitForStored(2);
        await Settle();
        StoredShouldBe("one", "two");
    }

    [Fact]
    public async Task ManyQuietFiles_AreEachReadOnce()
    {
        var files = Enumerable.Range(0, 40).Select(i => Path.Combine(LogDirectory, $"map{i:D2}.log")).ToList();
        foreach (var file in files)
        {
            await Append(file, Path.GetFileNameWithoutExtension(file));
        }

        await StartAgent(input => input.CloseInactiveSeconds = 1);
        await WaitForStored(40);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Append(files[7], "later");
        await WaitForStored(41);
        await Settle();

        _collector.Stored.Should().HaveCount(41).And.OnlyHaveUniqueItems().And.Contain("later");
    }

    private static async Task Append(string path, params string[] lines)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));
    }

    private Task WaitForStored(int count) =>
        FakeCollector.WaitUntilAsync(() => _collector.Stored.Count >= count, $"{count} events are stored (have {_collector.Stored.Count})");

    private void StoredShouldBe(params string[] expected) =>
        _collector.Stored.Should().BeEquivalentTo(expected);

    /// <summary>Gives a wrongly re-read line time to show up before asserting it didn't.</summary>
    private static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(1300));

    [Fact]
    public async Task GrowingSmallFile_IsNotReadAgain()
    {
        // The agent used to hash the first 256 bytes, so a short file looked rotated on every append.
        await Append(LogFile, "one");
        await StartAgent();
        await WaitForStored(1);

        await Append(LogFile, "two");
        await WaitForStored(2);
        await Append(LogFile, "three");
        await WaitForStored(3);

        await Settle();
        _collector.EventsSent.Should().Be(3, "no line should even be sent twice");
        StoredShouldBe("one", "two", "three");
    }

    [Fact]
    public async Task PartialLine_IsSentOnceComplete()
    {
        await File.WriteAllTextAsync(LogFile, "first\npart");
        await StartAgent();
        await WaitForStored(1);

        await Settle();
        StoredShouldBe("first");

        await File.AppendAllTextAsync(LogFile, "ial\n");
        await WaitForStored(2);

        StoredShouldBe("first", "partial");
    }

    [Fact]
    public async Task Restart_ResumesWithoutResending()
    {
        await Append(LogFile, "one", "two");
        var agent = await StartAgent();
        await WaitForStored(2);
        await StopAgent(agent);

        await Append(LogFile, "three");
        await StartAgent();
        await WaitForStored(3);

        await Settle();
        _collector.EventsSent.Should().Be(3);
        StoredShouldBe("one", "two", "three");
    }

    [Fact]
    public async Task CheckpointLostInCrash_ResendsButStoresOnce()
    {
        // A crash after delivery but before the registry save sends those lines again.
        await Append(LogFile, "one", "two");
        var registryPath = Path.Combine(_directory, "registry.json");

        var agent = await StartAgent();
        await WaitForStored(2);
        await StopAgent(agent);

        var saved = await File.ReadAllTextAsync(registryPath);
        await File.WriteAllTextAsync(registryPath, System.Text.RegularExpressions.Regex.Replace(saved, "\"offset\": \\d+", "\"offset\": 0"));

        await StartAgent();
        await FakeCollector.WaitUntilAsync(() => _collector.EventsSent >= 4, "the lines are sent again");

        StoredShouldBe("one", "two");
    }

    [Fact]
    public async Task CrashSoonAfterNewFileAppears_StoresOnce()
    {
        // Found by chaos testing: a kill before the periodic save forgot the file's key, so the
        // restart re-read its lines under new IDs and they were stored twice.
        var registryPath = Path.Combine(_directory, "registry.json");
        await Append(LogFile, "one", "two");
        var agent = await StartAgent();
        await WaitForStored(2);

        // What's on disk at this moment is what a crash would leave behind.
        var atCrash = await File.ReadAllTextAsync(registryPath);
        await StopAgent(agent);
        await File.WriteAllTextAsync(registryPath, atCrash);

        await StartAgent();
        await FakeCollector.WaitUntilAsync(() => _collector.EventsSent >= 4, "the lines are read again");

        StoredShouldBe("one", "two");
    }

    [Fact]
    public async Task CrashAfterTruncation_StoresOnce()
    {
        // Found by chaos testing: truncation was noticed before the new first line was complete,
        // so the file's identity was saved empty and a restart couldn't recognize it.
        var registryPath = Path.Combine(_directory, "registry.json");
        await Append(LogFile, "old one", "old two");
        var agent = await StartAgent();
        await WaitForStored(2);

        await File.WriteAllTextAsync(LogFile, string.Empty);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Append(LogFile, "new one", "new two");
        await WaitForStored(4);

        var atCrash = await File.ReadAllTextAsync(registryPath);
        await StopAgent(agent);
        await File.WriteAllTextAsync(registryPath, atCrash);

        await StartAgent();
        await FakeCollector.WaitUntilAsync(() => _collector.EventsSent >= 6, "the new lines are read again");

        StoredShouldBe("old one", "old two", "new one", "new two");
    }

    [Fact]
    public async Task CrashDuringOutage_LosesNothing()
    {
        await Append(LogFile, "one", "two");
        _collector.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var agent = await StartAgent();
        await FakeCollector.WaitUntilAsync(() => _collector.Requests >= 1, "delivery is attempted");
        await StopAgent(agent);

        _collector.Respond = null;
        await StartAgent();

        await WaitForStored(2);
        StoredShouldBe("one", "two");
    }

    [Fact]
    public async Task RenameRotation_ReadsLinesWrittenBeforeTheSwitch()
    {
        await Append(LogFile, "old one");
        await StartAgent();
        await WaitForStored(1);

        // A writer adds lines, and the file is renamed before the next poll can see them.
        await Append(LogFile, "old two", "old three");
        var rotated = Path.Combine(LogDirectory, "app.log.1");
        File.Move(LogFile, rotated);
        await Append(LogFile, "new one");

        await WaitForStored(4);
        StoredShouldBe("old one", "old two", "old three", "new one");
    }

    [Fact]
    public async Task RenameRotation_MatchedByGlob_IsNotReadAgain()
    {
        var pattern = Path.Combine(LogDirectory, "app.log*");
        await Append(LogFile, "old one", "old two");
        await StartAgent(pattern: pattern);
        await WaitForStored(2);

        File.Move(LogFile, Path.Combine(LogDirectory, "app.log.1"));
        await Append(LogFile, "new one");

        await WaitForStored(3);
        await Settle();
        await Settle();

        StoredShouldBe("old one", "old two", "new one");
    }

    [Fact]
    public async Task RotatedWhileAgentIsDown_ReadsTheRestOfTheRotatedFile()
    {
        var pattern = Path.Combine(LogDirectory, "app.log*");
        await Append(LogFile, "one", "two");
        var agent = await StartAgent(pattern: pattern);
        await WaitForStored(2);
        await StopAgent(agent);

        // Written and rotated while the agent is stopped.
        await Append(LogFile, "three", "four");
        File.Move(LogFile, Path.Combine(LogDirectory, "app.log.1"));
        await Append(LogFile, "five");

        await StartAgent(pattern: pattern);

        await WaitForStored(5);
        await Settle();
        StoredShouldBe("one", "two", "three", "four", "five");
    }

    [Fact]
    public async Task CopyTruncateRotation_ReadsTheNewContents()
    {
        await Append(LogFile, "old one", "old two");
        await StartAgent();
        await WaitForStored(2);

        await File.WriteAllTextAsync(LogFile, string.Empty);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await Append(LogFile, "new");

        await WaitForStored(3);
        StoredShouldBe("old one", "old two", "new");
    }

    [Fact]
    public async Task RotatedWhileStopped_ReadsTheNewFileFromTheStart()
    {
        await Append(LogFile, "old line that is long enough to pass any new content");
        var agent = await StartAgent();
        await WaitForStored(1);
        await StopAgent(agent);

        File.Delete(LogFile);
        await Append(LogFile, "new one", "new two");
        await StartAgent();

        await WaitForStored(3);
        StoredShouldBe("old line that is long enough to pass any new content", "new one", "new two");
    }

    [Fact]
    public async Task NewFileWithSameFirstLine_IsNotConfusedWithTheOld()
    {
        // Files often start with the same header. Same bytes at the same offset are different events.
        await Append(LogFile, "# header", "monday");
        var agent = await StartAgent();
        await WaitForStored(2);
        await StopAgent(agent);

        File.Delete(LogFile);
        await File.WriteAllTextAsync(LogFile, "# header\n");
        await StartAgent();

        await WaitForStored(3);
        _collector.Stored.Should().HaveCount(3);
        _collector.Stored.Count(line => line == "# header").Should().Be(2);
    }

    [Fact]
    public async Task LargerNewFileWithSameFirstLine_IsReadFromTheStart()
    {
        // Same first line and bigger than the old file: only the checkpoint's anchor tells them apart.
        await Append(LogFile, "# header", "monday");
        var agent = await StartAgent();
        await WaitForStored(2);
        await StopAgent(agent);

        File.Delete(LogFile);
        await Append(LogFile, "# header", "tuesday one", "tuesday two", "tuesday three");
        await StartAgent();

        await WaitForStored(6);
        _collector.Stored.Should().Contain(["tuesday one", "tuesday two", "tuesday three"]);
    }

    [Fact]
    public async Task SameFileAfterRestart_PassesTheAnchorCheck()
    {
        await Append(LogFile, "# header", "monday");
        var agent = await StartAgent();
        await WaitForStored(2);
        await StopAgent(agent);

        await Append(LogFile, "still monday");
        await StartAgent();

        await WaitForStored(3);
        await Settle();
        _collector.EventsSent.Should().Be(3);
    }

    [Fact]
    public async Task MultiLineEvent_SpanningPolls_IsOneEvent()
    {
        await Append(LogFile, "2026-01-01 boom", "  at First()");
        await StartAgent(input => input.MultiLine = new MultiLineConfiguration
        {
            Pattern = @"^\d{4}-",
            TimeoutSeconds = 30,
        });

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await Append(LogFile, "  at Second()");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await Append(LogFile, "2026-01-02 next");

        await WaitForStored(1);
        _collector.Stored.Should().ContainSingle()
            .Which.Should().Be("2026-01-01 boom\n  at First()\n  at Second()");
    }

    [Fact]
    public async Task MultiLineEvent_IsSentAfterTheTimeout()
    {
        await Append(LogFile, "2026-01-01 last event");
        await StartAgent(input => input.MultiLine = new MultiLineConfiguration
        {
            Pattern = @"^\d{4}-",
            TimeoutSeconds = 1,
        });

        await WaitForStored(1);
        StoredShouldBe("2026-01-01 last event");
    }

    [Fact]
    public async Task OpenMultiLineEvent_IsNotCheckpointed()
    {
        await Append(LogFile, "2026-01-01 first", "2026-01-02 still open");
        var agent = await StartAgent(input => input.MultiLine = new MultiLineConfiguration
        {
            Pattern = @"^\d{4}-",
            TimeoutSeconds = 3600,
        });
        await WaitForStored(1);
        await StopAgent(agent);

        // The open event wasn't sent, so it has to be read again, not skipped.
        await Append(LogFile, "2026-01-03 after restart");
        await StartAgent(input => input.MultiLine = new MultiLineConfiguration
        {
            Pattern = @"^\d{4}-",
            TimeoutSeconds = 3600,
        });

        await WaitForStored(2);
        StoredShouldBe("2026-01-01 first", "2026-01-02 still open");
    }

    [Fact]
    public async Task HighVolume_EveryLineStoredOnce()
    {
        var agent = await StartAgent();
        var expected = new List<string>();

        for (var batch = 0; batch < 20; batch++)
        {
            var lines = Enumerable.Range(0, 200).Select(i => $"line {batch}-{i}").ToArray();
            expected.AddRange(lines);
            await Append(LogFile, lines);
            if (batch == 10)
            {
                await StopAgent(agent);
                agent = await StartAgent();
            }
        }

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count >= expected.Count, "every line is stored", seconds: 30);

        // A set comparison: BeEquivalentTo is quadratic, which takes over a minute at this size.
        _collector.Stored.Should().HaveCount(expected.Count);
        _collector.Stored.ToHashSet().SetEquals(expected).Should().BeTrue();
    }

    [Fact]
    public void EventId_IsStableForTheSameBytes()
    {
        FileMonitorService.CreateEventId("key", 10).Should().Be(FileMonitorService.CreateEventId("key", 10));
        FileMonitorService.CreateEventId("key", 10).Should().NotBe(FileMonitorService.CreateEventId("key", 11));
        FileMonitorService.CreateEventId("key", 10).Should().NotBe(FileMonitorService.CreateEventId("other", 10));
        FileMonitorService.CreateEventId("key", 10).Should().HaveLength(32);
    }

    [Fact]
    public async Task ExpandGlob_SupportsWildcardsInAnySegment()
    {
        var a = Path.Combine(LogDirectory, "a", "deep");
        var b = Path.Combine(LogDirectory, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        await File.WriteAllTextAsync(Path.Combine(a, "x.log"), "x");
        await File.WriteAllTextAsync(Path.Combine(b, "y.log"), "y");
        await File.WriteAllTextAsync(Path.Combine(b, "z.txt"), "z");

        FileMonitorService.ExpandGlob(Path.Combine(LogDirectory, "**", "*.log"), recursive: false)
            .Select(Path.GetFileName).Should().BeEquivalentTo("x.log", "y.log");
        FileMonitorService.ExpandGlob(Path.Combine(LogDirectory, "*", "*.log"), recursive: false)
            .Select(Path.GetFileName).Should().BeEquivalentTo("y.log");
        FileMonitorService.ExpandGlob(Path.Combine(LogDirectory, "*.log"), recursive: true)
            .Select(Path.GetFileName).Should().BeEquivalentTo("x.log", "y.log");
        FileMonitorService.ExpandGlob(Path.Combine(LogDirectory, "b", "?.txt"), recursive: false)
            .Select(Path.GetFileName).Should().BeEquivalentTo("z.txt");
    }

    public async Task DisposeAsync()
    {
        foreach (var agent in _running.ToList())
        {
            await agent.StopAsync();
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
