using Cabinet.API.Authentication;
using System.Collections.Concurrent;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Query;

namespace Cabinet.API.Services;

/// <summary>
/// Background service that processes queued search jobs.
/// Polls for queued jobs and executes them with the BQL query engine.
/// </summary>
public sealed class SearchJobService : BackgroundService
{
    private readonly ISearchJobStorage _storage;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SearchJobService> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly ILeaderLease _lease;
    private readonly ConcurrentDictionary<string, Task> _running = new();
    private const int MaxConcurrentJobs = 4;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);

    public SearchJobService(
        ISearchJobStorage storage,
        IServiceScopeFactory scopeFactory,
        ILogger<SearchJobService> logger,
        ILeaderLease? lease = null)
    {
        _storage = storage;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _lease = lease ?? new SingleInstanceLease();
        _concurrencyLimiter = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Search job service started (max {MaxConcurrent} concurrent jobs)", MaxConcurrentJobs);

        var lastPurge = DateTime.UtcNow;
        var wasLeader = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_lease.IsLeader)
                {
                    if (!wasLeader)
                    {
                        await RequeueAbandonedJobsAsync(stoppingToken);
                        wasLeader = true;
                    }

                    if (DateTime.UtcNow - lastPurge > PurgeInterval)
                    {
                        var purged = await _storage.PurgeExpiredAsync(stoppingToken);
                        if (purged > 0)
                            _logger.LogInformation("Purged {Count} expired search jobs", purged);
                        lastPurge = DateTime.UtcNow;
                    }

                    await StartQueuedJobsAsync(stoppingToken);
                }
                else
                {
                    wasLeader = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in search job processing loop");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // A job that's stopping records why, so wait for that
        await Task.WhenAll(_running.Values);
    }

    /// <summary>
    /// A job left Running with nothing running it belonged to an instance that died. Run it again.
    /// </summary>
    private async Task RequeueAbandonedJobsAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await _storage.GetAllAsync(cancellationToken: cancellationToken))
        {
            if (job.Status == SearchJobStatus.Running && !_running.ContainsKey(job.Id))
            {
                var full = await _storage.GetByIdAsync(job.Id, cancellationToken) ?? job;
                full.Status = SearchJobStatus.Queued;
                full.Progress = 0;
                await _storage.UpdateAsync(full, cancellationToken);
                _logger.LogWarning("Search job {JobId} was abandoned by a stopped instance; running it again", job.Id);
            }
        }
    }

    private async Task StartQueuedJobsAsync(CancellationToken stoppingToken)
    {
        var jobs = await _storage.GetAllAsync(cancellationToken: stoppingToken);
        foreach (var job in jobs.Where(j => j.Status == SearchJobStatus.Queued && !_running.ContainsKey(j.Id)))
        {
            stoppingToken.ThrowIfCancellationRequested();
            await _concurrencyLimiter.WaitAsync(stoppingToken);

            _running[job.Id] = Task.Run(async () =>
            {
                try
                {
                    await ExecuteJobAsync(job, stoppingToken);
                }
                finally
                {
                    _running.TryRemove(job.Id, out _);
                    _concurrencyLimiter.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task WatchForCancellationAsync(string jobId, CancellationTokenSource running, CancellationToken finished)
    {
        try
        {
            while (!finished.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, finished);
                if (await IsCancelledAsync(jobId, finished))
                {
                    await running.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished, so there's nothing left to watch
            return;
        }
    }

    private async Task<bool> IsCancelledAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await _storage.GetByIdAsync(jobId, cancellationToken);
        return job is null or { Status: SearchJobStatus.Cancelled };
    }

    private async Task ExecuteJobAsync(SearchJob job, CancellationToken stoppingToken)
    {
        job.Status = SearchJobStatus.Running;
        job.StartedAt = DateTime.UtcNow;
        job.Progress = 0;
        await _storage.UpdateAsync(job, stoppingToken);

        _logger.LogInformation("Starting search job {JobId}: {Query}", job.Id, job.Query[..Math.Min(100, job.Query.Length)]);

        try
        {
            using var scope = _scopeFactory.CreateScope();

            // The owner's access, as of when they submitted it
            scope.ServiceProvider.GetRequiredService<IndexAccessScope>().ReadableIndexes = job.AllowedIndexes;
            var queryEngine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10)); // 10 min max for async jobs

            // Someone can cancel or delete it on any instance, so its stored state decides
            using var finished = new CancellationTokenSource();
            var watching = WatchForCancellationAsync(job.Id, timeoutCts, finished.Token);

            var context = new QueryExecutionContext
            {
                Query = job.Query,
                Limit = job.Limit,
                Index = job.Index,
                Timeout = TimeSpan.FromMinutes(10)
            };

            job.Progress = 10;
            await _storage.UpdateAsync(job, stoppingToken);

            QueryResult result;
            try
            {
                result = await queryEngine.ExecuteAsync(context, timeoutCts.Token);
            }
            finally
            {
                await finished.CancelAsync();
                await watching;
            }

            if (await IsCancelledAsync(job.Id, stoppingToken))
            {
                _logger.LogInformation("Search job {JobId} was cancelled", job.Id);
                return;
            }

            job.Progress = 90;
            await _storage.UpdateAsync(job, stoppingToken);

            // Serialize results
            job.ResultsJson = JsonSerializer.Serialize(result.Events, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
            job.ResultCount = result.Events.Count;
            job.EventsScanned = result.Statistics?.EventsScanned ?? 0;
            job.ExecutionTime = result.Statistics?.ExecutionTime;
            job.Status = SearchJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.Progress = 100;
            await _storage.UpdateAsync(job, stoppingToken);

            _logger.LogInformation("Search job {JobId} completed: {ResultCount} results in {Duration}ms",
                job.Id, job.ResultCount, job.ExecutionTime?.TotalMilliseconds ?? 0);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            job.Status = SearchJobStatus.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            job.Error = "Job cancelled due to service shutdown";
            await _storage.UpdateAsync(job);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && await IsCancelledAsync(job.Id, CancellationToken.None))
            {
                _logger.LogInformation("Search job {JobId} was cancelled", job.Id);
                return;
            }

            _logger.LogError(ex, "Search job {JobId} failed", job.Id);
            job.Status = SearchJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.Error = ex.Message;
            await _storage.UpdateAsync(job);
        }
    }
}
