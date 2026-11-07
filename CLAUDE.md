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

# Build cross-platform release artifacts + .deb packages into dist/
./scripts/build-release.sh 1.0.0

# Docker stack (API on host port 5000, CLI, Agent, Postgres, Redis)
# Optional profiles: monitoring (Grafana/Prometheus), development (LocalStack)
docker-compose up -d
```

Dev API keys (development only): `dev-basic-key`, `dev-professional-key`, `dev-enterprise-key` — pass via `X-API-Key` header.

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

Backends are interchangeable: SQLite (default/dev), In-Memory (tests), PostgreSQL (prod), OpenSearch (enterprise). Selection is by `Storage:Type` config (`SQLite`, `Memory`, `PostgreSQL`/`Postgres`, `OpenSearch`/`Elasticsearch`; validated at startup in `Program.cs`). If a remote backend is unreachable at startup, the API retries, then exits with a failure code; there is no fallback backend. Stores with no implementation on the selected backend use a local SQLite database (`Storage:MetadataConnectionString`, see `Infrastructure/StorageRegistration.cs`), so that metadata is per-instance. When adding a backend, implement all `ILogStorage` methods, follow the volatile + `SemaphoreSlim` init pattern (see ARCHITECTURE.md "Thread-Safety Patterns"), parameterize **all** queries, and register in `Program.cs`'s storage switch.

### Optional extensions

Game log parsers and moderation detectors live in `Cabinet.Extras.Gaming`, off unless `Gaming:Enabled` is set. Keep game-specific code out of Core and the other services. `DetectionController` stays in the API because extensions can't reference it, and `ExcludeControllersFeatureProvider` removes its routes when the extension is off. `api-spec.json` describes the default configuration, so it omits them. Parser order matters: a line goes to the first parser that accepts it (`AddCabinetParsers`).

### BQL query engine (`Cabinet.Query`)

Hand-written recursive-descent parser (~3000 lines in `Bql/BqlParser.cs`) — not a parser generator. Pipeline:

```
Text → BqlLexer → BqlParser → AST → BqlOptimizer (predicate pushdown) → BqlQueryEngine → Results
```

The optimizer pushes only the leading run of filters, and only predicates every backend evaluates exactly like the pipeline (search text, `severity >=`, `host =`, `source_type =`, time range, sort by time); everything else runs as pipeline stages. Don't push a predicate unless backends match pipeline semantics; `BqlEndToEndTests` runs the same queries on all four backends. Rows are `BqlRow`, a dictionary with case-insensitive field names and aliases, because stages (`eval`, `rename`, `rex`, `stats`) change the schema at runtime. Materializing rows is a known bottleneck for large results (ADR-004 / PERF-01). Queries reading more than `QueryContext.MaxLimit` events set `Statistics.Truncated`.

### Identity & auth

- Event IDs are **ULIDs** (time-ordered, 26-char Crockford Base32) — not GUIDs.
- Auth is **API key via `X-API-Key` header only** (no query string). Keys are stored as SHA-256 hashes and looked up by hash in a dictionary (`ApiKeyService`). `ARCHITECTURE.md` and the class comment say "constant-time comparison", but the code doesn't do one. Tiered keys (Basic/Professional/Enterprise) map to RBAC roles.
- SSO: **only OIDC is implemented** (ADR-008). `SamlSettings`/`LdapSettings` config models exist but selecting them only logs a warning — no handler is wired. See `Cabinet.Core/Models/SsoConfiguration.cs` and `Cabinet.API/Authentication/OidcAuthenticationService.cs`.

### Security pipeline (every request)

Order as wired in `Program.cs`, omitting logging, CORS, and static files:

`IpRateLimitingMiddleware (pre-auth) → Authentication (API key / OIDC) → Authorization → ASP.NET rate limiter → [RequireCapability] filter → Controller`

Capability checks are per-controller or per-action attributes, not middleware, so a new endpoint has no RBAC check unless you add `[RequireCapability]`. Each deployment serves one organization; there is no multi-tenancy. Audit log is SHA-256 hash-chained (`TamperEvidentAuditLogger`).

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

Tests live in `tests/Cabinet.Tests/` (`Agent/`, `CLI/`, `Controllers/`, `Core/`, `Gaming/`, `Middleware/`, `Parsers/`, `Query/`, `Services/`, `Storage/`). `CONTRIBUTING.md` lists `API/`, `Alerts/`, and `Integration/`, but those directories don't exist. xUnit + FluentAssertions + Moq, AAA structure. Bug fixes need a regression test that fails without the fix. New storage backends need integration tests against the real backend (or in-memory substitute).

Postgres and OpenSearch integration tests skip themselves unless `CABINET_TEST_POSTGRES_CONNSTR` or `CABINET_TEST_OPENSEARCH_URL` (plus optional `_USER`/`_PASS`) is set, so skipped tests in a normal run are expected. CI sets the Postgres variable against a real database.

The test project has no coverlet package, so coverage flags (`/p:CollectCoverage=true`, `--collect:"XPlat Code Coverage"`) produce no coverage data.

## Further reading

- `docs/ARCHITECTURE.md` — layer rules, ADRs, thread-safety patterns, storage/BQL internals
- `docs/BQL-GUIDE.md` — BQL language reference
- `docs/API-GUIDE.md` — REST API guide
- `docs/DEPLOYMENT.md` — deployment topologies
- `CONTRIBUTING.md` — full code standards, PR process, branch naming
- `docs/CI.md` — runner policy. CI uses shared self-hosted runners; read this before changing workflows
- `api-spec.json` — OpenAPI spec generated from the API; a test fails if it drifts. Regenerate with `CABINET_UPDATE_API_SPEC=1 dotnet test --filter ApiSpec_MatchesTheRunningApi`
