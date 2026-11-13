# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Cabinet is a .NET 10.0 enterprise log-intelligence system (Splunk alternative) split into ~11 projects under `src/`, with all tests in `tests/Cabinet.Tests/`. Solution file: `Cabinet.sln`.

## Common Commands

```bash
dotnet restore
dotnet build                         # Build all projects
dotnet build -c Release              # Release build (used in CI / before PRs)
dotnet test                          # Run the full test suite
dotnet test --filter "Category!=Integration"   # Skip backend integration tests (only `Integration` is tagged; there is no `Unit` trait)

# Run a single test class or method:
dotnet test --filter "FullyQualifiedName~BqlParserTests"
dotnet test --filter "FullyQualifiedName=Cabinet.Tests.Query.BqlParserTests.ParseBql_SimpleSearch_ProducesSearchStage"

# CLI-driven end-to-end script (bash)
./tests/EndToEnd/run-tests.sh

# Run the API locally
dotnet run --project src/Cabinet.API   # Dashboard: http://localhost:5068

# PostgreSQL backup drill, and deploying k8s/ to a throwaway kind cluster
./tests/EndToEnd/backup-drill.sh
./tests/EndToEnd/k8s-smoke.sh

# Measure scale (results in docs/BENCHMARKS.md)
dotnet run -c Release --project benchmarks/Cabinet.Benchmarks -- sqlite 1000000

# Build cross-platform release artifacts + .deb packages into dist/
./scripts/build-release.sh 1.0.0

# Docker stack (API on host port 5000, CLI, Agent, Postgres)
# Optional profiles: monitoring (Grafana/Prometheus), development (LocalStack)
docker-compose up -d
```

Dev API keys (development only): `dev-basic-key`, `dev-professional-key`, `dev-enterprise-key`, `dev-admin-key` — pass via `X-API-Key` header.

## Build Configuration (enforced repo-wide via `Directory.Build.props`)

- `TargetFramework=net10.0`, `Nullable=enable`, `ImplicitUsings=enable`
- **`TreatWarningsAsErrors=true`** — any warning fails the build. Resolve all warnings; never suppress. Existing `#pragma warning disable CS0612, CS0618` lines (obsolete APIs in `Cabinet.Database` and its callers) are legacy; don't add more.
- This also applies to NuGet audit warnings (`NU190x`): a newly published advisory can fail `dotnet restore` with no code change. `-p:NuGetAudit=false` gets past it for local diagnosis only; the real fix is a manual package update.

`Cabinet.sln` does not list `Cabinet.AWS` or `Cabinet.Alerts`. They build transitively through `Cabinet.API` and `Cabinet.CLI`.

## Architecture

Strict 4-layer dependency rule (see `docs/ARCHITECTURE.md`):

```
Presentation (Cabinet.API, Cabinet.CLI, Cabinet.Agent)
        ↓
Services (Cabinet.Query, Cabinet.Parsers, Cabinet.Alerts, Cabinet.Extras.Gaming)
        ↓
Data (Cabinet.Storage [SQLite/InMemory], Cabinet.Database [Postgres/OpenSearch], Cabinet.AWS)
        ↓
Cabinet.Core  (models, interfaces, config — depends on NOTHING)
```

Rules to preserve when adding code:
- `Cabinet.Core` must have zero project references. Models and interfaces live here.
- Service projects (`Query`, `Parsers`, `Alerts`) do **not** reference each other.
- Data-layer projects do **not** reference each other.
- Known exceptions to the diagram: `Cabinet.AWS` references `Cabinet.Parsers` (upward), `Cabinet.Extras.Gaming` references `Cabinet.Parsers` for its base class, and `Cabinet.Agent` is standalone with no project references (it forwards logs over HTTP). Don't add references to the Agent.
- No circular references. New services are registered via DI in `Program.cs` / `ServiceCollectionExtensions.cs`.

### Storage is pluggable behind `ILogStorage` (`Cabinet.Core/Interfaces/ILogStorage.cs`)

Backends are interchangeable: SQLite (default/dev), In-Memory (tests), PostgreSQL (prod), OpenSearch (enterprise), ClickHouse (`Cabinet.Database/Providers/ClickHouseLogStorage.cs`, HTTP interface, typed query parameters), and the Cabinet engine (`Cabinet.Storage/Engine`, our own columnar store: write-ahead log, in-memory tier, immutable segments with per-block summaries and Bloom filters, background merges; described in ARCHITECTURE.md "The Cabinet Engine"). Selection is by `Storage:Type` config (`SQLite`, `Memory`, `PostgreSQL`/`Postgres`, `OpenSearch`/`Elasticsearch`, `ClickHouse`, `Cabinet`/`Engine`; validated at startup in `Program.cs`). If a remote backend is unreachable at startup, the API retries, then exits with a failure code; there is no fallback backend. Metadata lives on the backend where it can (`Infrastructure/StorageRegistration.cs`). On OpenSearch and the Cabinet engine it uses a local SQLite database (`Storage:MetadataConnectionString`), or PostgreSQL with `Storage:MetadataPostgresConnectionString`. New shared state goes in `IDocumentStore`. Work only one instance may do checks `ILeaderLease`. When adding a backend, implement all `ILogStorage` methods, follow the volatile + `SemaphoreSlim` init pattern (see ARCHITECTURE.md "Thread-Safety Patterns"), parameterize **all** queries, and register in `Program.cs`'s storage switch.

### Optional extensions

Game log parsers and moderation detectors live in `Cabinet.Extras.Gaming`, off unless `Gaming:Enabled` is set. Keep game-specific code out of Core and the other services. `DetectionController` stays in the API because extensions can't reference it, and `ExcludeControllersFeatureProvider` removes its routes when the extension is off. `api-spec.json` describes the default configuration, so it omits them. Parser order matters: a line goes to the first parser that accepts it (`AddCabinetParsers`).

### Agent delivery (`Cabinet.Agent`)

Inputs are files, `kubernetes` (files, unwrapped), `journald` and `wineventlog`. The last two keep their position in `InputCheckpoints` (`LogEventDto.InputKey`/`Checkpoint`), moved on `BatchDurable` like file checkpoints.

Delivery is at-least-once with server-side dedupe, and both halves matter. A checkpoint moves only from `LogForwarder.BatchDurable`, after the server accepts a batch or it's on disk, never when an event is queued. Event IDs derive from `FileKey` and byte offset, so retries and re-reads collapse. `FileKey` belongs to a file's contents, surviving renames and replaced on rotation. `ILogStorage` implementations must ignore an existing ID rather than overwrite it. Never drop events to relieve pressure: a full queue pauses reading.

After changing agent delivery, run `./tests/EndToEnd/agent-chaos.sh 5`. It kills the API, agent and Postgres mid-stream and needs every line stored exactly once. Its bugs are timing-dependent, so one clean run proves little.

### BQL query engine (`Cabinet.Query`)

Hand-written recursive-descent parser (~3000 lines in `Bql/BqlParser.cs`) — not a parser generator. Pipeline:

```
Text → BqlLexer → BqlParser → AST → BqlOptimizer (predicate pushdown) → BqlQueryEngine → Results
```

The optimizer pushes only the leading run of filters, and only predicates every backend evaluates exactly like the pipeline (search text, `severity >=`, `host =`, `source_type =`, time range, sort by time); everything else runs as pipeline stages. A required `index=` term or `where index=` only narrows storage through `AllowedIndexes`, which every backend matches ignoring case, and the stage stays (`BqlOptimizer.NarrowToIndex`); `IndexRestrictedLogStorage` intersects it with the caller's indexes. Don't push a predicate unless backends match pipeline semantics; `BqlEndToEndTests` runs the same queries on all four backends. Plain-English questions are read before the engine by `Cabinet.Query/Natural` (`QueryUnderstander`, `NaturalReader`, `Lexicon`), only on interactive paths; the engine itself never interprets English, so saved searches, alerts and rules run exactly as stored. A new phrasing needs a row in `NaturalQueryTests`, and the "plain words" tables in BQL-GUIDE.md and README.md are tests too. Rows are `BqlRow`, a dictionary with case-insensitive field names and aliases, because stages (`eval`, `rename`, `rex`, `stats`) change the schema at runtime. `BqlQueryEngine` reads storage a page at a time with `QueryContext.After` and feeds a pipeline's leading row stages into its first stateful stage, an `IRowSink` (`BqlStreaming.cs`), so memory follows what's computed rather than events read. Stages that need all their rows hold up to `QueryLimits.MaxRowsInMemory` and set `Statistics.Truncated`. Every aggregate goes through `AggregateAccumulator`. A leading `stats`/`timechart` can run in storage through `IAggregatingLogStorage` instead; it must give exactly the pipeline's answer or return null, and any `ILogStorage` decorator must forward it or refuse (`AggregatePushdownTests` compares both on every backend). A word means one thing everywhere: `SearchTerms`. Search text is parsed by `BqlTextSearch` (OR, NOT, phrases, wildcards, field terms); the optimizer pushes only its `RequiredWords` and keeps the stage unless `IsPlainWords`. A failed subsearch, join, macro or lookup throws `QueryExecutionException` rather than changing the answer. Comparisons with a missing value are false, and `stats ... by` drops events missing a by-field, as in SPL. Operators after the first stateful stage get the query's `QueryBudget` through `QueryOperator.Budget`; a new stage needs a case in both `CreateOperator` (engine) and the parser's legacy conversion.

### Detection

The storage chain is `PipelineLogStorage` (the ingest pipeline: drop, sample, route, redact) → `DetectingLogStorage` → encryption → backend, so everything stored passes the pipeline and then reaches `DetectionEngine` by any route. `DetectingLogStorage` also tells `QueryCacheService` about every write. Rules are `DetectionRule`s: `BqlEventMatcher` evaluates them, built-in ones are in `BuiltinDetectionRules` (every one needs an attack line in `BuiltinDetectionRuleTests`). Firings go to `DetectionActionExecutor`, a background queue that saves history first and runs each action with a timeout, on the event masked as for a Viewer; other alerts go through `AlertDispatcher`, which saves history before sending. Outbound URLs people configure (webhooks, connectors) go through `OutboundUrlGuard`'s client, which refuses private addresses and redirects. Anomaly rules (`AnomalyDetector`), scheduled reports (`ReportService`) and data-model rebuilds run on the leader only, through an unprivileged scope, so their values are masked as for a Viewer. Counts and cooldowns go through `IClusterCoordinator`, so they're shared by instances. Alert history stores the event's ID and index, never its line.

### Dashboard

`wwwroot/index.html`, `app.js`, `charts.js` and `v3.js`, with no build step. Pages added since 3.0 live in `v3.js` and register in `window.EXTRA_PAGES`, which `app.js`'s navigation reads. Without an API key the dashboard uses the SSO session cookie and sends `authHeaders()`, which adds `X-Cabinet-Request`. The policy forbids inline script, so handlers are `data-click="fn"` with optional `data-args`, not `onclick`. Anything from a log goes through `esc`. `charts.js` keeps its logic pure so that `DashboardTests` can run it in Jint.

### Identity & auth

- Event IDs are **ULIDs** (time-ordered, 26-char Crockford Base32) — not GUIDs.
- Auth is **API key via `X-API-Key` header only** (no query string). Keys are stored as SHA-256 hashes and compared with every stored hash in constant time (`ApiKeyService`). `/metrics` needs a key above Ingest when `Metrics:RequireKey` is on (new installs), except from loopback and `Metrics:AllowFrom`. Tiered keys (Basic/Professional/Enterprise) map to RBAC roles.
- SSO: OIDC bearer tokens for API clients; for the dashboard, OIDC code flow, SAML (ITfoxtec) and LDAP all end in the `Session` cookie scheme (`BrowserSession`, `AuthController` under `/auth`). A cookie request that changes anything must carry `X-Cabinet-Request`. Claims map to roles through `ClaimRoleMapper`.

### Security pipeline (every request)

Order as wired in `Program.cs`, omitting logging, CORS, and static files:

`IpRateLimitingMiddleware (pre-auth) → Authentication (API key / OIDC) → Authorization → ASP.NET rate limiter → [RequireCapability] filter → Controller`

Capability checks are per-controller or per-action attributes, not middleware, so a new endpoint has no RBAC check unless you add `[RequireCapability]`; `EveryEndpoint_RequiresACapability` fails if you don't. Anything that then applies to every index whoever triggers it also needs `[RequireEveryIndex(capability)]`. The attribute checks a capability on any index and records the caller in the scoped `IndexAccessScope`. Reads go through `IndexRestrictedLogStorage` to enforce it, so anything reading `ILogStorage` for a caller must use `scope.Restrict(storage)`, and writes and deletes check `scope.Can(capability, index)`. Each deployment serves one organization; there is no multi-tenancy. Audit log is SHA-256 hash-chained in `IDocumentStore`, one chain per process (`TamperEvidentAuditLogger`), and masking applies in `IndexRestrictedLogStorage`.

## Code Conventions

- File-scoped namespaces. `sealed` classes by default unless inheritance is intended.
- Async methods end in `Async` and take `CancellationToken` as the last parameter with a `default` value; propagate it through the chain.
- Streaming returns use `IAsyncEnumerable<T>` with `[EnumeratorCancellation]`.
- Private fields: `_camelCase`. Interfaces: `I`-prefixed.
- Use `required` for mandatory properties; use string interpolation, not `string.Format`.
- Never use `Thread.Sleep` — use `await Task.Delay`.
- Empty catch blocks are forbidden — log or rethrow.
- Public interfaces/models get XML doc comments.

## Thread-safety patterns to reuse (don't invent new ones)

1. **One-time async init**: `volatile bool _initialized` + `SemaphoreSlim _initLock` with double-checked locking.
2. **Shared maps**: `ConcurrentDictionary`.
3. **Counters**: `Interlocked.Increment`.
4. **Multi-field atomic update**: plain `lock`.
5. **Async mutex over I/O**: `SemaphoreSlim(1,1)`.

See ARCHITECTURE.md "Thread-Safety Patterns" for canonical examples.

## Testing

Tests live in `tests/Cabinet.Tests/` (`Agent/`, `CLI/`, `Controllers/`, `Core/`, `Gaming/`, `Middleware/`, `Parsers/`, `Query/`, `Services/`, `Storage/`). The test project references the API, Agent and CLI projects. `CONTRIBUTING.md` lists `API/`, `Alerts/`, and `Integration/`, but those directories don't exist. xUnit + FluentAssertions + Moq, AAA structure. Bug fixes need a regression test that fails without the fix. New storage backends need integration tests against the real backend (or in-memory substitute).

Postgres, OpenSearch and ClickHouse integration tests skip themselves unless `CABINET_TEST_POSTGRES_CONNSTR`, `CABINET_TEST_OPENSEARCH_URL` or `CABINET_TEST_CLICKHOUSE_URL` (plus optional `_USER`/`_PASS`) is set, so skipped tests in a normal run are expected. CI sets all three against real services.

The test project has no coverlet package, so coverage flags (`/p:CollectCoverage=true`, `--collect:"XPlat Code Coverage"`) produce no coverage data.

## Further reading

- `docs/ARCHITECTURE.md` — layer rules, ADRs, thread-safety patterns, storage/BQL internals
- `docs/BQL-GUIDE.md` — BQL language reference
- `docs/API-GUIDE.md` — REST API guide
- `docs/DEPLOYMENT.md` — deployment topologies
- `CONTRIBUTING.md` — full code standards, PR process, branch naming
- `docs/CI.md` — runner policy. CI uses shared self-hosted runners; read this before changing workflows
- `api-spec.json` — OpenAPI spec generated from the API; a test fails if it drifts. Regenerate with `CABINET_UPDATE_API_SPEC=1 dotnet test --filter ApiSpec_MatchesTheRunningApi`
