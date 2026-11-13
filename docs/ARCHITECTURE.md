# Cabinet Architecture

This document describes Cabinet's architecture, key design decisions, and patterns used throughout the codebase.

## System Overview

```
                    ┌─────────────┐     ┌──────────────┐
                    │   CLI Tool  │     │ Web Dashboard│
                    └──────┬──────┘     └──────┬───────┘
                           │                    │
                    ┌──────▼────────────────────▼───────┐
                    │         REST API (ASP.NET)         │
                    │  Authentication · Capabilities     │
                    │  Rate Limiting · Audit Logging     │
                    └──┬────────┬────────┬──────────┬───┘
                       │        │        │          │
              ┌────────▼──┐ ┌──▼─────┐ ┌▼───────┐ ┌▼──────────┐
              │  Parsers   │ │ Query  │ │ Gaming │ │  Alerts   │
              │ JSON,CEF,  │ │ BQL    │ │ (opt.) │ │ Webhook,  │
              │ Apache,    │ │ Engine │ │ games, │ │ Email,    │
              │ CSV,Generic│ │ + NL   │ │ detect │ │ File      │
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
         │ SQLite │ │InMemory│ │Postgres│ │ OpenSearch │ │  AWS    │
         │        │ │ (test) │ │        │ │            │ │(optional)
         └────────┘ └────────┘ └────────┘ └───────────┘ └─────────┘
```

The web dashboard is a single static page (`Cabinet.API/wwwroot/index.html`) served by the API outside the Development environment. In Development, Swagger UI takes the root URL.

`Cabinet.Agent` is a standalone forwarder with no project references. It sends logs to the API over HTTP.

Each deployment serves one organization. Run a single API instance; see [Deployment](DEPLOYMENT.md#scaling).

## Layer Architecture

### Layer 1: Presentation (API + CLI)
- `Cabinet.API` — ASP.NET Core REST API with controllers, middleware, authentication
- `Cabinet.CLI` — System.CommandLine + Spectre.Console terminal application

### Layer 2: Services (Query, Parsers, Alerts, Extras.Gaming)
- `Cabinet.Query` — BQL parser, optimizer, execution engine, natural language translator
- `Cabinet.Extras.Gaming` — Optional game log parsers and on-demand moderation detection, off unless `Gaming:Enabled` is set
- `Cabinet.Parsers` — Log format parsers with auto-detection
- `Cabinet.Alerts` — Alert channels (webhook, email, file, console)

### Layer 3: Data (Storage, Database, AWS)
- `Cabinet.Storage` — Lightweight/embedded backends (SQLite, In-Memory)
- `Cabinet.Database` — External service backends (PostgreSQL, OpenSearch)
- `Cabinet.AWS` — CloudWatch and CloudTrail access used by the CLI, and S3 client code

### Layer 4: Foundation (Core)
- `Cabinet.Core` — Models, interfaces, configuration, utilities. No project references.

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
- `Cabinet.Query` references `Cabinet.Storage`
- `Cabinet.Extras.Gaming` references `Cabinet.Parsers` for the parser base class, an exception to "services never depend on each other"
- `Cabinet.AWS` references `Cabinet.Parsers`, an exception to the layer order
- `Cabinet.API` and `Cabinet.CLI` reference service and data projects and wire them with DI. `Cabinet.CLI` doesn't reference `Cabinet.Database`.
- `Cabinet.Agent` references nothing

## Architecture Decision Records

### ADR-001: ULID for Event IDs (vs GUID)

**Decision:** Use ULID (Universally Unique Lexicographically Sortable Identifier) instead of GUID/UUID.

**Context:** Log events need unique IDs that support time-based ordering without additional indexes.

**Rationale:**
- ULIDs are time-ordered — events created later have lexicographically greater IDs
- 26-character Crockford Base32 encoding is URL-safe and compact
- No coordination needed (unlike auto-increment)
- Timestamp extractable from ID (first 10 chars = 48-bit millisecond timestamp)
- Compatible with string storage in all backends

**Trade-offs:**
- Slightly larger than auto-increment integers (26 chars vs 8 bytes)
- Less familiar to developers than GUID

### ADR-002: SQLite as Default Storage (vs PostgreSQL)

**Decision:** SQLite is the default storage backend; PostgreSQL and OpenSearch are the external options.

**Context:** Need a zero-configuration start, with an external database available when a deployment outgrows one file.

**Rationale:**
- SQLite requires no separate server — just a file
- WAL mode supports concurrent reads with single-writer
- FTS5 provides full-text search
- Developers can start immediately without Docker/database setup
- All log backends implement the same `ILogStorage` interface

**Trade-offs:**
- A single writer at a time
- No multi-node support

### ADR-003: Recursive-Descent Parser (vs Parser Generator)

**Decision:** Hand-written recursive-descent parser for BQL instead of ANTLR/Irony/etc.

**Context:** BQL needs SPL backward compatibility with natural-language extensions.

**Rationale:**
- Full control over error messages and recovery
- No build-time code generation step
- Easier to add new stages incrementally
- Custom precedence rules for BQL's mixed SPL/natural-language syntax
- Concurrent `ParseBql` calls are serialized with a lock, because the parser keeps per-parse state on the instance

**Trade-offs:**
- More code to maintain (about 3,000 lines in BqlParser.cs)
- No automatic grammar visualization
- Manual left-recursion elimination needed

### ADR-004: Dynamic Rows for Query Results (vs Typed Models)

**Decision:** Use dynamic rows for query pipeline results. In the BQL pipeline a row is a `BqlRow`, a dictionary whose field names match case-insensitively and through a fixed set of aliases. The older `QueryEngine`, which the CLI's local mode uses, works with `ExpandoObject`.

**Context:** BQL stages can add, remove, and rename fields dynamically.

**Rationale:**
- `eval` creates new fields at runtime
- `rename` changes field names
- `rex` extracts regex groups as new fields
- `stats` produces entirely new schemas per group-by
- Static typing would require runtime code generation

**Trade-offs:**
- Per-instance memory overhead compared with typed models
- No compile-time type safety in pipeline stages
- Serialization requires dynamic field enumeration
- **Update:** Queries no longer hold every row. See `BqlStreaming.cs` and [BENCHMARKS.md](BENCHMARKS.md), where each row costs about 15 microseconds to read and build.

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
- Role mappings for API key tiers are fixed, though `/api/v1/roles` can override any principal

OIDC bearer tokens are covered in ADR-008.

### ADR-007: SHA-256 for API Key Hashing (vs bcrypt/Argon2)

**Decision:** SHA-256 hash for API keys held in memory instead of bcrypt/Argon2.

**Context:** API keys are high-entropy random strings, not user-chosen passwords.

**Rationale:**
- API keys are 32+ character random strings — not vulnerable to dictionary attacks
- SHA-256 is fast, and a key is validated on every request
- bcrypt/Argon2 are deliberately slow, which would add latency to every request
- `ApiKeyService` hashes a presented key with SHA-256 and compares it with every stored hash in constant time.

Configuration holds the raw key. It is hashed when loaded, and only the hash is kept in memory. Validation is local on every instance.

**Trade-offs:**
- If the hashes leak, SHA-256 is faster to brute-force than bcrypt
- Acceptable because: (a) keys are high-entropy, (b) keys can be rotated, (c) defense-in-depth via access controls

### ADR-008: OIDC-First SSO Strategy (SAML 2.0 and LDAP Not Yet Implemented)

**Decision:** Implement OpenID Connect (OIDC) as the first and currently only SSO provider. SAML 2.0 and LDAP configuration models exist as foundation but have no runtime implementation.

**Context:** Cabinet needs SSO to move beyond API key-only authentication. The three main SSO protocols are OIDC, SAML 2.0, and LDAP/AD.

**Status:** OIDC is partial. JWT Bearer validation and claim mapping run, but `[RequireCapability]` resolves capabilities through `IRbacService`, which has no principal for an OIDC subject, so routes that require a capability return 403 for those tokens. SAML 2.0 and LDAP are **not implemented** — only their configuration models (`SamlSettings`, `LdapSettings` in `Cabinet.Core/Models/SsoConfiguration.cs`) exist. Selecting SAML or LDAP as the SSO provider logs a warning and does not configure any authentication handler.

**Rationale:**
- OIDC is the modern standard — supported by all major identity providers (Microsoft Entra ID and Amazon Cognito, with presets, and Okta, Auth0, Google Workspace and others)
- ASP.NET Core has first-party OIDC/JWT support via `Microsoft.AspNetCore.Authentication.JwtBearer` — no third-party packages required
- SAML 2.0 requires `Sustainsys.Saml2` (third-party) and has a more complex integration surface (XML signatures, assertion parsing, artifact resolution)
- LDAP requires `Novell.Directory.Ldap.NETStandard` (third-party) and adds connection pool management, bind credential handling, and direct coupling to directory infrastructure
- Most organizations that still use SAML or LDAP also have an OIDC-capable identity provider (or can front LDAP with one, as Entra ID does through its directory sync)

**Implementation details:**
- `SsoConfiguration` (`Core/Models/SsoConfiguration.cs`) — top-level config with `Provider` enum (`Oidc`, `Saml`, `Ldap`)
- `Program.cs` wires OIDC JWT Bearer auth alongside API key auth. `OidcAuthenticationExtensions.AddCabinetSso()` (`API/Authentication/OidcAuthenticationService.cs`) is a separate implementation that `Program.cs` doesn't call.
- `ClaimRoleMapper` — maps OIDC claims to Cabinet role and capability claims. Capability checks don't read those claims.
- `NoOpAuthHandler` — fallback Bearer handler when SSO is disabled

**What is NOT implemented:**
- Capability checks for OIDC principals
- SAML 2.0 authentication handler (would require `Sustainsys.Saml2.AspNetCore2` package)
- LDAP bind authentication (would require `Novell.Directory.Ldap.NETStandard` package)
- SAML/LDAP claim extraction and role mapping at runtime
- SAML Single Logout (SLO)
- LDAP group membership resolution

**Future path:**
- SAML 2.0: Add `Sustainsys.Saml2` package, implement handler in `AddCabinetSso()` SAML branch, map SAML attributes via `ClaimRoleMapper`
- LDAP: Add `Novell.Directory.Ldap.NETStandard` package, implement custom `AuthenticationHandler<LdapSettings>`, bind-verify credentials, resolve group membership

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
| One server, large volumes, fast searches and aggregates | Cabinet engine | `Cabinet` |
| Development, small single-server deployment | SQLite | `SQLite` |
| Unit tests / CI | In-Memory | `Memory` |
| External relational database | PostgreSQL | `PostgreSQL` |
| Existing OpenSearch cluster | OpenSearch | `OpenSearch` |
| More than one machine's disk, with a ClickHouse server to run | ClickHouse | `ClickHouse` |

[BENCHMARKS.md](BENCHMARKS.md) has measured figures for each backend on one machine.

### Metadata Storage

`ILogStorage` holds log events. Other data is registered separately in `StorageRegistration.cs` and `Program.cs`:

| Data | Store |
|------|-------|
| Dashboards, knowledge objects, saved queries, schedules | PostgreSQL on that backend; otherwise SQLite or in-memory, matching the backend |
| Search jobs, data models | SQLite on the local instance, or in-memory on the `Memory` backend |
| RBAC principals | Local `cabinet-rbac.db` |
| Field extractions, alert history, retention definitions | Process memory |

For PostgreSQL, OpenSearch and the Cabinet engine, the local SQLite store is `Storage:MetadataConnectionString` (default `Data Source=cabinet-metadata.db`). With OpenSearch and the Cabinet engine it holds all of the SQLite-backed metadata above, unless `Storage:MetadataPostgresConnectionString` names a PostgreSQL database for it.

### The Cabinet Engine

`Cabinet.Storage.Engine` is Cabinet's own event store, built for one server holding a lot. Each index has a folder under `Storage:DataPath` with:

- **A write-ahead log.** A batch is one record (length, CRC-32, events), synced to disk before `StoreBatchAsync` returns unless `Storage:EngineSyncWrites` is off. At startup the logs are replayed into a segment; a record a crash cut short fails its CRC and is dropped, and it was never acknowledged.
- **The newest events in memory**, searchable at once, until there are `Storage:EngineFlushEvents` of them or the oldest is `Storage:EngineFlushSeconds` old. Then they're written as a segment and the log is deleted.
- **Immutable segments**, sorted by time then ID, in blocks of 4,096 events. Each block stores each column (time as varint deltas, ID, host, source type, severity, raw line, other metadata, and one column per field name) compressed separately, so a query decompresses only the columns it reads. The footer summarizes each block: time and severity ranges, its hosts and source types (up to 64 of each), counts by source type and severity, and Bloom filters of its words (`SearchTerms`) and IDs. A segment is written to a `.tmp` file and renamed, so it's whole or absent.

Reads take the in-memory events and the segment list together under the write lock (`IndexShard.Snapshot`), so a flush between the two can't show an event twice or not at all. Filters skip segments by time, then blocks by their summaries, then match rows reading only the columns they need. Results stream from a k-way merge of segments in time order, and a segment joins the merge only when the merge reaches its time range, so the newest 100 matches read a few blocks however much is stored.

Duplicates: a new event's ID is checked against its batch, the in-memory IDs, and each segment's ID filter, then the filters of that segment's blocks, and only then a block's IDs. Every segment is checked, not just those covering the event's time, because a collector can stamp a retry with a new time. ID filters use 16 bits and 7 hashes per ID (about 0.07% false positives), since every new event probes every segment.

Merges: once ten segments of an index are of a size by powers of ten, the oldest of them are merged into one, up to `Storage:EngineMaxSegmentEvents`, so each event is rewritten about once per size. A merge streams sorted rows into the new segment a batch of blocks at a time, in the background; writes carry on, and only swapping the segment in takes the write lock. The new segment's footer lists the segments it replaces, so a merge cut short after writing it is finished at the next start. Purging deletes whole segments, and rewrites one that straddles the cut-off the same way. Replaced segments are deleted at once; open readers keep their handles for ten minutes.

Aggregates (`EngineAggregation.cs`) count blocks every event of which matches from their summaries when nothing else is needed (a count with no grouping, or a timechart bucket that holds the whole block), and read the rest in parallel, each worker with its own groups, merged at the end.

### Startup

Remote backends get three retries. A backend still unreachable after that makes startup fail with a non-zero exit code. There is no fallback backend.

### Retention

`RetentionBackgroundService` runs at startup and hourly. It purges events older than `Storage:RetentionDays`, and does nothing when the value is 0, the default.

### Implementing a New Backend

1. Create a class implementing `ILogStorage` from `Cabinet.Core/Interfaces/ILogStorage.cs`
2. Implement all methods (Initialize, Store, StoreBatch, Query, GetById, GetCount, PurgeOldEvents, GetStats, and DeleteByIndex, which has a default that deletes nothing)
3. Use the volatile + SemaphoreSlim initialization pattern
4. Parameterize all queries (no string interpolation of user input)
5. Register in the `ConfigureCabinetServices()` storage switch in `Program.cs` and in `StorageRegistration.cs`
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

The optimizer moves filtering into storage where that can't change a query's answer:

| Predicate Type | Example | Pushed to |
|---------------|---------|-----------|
| Full-text search | `find errors` | `QueryContext.SearchText` |
| Numeric severity filter | `where severity >= 4` | `QueryContext.MinSeverity` |
| Host equality | `where host = "web-01"` | `QueryContext.Host` |
| Source type equality | `where sourcetype = "app"` | `QueryContext.SourceType` |
| Time range | `from last 24h` | `QueryContext.StartTime/EndTime` |
| Timestamp sort | `sort by timestamp desc` | `QueryContext.SortField/SortDescending` |

Only the leading run of filters is pushed, because a filter after `head`, `eval` or `stats` applies to that stage's output rather than to stored events. Everything else stays in the pipeline for post-fetch filtering.

### Plain-English Questions

`Cabinet.Query/Natural` turns questions into BQL before the engine sees them. `QueryUnderstander` decides what runs: strict BQL and SPL (anything with a pipe, `field=value`, `OR`/`AND`/`NOT`, wildcards or BQL time syntax) as typed; a mistyped stage or field fixed when exactly one candidate is close; text with no pipe read by `NaturalReader` when it finds structure (a time, a comparison with a field, a count, grouping, sorting, columns); and otherwise nothing, with readings to choose from. The reader is position-independent: passes over the tokens claim what they recognize (times, charts over time, top and rare, aggregates, sorting and limits, grouping, comparisons, levels, known values, columns), and leftover words become the text search. `Lexicon` holds every English word it knows, so another language would be another lexicon. `FieldCatalog` (built by `FieldCatalogService` from recent events the caller may read) maps spoken names to stored fields and recognizes values such as hosts and users. Every reading in `NaturalQueryTests` and in the docs' tables is pinned to its exact BQL and run against real events. Only interactive endpoints (`/query/execute`, `/query/stream`, `/query/understand`, saving a search, the CLI) read questions; scheduled searches, alerts and rules call the engine with the stored query.

### Aggregate Pushdown

When the first stage after those filters is `stats` or `timechart`, `BqlQueryEngine` asks storage to compute it through `IAggregatingLogStorage`. The answer must be exactly what the pipeline would give, so storage mirrors the pipeline's rules: field names match ignoring case, a group takes its values from its newest event, numbers held as text count as numbers, groups come out newest first, and time buckets count from year 1. Where it can't match them, it returns null and the pipeline runs as usual:

| Backend | Computes |
|---|---|
| PostgreSQL | Every aggregate, as SQL over built-in columns and JSON fields |
| SQLite | All but percentiles and deviations, which SQLite lacks |
| Cabinet engine | Every aggregate, in parallel over blocks; percentiles exactly up to ten million kept values; `min` and `max` of text alone fall back |
| OpenSearch | Counts by built-in fields and aligned time buckets |
| ClickHouse | Counts by built-in fields and time buckets, `top` and `rare` of built-in fields |

Decorators decide too: encrypted fields, extracted fields and masking for the caller all refuse, because storage can't see what the caller would. `AggregatePushdownTests` runs the same queries pushed and not pushed on every backend and requires identical answers. A pushed query reports the events it covered in `eventsScanned`, and its trace span has `cabinet.pushed_down`.

## Security Architecture

```
Request → Per-address limiter → Authentication → Authorization → Rate limiter → Capability filter → Controller
                                      │                               │                │
                                SHA-256 hash                    Per key, or per   Per endpoint,
                                dictionary lookup               address if anon   and per index
```

### Defense Layers

1. **Rate Limiting** — A per-address limit before authentication, then per-tier limits and a per-address limit for anonymous requests. Counters are in process memory, and each instance enforces its share of a limit, dividing by the live instances `IClusterCoordinator` reports. `X-Forwarded-For` is honored only from `Network:TrustedProxies`.
2. **Authentication** — API key looked up by SHA-256 hash in a dictionary, with optional expiry. OIDC tokens can't carry Cabinet's own claim names.
3. **Authorization** — Roles and capabilities checked per endpoint through `[RequireCapability]`, and per index through `IndexAccessScope`. Things that act on every index whoever triggers them (detection rules, data models, knowledge objects, field extractions, backups) also need `[RequireEveryIndex]`.
4. **Masking and encryption** — `FieldMaskingService` masks events by role as they're read, through `IndexRestrictedLogStorage`, in fields and in the raw line; search text is matched against the masked line. `EncryptingLogStorage` encrypts configured fields with AES-256-GCM, with each value naming the key that wrote it so keys can rotate.
5. **Audit Logging** — Hash-chained records in the document store, one chain per process, covering every field, optionally as HMACs (`Audit:HmacKey`). `GET /api/v1/audit/verify` checks them. It's tamper-evident, not tamper-proof.

## Distributed Components

Several API instances can share a PostgreSQL database. Roles, search jobs, data models, alert history and field extractions live in `IDocumentStore`. A PostgreSQL advisory lock (`PostgresLeaderLease`) picks one instance to run scheduled searches, retention, search jobs and data model builds, and the server releases it if that instance dies.

`IClusterCoordinator` (`PostgresClusterCoordinator`) holds what instances must agree on as it happens: heartbeats, so each knows how many are running, and detection state. Each rule match is recorded once by event ID, a threshold counts matches from every instance within its window, and firing is claimed with a single conditional upsert, so a rule fires once per cooldown however many instances saw it. One instance alone uses `InMemoryClusterCoordinator`. The query cache stays per instance.
