# Cabinet Deployment Guide

This guide covers installing and operating Cabinet. Run a single API instance; see [Scaling](#scaling).

## Table of Contents

- [Prerequisites](#prerequisites)
- [Docker Compose](#docker-compose)
- [Packages and Installers](#packages-and-installers)
- [From Source](#from-source)
- [Forwarding Agent](#forwarding-agent)
- [Kubernetes](#kubernetes)
- [Configuration Reference](#configuration-reference)
- [Security Hardening](#security-hardening)
- [Monitoring & Observability](#monitoring--observability)
- [Backup & Recovery](#backup--recovery)
- [Scaling](#scaling)
- [Troubleshooting](#troubleshooting)
- [Runbooks](#runbooks)

## Prerequisites

| Component | Version |
|-----------|---------|
| .NET | 10 SDK to build from source. Release binaries are self-contained. |
| Docker | With the Compose plugin, for the Compose stack |

[docs/BENCHMARKS.md](BENCHMARKS.md) has measured figures for one machine. Use them to judge scale, then size CPU, memory and disk against your own ingest rate and retention.

### Storage Backends

| Backend | `Storage:Type` | Notes |
|---------|----------------|-------|
| SQLite | `SQLite` | Default. Embedded, single file. |
| In-Memory | `Memory` | Lost on restart. For tests. |
| PostgreSQL | `PostgreSQL` | CI tests against PostgreSQL 16, and Compose runs it. |
| OpenSearch | `OpenSearch` | Connection string is the cluster URL. |

## Docker Compose

```bash
git clone https://github.com/bogware/cabinet.git
cd cabinet
cp .env.example .env
# Set every value in .env. Each must be non-empty; Compose refuses to start otherwise.
#   openssl rand -hex 32   # run once per value
docker compose up -d

# Verify
curl http://localhost:5000/health
# Expected: Healthy
```

The API and dashboard are published on host port 5000 (8080 in the container). Postgres is reachable only inside the Compose network.

Services:

| Service | Notes |
|---------|-------|
| `cabinet-api` | Uses PostgreSQL, with `CABINET_API_KEY` as an Enterprise-tier key. |
| `postgres` | Not published to the host. |
| `cabinet-agent` | Built from `Dockerfile.agent`. Forwards `*.log` and `*.txt` files from `./logs`, mounted at `/var/log/host`. |
| `cabinet-cli` | Prints its version and exits. Run commands with `docker compose run --rm cabinet-cli <command>`. |

Optional profiles:

```bash
# Grafana (127.0.0.1:3000) and Prometheus (127.0.0.1:9090)
docker compose --profile monitoring up -d

# LocalStack (127.0.0.1:4566)
docker compose --profile development up -d
```

To change resource limits or add settings, use a `docker-compose.override.yml`:

```yaml
services:
  cabinet-api:
    environment:
      - Storage__RetentionDays=90
      - Network__TrustedProxies__0=172.20.0.10
    deploy:
      resources:
        limits:
          cpus: '4'
          memory: 4G
```

### Images

Containers run as non-root users. The API and CLI images use the base image's `app` user (uid 1654). The agent image creates its own `cabinet` system user. The API image has a `curl`-based `HEALTHCHECK` against `/health/live`.

CI publishes only the API image, to `ghcr.io/bogware/cabinet`. Build the agent and CLI images yourself from `Dockerfile.agent` and `Dockerfile.cli`.

## Packages and Installers

CI builds these artifacts on `v*` tags and attaches them to a GitHub release. The latest published release, v1.0.0, predates the current `main` and this guide. To install the current code, run `./scripts/build-release.sh <version>`, which writes to `dist/`. It builds API, CLI and agent archives for Linux, Windows and macOS, plus `.deb` packages when `dpkg-deb` is available.

Binaries are self-contained and single-file, so no .NET runtime is needed.

### Linux: .deb

```bash
sudo dpkg -i cabinet-api_<version>_amd64.deb     # or _arm64
```

Installs to `/opt/cabinet/`, with data in `/var/lib/cabinet/` and a `cabinet-api` systemd service listening on port 8080. Add a key to `/etc/cabinet/cabinet.env` (see [API Keys](#api-keys)) before starting:

```bash
sudo systemctl start cabinet-api
journalctl -u cabinet-api -f
```

### Linux: installer script

```bash
# From a directory containing the Cabinet.API binary
sudo ./install-server.sh --port 8080 --storage postgresql \
  --conn-string "Host=db;Database=cabinet;Username=cabinet;Password=secret"

# Or run the setup wizard after installing
sudo ./install-server.sh --run-wizard
```

The installer creates a non-root `cabinet` service user, installs to `/opt/cabinet/` with data in `/var/lib/cabinet/`, writes configuration to `/etc/cabinet/`, and installs a systemd unit (`Type=notify`) with `ProtectSystem=strict`, `PrivateTmp` and `NoNewPrivileges`. Secrets go in `/etc/cabinet/cabinet.env`. The name field for a key is `ApiKeys__Keys__N__Name`.

### Windows

Extract `cabinet-win-x64.zip`, put `install-server.ps1` from the same release in that folder, and in an Administrator PowerShell there:

```powershell
.\install-server.ps1 -Gaming -IngestKeys game-dev,game-prod -AllowFrom 100.64.0.0/10
```

It copies the binaries to `C:\Program Files\Cabinet\`, writes settings to `C:\ProgramData\Cabinet\appsettings.Production.json`, and on first install creates an admin key and an ingest key for each name in `-IngestKeys`, listed in `C:\ProgramData\Cabinet\api-keys.txt`. Both files are readable only by administrators. It then installs and starts the `CabinetAPI` service, set to restart after a crash, and opens the port (`-Port`, default 8080) to `-AllowFrom` only. `100.64.0.0/10` is the Tailscale range. `-RetentionDays` (default 90) limits how long events are kept, and `-Gaming` turns on the game log parsers.

Run it again from a newer release to upgrade; settings and keys are kept. To change settings, edit the file in `C:\ProgramData\Cabinet\` and run the installer again. Logs are in `C:\Program Files\Cabinet\api\logs\`.

### macOS

Release archives are provided for `osx-arm64` and `osx-x64`. There is no installer or service integration.

```bash
tar xzf cabinet-osx-arm64.tar.gz
./api/Cabinet.API --urls http://localhost:8080
```

### Setup wizard

The setup wizard is part of the CLI. There is no web-based wizard.

```bash
cabinet setup
```

It supports `--non-interactive`, `--mode`, `--storage`, `--api-key`, `--server-url` and `--skip-sample-data`. For several API instances, choose PostgreSQL; see [Scaling](#scaling).

## From Source

```bash
dotnet publish src/Cabinet.API -c Release -o /opt/cabinet
cd /opt/cabinet && ASPNETCORE_URLS=http://0.0.0.0:8080 ApiKeys__Keys__0__Key=<key> ApiKeys__Keys__0__Tier=Enterprise dotnet Cabinet.API.dll
```

A default Production configuration starts on SQLite. The API runs as a systemd (`Type=notify`) or Windows service when started by a service manager.

## Forwarding Agent

The agent tails files and forwards them to `/services/collector/event`.

```bash
# Linux, from the extracted cabinet-linux-x64.tar.gz
sudo ./install-agent.sh --server http://cabinet:8080 --api-key "$KEY" --paths "/var/log/*.log"

# Several inputs: write the settings yourself, and let the agent read other users' folders
sudo ./install-agent.sh --config agent.json --groups gameuser1,gameuser2

# Or the package
sudo dpkg -i cabinet-agent_<version>_amd64.deb
# Edit the agent's appsettings.json with your server URL and API key
sudo systemctl start cabinet-agent

# Or the installer script
sudo ./install-agent.sh --server https://cabinet.example.com \
  --api-key "your-api-key" --paths "/var/log/*.log,/var/log/syslog"
```

Give each agent a key of the `Ingest` tier, which can send events to any index but not read them, so a compromised host can't search what everyone sends. The installer makes the agent's settings file readable only by root, since it holds the key.

```powershell
# Windows, as Administrator
.\install-agent.ps1 -ServerUrl https://cabinet.example.com -ApiKey "your-api-key" `
  -Paths "C:\inetpub\logs\*.log,C:\app\logs\*.log"
```

The agent image isn't published, so build it to run in Docker:

```bash
docker build -t cabinet-agent -f Dockerfile.agent .
docker run -d \
  -v /var/log:/var/log:ro \
  -e Agent__Output__ServerUrl=https://cabinet.example.com \
  -e Agent__Output__ApiKey=your-key \
  -e Agent__Inputs__0__Paths__0=/var/log/syslog \
  cabinet-agent
```

The key needs the `Ingest` capability (Professional or Enterprise tier).

### Agent behaviour

Delivery is at-least-once, and the server stores each event once.

- **Checkpoints follow delivery.** A file's position in the registry moves only when its events are accepted by the server or written to disk. A crash re-reads anything else.
- **Re-sent events are stored once.** Each event's ID comes from its file and byte offset, so a retry or re-read has the same ID, and the server ignores IDs it already has.
- **Outages.** A batch is retried with exponential backoff up to `MaxRetries`, honoring `Retry-After`, then written to `SpillDirectory` and replayed every `SpoolRetryIntervalSeconds` until accepted. Keep `SpillDirectory` on persistent storage.
- **Full spool.** At `MaxSpoolBytes` the agent keeps retrying and pauses reading, so lines wait in the source files. If those files are rotated away and deleted before the server returns, those lines are lost.
- **Rejected events.** A batch the server refuses with 400, 413 or 422 is split to isolate the cause. An event refused on its own goes to `dead-letter` under `SpillDirectory`, for you to inspect. A 401 or 403 is retried and spooled like an outage, so a bad API key doesn't lose data.
- **Rotation.** Files are identified by their first line, which doesn't change as they grow, plus a check that the last delivered event is still in place. Rename and copy-and-truncate rotation are both handled, and lines written just before a rename are still read. A rotated file that an input's pattern also matches isn't read twice.
- **Partial lines.** A line without its line ending waits until the ending arrives, so a last line that never gets one isn't sent. Lines longer than `MaxLineBytes` (default 1 MiB) are truncated.
- **Multi-line events.** `MultiLine:Pattern`, `Negate`, `MaxLines` and `TimeoutSeconds` configure merging per input. An event stays open across polls until the next one starts or `TimeoutSeconds` passes with no new lines.
- **Paths.** `*`, `?` and `**` work in any path segment. `Recursive` also matches the last segment in subdirectories. New files are found every `ScanIntervalSeconds` (default 10).
- **Old files.** `IgnoreOlderThanHours` on an input skips files last written longer ago than that, for folders with years of history. A file the agent was already reading is always finished, and an ignored file is read in full if it's written to again.
- **Quiet files.** A file read to the end that hasn't changed for `CloseInactiveSeconds` (default 300) is closed, and reopened from its checkpoint when it changes. Programs that start a new file per session, such as game servers, don't leave thousands open.
- **Time zones.** The agent sends its machine's time zone (or `Agent:TimeZone`), and the server uses it for lines whose times have no offset, such as game server logs. Other senders' such times are read as UTC.
- **Shutdown.** The agent delivers what it has read for up to `ShutdownFlushSeconds` (default 20), then spools the rest.

### Agent limits

- A file deleted before it's fully read loses its unread lines.
- If the registry file is lost, files are read again from the start under new event IDs, which creates duplicates.
- A replacement file with the same first line, and the same text at the checkpoint offset, can't be told from the original.

## Kubernetes

The manifests in `k8s/` deploy two API replicas and a PostgreSQL pod. `tests/EndToEnd/k8s-smoke.sh` deploys them to a throwaway kind cluster, stores and reads events, and deletes a pod.

Create the secret, then apply:

```bash
kubectl create namespace cabinet
kubectl -n cabinet create secret generic cabinet-secrets \
  --from-literal=postgres-password="$(openssl rand -hex 24)" \
  --from-literal=api-key-0="$(openssl rand -hex 32)"
kubectl apply -k k8s/
```

Before using it for real:

- Set `newTag` in `kustomization.yaml` to a released version.
- Set the host in `ingress.yaml`, and add TLS through your own issuer.
- Replace `postgres.yaml` with a managed database or an operator. The pod included is a single point of failure with no backups.
- `network-policy.yaml` needs a network plugin that enforces it, and assumes `ingress-nginx` and `monitoring` namespaces.
- The Horizontal Pod Autoscaler needs metrics-server.

The API key in the secret has the Admin tier. Add others with more `ApiKeys__Keys__N__*` variables.

## Configuration Reference

Settings come from `appsettings*.json` or environment variables, with `__` as the section separator (`Storage__Type`).

### Listening address

| Scenario | Address |
|----------|---------|
| Compose | Host port 5000 |
| `.deb` and installer scripts | `0.0.0.0:8080` (`--port` to change) |
| `dotnet run` | `localhost:5068` (Development) |
| Published binary with no settings | `localhost:5000` |

Set `ASPNETCORE_URLS` or `--urls` to override.

### Storage

```json
{
  "Storage": {
    "Type": "PostgreSQL",
    "ConnectionString": "Host=localhost;Port=5432;Database=cabinet;Username=cabinet;Password=secret",
    "MetadataConnectionString": "Data Source=cabinet-metadata.db",
    "RetentionDays": 0
  }
}
```

OpenSearch also reads `Storage:IndexPrefix` (default `cabinet-logs`), `Storage:Username`, `Storage:Password` and `Storage:AllowInsecureSsl`.

There is no fallback backend. A remote backend still unreachable after retries (2, 5 and 10 seconds apart) makes startup fail with a non-zero exit code.

Where non-log data lives:

| Data | SQLite | PostgreSQL | OpenSearch |
|------|--------|------------|------------|
| Dashboards, knowledge objects, saved queries, schedules | Log database | PostgreSQL | Local metadata database |
| Search jobs, data models | Log database | Local metadata database | Local metadata database |
| RBAC principals | Local `cabinet-rbac.db` | Local `cabinet-rbac.db` | Local `cabinet-rbac.db` |
| Field extractions, alert history, retention definitions | Memory | Memory | Memory |

The local metadata database is set by `Storage:MetadataConnectionString` (default `Data Source=cabinet-metadata.db`). It is per instance, so keep it on persistent storage. Compose puts it on the `cabinet-data` volume.

### Gaming extension

Set `Gaming:Enabled=true` (`Gaming__Enabled=true` as an environment variable) to add the L4D2, SourceMod and SteamCMD parsers and the `/api/v1/detection/*` routes. It's off by default. For the CLI, use `CABINET_Gaming__Enabled=true`.

### Retention

`Storage:RetentionDays` deletes older events across every index. 0, the default, keeps everything. `/api/v1/retention/{index}` sets a policy for one index, enforced hourly; changing one needs `IndexManagement` on that index, and is audited.

### API Keys

```bash
ApiKeys__Keys__0__Key=<raw key>        # openssl rand -hex 32
ApiKeys__Keys__0__Tier=Professional    # Ingest, Basic, Professional, Enterprise or Admin
ApiKeys__Keys__0__Name=production-client
ApiKeys__Keys__0__ExpiresAt=2027-01-31T00:00:00Z   # optional
```

The value is the raw key, which the API hashes with SHA-256 in memory. Don't store a hash here. Validation is local on every instance. Keys are read at startup, so restart after changing them. With no keys configured, the API starts and rejects authenticated routes.

`Ingest` keys can only send events, for agents and other senders. A key stops working at `ExpiresAt`, and an unreadable date stops the API from starting rather than leaving the key valid forever. To rotate a key, add the new one beside it, move clients over, then remove or expire the old one.

### Rate Limiting

```json
{
  "RateLimiting": {
    "PreAuth": { "MaxRequestsPerMinute": 300, "WindowSeconds": 60 }
  }
}
```

These two keys set the per-address limit, which counts all requests from an address except `/health*` and `/metrics`. The tier limits (Basic 100/min, Professional 1,000/min, Enterprise 10,000/min) can be changed with `RateLimiting:Tiers:Basic`, `:Professional` and `:Enterprise`. The Admin and Ingest tiers follow Enterprise.

Limits are for the whole deployment. With several instances on PostgreSQL, each instance takes its share, dividing by the number of instances that have sent a heartbeat in the last minute. A load balancer that sends a client mostly to one instance makes it reach its limit early, so balance evenly or raise the limits.

### Reverse Proxies

```bash
Network__TrustedProxies__0=10.0.0.5
```

`X-Forwarded-For` is ignored unless the proxy's IP address is listed. Each entry must be a single IP address, not a range, and an invalid entry fails startup.

### CORS

`Cors:AllowedOrigins` lists allowed origins. If the key is unset, only `http://localhost:3000` is allowed.

### Tracing

Set `Telemetry:OtlpEndpoint` to export OpenTelemetry traces of requests, queries and ingest to a collector:

```json
{ "Telemetry": { "OtlpEndpoint": "http://otel-collector:4317", "ServiceName": "cabinet-api" } }
```

It uses OTLP over gRPC. Set `Telemetry:OtlpProtocol` to `http` for HTTP, with the full `/v1/traces` URL. Standard `OTEL_*` variables also apply. Health checks and metrics aren't traced. Log lines carry the request's correlation ID.

### Logging

The `Serilog` section adjusts logging, for example `Serilog:MinimumLevel:Default`. Logs go to the console and to `logs/cabinet-api-*.log`, keeping 30 files of up to 100 MB.

### Query limits

```json
{
  "Query": {
    "CacheTtlSeconds": 30,
    "Limits": {
      "MaxRowsInMemory": 100000,
      "MaxGroups": 100000,
      "MaxRetainedValues": 20000000,
      "MaxExactPercentileValues": 100000,
      "MaxEventsScanned": 0
    },
    "Concurrency": {
      "MaxConcurrent": 32,
      "MaxConcurrentPerClient": 8,
      "MaxStreams": 64,
      "MaxStreamsPerClient": 4
    }
  }
}
```

These stop one query or client from taking the server down. Over `MaxConcurrent*` or `MaxStreams*`, a request gets 429 with `Retry-After`. Over `MaxGroups` or `MaxRetainedValues`, a query fails with an explanation. Over `MaxRowsInMemory`, or `MaxEventsScanned` where it's above 0, the result is marked `truncated`. Beyond `MaxExactPercentileValues`, a percentile comes from a sample and the result is marked `approximate`. Each query also has a time limit: 60 seconds for `/query/execute`, 5 minutes for streams, and 10 minutes for search jobs.

### Syslog

Off by default. Accepts RFC 3164 and RFC 5424 over UDP, TCP and TLS, with newline or octet-counted framing. Compose doesn't publish the ports, so add port mappings if you enable it there.

```json
{
  "Syslog": {
    "Enabled": true,
    "BindAddress": "0.0.0.0",
    "TcpPort": 1514,
    "UdpPort": 1514,
    "TlsPort": 6514,
    "TlsCertificatePath": "/etc/cabinet/syslog.pem",
    "TlsKeyPath": "/etc/cabinet/syslog.key"
  }
}
```

`TlsPort` is off unless set. `TlsCertificatePath` takes a PEM file or a `.pfx`/`.p12`, with `TlsCertificatePassword`. Clients aren't asked for certificates, and syslog senders aren't authenticated, so restrict the ports at the network. If a port can't be bound or the certificate can't be loaded, the server stops rather than running without the listener.

When storage falls behind or is down, TCP and TLS senders wait and messages wait in a queue of 10,000. UDP can't wait, so datagrams are dropped and a warning gives the count. Use TCP where losing messages matters.

### Detection Rules

Rules run on each event as it's ingested, by any route, and what they find goes to alert history and the alert channels. Manage them on the dashboard's Detections page, or at `/api/v1/rules` with the `Alerts` capability.

`Detection:BuiltinRulesEnabled` turns all the built-in rules on by default. Otherwise turn on those you want with `POST /api/v1/rules/{id}/enable`.

```bash
curl -X POST http://localhost:5000/api/v1/rules -H "X-API-Key: $KEY" -H "Content-Type: application/json" -d '{
  "name": "Failed logins by address",
  "severity": "Error",
  "pattern": "login failed",
  "extract": "from (?<src_ip>\\S+)",
  "groupBy": "src_ip",
  "threshold": 5,
  "windowSeconds": 300,
  "cooldownSeconds": 900
}'
```

`POST /api/v1/rules/test` tries a rule's expressions on a line without saving anything. `POST /api/v1/rules/backtest` runs a rule, saved or not, over stored events (`hours`, default 24) and reports how often it would have fired and for which groups, without alerting. Use it to tune thresholds against real traffic before turning a rule on.

Windows use event time, so a backlog being replayed counts as it happened. If checking falls far behind, ingest slows rather than skipping events. With several instances on PostgreSQL, counts and cooldowns are shared, so a threshold counts events wherever they arrived and a rule fires once.

Rules apply to every index, so creating, changing, turning on or off and deleting one needs `Search` on every index, not just some. Alert history shows each caller only alerts about indexes they can read, and keeps the event ID rather than the line, which stays in the event itself where encryption and masking apply.

### Alert Channels

Console and file channels are always registered. Scheduled searches send alerts through all channels.

```json
{
  "Webhook": { "Url": "https://hooks.example.com/...", "Format": "slack" },
  "Email": {
    "Enabled": true,
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "FromAddress": "cabinet@example.com",
    "ToAddresses": ["ops@example.com"]
  }
}
```

Webhook `Format` is `generic`, `slack`, `discord`, `teams` or `pagerduty`.

### SSO

OIDC JWT validation is configured under `SSO` (`Enabled`, `Provider`, `Oidc:Authority`, `Oidc:ClientId`). A token gets the role and index permissions that `SSO:RoleMappings` assign to its claims, and none when nothing matches and `AutoProvision` is off. Claims in the token named like Cabinet's own (`CabinetRole`, `IndexPermission`, `ClientId`, `Tier`, `Permission`) are dropped, so a user attribute at the identity provider can't grant access. SAML and LDAP are not implemented.

### Not implemented

These have configuration or classes in the code but no effect, so don't rely on them:

- Hot/warm/cold tiering and S3 archival.
- Server-side multi-line merging. Use the agent.

## Security Hardening

See also [SECURITY.md](../SECURITY.md).

### Production Checklist

- [ ] Set `ASPNETCORE_ENVIRONMENT=Production`; dev keys exist only in Development configuration
- [ ] Generate real API keys and give each client the lowest tier it needs
- [ ] Terminate TLS at a reverse proxy and list it in `Network:TrustedProxies`
- [ ] Restrict `/metrics` at the network, since it is unauthenticated
- [ ] Set `Cors:AllowedOrigins`
- [ ] Set `Storage:RetentionDays` if you want events to expire
- [ ] Use disk or database encryption if logs are sensitive, and field encryption and masking for what's most sensitive
- [ ] Ship application logs, which hold audit chain heads, to separate storage
- [ ] Schedule backups, and run a restore drill
- [ ] Mount secrets from a secret store rather than config files

The API limits request bodies to 50 MB (5 MB on HEC routes) and sends CSP, `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy` and `Strict-Transport-Security` headers.

### Audit Log

Security events go to the metadata database as hash-chained records, as well as the log output. Each record's hash covers all its fields and the hash before it, and each process writes its own chain.

- `GET /api/v1/audit` searches events. `principalId`, `action`, `startTime` and `endTime` filter them.
- `GET /api/v1/audit/verify` checks every chain, and reports changed, removed and missing records.

Both need `UserManagement`. Records older than `Audit:RetentionDays` (default 365) are removed hourly, in a way verification recognizes.

This makes tampering evident rather than impossible. Someone with write access to the database could rewrite an entire chain, so every 1,000 records the current head goes to the log output too. Keep those logs somewhere else if that matters to you.

Set `Audit:HmacKey` (or `CABINET_AUDIT_HMAC_KEY`), at least 32 characters, to make the hashes HMACs. Rewriting a chain then also needs the key, so keep it out of the database's reach. Chains record which key signed them, and verification reports a chain signed with a different key rather than calling it changed.

### Encryption

Set `Encryption:Enabled` to encrypt chosen fields with AES-256-GCM before they reach storage:

```json
{
  "Encryption": {
    "Enabled": true,
    "KeyProvider": "Environment",
    "EncryptedFields": ["card", "ssn", "secret_*"],
    "EncryptRawLine": false
  }
}
```

A field's value is also removed from the raw line, so it isn't stored there in plain text. With `EncryptRawLine`, the whole line is encrypted. Queries see decrypted events, so `where` works on these fields, but `find` can't find them in storage, and with `EncryptRawLine` it can't find anything.

`KeyProvider` is `Local`, which keeps a key file at `KeyFilePath` and suits one instance, or `Environment`, which reads a base64 key from `CABINET_ENCRYPTION_KEY`. Several instances must share a key. **Back the key up separately: without it the data can't be read.** Events stored before enabling it stay as they were.

To rotate the key, make the new key current and list the old ones in `Encryption:PreviousKeyFilePaths`, or as comma-separated base64 in `CABINET_ENCRYPTION_PREVIOUS_KEYS`. New values are written with the current key, and each value records which key wrote it (`enc:v2:<key id>:...`), so values under older keys still read. Keep an old key until retention has removed everything written with it.

### Field masking

`Masking:Rules` masks values by the reader's role, in every read:

```json
{
  "Masking": {
    "Rules": [
      { "FieldPattern": "card", "Strategy": "Partial", "ExemptRoles": ["SuperAdmin"] },
      { "FieldPattern": "email", "Strategy": "Hash" },
      { "FieldPattern": "*", "Strategy": "Pattern", "ValuePattern": "\\b\\d{3}-\\d{2}-\\d{4}\\b", "Replacement": "[ssn]" }
    ]
  }
}
```

A masked field's value is masked in the raw line as well, and a `ValuePattern` also applies to the line directly, which catches values no parser extracted. The stored event doesn't change. Searching for a masked value still matches, so someone can tell that a value exists, without seeing it. For data nobody should read, use encryption or don't ingest it. Scheduled searches run as the lowest role, so alerts carry masked values.

### TLS

The API container serves plain HTTP. Terminate TLS at a reverse proxy, a load balancer, or cert-manager on Kubernetes.

## Monitoring & Observability

### Health Endpoints

| Endpoint | Purpose | Auth Required | Rate Limited |
|----------|---------|---------------|--------------|
| `GET /health` | Overall health, plain text | No | No |
| `GET /health/ready` | Readiness (storage reachable) | No | No |
| `GET /health/live` | Liveness | No | No |
| `GET /metrics` | Prometheus metrics | No | No |
| `GET /api/health`, `/api/health/ready`, `/api/health/live` | JSON equivalents | No | Yes |
| `GET /api/health/version` | Version info | No | Yes |
| `GET /api/health/metrics` | Memory, GC, request stats | Yes | Yes |
| `GET /api/health/status` | Detailed service health | Yes | Yes |

### Logging

Cabinet logs through Serilog, configured in code: text to the console and to `logs/cabinet-api-.log`, rolling daily, with 30 files kept and 100 MB per file. These settings aren't read from `appsettings.json`.

### Metrics

`/metrics` exposes:

- `cabinet_http_requests_total`, `cabinet_http_request_duration_seconds`
- `cabinet_events_total`, `cabinet_storage_size_bytes`, `cabinet_storage_errors_total`
- `cabinet_query_cache_entries`, `cabinet_query_cache_hits_total`, `cabinet_query_cache_misses_total`

The `monitoring` Compose profile provides a Prometheus scrape config and a Grafana dashboard. No Prometheus alert rules are included, so define your own.

## Backup & Recovery

### SQLite

The built-in service takes a consistent copy while the server runs, checks it, and keeps a checksum:

```bash
# Take a backup now, and check one later
curl -X POST -H "X-API-Key: $KEY" http://localhost:5000/api/v1/backups
curl -X POST -H "X-API-Key: $KEY" http://localhost:5000/api/v1/backups/<id>/verify
```

Or set `Backup:AutoBackupEnabled`, with `Backup:AutoBackupIntervalHours`, `Backup:RetentionDays` and `Backup:ArchivePath`. `Backup:S3Bucket` also uploads each backup, using the default AWS credentials.

To restore, stop the server, then:

```bash
dotnet Cabinet.API.dll --restore /backups/backup-20260115-020000-ab12cd34.db.gz --force
```

It uses the configured `Storage:ConnectionString`, checks the backup before touching anything, and without `--force` won't replace a database. This restores events and all metadata, since they share the file. Copy `Encryption:KeyFilePath` separately if you use it.

### PostgreSQL

```bash
pg_dump -h db -U cabinet -d cabinet -Fc -f /backups/cabinet-$(date +%Y%m%d).dump

# With every API instance stopped, on an empty database:
pg_restore -h db -U cabinet -d cabinet --exit-on-error /backups/cabinet-20260115.dump
```

The database holds events and all shared state, including the audit log.

`tests/EndToEnd/backup-drill.sh` runs this whole sequence in CI: it stores events, dumps, drops the database, restores, and checks that events, a retention policy and the audit log all came back. Copy it for your own drills. A backup you haven't restored isn't a backup.

### OpenSearch

Use snapshots, into a repository registered with `path.repo` on the nodes:

```bash
curl -X PUT "http://opensearch:9200/_snapshot/cabinet-backups" -H 'Content-Type: application/json' -d '{
  "type": "fs",
  "settings": { "location": "/backups/opensearch" }
}'
curl -X PUT "http://opensearch:9200/_snapshot/cabinet-backups/snapshot-$(date +%Y%m%d)"
```

This path isn't drill-tested. Metadata isn't in OpenSearch, so back that up too: the SQLite file at `Storage:MetadataConnectionString`, or the database at `Storage:MetadataPostgresConnectionString`.

## Scaling

Several API instances can run against one PostgreSQL database, behind a load balancer:

```bash
Storage__Type=PostgreSQL
Storage__ConnectionString="Host=db;Database=cabinet;Username=cabinet;Password=..."
```

With OpenSearch as the event store, shared state needs a database of its own. Set `Storage__MetadataPostgresConnectionString`, or each instance keeps its own metadata in SQLite.

All instances serve every request and see the same dashboards, saved queries, roles, search jobs and alert history. One at a time holds a PostgreSQL advisory lock and runs scheduled searches, retention, search jobs and data model builds. If it stops, another takes over within about 5 seconds, and search jobs it was running start again.

What stays per instance:

- Rate limits, so a client's effective limit grows with the number of instances.
- The query cache, so instances can differ for up to `Query:CacheTtlSeconds`.

SQLite and in-memory storage serve a single instance. See [BENCHMARKS.md](BENCHMARKS.md) for measured figures.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Compose refuses to start with `Set CABINET_...` | An `.env` value is empty | Set every value in `.env` |
| `Connection refused` | API not started or wrong port | Check `docker compose ps`. Compose publishes port 5000. |
| API exits at startup with `Storage backend ... is unreachable` | Remote backend down after retries | Fix the backend or connection string. There is no fallback. |
| `401 Unauthorized` | Invalid or missing API key | Check `ApiKeys__Keys__N__Key` and restart |
| `403` on `DELETE /api/v1/logs`, roles or index routes | No tier has these capabilities | Not available today |
| `403` on export or scheduled searches | Requires Enterprise tier | Use an Enterprise key |
| `429 Too Many Requests` | Rate limit exceeded | Back off. Raise `RateLimiting:PreAuth:MaxRequestsPerMinute` if the per-address limit is the cause. |
| All clients share one rate-limit bucket behind a proxy | Proxy not trusted | Add it to `Network:TrustedProxies` |
| Slow queries | Large result set | Narrow the time range or add `\| limit 1000` |
| High memory usage | Large query results in memory | Narrow queries |
| Agent forwards nothing with a `**` path | `**` isn't supported in directories | Use `*.log` with `Recursive: true` |

### Debug Mode

```bash
# Development environment: enables Swagger UI and the dev keys. Not for production.
ASPNETCORE_ENVIRONMENT=Development

# Check service health
curl -H "X-API-Key: <key>" http://localhost:5000/api/health/status
```

## Runbooks

### Runbook: Upgrade (Compose)

```
1. Back up (see Backup & Recovery).

2. Rebuild and restart:
   git pull
   docker compose up -d --build

3. Verify health:
   curl http://localhost:5000/health

4. If there are issues, check out the previous revision and repeat step 2.
   Restore from backup if the data changed.
```

### Runbook: Disaster Recovery

```
1. Assess damage:
   docker compose ps
   docker compose logs cabinet-api
   curl -H "X-API-Key: <key>" http://localhost:5000/api/health/status

2. If storage is corrupted:
   a. Stop the API: docker compose stop cabinet-api
   b. Restore from backup (see Backup & Recovery)
   c. Restart: docker compose start cabinet-api

3. If the database must move:
   a. Identify the last good backup
   b. Restore to a new database
   c. Update Storage__ConnectionString
   d. Restart the API

4. Post-incident:
   - Review logs for root cause
   - Update backup frequency if needed
```

### Runbook: High Load

```
1. Check current load:
   curl -H "X-API-Key: <key>" http://localhost:5000/api/health/metrics

2. If CPU is sustained high, give the instance more CPU, or on PostgreSQL add instances.

3. If storage is slow, check backend health (PostgreSQL connections, OpenSearch cluster).

4. If memory is high:
   a. Check for unbounded queries and add limits
   b. Increase the memory limit
```

### Runbook: API Key Rotation

```
1. Generate a new key:
   openssl rand -hex 32

2. Add it alongside the old one under the next index:
   ApiKeys__Keys__1__Key=<new key>
   ApiKeys__Keys__1__Tier=<tier>
   ApiKeys__Keys__1__Name=<client>

3. Restart the API to pick up the change.

4. Distribute the new key to clients.

5. After all clients have moved, remove the old key and restart again.
```
