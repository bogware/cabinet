using System.Net;
using System.Net.Http.Headers;
using Cabinet.Agent.Configuration;
using Cabinet.Agent.Forwarding;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cabinet.Tests.Agent;

/// <summary>
/// What happens to events when delivery goes wrong. A batch is safe once the server accepts it or
/// it's on disk, and only then may checkpoints move.
/// </summary>
public sealed class LogForwarderDeliveryTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cabinet-forwarder-").FullName;
    private readonly FakeCollector _collector = new();
    private readonly List<LogEventDto> _durable = [];
    private LogForwarder? _forwarder;

    private string Spool => Path.Combine(_directory, "spool");

    private string DeadLetter => Path.Combine(Spool, "dead-letter");

    private LogForwarder Start(Action<OutputConfiguration>? configure = null)
    {
        var config = new OutputConfiguration
        {
            ServerUrl = "http://collector.invalid",
            SpillDirectory = Spool,
            BatchSize = 10,
            FlushIntervalSeconds = 1,
            MaxRetries = 1,
            RetryDelayMilliseconds = 1,
            SpoolRetryIntervalSeconds = 1,
            ShutdownFlushSeconds = 5,
        };
        configure?.Invoke(config);

        _forwarder = new LogForwarder(config, new HttpClient(_collector, disposeHandler: false), NullLogger<LogForwarder>.Instance);
        _forwarder.BatchDurable += batch =>
        {
            lock (_durable)
            {
                _durable.AddRange(batch);
            }
        };
        return _forwarder;
    }

    private static LogEventDto Event(int number) => new()
    {
        Id = $"event-id-{number:0000000000}",
        FileKey = "file",
        EndOffset = number,
        Event = $"line {number}",
    };

    private int DurableCount
    {
        get
        {
            lock (_durable)
            {
                return _durable.Count;
            }
        }
    }

    private int SpoolFiles => Directory.Exists(Spool) ? Directory.GetFiles(Spool, "batch_*.json").Length : 0;

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public async Task Accepted_MarksDurable()
    {
        var forwarder = Start();

        await forwarder.EnqueueAsync(Event(1));

        await FakeCollector.WaitUntilAsync(() => DurableCount == 1, "the event is durable");
        _collector.Stored.Should().ContainSingle().Which.Should().Be("line 1");
    }

    [Fact]
    public async Task ServerDown_NotDurableUntilSpooled()
    {
        var unavailable = new TaskCompletionSource();
        _collector.Respond = _ =>
        {
            unavailable.TrySetResult();
            return Status(HttpStatusCode.ServiceUnavailable);
        };
        var forwarder = Start(c =>
        {
            c.MaxRetries = 1;
            c.RetryDelayMilliseconds = 500;
        });

        await forwarder.EnqueueAsync(Event(1));
        await unavailable.Task;

        DurableCount.Should().Be(0, "a batch still being retried could be lost in a crash");
        await FakeCollector.WaitUntilAsync(() => DurableCount == 1, "the batch is spooled");
        SpoolFiles.Should().Be(1);
    }

    [Fact]
    public async Task Outage_IsReplayedWithoutRestart()
    {
        var down = true;
        _collector.Respond = _ => Volatile.Read(ref down) ? Status(HttpStatusCode.ServiceUnavailable) : null;
        var forwarder = Start();

        for (var i = 0; i < 25; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await FakeCollector.WaitUntilAsync(() => DurableCount == 25, "all events are spooled");
        _collector.Stored.Should().BeEmpty();

        Volatile.Write(ref down, false);

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count == 25, "the spool is replayed");
        await FakeCollector.WaitUntilAsync(() => SpoolFiles == 0, "delivered batches are removed");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task BadApiKey_DoesNotLoseEvents(HttpStatusCode status)
    {
        var rejected = true;
        _collector.Respond = _ => Volatile.Read(ref rejected) ? Status(status) : null;
        var forwarder = Start();

        await forwarder.EnqueueAsync(Event(1));
        await FakeCollector.WaitUntilAsync(() => SpoolFiles == 1, "the batch is spooled, not dropped");

        Volatile.Write(ref rejected, false);

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count == 1, "the event arrives after the key is fixed");
    }

    [Fact]
    public async Task RejectedSpooledBatch_DoesNotBlockTheOthers()
    {
        Directory.CreateDirectory(Spool);
        await File.WriteAllTextAsync(Path.Combine(Spool, "batch_1_poison.json"), """{"event":"poison"}""");
        await File.WriteAllTextAsync(Path.Combine(Spool, "batch_2_good.json"), """{"id":"good-event-id-0000","event":"good"}""");
        _collector.Respond = lines => lines.Any(l => l.Contains("poison")) ? Status(HttpStatusCode.BadRequest) : null;

        Start();

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Contains("good"), "the batch behind it is delivered");
        Directory.GetFiles(DeadLetter).Should().ContainSingle().Which.Should().Contain("poison");
        SpoolFiles.Should().Be(0);
    }

    [Fact]
    public async Task OneBadEvent_DoesNotCostTheRestOfItsBatch()
    {
        _collector.Respond = lines => lines.Any(l => l.Contains("line 3")) ? Status(HttpStatusCode.BadRequest) : null;
        var forwarder = Start();

        for (var i = 0; i < 8; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await FakeCollector.WaitUntilAsync(() => DurableCount == 8, "every event is accounted for");

        _collector.Stored.Should().HaveCount(7).And.NotContain("line 3");
        Directory.GetFiles(DeadLetter).Should().ContainSingle();
        (await File.ReadAllTextAsync(Directory.GetFiles(DeadLetter)[0])).Should().Contain("line 3");
    }

    [Fact]
    public async Task TooLargeForServer_SplitsTheBatch()
    {
        _collector.Respond = lines => lines.Count > 2 ? Status(HttpStatusCode.RequestEntityTooLarge) : null;
        var forwarder = Start();

        for (var i = 0; i < 8; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count == 8, "all events arrive in smaller batches");
    }

    [Fact]
    public async Task Batches_StayUnderTheByteLimit()
    {
        var largest = 0;
        _collector.Respond = lines =>
        {
            largest = Math.Max(largest, lines.Sum(l => l.Length + 1));
            return null;
        };
        var forwarder = Start(c => c.MaxBatchBytes = 2000);

        for (var i = 0; i < 9; i++)
        {
            await forwarder.EnqueueAsync(new LogEventDto { Id = $"event-id-{i:0000000000}", Event = new string('x', 500) });
        }

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count == 9, "all events arrive");
        largest.Should().BeLessOrEqualTo(2000);
    }

    [Fact]
    public async Task KnownOutage_SpoolsWithoutRetryingEveryBatch()
    {
        var requests = 0;
        _collector.Respond = _ =>
        {
            Interlocked.Increment(ref requests);
            return Status(HttpStatusCode.ServiceUnavailable);
        };
        var forwarder = Start(c =>
        {
            c.MaxRetries = 2;
            c.BatchSize = 1;
            c.SpoolRetryIntervalSeconds = 3600;
        });

        for (var i = 0; i < 5; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await FakeCollector.WaitUntilAsync(() => DurableCount == 5, "all events are spooled");

        // Three attempts for the first batch, then one each.
        Volatile.Read(ref requests).Should().Be(7);
    }

    [Fact]
    public async Task RetryAfter_IsHonored()
    {
        var times = new List<DateTime>();
        _collector.Respond = _ =>
        {
            lock (times)
            {
                times.Add(DateTime.UtcNow);
                if (times.Count > 1)
                {
                    return null;
                }
            }

            var response = Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        };
        var forwarder = Start();

        await forwarder.EnqueueAsync(Event(1));

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Count == 1, "the retry succeeds");
        (times[1] - times[0]).Should().BeGreaterOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task FullSpool_KeepsEventsInsteadOfDroppingThem()
    {
        var down = true;
        _collector.Respond = _ => Volatile.Read(ref down) ? Status(HttpStatusCode.ServiceUnavailable) : null;
        var forwarder = Start(c => c.MaxSpoolBytes = 1);

        await forwarder.EnqueueAsync(Event(1));
        await FakeCollector.WaitUntilAsync(() => _collector.Requests >= 2, "delivery is attempted");
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        DurableCount.Should().Be(0, "the checkpoint must not move past events that aren't safe");
        SpoolFiles.Should().Be(0);
        forwarder.GetStats().EventsDropped.Should().Be(0);
    }

    [Fact]
    public async Task Queue_AppliesBackpressure()
    {
        _collector.Respond = _ => Status(HttpStatusCode.ServiceUnavailable);
        var forwarder = Start(c =>
        {
            c.MaxBufferSize = 2;
            c.BatchSize = 1;
            c.MaxSpoolBytes = 1;
        });

        var blocked = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                await forwarder.EnqueueAsync(Event(i));
            }
        });

        var finished = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(500)));

        finished.Should().NotBe(blocked, "a full queue should make the reader wait");
    }

    [Fact]
    public async Task Shutdown_DeliversWhatsQueued()
    {
        var forwarder = Start(c => c.FlushIntervalSeconds = 60);

        for (var i = 0; i < 5; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await forwarder.FlushAsync();

        _collector.Stored.Should().HaveCount(5);
        DurableCount.Should().Be(5);
    }

    [Fact]
    public async Task Shutdown_DuringOutage_SpoolsWhatsQueued()
    {
        _collector.Respond = _ => Status(HttpStatusCode.ServiceUnavailable);
        var forwarder = Start(c =>
        {
            c.MaxRetries = 100;
            c.RetryDelayMilliseconds = 1000;
            c.ShutdownFlushSeconds = 1;
        });

        for (var i = 0; i < 25; i++)
        {
            await forwarder.EnqueueAsync(Event(i));
        }

        await forwarder.FlushAsync();

        DurableCount.Should().Be(25, "every event ends up on disk");
        SpoolFiles.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SpoolFromAnEarlierRun_IsDeliveredAtStart()
    {
        Directory.CreateDirectory(Spool);
        await File.WriteAllTextAsync(Path.Combine(Spool, "batch_old.json"), """{"id":"earlier-run-id-0000","event":"from before"}""");

        Start();

        await FakeCollector.WaitUntilAsync(() => _collector.Stored.Contains("from before"), "the old batch is delivered");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_forwarder != null)
        {
            await _forwarder.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }
}
