# Cabinet - Enterprise Log Intelligence System

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![Build](https://img.shields.io/github/actions/workflow/status/bogware/cabinet/ci.yml?branch=main)](https://github.com/bogware/cabinet/actions)
[![Docker](https://img.shields.io/badge/Docker-Ready-blue)](https://www.docker.com/)
[![Status](https://img.shields.io/badge/Status-Production%20Ready-green)](https://github.com)

Cabinet is an enterprise-grade log intelligence system — a modern, open-source alternative to Splunk. Built with .NET 8.0, it provides log ingestion, real-time search, a powerful query language (BQL), alerting, dashboards, and a lightweight forwarding agent. Storage is pluggable: SQLite (dev), PostgreSQL (production), or OpenSearch (enterprise scale).

## Quick Start

### Docker (fastest)

```bash
git clone https://github.com/draxios/cabinet.git && cd cabinet
docker-compose up -d
# Dashboard: http://localhost:8080
# API: http://localhost:8080/api/v1
```

### Local Development

```bash
# Prerequisites: .NET 8.0 SDK
dotnet build && dotnet test    # 786 tests, all passing
dotnet run --project src/Cabinet.API
# Dashboard: http://localhost:5068
```

---

## Installation

Cabinet ships as self-contained single-file binaries for all major platforms. No runtime required.

### Linux — .deb Package (Debian/Ubuntu)

```bash
# Download from GitHub Releases
wget https://github.com/draxios/cabinet/releases/latest/download/cabinet-api_1.0.0_amd64.deb

# Install — creates systemd service, cabinet user, directory structure
sudo dpkg -i cabinet-api_1.0.0_amd64.deb

# Start the server
sudo systemctl start cabinet-api

# Dashboard: http://localhost:8080
# Logs: journalctl -u cabinet-api -f
```

ARM64 (Raspberry Pi, AWS Graviton): use `cabinet-api_1.0.0_arm64.deb`

### Linux — Installer Script (Any Distro)

```bash
# Download installer
wget https://github.com/draxios/cabinet/releases/latest/download/install-server.sh

# Install with interactive setup wizard
sudo ./install-server.sh --run-wizard

# Or install with specific options
sudo ./install-server.sh --port 8080 --storage postgresql \
  --conn-string "Host=db;Database=cabinet;Username=cabinet;Password=secret"
```

The installer:
- Creates a `cabinet` service user (non-root)
- Installs to `/opt/cabinet/` with data in `/var/lib/cabinet/`
- Sets up a security-hardened systemd service (ProtectSystem, PrivateTmp, NoNewPrivileges)
- Creates a CLI symlink at `/usr/local/bin/cabinet`
- Generates initial configuration in `/etc/cabinet/`

### Windows — PowerShell Installer

```powershell
# Run PowerShell as Administrator
.\install-server.ps1

# Or with options
.\install-server.ps1 -Port 8080 -Storage sqlite -RunWizard

# With agent bundled
.\install-server.ps1 -IncludeAgent -RunWizard
```

The installer:
- Installs to `C:\Program Files\Cabinet\` with data in `C:\ProgramData\Cabinet\`
- Registers `CabinetAPI` Windows Service (auto-start)
- Adds firewall rule for the API port (Domain + Private networks)
- Adds the CLI to system PATH

### macOS — Manual Install

```bash
# Download the binary
wget https://github.com/draxios/cabinet/releases/latest/download/cabinet-osx-arm64.tar.gz
tar xzf cabinet-osx-arm64.tar.gz

# Run directly
./api/Cabinet.API --urls http://localhost:8080
```

For Intel Macs, use `cabinet-osx-x64.tar.gz`.

### Docker Compose (Production)

```bash
# API + PostgreSQL + Redis
docker-compose up -d

# With monitoring (Grafana + Prometheus)
docker-compose --profile monitoring up -d
```

### Kubernetes

Manifests in `k8s/` include Deployment, Service, ConfigMap, HPA, PDB, NetworkPolicy, Ingress, and PVC.

```bash
kubectl apply -f k8s/namespace.yaml
kubectl apply -f k8s/
```

---

## Installing the Agent

Cabinet Agent is a lightweight log forwarder — install it on any machine to ship logs to your Cabinet server. Similar to Splunk Universal Forwarder or Elastic Filebeat.

### Linux Agent

```bash
# .deb package
sudo dpkg -i cabinet-agent_1.0.0_amd64.deb
# Edit /opt/cabinet/agent/appsettings.json with your server URL and API key
sudo systemctl start cabinet-agent

# Or use the installer script
sudo ./install-agent.sh \
  --server https://cabinet.example.com \
  --api-key "your-api-key" \
  --paths "/var/log/*.log,/var/log/syslog"
```

### Windows Agent

```powershell
# Run PowerShell as Administrator
.\install-agent.ps1 -ServerUrl https://cabinet.example.com -ApiKey "your-api-key"
# Prompts for log paths if not provided

# Or with all options
.\install-agent.ps1 -ServerUrl http://cabinet:8080 `
  -ApiKey "your-key" `
  -Paths "C:\inetpub\logs\*.log,C:\app\logs\*.log"
```

### Docker Agent

```bash
docker run -d \
  -v /var/log:/var/log:ro \
  -e Agent__Output__ServerUrl=http://cabinet:8080 \
  -e Agent__Output__ApiKey=your-key \
  ghcr.io/draxios/cabinet-agent
```

### Agent Features

- **File monitoring**: Glob patterns, periodic scan for new files
- **Checkpointing**: Resumes from last position across restarts — never re-sends data
- **Log rotation**: SHA256 header hash detects rotation and truncation
- **Multi-line merging**: Java stack traces, Python tracebacks, custom patterns
- **Batched delivery**: Configurable batch size + flush interval
- **Reliable**: Exponential backoff retry, disk spill buffer for offline resilience
- **Splunk HEC compatible**: Uses `/services/collector/event` protocol

---

## Setup Wizard

On first launch, Cabinet provides an interactive setup wizard at `http://localhost:8080/setup` to configure:

- Storage backend (SQLite, PostgreSQL, OpenSearch)
- API key generation
- Ingestion settings
- Optional features (syslog listener, scheduled searches, alerts)

Use `--run-wizard` flag with any installer to auto-open the wizard after install.

---

## Architecture

```
┌─────────────────┐     ┌─────────────────────────────────────┐
│  Cabinet Agent  │────▶│           Cabinet API                │
│ (Log Forwarder) │     │                                     │
├─────────────────┤     │  ┌──────────┐  ┌────────────────┐   │
│   Cabinet CLI   │────▶│  │ REST API │  │   Web Dashboard│   │
└─────────────────┘     │  └──────────┘  └────────────────┘   │
                        │  ┌──────────┐  ┌────────────────┐   │
┌─────────────────┐     │  │BQL Query │  │   Detection    │   │
│  Syslog/HEC     │────▶│  │ Engine   │  │    Suites      │   │
│  (TCP/UDP)      │     │  └──────────┘  └────────────────┘   │
└─────────────────┘     │  ┌──────────┐  ┌────────────────┐   │
                        │  │ Alerting │  │  Dashboards &  │   │
                        │  │ Engine   │  │  Saved Queries │   │
                        │  └──────────┘  └────────────────┘   │
                        └──────────┬──────────────────────────┘
                                   │
                 ┌─────────────────┼─────────────────┐
                 ▼                 ▼                  ▼
          ┌──────────┐     ┌──────────────┐   ┌──────────────┐
          │  SQLite   │     │ PostgreSQL   │   │  OpenSearch  │
          │  (dev)    │     │ (production) │   │ (enterprise) │
          └──────────┘     └──────────────┘   └──────────────┘
```

## Ingestion

Cabinet accepts logs from multiple sources:

| Method | Endpoint | Format |
|--------|----------|--------|
| REST API | `POST /api/v1/logs` | Cabinet JSON |
| Splunk HEC | `POST /services/collector/event` | Splunk HEC JSON |
| Splunk HEC Raw | `POST /services/collector/raw` | Plain text |
| Syslog | TCP/UDP port 1514 | RFC 3164 |
| Agent | File monitoring | Any format |

```bash
# REST API ingest
curl -X POST http://localhost:8080/api/v1/logs \
  -H "X-API-Key: dev-enterprise-key" \
  -H "Content-Type: application/json" \
  -d '[{"rawLine": "2024-01-15 ERROR Database connection timeout", "sourceType": "application"}]'

# Splunk HEC ingest (compatible with any Splunk forwarder)
curl -X POST http://localhost:8080/services/collector/event \
  -H "Authorization: Splunk dev-enterprise-key" \
  -d '{"event": "Server started successfully", "sourcetype": "syslog", "host": "web-01"}'
```

---

## BQL Query Language

BQL (Bog Query Language) is Cabinet's native query language — natural language-inspired with full SPL backward compatibility.

### Search & Filter

```
find errors from last 24h
find * | where severity >= warning and host = "web-01"
find * | where status in (500, 502, 503)
find connection timeout | where source = "database"
```

### Aggregation & Statistics

```
find * | stats count, avg(duration) by host, status
find * | stats dc(user) as unique_users by host
find * | stats median(latency), stdev(latency), perc95(latency) by endpoint
find * | timechart span=1h count by severity
find * | top 10 host
find * | rare source
```

### Field Manipulation

```
find * | eval total = price * quantity, tax = total * 0.1
find * | extract "user=(?<username>\w+)" from message
find * | rename old_field as new_field
find * | select timestamp, severity, message
find * | unique by host, source
find * | convert dur2sec(duration) memk(memory)
```

### Advanced Pipeline

```
find * | where severity >= 4
  | stats count as errors, avg(response_time) as avg_rt by host
  | where errors > 100
  | sort by errors desc
  | limit 20

find * | transaction host maxspan=1h maxevents=100
find * | eventstats avg(duration) as avg_dur by host
find * | streamstats count as running_count by source
find * | lookup users user_id OUTPUT email, name
find * | fieldsummary
```

### SPL Compatibility

```
search error | where severity >= 3 | head 10
* | stats count by source
* | sort -timestamp | dedup host
* | eval duration = end_time - start_time
* | rex field=source "(?<ip>\d+\.\d+\.\d+\.\d+)"
```

### Natural Language

```
show me errors from the last 24 hours
how many events by host
top 10 errors by source
```

Full BQL reference: [docs/BQL-GUIDE.md](docs/BQL-GUIDE.md)

---

## API Reference

Base URL: `/api/v1` | Auth: `X-API-Key` header

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/v1/logs` | GET/POST | Log CRUD + ingest |
| `/api/v1/query/execute` | POST | Execute BQL query |
| `/api/v1/query/stream` | POST | Stream results (NDJSON) |
| `/api/v1/query/validate` | POST | Validate BQL syntax |
| `/api/v1/query/explain` | POST | Show query execution plan |
| `/api/v1/query/translate` | POST | Natural language to BQL |
| `/api/v1/query/export` | POST | Export results as CSV |
| `/api/v1/query/saved` | GET/POST/PUT/DELETE | Saved queries CRUD |
| `/api/v1/dashboards` | GET/POST/PUT/DELETE | Dashboard CRUD |
| `/api/v1/knowledge` | GET/POST/PUT/DELETE | Lookups, macros, field aliases |
| `/api/v1/scheduled-searches` | GET/POST/PUT/DELETE | Scheduled search CRUD |
| `/api/v1/alerts` | GET/POST | Alert history |
| `/api/v1/detection/{suite}` | POST | Run detection suites |
| `/api/v1/indexes` | GET/DELETE | Index management |
| `/api/v1/roles` | GET/PUT/DELETE | RBAC role management |
| `/api/v1/retention` | GET/PUT/DELETE | Per-index retention policies |
| `/api/v1/field-extractions` | GET/POST/PUT/DELETE | Runtime field extractions |
| `/api/v1/datamodels` | GET/POST/DELETE | Data model acceleration |
| `/health` | GET | Health/readiness/liveness probes |
| `/metrics` | GET | Prometheus metrics |
| `/services/collector/event` | POST | Splunk HEC JSON endpoint |
| `/services/collector/raw` | POST | Splunk HEC raw endpoint |

All list endpoints support `offset` and `count` query parameters with `X-Total-Count` response header.

All error responses use RFC 7807 ProblemDetails format.

Dev API keys (development only): `dev-basic-key`, `dev-professional-key`, `dev-enterprise-key`

Full API guide: [docs/API-GUIDE.md](docs/API-GUIDE.md)

---

## Enterprise Features

| Feature | Description |
|---------|-------------|
| **RBAC** | Role-based access (Viewer/Analyst/Admin/SuperAdmin) with per-index permissions |
| **Multi-tenancy** | Tenant isolation via index prefix scoping |
| **PII Masking** | Field-level masking at query time (redact, hash, partial, regex, remove) |
| **Encryption at Rest** | AES-256-GCM with configurable key management |
| **Tamper-Evident Audit** | SHA256 hash-chained audit trail |
| **Scheduled Searches** | Cron-based queries with alert conditions |
| **Alerting** | Console, File, Webhook (Slack/Discord/Teams/PagerDuty), Email |
| **Dashboards** | CRUD API with panels, grid layout, visualization types |
| **Knowledge Objects** | Lookups, macros, field aliases |
| **Distributed Mode** | Redis-backed rate limiting, leader election, API key caching |
| **Data Model Acceleration** | Pre-aggregated summary tables for tstats-like queries |
| **Multi-line Merging** | Java stack traces, Python tracebacks, custom patterns |
| **Syslog Listener** | RFC 3164 TCP/UDP ingestion |
| **Log Parsers** | L4D2, SourceMod, SteamCMD, Apache, Nginx, JSON, CSV, CEF, Generic |

---

## Configuration

### Storage Backend

Set via `Storage:Type` environment variable or `appsettings.json`:

| Backend | Config Value | Use Case | Connection String |
|---------|-------------|----------|-------------------|
| SQLite | `SQLite` | Development, single-node | `Data Source=cabinet.db` |
| In-Memory | `Memory` | Testing | N/A |
| PostgreSQL | `PostgreSQL` | Production | `Host=localhost;Database=cabinet;Username=cabinet;Password=secret` |
| OpenSearch | `OpenSearch` | Enterprise, distributed | `https://localhost:9200` |

### Environment Variables

```bash
Storage__Type=PostgreSQL
Storage__ConnectionString="Host=db;Database=cabinet;Username=cabinet;Password=secret"
ApiKeys__Keys__0__Key=your-api-key-here
ApiKeys__Keys__0__Tier=Enterprise
ApiKeys__Keys__0__ClientName="Production App"
Distributed__Enabled=true
Distributed__RedisConnectionString=redis:6379
```

### Distributed Mode (Multi-Instance)

```json
{
  "Distributed": {
    "Enabled": true,
    "RedisConnectionString": "redis:6379",
    "InstanceId": "cabinet-pod-1",
    "LeaderElection": { "Enabled": true, "LeaseTimeSeconds": 30 },
    "Cache": { "ApiKeyTtlSeconds": 60 }
  }
}
```

---

## Security

- **Authentication**: API key via `X-API-Key` header only (no query string)
- **RBAC**: Granular capabilities (Search, Ingest, Export, Detection, IndexManagement) with per-index permissions
- **Rate Limiting**: Pre-auth per-IP sliding window + per-tier limits
- **Input Validation**: All inputs validated; regex timeouts prevent ReDoS; query size limits prevent DoS
- **SQL Injection**: Fully parameterized queries in all storage backends
- **Encryption**: AES-256-GCM at rest, HTTPS in transit
- **Audit Trail**: Tamper-evident hash-chained audit logging
- **Container Security**: Non-root user (uid 1000), port 8080, minimal base images
- **systemd Hardening**: ProtectSystem=strict, PrivateTmp, NoNewPrivileges, PrivateDevices
- **Security Headers**: CSP, X-Content-Type-Options, X-Frame-Options, Referrer-Policy

---

## Monitoring

| Endpoint | Purpose |
|----------|---------|
| `GET /health` | Overall health status |
| `GET /health/ready` | Readiness probe (for K8s) |
| `GET /health/live` | Liveness probe (for K8s) |
| `GET /metrics` | Prometheus-compatible metrics |

Metrics include HTTP request counts/latencies, storage stats, and .NET process/GC metrics.

---

## Building Releases

```bash
# Build all platforms + .deb packages
./scripts/build-release.sh 1.0.0

# Output in dist/:
#   cabinet-api-1.0.0-linux-x64.tar.gz     (+ SHA256)
#   cabinet-api-1.0.0-linux-arm64.tar.gz
#   cabinet-api-1.0.0-win-x64.zip
#   cabinet-api-1.0.0-osx-x64.tar.gz
#   cabinet-api-1.0.0-osx-arm64.tar.gz
#   cabinet-cli-1.0.0-{platform}.{ext}
#   cabinet-agent-1.0.0-{platform}.{ext}
#   cabinet-api_1.0.0_amd64.deb
#   cabinet-agent_1.0.0_amd64.deb
#   install-server.sh, install-server.ps1
#   install-agent.sh, install-agent.ps1
```

CI/CD automatically builds all artifacts on tagged releases (`v*`).

---

## Testing

```bash
dotnet test                          # Run all 786+ tests
dotnet test --filter Category=Unit   # Unit tests only
dotnet test /p:CollectCoverage=true  # With coverage
```

Coverage areas: Parsers, BQL query engine, expression evaluator, storage backends, detection suites, all API controllers, middleware, alert engine, agent components.

---

## Development

### Prerequisites

- .NET 8.0 SDK
- Docker & Docker Compose (optional, for PostgreSQL/Redis)

### Project Structure

```
cabinet/
├── src/
│   ├── Cabinet.Core/        # Models, interfaces, configuration
│   ├── Cabinet.API/         # ASP.NET Core REST API + Web Dashboard
│   ├── Cabinet.CLI/         # Command-line interface
│   ├── Cabinet.Agent/       # Log forwarding agent
│   ├── Cabinet.Query/       # BQL parser, optimizer, query engine
│   ├── Cabinet.Parsers/     # Log parsers (L4D2, Apache, Nginx, etc.)
│   ├── Cabinet.Storage/     # SQLite + In-Memory backends
│   ├── Cabinet.Database/    # PostgreSQL + OpenSearch backends
│   ├── Cabinet.Detection/   # Security detection suites
│   ├── Cabinet.Alerts/      # Alert channels (webhook, email, file)
│   └── Cabinet.AWS/         # AWS integrations (S3, CloudWatch)
├── tests/Cabinet.Tests/     # 786+ xUnit tests
├── scripts/                 # Install scripts + build-release.sh
├── k8s/                     # Kubernetes manifests
├── docs/                    # Architecture, API guide, BQL guide
├── docker-compose.yml       # Docker orchestration
└── .github/workflows/       # CI/CD (build, test, release, Docker)
```

### Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

---

**Version**: 1.0.0
**Last Updated**: December 2024
**Status**: Production Ready
**Build**: All tests passing (28/28)
