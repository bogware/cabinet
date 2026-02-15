using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Cabinet.API.Infrastructure;

/// <summary>
/// Manages a shared Redis connection using lazy initialization.
/// Thread-safe and reusable across the application lifetime.
/// </summary>
public sealed class RedisConnectionManager : IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<RedisConnectionManager> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile ConnectionMultiplexer? _connection;
    private bool _disposed;

    public RedisConnectionManager(string connectionString, ILogger<RedisConnectionManager> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    /// <summary>
    /// Whether a Redis connection is currently available
    /// </summary>
    public bool IsConnected => _connection?.IsConnected == true;

    /// <summary>
    /// Get a Redis database instance. Creates the connection if needed.
    /// </summary>
    public async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);
        return conn.GetDatabase();
    }

    /// <summary>
    /// Get a Redis subscriber for pub/sub operations.
    /// </summary>
    public async Task<ISubscriber> GetSubscriberAsync(CancellationToken cancellationToken = default)
    {
        var conn = await GetConnectionAsync(cancellationToken);
        return conn.GetSubscriber();
    }

    private async Task<ConnectionMultiplexer> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection?.IsConnected == true) return _connection;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection?.IsConnected == true) return _connection;

            _connection?.Dispose();
            _logger.LogInformation("Connecting to Redis: {Endpoint}", RedactConnectionString(_connectionString));
            _connection = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            _logger.LogInformation("Redis connection established");
            return _connection;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Redis");
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static string RedactConnectionString(string cs)
    {
        // Mask password and other sensitive parts from Redis connection string.
        // Only show host:port, redact everything else.
        var parts = cs.Split(',');
        var safeParts = new List<string>();
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('='))
            {
                var key = trimmed.Split('=')[0].Trim().ToLowerInvariant();
                if (key is "password" or "pass" or "requirepass")
                {
                    safeParts.Add($"{key}=***");
                    continue;
                }
            }
            safeParts.Add(trimmed);
        }
        return string.Join(",", safeParts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection?.Dispose();
        _connectLock.Dispose();
    }
}
