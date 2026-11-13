using System.Text.Json;
using Cabinet.API.Services.Settings;
using Cabinet.Core.Interfaces;
using Cabinet.Core.Models;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Cabinet.API.Services.Storage;

/// <summary>What a migration is doing, for the dashboard.</summary>
public sealed class MigrationStatus
{
    /// <summary>Idle, Running, WaitingForRestart, Succeeded, Failed or Cancelled.</summary>
    public string State { get; set; } = "Idle";
    public string? Phase { get; set; }
    public string? Source { get; set; }
    public string? Target { get; set; }
    public bool Copy { get; set; }
    public long EventsToCopy { get; set; }
    public long EventsCopied { get; set; }
    public int MetadataCopied { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public List<string> Log { get; set; } = [];
}

/// <summary>What testing a database found.</summary>
public sealed class StorageTestResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Location { get; set; }
    public long? EventsStored { get; set; }
}

/// <summary>
/// Moves Cabinet to another database: a new SQLite file, or PostgreSQL, copying events and every
/// kind of metadata, or starting empty. Ingest pauses only for the final switch; senders resend.
/// When the copy matches, the setting changes and the server restarts on the new database. The
/// old one is left as it was.
/// </summary>
public sealed class StorageMigrationService
{
    private const int EventBatch = 2000;

    // Late events can carry timestamps from before the copy started; the final pass looks back this far
    private static readonly TimeSpan LateArrivalWindow = TimeSpan.FromDays(1);

    private readonly IConfiguration _configuration;
    private readonly IngestGate _gate;
    private readonly SettingsService _settings;
    private readonly ServiceRestarter _restarter;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<StorageMigrationService> _logger;
    private readonly object _lock = new();
    private MigrationStatus _status = new();
    private CancellationTokenSource? _cancellation;

    public StorageMigrationService(IConfiguration configuration, IngestGate gate, SettingsService settings, ServiceRestarter restarter, ILoggerFactory loggers)
    {
        _configuration = configuration;
        _gate = gate;
        _settings = settings;
        _restarter = restarter;
        _loggers = loggers;
        _logger = loggers.CreateLogger<StorageMigrationService>();
    }

    public MigrationStatus Status
    {
        get
        {
            lock (_lock)
            {
                return new MigrationStatus
                {
                    State = _status.State, Phase = _status.Phase, Source = _status.Source, Target = _status.Target, Copy = _status.Copy,
                    EventsToCopy = _status.EventsToCopy, EventsCopied = _status.EventsCopied, MetadataCopied = _status.MetadataCopied,
                    StartedAt = _status.StartedAt, FinishedAt = _status.FinishedAt, Error = _status.Error, Log = [.. _status.Log],
                };
            }
        }
    }

    private string CurrentType => StorageBackends.Normalize(_configuration["Storage:Type"]) ?? "SQLite";

    private string CurrentConnection => _configuration["Storage:ConnectionString"] ?? "Data Source=cabinet.db";

    private void Note(string phase, string? detail = null)
    {
        lock (_lock)
        {
            _status.Phase = phase;
            _status.Log.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {detail ?? phase}");
        }

        _logger.LogInformation("Storage migration: {Message}", detail ?? phase);
    }

    /// <summary>Connects to a database and says what's in it, without changing anything.</summary>
    public async Task<StorageTestResult> TestAsync(string type, string connectionString, CancellationToken cancellationToken = default)
    {
        var normalized = StorageBackends.Normalize(type);
        if (normalized is null || !StorageBackends.Movable.Contains(normalized))
        {
            return new StorageTestResult { Message = "Choose SQLite, PostgreSQL or the Cabinet engine." };
        }

        var location = StorageBackends.Describe(normalized, connectionString);
        if (normalized == "Cabinet")
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new StorageTestResult { Message = "Give the folder the Cabinet engine keeps its files in." };
            }

            if (!Directory.Exists(connectionString) || !Directory.EnumerateFileSystemEntries(connectionString).Any())
            {
                return new StorageTestResult { Ok = true, Location = location, EventsStored = 0, Message = "An empty folder: ready to copy into. It's created when you move." };
            }
        }
        if (normalized == "SQLite" && StorageInfoService.SqliteFile(connectionString) is { } file && !File.Exists(file))
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(file));
            return folder is not null && !Directory.Exists(folder)
                ? new StorageTestResult { Location = location, Message = $"The folder {folder} doesn't exist. It's created when you move." , Ok = true, EventsStored = 0 }
                : new StorageTestResult { Ok = true, Location = location, EventsStored = 0, Message = "A new file will be created there." };
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var storage = StorageBackends.CreateLogStorage(StorageBackends.With(_configuration, normalized, connectionString), _loggers);
            try
            {
                await storage.InitializeAsync(timeout.Token);
                var count = await storage.GetCountAsync(null, timeout.Token);
                return new StorageTestResult
                {
                    Ok = true,
                    Location = location,
                    EventsStored = count,
                    Message = count == 0 ? "Connected. It's empty, ready to copy into." : $"Connected. It already holds {count:N0} events.",
                };
            }
            finally
            {
                (storage as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or SqliteException or InvalidOperationException or ArgumentException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return new StorageTestResult { Location = location, Message = Explain(ex) };
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        PostgresException { SqlState: "3D000" } => "That database doesn't exist yet. Tick \"Create the database\" to make it.",
        PostgresException { SqlState: "28P01" } => "PostgreSQL refused the user name or password.",
        PostgresException pg => $"PostgreSQL said: {pg.MessageText}",
        NpgsqlException { InnerException: System.Net.Sockets.SocketException } => "Nothing answered at that host and port. Is PostgreSQL running, and listening there?",
        OperationCanceledException => "No answer within 20 seconds.",
        _ => ex.Message,
    };

    /// <summary>
    /// Creates a PostgreSQL database on the server the connection string names, connecting to its
    /// "postgres" database with the same credentials. Returns the connection string for the new one.
    /// </summary>
    public async Task<string> CreatePostgresDatabaseAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database;
        if (string.IsNullOrWhiteSpace(database) || !System.Text.RegularExpressions.Regex.IsMatch(database, "^[A-Za-z_][A-Za-z0-9_]{0,62}$"))
        {
            throw new ArgumentException("Database names start with a letter and hold letters, digits and underscores.");
        }

        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            exists.Parameters.AddWithValue("name", database);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return builder.ConnectionString;
            }
        }

        // The name was checked against a strict pattern above, and is quoted
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\" ENCODING 'UTF8'", connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        _logger.LogInformation("Created PostgreSQL database {Database} on {Host}", database, builder.Host);
        return builder.ConnectionString;
    }

    /// <summary>
    /// Starts moving to another database in the background. With copy, events and metadata are
    /// copied first and the new database must be empty; without, Cabinet starts on it as it is.
    /// </summary>
    public MigrationStatus Start(string type, string connectionString, bool copy, string principal)
    {
        var target = StorageBackends.Normalize(type);
        if (target is null || !StorageBackends.Movable.Contains(target))
        {
            throw new InvalidOperationException("Choose SQLite, PostgreSQL or the Cabinet engine.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Say where the new database is.");
        }

        if (!StorageBackends.Movable.Contains(CurrentType))
        {
            throw new InvalidOperationException($"Moving from {CurrentType} isn't supported. Use the backend's own tools.");
        }

        if (target == CurrentType && SameLocation(target, connectionString, CurrentConnection))
        {
            throw new InvalidOperationException("That's the database Cabinet already uses.");
        }

        lock (_lock)
        {
            if (_status.State is "Running" or "WaitingForRestart")
            {
                throw new InvalidOperationException("A move is already under way.");
            }

            _cancellation = new CancellationTokenSource();
            _status = new MigrationStatus
            {
                State = "Running",
                Source = StorageBackends.Describe(CurrentType, CurrentConnection),
                Target = StorageBackends.Describe(target, connectionString),
                Copy = copy,
                StartedAt = DateTimeOffset.UtcNow,
            };
        }

        var token = _cancellation.Token;
        _ = Task.Run(() => RunAsync(target, connectionString, copy, principal, token), CancellationToken.None);
        return Status;
    }

    /// <summary>Stops a move before it switches. The current database stays in use.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_status.State != "Running" || _cancellation is null)
            {
                return false;
            }

            _cancellation.Cancel();
            return true;
        }
    }

    private bool SameLocation(string type, string a, string b) => type switch
    {
        "SQLite" => string.Equals(Path.GetFullPath(StorageInfoService.SqliteFile(a) ?? a), Path.GetFullPath(StorageInfoService.SqliteFile(b) ?? b), StringComparison.OrdinalIgnoreCase),
        "Cabinet" => string.Equals(Path.GetFullPath(a), StorageBackends.EngineDataPath(_configuration), StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(StorageBackends.Describe(type, a), StorageBackends.Describe(type, b), StringComparison.OrdinalIgnoreCase),
    };

    private async Task RunAsync(string targetType, string targetConnection, bool copy, string principal, CancellationToken cancellationToken)
    {
        try
        {
            if (copy && CurrentType == "SQLite" && targetType == "SQLite")
            {
                await CopySqliteFileAsync(targetConnection, cancellationToken);
            }
            else if (copy)
            {
                await CopyThroughCabinetAsync(targetType, targetConnection, cancellationToken);
            }
            else
            {
                Note("Checking", $"Starting on {StorageBackends.Describe(targetType, targetConnection)} as it is, without copying");
                var test = await TestAsync(targetType, targetConnection, cancellationToken);
                if (!test.Ok)
                {
                    throw new InvalidOperationException(test.Message);
                }

                _gate.Pause("Cabinet is switching databases");
            }

            await SwitchAsync(targetType, targetConnection, principal, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _gate.Resume();
            Finish("Cancelled", "Cancelled. Cabinet carries on with its current database; the new one was left as it got.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _gate.Resume();
            _logger.LogError(ex, "Storage migration failed");
            Finish("Failed", $"Stopped: {Explain(ex)} Cabinet carries on with its current database.", ex.Message);
        }
    }

    private void Finish(string state, string message, string? error = null)
    {
        lock (_lock)
        {
            _status.State = state;
            _status.FinishedAt = DateTimeOffset.UtcNow;
            _status.Error = error;
            _status.Phase = message;
            _status.Log.Add($"{DateTimeOffset.UtcNow:HH:mm:ss} {message}");
        }
    }

    /// <summary>
    /// A SQLite file holds events and metadata together, so a move copies it whole. VACUUM INTO
    /// makes a consistent copy while Cabinet keeps reading; ingest waits for those seconds.
    /// </summary>
    private async Task CopySqliteFileAsync(string targetConnection, CancellationToken cancellationToken)
    {
        var source = StorageInfoService.SqliteFile(CurrentConnection) ?? throw new InvalidOperationException("The current SQLite file couldn't be found.");
        var target = Path.GetFullPath(StorageInfoService.SqliteFile(targetConnection) ?? throw new InvalidOperationException("Give the new file's path."));
        if (File.Exists(target))
        {
            throw new InvalidOperationException($"{target} already exists. Choose a new file name, or start on it without copying.");
        }

        var size = new FileInfo(source).Length;
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var free = new DriveInfo(Path.GetPathRoot(target)!).AvailableFreeSpace;
        if (free < size * 1.1)
        {
            throw new InvalidOperationException($"{folder} has {free / 1048576:N0} MB free; the database needs {size / 1048576:N0} MB.");
        }

        Note("Copying", $"Copying {size / 1048576.0:N1} MB to {target}; new events wait meanwhile");
        _gate.Pause("Cabinet is moving its database");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM INTO @target";
            vacuum.Parameters.AddWithValue("@target", target);
            vacuum.CommandTimeout = 0;
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }

        // Check what was copied the way the server will read it
        var copied = StorageBackends.CreateLogStorage(StorageBackends.With(_configuration, "SQLite", targetConnection), _loggers);
        var original = StorageBackends.CreateLogStorage(_configuration, _loggers);
        try
        {
            var expected = await original.GetCountAsync(null, cancellationToken);
            var actual = await copied.GetCountAsync(null, cancellationToken);
            lock (_lock)
            {
                _status.EventsToCopy = expected;
                _status.EventsCopied = actual;
            }

            if (actual != expected)
            {
                throw new InvalidOperationException($"The copy holds {actual:N0} events, not {expected:N0}.");
            }

            Note("Verified", $"Copied {actual:N0} events and all settings, rules, keys and history");
        }
        finally
        {
            (copied as IDisposable)?.Dispose();
            (original as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Copies through Cabinet's own storage code: events oldest first, then every kind of
    /// metadata, then a final pass with ingest paused for anything that arrived meanwhile. The
    /// counts must match before it switches.
    /// </summary>
    private async Task CopyThroughCabinetAsync(string targetType, string targetConnection, CancellationToken cancellationToken)
    {
        var targetConfiguration = StorageBackends.With(_configuration, targetType, targetConnection);
        var source = StorageBackends.CreateLogStorage(_configuration, _loggers);
        var target = StorageBackends.CreateLogStorage(targetConfiguration, _loggers);
        try
        {
            Note("Checking", $"Connecting to {StorageBackends.Describe(targetType, targetConnection)}");
            await source.InitializeAsync(cancellationToken);
            await target.InitializeAsync(cancellationToken);
            if (await target.GetCountAsync(null, cancellationToken) is var already and > 0)
            {
                throw new InvalidOperationException($"The new database already holds {already:N0} events. Copy into an empty one, or start on it without copying.");
            }

            var started = DateTime.UtcNow;
            var total = await source.GetCountAsync(null, cancellationToken);
            lock (_lock)
            {
                _status.EventsToCopy = total;
            }

            Note("Copying events", $"Copying {total:N0} events while Cabinet keeps running");
            await CopyEventsAsync(source, target, from: null, cancellationToken);

            Note("Copying metadata", "Copying settings, rules, keys, saved searches, dashboards and history");
            await CopyMetadataAsync(targetConfiguration, cancellationToken);

            Note("Final pass", "Catching up on events that arrived during the copy; new events wait meanwhile");
            _gate.Pause("Cabinet is switching databases");
            await CopyEventsAsync(source, target, started - LateArrivalWindow, cancellationToken);
            var expected = await source.GetCountAsync(null, cancellationToken);
            var actual = await target.GetCountAsync(null, cancellationToken);
            if (actual < expected)
            {
                Note("Final pass", $"{expected - actual:N0} events are still missing; checking every event");
                await CopyEventsAsync(source, target, from: null, cancellationToken);
                actual = await target.GetCountAsync(null, cancellationToken);
            }

            lock (_lock)
            {
                _status.EventsToCopy = expected;
                _status.EventsCopied = actual;
            }

            if (actual != expected)
            {
                throw new InvalidOperationException($"The new database holds {actual:N0} events, not {expected:N0}.");
            }

            Note("Verified", $"All {actual:N0} events copied");
        }
        finally
        {
            (source as IDisposable)?.Dispose();
            (target as IDisposable)?.Dispose();
        }
    }

    private async Task CopyEventsAsync(ILogStorage source, ILogStorage target, DateTime? from, CancellationToken cancellationToken)
    {
        EventCursor? after = null;
        while (true)
        {
            var page = new List<LogEvent>(EventBatch);
            var context = new QueryContext { SortField = "Timestamp", SortDescending = false, Limit = EventBatch, After = after, StartTime = from };
            await foreach (var logEvent in source.QueryAsync(context, cancellationToken))
            {
                page.Add(logEvent);
            }

            if (page.Count == 0)
            {
                return;
            }

            // Event IDs make this safe to repeat: the target keeps what it already has
            await target.StoreBatchAsync(page, cancellationToken);
            if (from is null)
            {
                lock (_lock)
                {
                    _status.EventsCopied += page.Count;
                }
            }

            if (page.Count < EventBatch)
            {
                return;
            }

            after = new EventCursor(page[^1].Timestamp, page[^1].Id);
        }
    }

    private async Task CopyMetadataAsync(IConfiguration targetConfiguration, CancellationToken cancellationToken)
    {
        await using var from = StorageBackends.CreateMetadata(_configuration, _loggers);
        await using var to = StorageBackends.CreateMetadata(targetConfiguration, _loggers);
        var copied = 0;

        var sourceDocuments = from.GetRequiredService<IDocumentStore>();
        var targetDocuments = to.GetRequiredService<IDocumentStore>();
        await sourceDocuments.InitializeAsync(cancellationToken);
        await targetDocuments.InitializeAsync(cancellationToken);
        foreach (var collection in await sourceDocuments.ListCollectionsAsync(cancellationToken))
        {
            foreach (var (id, json) in await sourceDocuments.ExportAsync(collection, cancellationToken))
            {
                await targetDocuments.ImportAsync(collection, id, json, cancellationToken);
                copied++;
            }
        }

        async Task CopyAll<TStore, TItem>(Func<TStore, Task<IReadOnlyList<TItem>>> list, Func<TStore, TItem, Task> save)
            where TStore : notnull
        {
            var source = from.GetRequiredService<TStore>();
            var target = to.GetRequiredService<TStore>();
            foreach (var item in await list(source))
            {
                await save(target, item);
                copied++;
            }
        }

        await from.GetRequiredService<IDashboardStorage>().InitializeAsync(cancellationToken);
        await to.GetRequiredService<IDashboardStorage>().InitializeAsync(cancellationToken);
        await CopyAll<IDashboardStorage, Dashboard>(s => s.GetAllAsync(cancellationToken), (t, d) => t.SaveAsync(d, cancellationToken));

        await from.GetRequiredService<IKnowledgeStorage>().InitializeAsync(cancellationToken);
        await to.GetRequiredService<IKnowledgeStorage>().InitializeAsync(cancellationToken);
        await CopyAll<IKnowledgeStorage, LookupTable>(s => s.GetAllLookupsAsync(cancellationToken), (t, l) => t.SaveLookupAsync(l, cancellationToken));
        await CopyAll<IKnowledgeStorage, QueryMacro>(s => s.GetAllMacrosAsync(cancellationToken), (t, m) => t.SaveMacroAsync(m, cancellationToken));
        await CopyAll<IKnowledgeStorage, FieldAlias>(s => s.GetAllAliasesAsync(cancellationToken), (t, a) => t.SaveAliasAsync(a, cancellationToken));

        await from.GetRequiredService<IScheduledSearchStorage>().InitializeAsync(cancellationToken);
        await to.GetRequiredService<IScheduledSearchStorage>().InitializeAsync(cancellationToken);
        await CopyAll<IScheduledSearchStorage, ScheduledSearch>(s => s.GetAllAsync(cancellationToken), (t, x) => t.SaveAsync(x, cancellationToken));

        await from.GetRequiredService<ISavedQueryStorage>().InitializeAsync(cancellationToken);
        await to.GetRequiredService<ISavedQueryStorage>().InitializeAsync(cancellationToken);
        await CopyAll<ISavedQueryStorage, SavedQuery>(s => s.GetAllAsync(cancellationToken), (t, q) => t.SaveAsync(q, cancellationToken));

        lock (_lock)
        {
            _status.MetadataCopied = copied;
        }

        Note("Copying metadata", $"Copied {copied:N0} metadata records");
    }

    /// <summary>Points the settings at the new database and restarts on it, if Cabinet can.</summary>
    private async Task SwitchAsync(string targetType, string targetConnection, string principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Note("Switching", $"Switching to {StorageBackends.Describe(targetType, targetConnection)}");
        await _settings.SaveAsync(
            StorageBackends.StorageSettings(_configuration, targetType, targetConnection).ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value)),
            principal,
            "storage migration",
            CancellationToken.None);

        var (canRestart, reason) = _restarter.CanRestart();
        if (canRestart)
        {
            Finish("Succeeded", "Done. Cabinet is restarting on the new database; the old one is untouched.");
            _restarter.Restart(principal);
        }
        else
        {
            // Ingest stays paused: anything written now would land in the database being left
            Finish("WaitingForRestart", $"Copied and switched. Restart Cabinet to start using the new database; new events wait until then. ({reason})");
        }
    }
}
