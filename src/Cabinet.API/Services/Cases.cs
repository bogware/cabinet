using System.Collections.Concurrent;
using Cabinet.Core.Interfaces;

namespace Cabinet.API.Services;

/// <summary>Where a case stands.</summary>
public enum CaseStatus
{
    Open,
    Investigating,
    Resolved,
    Closed,
}

/// <summary>
/// An investigation: the alerts, events and searches that belong together, notes on what was
/// found, and a history of who changed what. Events are kept by ID and index, never their
/// lines, so a case shows each reader only what they may read.
/// </summary>
public sealed class Case
{
    public string Id { get; set; } = Cabinet.Core.Utilities.Ulid.NewUlid();
    public required string Title { get; set; }

    /// <summary>
    /// The number people call it by (#42), one more than the highest so far. 0 for a case opened
    /// before cases were numbered.
    /// </summary>
    public int Number { get; set; }

    public string? Description { get; set; }
    public CaseStatus Status { get; set; } = CaseStatus.Open;
    public string Severity { get; set; } = "Warning";
    public string? Assignee { get; set; }
    public string CreatedBy { get; set; } = "unknown";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<CaseEventRef> Events { get; set; } = [];
    public List<string> AlertIds { get; set; } = [];
    public List<CaseEntry> Notes { get; set; } = [];
    public List<CaseEntry> Searches { get; set; } = [];
    public List<CaseEntry> History { get; set; } = [];
}

/// <summary>An event in a case.</summary>
public sealed class CaseEventRef
{
    public required string EventId { get; set; }
    public string? Note { get; set; }
    public string AddedBy { get; set; } = "unknown";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A note, a saved search or a change, with who and when.</summary>
public sealed class CaseEntry
{
    public required string Text { get; set; }
    public string By { get; set; } = "unknown";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>One thing on a case's timeline, in the order it happened.</summary>
public sealed class TimelineItem
{
    /// <summary>When it happened: an event's own time, an alert's firing, or when a note was written.</summary>
    public required DateTime Time { get; init; }

    /// <summary>event, alert, note, search or change.</summary>
    public required string Kind { get; init; }

    public required string Text { get; init; }
    public string? By { get; init; }
    public string? Host { get; init; }
    public string? Index { get; init; }
    public string? Reference { get; init; }
}

/// <summary>
/// Cases in the shared document store. A change reads the case, applies, and saves, one at a time
/// per case on this instance.
/// </summary>
public sealed class CaseStore
{
    private const string Collection = "cases";
    public const int MaxItemsPerList = 1000;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private readonly IDocumentStore _documents;

    public CaseStore(IDocumentStore documents)
    {
        _documents = documents;
    }

    public async Task<List<Case>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _documents.ListAsync<Case>(Collection, cancellationToken)).OrderByDescending(c => c.UpdatedAt).ToList();

    public Task<Case?> GetAsync(string id, CancellationToken cancellationToken = default) => _documents.GetAsync<Case>(Collection, id, cancellationToken);

    public Task SaveAsync(Case item, CancellationToken cancellationToken = default) => _documents.PutAsync(Collection, item.Id, item, cancellationToken);

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => _documents.DeleteAsync(Collection, id, cancellationToken);

    /// <summary>Changes a case, or returns null if there's no such case.</summary>
    public async Task<Case?> UpdateAsync(string id, Action<Case> change, CancellationToken cancellationToken = default)
    {
        var gate = Locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (await GetAsync(id, cancellationToken) is not { } item)
            {
                return null;
            }

            change(item);
            item.UpdatedAt = DateTime.UtcNow;
            await SaveAsync(item, cancellationToken);
            return item;
        }
        finally
        {
            gate.Release();
        }
    }
}
