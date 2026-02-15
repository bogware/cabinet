# Cabinet Architecture

This document describes Cabinet's architecture, key design decisions, and patterns used throughout the codebase.

## System Overview

```
                    ┌─────────────┐     ┌──────────────┐
                    │   CLI Tool  │     │  Web UI (*)  │
                    └──────┬──────┘     └──────┬───────┘
                           │                    │
                    ┌──────▼────────────────────▼───────┐
                    │         REST API (ASP.NET)         │
                    │  Authentication · RBAC · Tenancy   │
                    │  Rate Limiting · Audit Logging     │
                    └──┬────────┬────────┬──────────┬───┘
                       │        │        │          │
              ┌────────▼──┐ ┌──▼─────┐ ┌▼───────┐ ┌▼──────────┐
              │  Parsers   │ │ Query  │ │Detect  │ │  Alerts   │
              │ L4D2,SM,   │ │ BQL    │ │Hack,   │ │ Webhook,  │
              │ SteamCMD,  │ │ Engine │ │Scum,   │ │ Email,    │
              │ Generic    │ │ + NL   │ │Admin   │ │ File      │
              └────────┬───┘ └──┬─────┘ └┬───────┘ └┬──────────┘
                       │        │        │          │
              ┌────────▼────────▼────────▼──────────▼───────────┐
              │              Cabinet.Core                        │
              │  Models · Interfaces · Configuration · Utilities │
              └────────────────────┬────────────────────────────┘
                                   │
              ┌──────────┬─────────┼──────────┬─────────────────┐
              │          │         │          │                  │
         ┌────▼───┐ ┌───▼────┐ ┌─▼──────┐ ┌▼──────────┐ ┌────▼────┐
         │ SQLite │ │InMemory│ │Postgres│ │ OpenSearch │ │  Redis  │
         │ (dev)  │ │ (test) │ │ (prod) │ │(enterprise)│ │ (coord) │
         └────────┘ └────────┘ └────────┘ └───────────┘ └─────────┘
```

(*) Web UI is not included — Cabinet is API/CLI-first.

## Layer Architecture

### Layer 1: Presentation (API + CLI)
- `Cabinet.API` — ASP.NET Core REST API with controllers, middleware, authentication
- `Cabinet.CLI` — System.CommandLine + Spectre.Console terminal application

### Layer 2: Services (Query, Detection, Parsers, Alerts)
- `Cabinet.Query` — BQL parser, optimizer, execution engine, natural language translator
- `Cabinet.Detection` — Security detection suites (game-server focused)
- `Cabinet.Parsers` — Log format parsers with auto-detection
- `Cabinet.Alerts` — Alert channels (webhook, email, file, console)

### Layer 3: Data (Storage, Database)
- `Cabinet.Storage` — Lightweight/embedded backends (SQLite, In-Memory)
- `Cabinet.Database` — External service backends (PostgreSQL, OpenSearch, MySQL, DynamoDB)

### Layer 4: Foundation (Core)
- `Cabinet.Core` — Models, interfaces, configuration, utilities. No external dependencies.

### Dependency Rules

```
API/CLI → Services → Data → Core
                ↘      ↗
                 Core
```

- `Core` depends on **nothing** (foundation layer)
- All other projects depend on `Core`
- Services never depend on each other directly
- Data layer projects don't depend on each other
- Presentation references all service and data projects via DI

## Architecture Decision Records

### ADR-001: ULID for Event IDs (vs GUID)

**Decision:** Use ULID (Universally Unique Lexicographically Sortable Identifier) instead of GUID/UUID.

**Context:** Log events need unique IDs that support time-based ordering without additional indexes.

**Rationale:**
- ULIDs are time-ordered — events created later have lexicographically greater IDs
- 26-character Crockford Base32 encoding is URL-safe and compact
- No coordination needed (unlike auto-increment) — works in distributed deployments
- Timestamp extractable from ID (first 10 chars = 48-bit millisecond timestamp)
- Compatible with string storage in all backends

**Trade-offs:**
- Slightly larger than auto-increment integers (26 chars vs 8 bytes)
- Less familiar to developers than GUID

### ADR-002: SQLite as Default Storage (vs PostgreSQL)

**Decision:** SQLite is the default storage backend; PostgreSQL is for production.

**Context:** Need zero-configuration development experience with production-grade option.

**Rationale:**
- SQLite requires no separate server — just a file
- WAL mode supports concurrent reads with single-writer
- FTS5 provides full-text search comparable to simple Splunk queries
- Developers can start immediately without Docker/database setup
- Same `ILogStorage` interface means code works identically across backends

**Trade-offs:**
- Single-writer bottleneck limits throughput (~5K events/sec vs 50K+ on PostgreSQL)
- No multi-node support
- Limited to ~10GB practical database size

### ADR-003: Recursive-Descent Parser (vs Parser Generator)

**Decision:** Hand-written recursive-descent parser for BQL instead of ANTLR/Irony/etc.

**Context:** BQL needs SPL backward compatibility with natural-language extensions.

**Rationale:**
- Full control over error messages and recovery
- No build-time code generation step
- Easier to add new stages incrementally
- Custom precedence rules for BQL's mixed SPL/natural-language syntax
- Thread-safe via simple lock (no shared parser state)

**Trade-offs:**
- More code to maintain (~3000 lines in BqlParser.cs)
- No automatic grammar visualization
- Manual left-recursion elimination needed

### ADR-004: ExpandoObject for Query Results (vs Typed Models)

**Decision:** Use `ExpandoObject` (dynamic) for query pipeline results.

**Context:** BQL stages can add, remove, and rename fields dynamically.

**Rationale:**
- `eval` creates new fields at runtime
- `rename` changes field names
- `rex` extracts regex groups as new fields
- `stats` produces entirely new schemas per group-by
- Static typing would require runtime code generation

**Trade-offs:**
- ~300 bytes overhead per ExpandoObject instance
- No compile-time type safety in pipeline stages
- Serialization requires dynamic property enumeration
- **Known issue:** This is a performance bottleneck for large result sets (see PERF-01 in audit)

### ADR-005: Interface-Driven Storage (vs ORM)

**Decision:** Raw SQL/API calls behind `ILogStorage` interface instead of Entity Framework.

**Context:** Log storage has unique requirements (append-heavy, time-series, full-text search).

**Rationale:**
- EF Core's change tracking adds overhead for append-only workloads
- Each backend has unique features (PostgreSQL JSONB, OpenSearch aggregations, SQLite FTS5)
- Raw SQL allows backend-specific optimization (BRIN indexes, GIN indexes, bulk API)
- `ILogStorage` interface keeps all backends interchangeable

**Trade-offs:**
- No automatic schema migration (each backend manages its own schema)
- Manual SQL for each backend (more code)
- Must test each backend independently

### ADR-006: API Key Authentication (vs OAuth2/JWT)

**Decision:** API key via `X-API-Key` header as primary authentication method.

**Context:** Log ingestion systems need simple, scriptable authentication for agents and scripts.

**Rationale:**
- Simple for log shippers and scripts (single header)
- Splunk HEC uses the same pattern (Authorization: Splunk <token>)
- No token refresh complexity for long-running ingestion agents
- Tiered keys (Basic/Professional/Enterprise) map to RBAC roles

**Trade-offs:**
- Keys don't expire automatically (must be rotated manually)
- No delegation or scoping (key = full tier access)
- No user identity — only client identity
- **Future:** OAuth2/OIDC configuration models exist but aren't implemented yet

### ADR-007: SHA-256 for API Key Hashing (vs bcrypt/Argon2)

**Decision:** SHA-256 hash for stored API keys instead of bcrypt/Argon2.

**Context:** API keys are high-entropy random strings, not user-chosen passwords.

**Rationale:**
- API keys are 32+ character random strings — not vulnerable to dictionary attacks
- SHA-256 is fast enough for high-throughput key validation (every request)
- bcrypt/Argon2 add 100-500ms latency per validation — unacceptable for API traffic
- Constant-time comparison prevents timing attacks on the hash

**Trade-offs:**
- If key database is leaked, SHA-256 hashes are faster to brute-force than bcrypt
- Acceptable because: (a) keys are high-entropy, (b) keys can be rotated, (c) defense-in-depth via access controls

## Thread-Safety Patterns

### Pattern 1: Volatile + SemaphoreSlim Initialization

Used for one-time async initialization of storage backends:

```csharp
private volatile bool _initialized;
private readonly SemaphoreSlim _initLock = new(1, 1);

public async Task InitializeAsync(CancellationToken ct = default)
{
    if (_initialized) return;               // Fast path (no lock)
    await _initLock.WaitAsync(ct);
    try
    {
        if (_initialized) return;           // Double-check under lock
        await DoInitializeAsync(ct);
        _initialized = true;               // Volatile write
    }
    finally
    {
        _initLock.Release();
    }
}
```

### Pattern 2: ConcurrentDictionary for Shared State

Used for in-memory caches and registries:

```csharp
private readonly ConcurrentDictionary<string, LogEvent> _events = new();

// Safe: atomic add-or-update
_events.TryAdd(logEvent.Id, logEvent);

// Safe: snapshot for enumeration
var snapshot = _events.Values.ToList();
```

### Pattern 3: Interlocked for Counters

```csharp
private int _triggerCount;
public int TriggerCount => _triggerCount;
public void IncrementTriggerCount() => Interlocked.Increment(ref _triggerCount);
```

### Pattern 4: Lock for Critical Sections

Used when multiple fields must be updated atomically:

```csharp
private readonly object _resultLock = new();

lock (_resultLock)
{
    result.FilesProcessed++;
    result.BytesProcessed += fileSize;
}
```

### Pattern 5: SemaphoreSlim for Async Mutual Exclusion

```csharp
private readonly SemaphoreSlim _writeLock = new(1, 1);

await _writeLock.WaitAsync(cancellationToken);
try
{
    await File.AppendAllTextAsync(path, content, cancellationToken);
}
finally
{
    _writeLock.Release();
}
```

## Storage Backend Guide

### Choosing a Backend

| Scenario | Backend | Config Value |
|----------|---------|-------------|
| Development / single developer | SQLite | `SQLite` |
| Unit tests / CI | In-Memory | `Memory` |
| Production / team | PostgreSQL | `PostgreSQL` |
| Enterprise / large scale | OpenSearch | `OpenSearch` |

### Performance Characteristics

| Backend | Write (events/sec) | Read Latency | Max Practical Size | Concurrency |
|---------|--------------------|--------------|--------------------|-------------|
| SQLite | ~5K | <10ms (warm) | ~10GB | Single writer |
| In-Memory | ~100K | <1ms | ~1GB (RAM) | Concurrent reads |
| PostgreSQL | ~10K | <50ms | ~100GB | Connection pool |
| OpenSearch | ~50K | <100ms | TB+ | Distributed |

### Implementing a New Backend

1. Create a class implementing `ILogStorage` from `Cabinet.Core/Interfaces/ILogStorage.cs`
2. Implement all 8 methods (Initialize, Store, StoreBatch, Query, GetById, GetCount, Purge, GetStats)
3. Use the volatile + SemaphoreSlim initialization pattern
4. Parameterize all queries (no string interpolation of user input)
5. Register in `Program.cs` `ConfigureCabinetServices()` storage switch
6. Add integration tests

## BQL Query Engine Internals

### Pipeline

```
Source Text → BqlLexer → Token Stream → BqlParser → AST (Pipeline)
                                                        │
                                                   BqlOptimizer
                                                   (predicate pushdown)
                                                        │
                                                   BqlQueryEngine
                                                   (stage execution)
                                                        │
                                                   Result Stream
```

### Predicate Pushdown

The optimizer extracts predicates that storage backends can handle natively:

| Predicate Type | Example | Pushed to |
|---------------|---------|-----------|
| Full-text search | `find errors` | `QueryContext.SearchText` |
| Severity filter | `where severity >= 4` | `QueryContext.MinSeverity` |
| Field equality | `where host = "web-01"` | `QueryContext.FieldFilters` |
| Time range | `from last 24h` | `QueryContext.StartTime/EndTime` |
| Simple sort | `sort by timestamp desc` | `QueryContext.SortField/SortDescending` |

Predicates that can't be pushed down remain in the pipeline for post-fetch filtering.

## Security Architecture

```
Request → Rate Limiter → API Key Auth → RBAC Check → Tenant Scoping → Controller
                              │              │              │
                         SHA-256 hash   Capability    Index prefix
                         Constant-time  enforcement   isolation
                         comparison     per endpoint
```

### Defense Layers

1. **Rate Limiting** — Per-tier request limits prevent abuse
2. **Authentication** — API key validated via SHA-256 hash comparison
3. **Authorization** — RBAC capabilities checked per endpoint
4. **Tenant Isolation** — Index prefix scoping prevents cross-tenant access
5. **Audit Logging** — All security events recorded with tamper-evident hash chain
6. **Field Masking** — PII redacted/hashed/removed at query time
7. **Encryption** — AES-256-GCM for data at rest
