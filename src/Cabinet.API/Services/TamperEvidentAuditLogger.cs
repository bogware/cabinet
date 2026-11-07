using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Cabinet.Core.Utilities;

namespace Cabinet.API.Services;

/// <summary>One audit event in its chain.</summary>
public sealed class AuditRecord
{
    public required long Sequence { get; init; }
    public required string PreviousHash { get; init; }
    public required string Hash { get; init; }
    public required AuditEvent Event { get; init; }
}

/// <summary>What's known about one instance's run of audit events.</summary>
public sealed class AuditChainInfo
{
    public required string Id { get; init; }
    public required DateTime StartedAt { get; init; }

    /// <summary>The last sequence number written. Missing records below this mean tampering.</summary>
    public long HeadSequence { get; set; } = -1;

    public string HeadHash { get; set; } = TamperEvidentAuditLogger.Genesis;

    /// <summary>Records below this were removed by retention, not lost.</summary>
    public long FirstRetainedSequence { get; set; }

    /// <summary>The hash of the record before the first one retained.</summary>
    public string RetainedFromHash { get; set; } = TamperEvidentAuditLogger.Genesis;
}

/// <summary>The result of checking the audit log.</summary>
public sealed class AuditVerificationResult
{
    public bool Valid => Problems.Count == 0;
    public int ChainsChecked { get; set; }
    public long RecordsChecked { get; set; }
    public List<string> Problems { get; } = new();
}

/// <summary>
/// Keeps audit events where every instance can read them, each linked to the one before by a hash
/// over all its fields, so that changing or removing a record shows up when the log is verified.
/// Each process writes its own chain, so instances don't have to coordinate. Someone who can write
/// to the database and also rewrites every later hash can't be detected from the database alone,
/// which is why chain heads also go to the log output, to be kept somewhere else.
/// </summary>
public sealed class TamperEvidentAuditLogger : IAuditLogger, IAsyncDisposable
{
    public const string Genesis = "genesis";

    private const string ChainsCollection = "audit-chains";
    private const int HeadLogInterval = 1000;

    private static readonly JsonSerializerOptions HashOptions = new() { WriteIndented = false };

    private readonly IAuditLogger _inner;
    private readonly IDocumentStore _documents;
    private readonly ILogger<TamperEvidentAuditLogger> _logger;
    private readonly object _chainLock = new();
    private readonly AuditChainInfo _chain;
    private readonly Channel<AuditRecord> _pending = Channel.CreateBounded<AuditRecord>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _writer;
    private long _sequence = -1;
    private string _previousHash = Genesis;

    public TamperEvidentAuditLogger(IAuditLogger inner, IDocumentStore documents, ILogger<TamperEvidentAuditLogger> logger)
    {
        _inner = inner;
        _documents = documents;
        _logger = logger;
        _chain = new AuditChainInfo { Id = Ulid.NewUlid(), StartedAt = DateTime.UtcNow };
        _writer = Task.Run(WriteRecordsAsync);
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        AuditRecord record;
        lock (_chainLock)
        {
            var hash = ComputeHash(_previousHash, auditEvent);
            record = new AuditRecord
            {
                Sequence = ++_sequence,
                PreviousHash = _previousHash,
                Hash = hash,
                Event = auditEvent,
            };
            _previousHash = hash;
        }

        await _pending.Writer.WriteAsync(record, cancellationToken);
        await _inner.LogAsync(auditEvent, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? principalId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<AuditEvent>();
        foreach (var chain in await _documents.ListAsync<AuditChainInfo>(ChainsCollection, cancellationToken))
        {
            foreach (var record in await _documents.ListAsync<AuditRecord>(RecordsCollection(chain.Id), cancellationToken))
            {
                var audit = record.Event;
                if ((startTime == null || audit.Timestamp >= startTime)
                    && (endTime == null || audit.Timestamp <= endTime)
                    && (principalId == null || audit.PrincipalId == principalId)
                    && (action == null || audit.Action == action))
                {
                    matches.Add(audit);
                }
            }
        }

        return matches.OrderByDescending(audit => audit.Timestamp).Take(limit).ToList();
    }

    /// <summary>Checks every chain: nothing changed, removed or out of order.</summary>
    public async Task<AuditVerificationResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);

        var result = new AuditVerificationResult();
        foreach (var chain in await _documents.ListAsync<AuditChainInfo>(ChainsCollection, cancellationToken))
        {
            result.ChainsChecked++;
            var records = await _documents.ListAsync<AuditRecord>(RecordsCollection(chain.Id), cancellationToken);

            var expectedSequence = chain.FirstRetainedSequence;
            var expectedPrevious = chain.RetainedFromHash;
            foreach (var record in records.OrderBy(record => record.Sequence))
            {
                result.RecordsChecked++;
                if (record.Sequence != expectedSequence)
                {
                    result.Problems.Add($"Chain {chain.Id}: expected record {expectedSequence}, found {record.Sequence}. Records are missing.");
                    expectedSequence = record.Sequence;
                }
                else if (record.PreviousHash != expectedPrevious)
                {
                    result.Problems.Add($"Chain {chain.Id}: record {record.Sequence} doesn't follow the one before it.");
                }

                if (ComputeHash(record.PreviousHash, record.Event) != record.Hash)
                {
                    result.Problems.Add($"Chain {chain.Id}: record {record.Sequence} was changed.");
                }

                expectedSequence++;
                expectedPrevious = record.Hash;
            }

            if (expectedSequence - 1 != chain.HeadSequence)
            {
                result.Problems.Add($"Chain {chain.Id}: ends at record {expectedSequence - 1}, but its head is {chain.HeadSequence}. Records are missing from the end.");
            }
            else if (chain.HeadSequence >= 0 && expectedPrevious != chain.HeadHash)
            {
                result.Problems.Add($"Chain {chain.Id}: the last record doesn't match its head.");
            }
        }

        return result;
    }

    /// <summary>Removes records older than a time, keeping what verification needs to accept that.</summary>
    public async Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken);

        var removed = 0;
        foreach (var chain in await _documents.ListAsync<AuditChainInfo>(ChainsCollection, cancellationToken))
        {
            var collection = RecordsCollection(chain.Id);
            var records = await _documents.ListAsync<AuditRecord>(collection, cancellationToken);
            var old = records.OrderBy(record => record.Sequence).TakeWhile(record => record.Event.Timestamp < before).ToList();
            if (old.Count == 0)
            {
                continue;
            }

            // Recorded first: a crash part way through leaves records verification would accept
            chain.FirstRetainedSequence = old[^1].Sequence + 1;
            chain.RetainedFromHash = old[^1].Hash;

            if (chain.Id == _chain.Id)
            {
                lock (_chainLock)
                {
                    _chain.FirstRetainedSequence = chain.FirstRetainedSequence;
                    _chain.RetainedFromHash = chain.RetainedFromHash;
                }
            }

            await _documents.PutAsync(ChainsCollection, chain.Id, chain, cancellationToken);
            foreach (var record in old)
            {
                await _documents.DeleteAsync(collection, SequenceId(record.Sequence), cancellationToken);
                removed++;
            }

            if (old.Count == records.Count && chain.Id != _chain.Id)
            {
                await _documents.DeleteAsync(ChainsCollection, chain.Id, cancellationToken);
            }
        }

        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        _pending.Writer.TryComplete();
        await _writer;
    }

    private static string RecordsCollection(string chainId) => $"audit-records-{chainId}";

    private static string SequenceId(long sequence) => sequence.ToString("D18", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Covers everything in the event, so that no part can be changed unnoticed.</summary>
    private static string ComputeHash(string previousHash, AuditEvent auditEvent)
    {
        var input = previousHash + "|" + JsonSerializer.Serialize(auditEvent, HashOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        long target;
        lock (_chainLock)
        {
            target = _sequence;
        }

        while (Volatile.Read(ref _writtenSequence) < target && !_writer.IsCompleted)
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private long _writtenSequence = -1;

    /// <summary>
    /// Writes in order, off the request path. A failure keeps retrying rather than dropping a
    /// record, which would look like tampering, and requests wait if the queue fills.
    /// </summary>
    private async Task WriteRecordsAsync()
    {
        await foreach (var record in _pending.Reader.ReadAllAsync())
        {
            var delay = TimeSpan.FromSeconds(1);
            while (true)
            {
                try
                {
                    await _documents.PutAsync(RecordsCollection(_chain.Id), SequenceId(record.Sequence), record);

                    AuditChainInfo snapshot;
                    lock (_chainLock)
                    {
                        _chain.HeadSequence = record.Sequence;
                        _chain.HeadHash = record.Hash;
                        snapshot = new AuditChainInfo
                        {
                            Id = _chain.Id,
                            StartedAt = _chain.StartedAt,
                            HeadSequence = _chain.HeadSequence,
                            HeadHash = _chain.HeadHash,
                            FirstRetainedSequence = _chain.FirstRetainedSequence,
                            RetainedFromHash = _chain.RetainedFromHash,
                        };
                    }

                    await _documents.PutAsync(ChainsCollection, _chain.Id, snapshot);
                    Volatile.Write(ref _writtenSequence, record.Sequence);

                    // Kept apart from the database, this is what catches a complete rewrite
                    if (record.Sequence % HeadLogInterval == 0)
                    {
                        _logger.LogInformation("Audit chain {ChainId} head {Sequence}: {Hash}", _chain.Id, record.Sequence, record.Hash);
                    }

                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to write audit record {Sequence}; retrying in {Delay}", record.Sequence, delay);
                    await Task.Delay(delay);
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
                }
            }
        }
    }
}
