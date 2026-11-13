using System.Collections.Concurrent;

namespace Cabinet.API.Services.Identity;

/// <summary>
/// Slows password guessing on this instance: after a few failures for an account, or rather more
/// from one address (offices share addresses), each further try has to wait, twice as long each
/// time up to a minute. Failures are forgotten after 15 minutes without one. The account lockout
/// in the directory is the limit that holds across instances; this makes reaching it slow.
/// </summary>
public sealed class SignInThrottle
{
    /// <summary>Failures for one account before tries are slowed.</summary>
    public const int AccountFreeFailures = 3;

    /// <summary>Failures from one address before tries are slowed.</summary>
    public const int AddressFreeFailures = 10;

    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(15);
    private const int MaxTracked = 100_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public SignInThrottle(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    private static string AccountKey(string userName) => $"account:{userName.Trim().ToLowerInvariant()}";

    private static string AddressKey(string? address) => $"address:{address ?? "unknown"}";

    /// <summary>How long until this account may be tried from this address again, or zero.</summary>
    public TimeSpan RetryAfter(string userName, string? address)
    {
        var now = _time.GetUtcNow();
        var account = Wait(AccountKey(userName), AccountFreeFailures, now);
        var fromAddress = Wait(AddressKey(address), AddressFreeFailures, now);
        return account > fromAddress ? account : fromAddress;
    }

    /// <summary>Counts a failed try.</summary>
    public void Failed(string userName, string? address)
    {
        var now = _time.GetUtcNow();
        if (_entries.Count > MaxTracked)
        {
            foreach (var stale in _entries.Where(pair => now - pair.Value.Last > ForgetAfter).Select(pair => pair.Key).ToList())
            {
                _entries.TryRemove(stale, out _);
            }

            if (_entries.Count > MaxTracked)
            {
                _entries.Clear();
            }
        }

        foreach (var key in new[] { AccountKey(userName), AddressKey(address) })
        {
            _entries.AddOrUpdate(
                key,
                _ => new Entry(1, now),
                (_, entry) => now - entry.Last > ForgetAfter ? new Entry(1, now) : new Entry(entry.Failures + 1, now));
        }
    }

    /// <summary>
    /// Forgets an account's failures once someone signs in to it. The address's stay, so signing
    /// in to one's own account can't reset a count run up guessing at others.
    /// </summary>
    public void Succeeded(string userName) => _entries.TryRemove(AccountKey(userName), out _);

    private TimeSpan Wait(string key, int freeFailures, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var entry) || now - entry.Last > ForgetAfter || entry.Failures <= freeFailures)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Min(entry.Failures - freeFailures - 1, 6);
        var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, exponent), MaxDelay.TotalSeconds));
        var remaining = entry.Last + delay - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed record Entry(int Failures, DateTimeOffset Last);
}
