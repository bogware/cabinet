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
    private const int MaxConcurrentJobs = 4;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(5);

    public SearchJobService(
        ISearchJobStorage storage,
        IServiceScopeFactory scopeFactory,
        ILogger<SearchJobService> logger)
    {
        _storage = storage;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Search job service started (max {MaxConcurrent} concurrent jobs)", MaxConcurrentJobs);

        var lastPurge = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Purge expired jobs periodically
                if (DateTime.UtcNow - lastPurge > PurgeInterval)
                {
                    var purged = await _storage.PurgeExpiredAsync(stoppingToken);
                    if (purged > 0)
                        _logger.LogInformation("Purged {Count} expired search jobs", purged);
                    lastPurge = DateTime.UtcNow;
                }

                // Find queued jobs
                var allJobs = await _storage.GetAllAsync(cancellationToken: stoppingToken);
                var queuedJobs = allJobs.Where(j => j.Status == SearchJobStatus.Queued).ToList();

                foreach (var job in queuedJobs)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    await _concurrencyLimiter.WaitAsync(stoppingToken);

                    // Fire and forget — the semaphore limits concurrency
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await ExecuteJobAsync(job, stoppingToken);
                        }
                        finally
                        {
                            _concurrencyLimiter.Release();
                        }
                    }, stoppingToken);
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

            await Task.Delay(PollInterval, stoppingToken);
        }
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
            var queryEngine = scope.ServiceProvider.GetRequiredService<IQueryEngine>();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(10)); // 10 min max for async jobs

            var context = new QueryExecutionContext
            {
                Query = job.Query,
                Limit = job.Limit,
                Index = job.Index,
                Timeout = TimeSpan.FromMinutes(10)
            };

            job.Progress = 10;
            await _storage.UpdateAsync(job, stoppingToken);

            var result = await queryEngine.ExecuteAsync(context, timeoutCts.Token);

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
            _logger.LogError(ex, "Search job {JobId} failed", job.Id);
            job.Status = SearchJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.Error = ex.Message;
            await _storage.UpdateAsync(job);
        }
    }
}
