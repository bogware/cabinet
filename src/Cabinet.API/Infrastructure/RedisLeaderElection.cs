using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Redis-based leader election using SET NX EX (atomic set-if-not-exists with expiry).
/// Only one instance holds leadership for a given resource at a time.
/// The leader periodically renews its lease; if it fails, another instance can claim it.
/// </summary>
public sealed class RedisLeaderElection : IDisposable
{
    private readonly RedisConnectionManager _redis;
    private readonly string _instanceId;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _renewalInterval;
    private readonly ILogger<RedisLeaderElection> _logger;
    private Timer? _renewalTimer;
    private volatile bool _isLeader;
    private string? _currentResource;

    public RedisLeaderElection(
        RedisConnectionManager redis,
        string instanceId,
        ILogger<RedisLeaderElection> logger,
        TimeSpan? leaseDuration = null,
        TimeSpan? renewalInterval = null)
    {
        _redis = redis;
        _instanceId = instanceId;
        _logger = logger;
        _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        _renewalInterval = renewalInterval ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Whether this instance currently holds leadership
    /// </summary>
    public bool IsLeader => _isLeader;

    /// <summary>
    /// The instance ID of this node
    /// </summary>
    public string InstanceId => _instanceId;

    /// <summary>
    /// Attempt to acquire leadership for the given resource.
    /// If acquired, starts automatic lease renewal.
    /// </summary>
    public async Task<bool> TryAcquireLeadershipAsync(string resourceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = await _redis.GetDatabaseAsync(cancellationToken);
            var key = $"leader:{resourceName}";

            // SET key instanceId NX EX leaseDuration
            var acquired = await db.StringSetAsync(
                key,
                _instanceId,
                _leaseDuration,
                When.NotExists);

            if (acquired)
            {
                _isLeader = true;
                _currentResource = resourceName;
                StartRenewal(resourceName);
                _logger.LogInformation(
                    "Instance {InstanceId} acquired leadership for {Resource}",
                    _instanceId, resourceName);
                return true;
            }

            // Check if we already hold the lease (restart scenario)
            var currentLeader = await db.StringGetAsync(key);
            if (currentLeader == _instanceId)
            {
                _isLeader = true;
                _currentResource = resourceName;
                StartRenewal(resourceName);
                _logger.LogInformation(
                    "Instance {InstanceId} re-confirmed leadership for {Resource}",
                    _instanceId, resourceName);
                return true;
            }

            _isLeader = false;
            _logger.LogDebug(
                "Instance {InstanceId} failed to acquire leadership for {Resource}, held by {Leader}",
                _instanceId, resourceName, currentLeader);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Leader election failed for {Resource}, assuming not leader", resourceName);
            _isLeader = false;
            return false;
        }
    }

    /// <summary>
    /// Voluntarily release leadership
    /// </summary>
    public async Task ReleaseLeadershipAsync(string resourceName)
    {
        StopRenewal();

        try
        {
            var db = await _redis.GetDatabaseAsync();
            var key = $"leader:{resourceName}";

            // Only release if we are the current leader (atomic check-and-delete)
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    return redis.call('DEL', KEYS[1])
                end
                return 0
            ";

            await db.ScriptEvaluateAsync(script, new RedisKey[] { key }, new RedisValue[] { _instanceId });
            _isLeader = false;
            _logger.LogInformation("Instance {InstanceId} released leadership for {Resource}", _instanceId, resourceName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release leadership for {Resource}", resourceName);
        }
    }

    private void StartRenewal(string resourceName)
    {
        StopRenewal();
        _renewalTimer = new Timer(
            async _ => await RenewLeaseAsync(resourceName),
            null,
            _renewalInterval,
            _renewalInterval);
    }

    private void StopRenewal()
    {
        _renewalTimer?.Dispose();
        _renewalTimer = null;
    }

    private async Task RenewLeaseAsync(string resourceName)
    {
        try
        {
            var db = await _redis.GetDatabaseAsync();
            var key = $"leader:{resourceName}";

            // Only renew if we still hold the lease
            var script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    redis.call('PEXPIRE', KEYS[1], ARGV[2])
                    return 1
                end
                return 0
            ";

            var renewed = (int)await db.ScriptEvaluateAsync(
                script,
                new RedisKey[] { key },
                new RedisValue[] { _instanceId, (long)_leaseDuration.TotalMilliseconds });

            if (renewed == 0)
            {
                _isLeader = false;
                StopRenewal();
                _logger.LogWarning("Instance {InstanceId} lost leadership for {Resource}", _instanceId, resourceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to renew lease for {Resource}", resourceName);
            // Don't immediately give up leadership — the lease will expire naturally
        }
    }

    public void Dispose()
    {
        StopRenewal();
        if (_isLeader && _currentResource != null)
        {
            // Best-effort release on dispose
            try
            {
                ReleaseLeadershipAsync(_currentResource).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Best-effort — lease will expire naturally
                _logger.LogDebug(ex, "Failed to release leadership during dispose for {Resource}", _currentResource);
            }
        }
    }
}
