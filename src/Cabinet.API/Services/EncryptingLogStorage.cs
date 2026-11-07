using System.Runtime.CompilerServices;
using System.Text.Json;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;

namespace Cabinet.API.Services;

/// <summary>
/// Encrypts configured fields before they reach storage, and decrypts them on the way back. A
/// value that's in the raw line too is replaced there with a placeholder, or the plaintext would
/// still be stored. Searching storage for an encrypted value finds nothing, which is the point,
/// but a filter in the query pipeline still works, since it sees decrypted events.
/// </summary>
public sealed class EncryptingLogStorage : ILogStorage, IAggregatingLogStorage
{
    private const string PlaceholderPrefix = "[encrypted:";

    private readonly ILogStorage _inner;
    private readonly EncryptionService _encryption;
    private readonly bool _encryptRawLine;

    public EncryptingLogStorage(ILogStorage inner, EncryptionService encryption, bool encryptRawLine)
    {
        _inner = inner;
        _encryption = encryption;
        _encryptRawLine = encryptRawLine;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _encryption.InitializeAsync(cancellationToken);
        await _inner.InitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> StoreAsync(LogEvent logEvent, CancellationToken cancellationToken = default)
    {
        await _encryption.InitializeAsync(cancellationToken);
        return await _inner.StoreAsync(Encrypt(logEvent), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> StoreBatchAsync(IEnumerable<LogEvent> events, CancellationToken cancellationToken = default)
    {
        await _encryption.InitializeAsync(cancellationToken);
        return await _inner.StoreBatchAsync(events.Select(Encrypt).ToList(), cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEvent> QueryAsync(QueryContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _encryption.InitializeAsync(cancellationToken);
        await foreach (var logEvent in _inner.QueryAsync(context, cancellationToken))
        {
            yield return Decrypt(logEvent);
        }
    }

    /// <inheritdoc />
    public async Task<LogEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await _encryption.InitializeAsync(cancellationToken);
        var logEvent = await _inner.GetByIdAsync(id, cancellationToken);
        return logEvent == null ? null : Decrypt(logEvent);
    }

    /// <inheritdoc />
    public Task<long> GetCountAsync(QueryContext? context = null, CancellationToken cancellationToken = default) => _inner.GetCountAsync(context, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeOldEventsAsync(before, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeIndexEventsAsync(string indexName, DateTime before, CancellationToken cancellationToken = default) => _inner.PurgeIndexEventsAsync(indexName, before, cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteByIndexAsync(string indexName, CancellationToken cancellationToken = default) => _inner.DeleteByIndexAsync(indexName, cancellationToken);

    /// <inheritdoc />
    public Task<StorageStats> GetStatsAsync(CancellationToken cancellationToken = default) => _inner.GetStatsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<AggregateResult?> AggregateAsync(AggregateRequest request, CancellationToken cancellationToken = default)
    {
        // Storage only holds ciphertext for these, which it can't group or add up
        var fields = request.GroupBy.Concat(request.Aggregates.Select(spec => spec.Field).OfType<string>());
        return _inner is IAggregatingLogStorage aggregating && !fields.Any(_encryption.ShouldEncryptField)
            ? aggregating.AggregateAsync(request, cancellationToken)
            : Task.FromResult<AggregateResult?>(null);
    }

    private LogEvent Encrypt(LogEvent logEvent)
    {
        var copy = Copy(logEvent);
        var rawLine = logEvent.RawLine;
        foreach (var field in logEvent.Fields)
        {
            if (field.Value == null || !_encryption.ShouldEncryptField(field.Key))
            {
                copy.Fields[field.Key] = field.Value;
                continue;
            }

            // As JSON, so a number comes back a number
            copy.Fields[field.Key] = _encryption.Encrypt(JsonSerializer.Serialize(field.Value));

            var text = field.Value.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                rawLine = rawLine.Replace(text, $"{PlaceholderPrefix}{field.Key}]", StringComparison.Ordinal);
            }
        }

        copy.RawLine = _encryptRawLine ? _encryption.Encrypt(rawLine) : rawLine;
        return copy;
    }

    private LogEvent Decrypt(LogEvent logEvent)
    {
        var copy = Copy(logEvent);
        var rawLine = _encryptRawLine && IsEncrypted(logEvent.RawLine) ? _encryption.Decrypt(logEvent.RawLine) : logEvent.RawLine;
        foreach (var field in logEvent.Fields)
        {
            if (field.Value is not string text || !IsEncrypted(text))
            {
                copy.Fields[field.Key] = field.Value;
                continue;
            }

            using var document = JsonDocument.Parse(_encryption.Decrypt(text));
            var value = JsonFieldValue.Unwrap(document.RootElement);
            copy.Fields[field.Key] = value;
            rawLine = rawLine.Replace($"{PlaceholderPrefix}{field.Key}]", value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        }

        copy.RawLine = rawLine;
        return copy;
    }

    private static bool IsEncrypted(string value) => value.StartsWith("enc:", StringComparison.Ordinal);

    private static LogEvent Copy(LogEvent logEvent) => new()
    {
        Id = logEvent.Id,
        Timestamp = logEvent.Timestamp,
        IndexTime = logEvent.IndexTime,
        SourceType = logEvent.SourceType,
        Host = logEvent.Host,
        Severity = logEvent.Severity,
        RawLine = logEvent.RawLine,
        SourceFile = logEvent.SourceFile,
        LineNumber = logEvent.LineNumber,
        ParserName = logEvent.ParserName,
        Index = logEvent.Index,
        Tags = logEvent.Tags,
    };
}
