# Cabinet

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Build](https://github.com/bogware/cabinet/actions/workflows/ci.yml/badge.svg)](https://github.com/bogware/cabinet/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue)](LICENSE)

Cabinet is a self-hosted log search system built on .NET 10. It accepts logs over a REST API, the Splunk HTTP Event Collector (HEC) protocol, syslog and a forwarding agent, stores them in SQLite, PostgreSQL or OpenSearch, and searches them with a pipeline query language called BQL. It also has a web dashboard, saved queries, and scheduled searches that send alerts.

Each deployment serves one organization and should run as a single API instance. Some of what the codebase contains is not finished; the [features table](#features) and [known limitations](#known-limitations) say which parts work today.

## Quick start

### Docker Compose

Requires Docker with the Compose plugin.

```bash
git clone https://github.com/bogware/cabinet.git && cd cabinet
cp .env.example .env
# Set every value in .env. Each must be non-empty. Generate one per value with:
#   openssl rand -hex 32
docker compose up -d
```

The API and dashboard are at `http://localhost:5000`. The dashboard asks for an API key; use the value of `CABINET_API_KEY` from `.env`. Postgres and Redis are not published to the host.

To add Grafana (`127.0.0.1:3000`) and Prometheus (`127.0.0.1:9090`):

```bash
docker compose --profile monitoring up -d
```

### From source

Requires the .NET 10 SDK (see `global.json`).

```bash
dotnet build
dotnet run --project src/Cabinet.API
```

This uses the Development environment on `http://localhost:5068` with SQLite. Swagger UI is at the root URL, and the keys `dev-basic-key`, `dev-professional-key` and `dev-enterprise-key` are accepted. Outside Development the root URL serves the dashboard instead, there is no Swagger UI, and you must configure your own keys.

Other install paths (packages, installer scripts, systemd, Windows service) are in [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md).

## Ingest and query

Examples use the Compose port. With `dotnet run`, use port 5068 and a dev key.

```bash
API_KEY=$(grep '^CABINET_API_KEY=' .env | cut -d= -f2)

# REST ingest: an array of raw lines, up to 10,000 per request
curl -X POST http://localhost:5000/api/v1/logs/ingest \
  -H "X-API-Key: $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"Logs": ["2026-01-15 10:30:00 ERROR Database connection timeout"]}'

# Splunk HEC. Also accepts X-API-Key.
curl -X POST http://localhost:5000/services/collector/event \
  -H "Authorization: Splunk $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"event": "Server started", "sourcetype": "app", "host": "web-01"}'

# Query
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"query": "find timeout | head 10"}'
```

BQL is a pipeline language with SPL-style stages:

```
find errors from last 24h
find * | where severity >= warning | stats count by host
find * | timechart span=1h count by severity
search error | sort -timestamp | head 10
```


## Features

| Feature | Status | Notes |
|---------|--------|-------|
| REST ingest | Available | `POST /api/v1/logs/ingest`. Events go to index `main`. |
| Splunk HEC ingest | Available | `/services/collector/event` and `/raw`. |
| Syslog listener | Working | RFC 3164 and RFC 5424 over UDP, TCP and TLS. Off by default, and Compose doesn't publish the port. |
| Forwarding agent | Available | Retries, a disk spool and checkpoints that follow delivery, so an outage or crash doesn't lose lines, and re-sent lines are stored once. See the limits below. |
| Storage backends | Available | SQLite, In-Memory, PostgreSQL, OpenSearch. |
| BQL query engine | Available | Every example in the guide runs as a test. Filters, `stats`, `timechart`, `chart`, `top`, `rare` and `sort | head` read every matching event without holding them in memory. Stages that need all their rows at once hold up to 100,000 and flag results as `truncated` beyond that. See [benchmarks](docs/BENCHMARKS.md). |
| Natural language to BQL | Available | Rule-based, `POST /api/v1/query/translate`. |
| Web dashboard | Available | Served from the API outside the Development environment. |
| Dashboards, saved queries, lookups, macros, field aliases | Available | REST APIs. |
| Async search jobs | Available | Private to their owner, and shared by all instances on PostgreSQL. |
| Scheduled searches | Available | Cron-based. Require an Enterprise-tier key. |
| Alert channels | Available | Console, file, email, and webhook with generic, Slack, Discord, Teams and PagerDuty formats. |
| Alert history API | Not implemented | `/api/v1/alerts` reads an in-memory store that nothing writes to, so it stays empty. |
| API key authentication | Available | `X-API-Key` header, with Basic, Professional, Enterprise and Admin tiers. |
| Roles and capabilities per endpoint | Available | A test fails if an endpoint has no capability check. |
| Per-index RBAC | Available | Grants limit a principal to some indexes for every read and write, including subsearches and search jobs. |
| Index and user management routes | Available | `IndexManagement` from the Enterprise tier, `UserManagement` from the Admin tier. |
| OIDC | Available | Bearer tokens, with claims mapped to roles and index grants. Tested end to end against a local identity provider. |
| SAML, LDAP | Not implemented | Configuration models only. |
| Rate limiting | Available | Per address and per key, counted separately on each instance. Queries and streams also have concurrency limits. |
| Audit log | Partial | Hash-chained in the log output only. |
| Global retention | Available | `Storage:RetentionDays`. 0 (the default) keeps everything. |
| Per-index retention | Not implemented | `/api/v1/retention` stores definitions in memory and doesn't enforce them. |
| Log parsers | Available | JSON, CEF, Apache, Nginx, CSV, Generic. |
| Data models | Available | Pre-aggregated summaries for `tstats`. |
| Field extractions | Partial | CRUD only. Definitions are held in memory and aren't applied to queries. |
| Gaming extension | Partial | Optional, off by default. Set `Gaming:Enabled` to add L4D2, SourceMod and SteamCMD parsers and on-demand moderation detection at `/api/v1/detection/*`. |
| Security detection rules | Not implemented | Nothing runs at ingest. |
| Health and Prometheus metrics | Available | `/health`, `/health/ready`, `/health/live`, `/metrics`. |
| AWS CloudWatch and CloudTrail | Available | CLI only (`cabinet aws`). |
| SQLite backup service | Partial | See below. Use `sqlite3 .backup` instead. |
| Kubernetes manifests | Partial | A starting point, not a working deployment. |
| Encryption at rest | Not implemented | The service is initialized, but nothing calls it. |
| PII and field masking | Not implemented | The service is registered, but never applied. |
| Hot/warm/cold tiering, S3 archival | Not implemented | |
| Multiple API instances | Available | On PostgreSQL, sharing all state except the audit chain head. One instance at a time runs background work. |
| Server-side multi-line merging | Not implemented | The agent merges multi-line events; the API does not. |
| Prometheus alert rules | Not implemented | Grafana dashboard and scrape config only. |

## Known limitations

- **Multiple instances need PostgreSQL.** With OpenSearch, set `Storage:MetadataPostgresConnectionString`. SQLite and in-memory storage serve one instance. Rate limits and the query cache are per instance.
- **Aggregates read every event through the API process.** They aren't pushed down to the database yet, so `stats` over a billion events takes a long time. See [docs/BENCHMARKS.md](docs/BENCHMARKS.md).
- **Agent delivery has edge cases.** Lines are lost if a file is deleted before they're read, for example during a long agent outage. If the registry file is lost, files are read again from the start under new event IDs. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#agent-behaviour).
- **The audit log is not tamper-proof.** Events are hash-chained in log output, there is no verifier, and the chain head is local to each instance.
- **The built-in SQLite backup** copies the database file without WAL consistency and has no restore. Use `sqlite3 cabinet.db ".backup ..."`, or `pg_dump` for PostgreSQL.
- **API keys** are looked up by SHA-256 hash in a dictionary. The comparison is not constant-time.
- **`/metrics` is unauthenticated.** Restrict it at the network. See [SECURITY.md](SECURITY.md).
- **Tier rate limits are hardcoded**: 100, 1,000 and 10,000 requests per minute. A separate per-address limit, 300 per minute by default, applies to all non-health requests and can be changed with `RateLimiting:PreAuth:*`.

## Command-line tools

`cabinet` is the CLI. Commands: `peek`, `tail`, `ingest`, `query`, `dashboard`, `aws`, `setup`, `version`, `help`. `cabinet setup` is the interactive setup wizard.

`Cabinet.Agent` is a separate forwarder that tails files and sends them to `/services/collector/event`. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#forwarding-agent).

## Project layout

```
src/
  Cabinet.Core/       Models, interfaces, configuration
  Cabinet.API/        REST API and web dashboard
  Cabinet.CLI/        Command-line interface
  Cabinet.Agent/      Log forwarding agent
  Cabinet.Query/      BQL parser, optimizer, query engine
  Cabinet.Parsers/    Log parsers
  Cabinet.Storage/    SQLite and in-memory backends
  Cabinet.Database/   PostgreSQL and OpenSearch backends
  Cabinet.Extras.Gaming/  Optional game log parsers and moderation detection
  Cabinet.Alerts/     Alert channels
  Cabinet.AWS/        CloudWatch and CloudTrail access, S3 client code
tests/Cabinet.Tests/  xUnit tests
tests/EndToEnd/       CLI end-to-end script
scripts/              Installers and build-release.sh
k8s/                  Kubernetes manifests (starting point)
```

## Documentation

- [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md): installation, configuration, operations
- [docs/API-GUIDE.md](docs/API-GUIDE.md): REST API. `api-spec.json` is the generated OpenAPI spec.
- [docs/BQL-GUIDE.md](docs/BQL-GUIDE.md): query language
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): layers, design decisions, internals
- [CONTRIBUTING.md](CONTRIBUTING.md): development workflow
- [SECURITY.md](SECURITY.md): reporting vulnerabilities, deployment guidance
- [CHANGELOG.md](CHANGELOG.md)

## License

Cabinet is licensed under the [Apache License 2.0](LICENSE).
