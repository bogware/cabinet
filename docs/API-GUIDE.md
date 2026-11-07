# Cabinet API Integration Guide

This guide covers integrating with Cabinet's REST API, including authentication, endpoint reference, request/response formats, and common workflows. `api-spec.json` at the repository root is the OpenAPI spec generated from the API, and it lists every operation.

## Base URL

| Environment | URL |
|-------------|-----|
| Docker Compose | `http://localhost:5000/api/v1` |
| `dotnet run` (Development) | `http://localhost:5068/api/v1` |
| Packaged install | `http://<host>:8080/api/v1` |

Examples below use port 5000. Route matching is case-insensitive. Swagger UI is available only in the Development environment.

## Authentication

Endpoints require an API key in the `X-API-Key` header, except the anonymous health routes listed under [Health Endpoints](#health-endpoints). Keys aren't accepted in the query string. HEC routes also accept `Authorization: Splunk <key>`.

```bash
curl -H "X-API-Key: your-api-key" http://localhost:5000/api/v1/logs/stats
```

Keys are validated locally on each instance by looking up their SHA-256 hash in a dictionary.

### API Key Tiers

Each tier maps to a fixed role. Rate limits are hardcoded per key.

| Tier | Role | Rate limit | Capabilities |
|------|------|-----------|--------------|
| Basic | Viewer | 100 req/min | Search, SystemHealth |
| Professional | Analyst | 1,000 req/min | Basic, plus Ingest, SavedQueries, Detection |
| Enterprise | Admin | 10,000 req/min | Professional, plus Export, Alerts |

Creating, updating or deleting dashboards, knowledge objects, saved queries, field extractions, retention definitions and scheduled searches also requires write permission, which Professional and Enterprise keys have.

No tier has `IndexManagement` or `UserManagement`. These routes therefore return 403 for every configured key:

- `DELETE /api/v1/logs`
- `DELETE /api/v1/indexes/{name}`
- `POST` and `DELETE` on `/api/v1/datamodels`
- All of `/api/v1/roles`. `PUT /api/v1/roles/{apiKeyHash}` is also not implemented and would return 501.

Capability checks always evaluate index `main`; per-index permissions are not implemented. OIDC bearer tokens are validated, but capability checks have no principal for an OIDC subject, so routes that require a capability return 403 for them.

### Development Keys

Available only when `ASPNETCORE_ENVIRONMENT=Development`:

| Key | Tier |
|-----|------|
| `dev-basic-key` | Basic |
| `dev-professional-key` | Professional |
| `dev-enterprise-key` | Enterprise |

## Response Formats

### Success Responses

```json
// Single resource
{
  "id": "01HXYZ...",
  "title": "Error Dashboard",
  "panels": [...]
}

// Collection
[
  { "id": "01HXYZ...", "name": "Query 1" },
  { "id": "01HABC...", "name": "Query 2" }
]

// Query results
{
  "query": "find errors | stats count by host",
  "results": [...],
  "count": 5,
  "executionTimeMs": 42,
  "timestamp": "2026-01-15T10:30:00Z"
}
```

List endpoints accept `offset` and `count` (1-1000, default 100) and return the total in an `X-Total-Count` header.

### Error Responses

Validation and server errors use RFC 7807 Problem Details:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Bad Request",
  "status": 400,
  "detail": "Query too long. Maximum length is 10,000 characters.",
  "traceId": "00-..."
}
```

Three responses use other shapes:

- 403 from a capability check: `{"error": "Forbidden", "message": "Missing required capability: Export"}`
- 429: see [Rate Limiting](#rate-limiting)
- HEC routes: `{"text": "...", "code": 5}`

### HTTP Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 204 | Deleted |
| 400 | Invalid request (validation error) |
| 401 | Missing or invalid API key |
| 403 | Insufficient permissions |
| 404 | Resource not found |
| 408 | Query or export timed out |
| 429 | Rate limit exceeded |
| 500 | Internal server error |
| 501 | Not implemented |

## Endpoints

### Log Ingestion

#### POST /api/v1/logs/ingest: Ingest log lines

Requires `Ingest`. The body is an array of raw lines, up to 10,000 per request. Each line is parsed by the first registered parser that accepts it (L4D2, SourceMod, SteamCMD, JSON, CSV, CEF, then Generic). Apache and Nginx parsers aren't registered in the API, so those lines go through the Generic parser. Events are stored in index `main`.

```bash
curl -X POST http://localhost:5000/api/v1/logs/ingest \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "Logs": [
      "2026-01-15 10:30:00 ERROR Connection timeout from 192.168.1.1",
      "{\"level\":\"info\",\"message\":\"Connection restored\"}"
    ]
  }'
```

**Response:**
```json
{
  "totalReceived": 2,
  "totalIngested": 2,
  "errors": []
}
```

To set host, source type or index per event, use the [HEC routes](#splunk-hec-compatibility).

#### GET /api/v1/logs: Query logs (simple)

```bash
# Basic query with pagination
curl -H "X-API-Key: dev-basic-key" \
  "http://localhost:5000/api/v1/logs?search=error&offset=0&count=50"

# With filters
curl -H "X-API-Key: dev-basic-key" \
  "http://localhost:5000/api/v1/logs?search=error&severity=Error&source=generic&startTime=2026-01-14T00:00:00Z&endTime=2026-01-15T00:00:00Z"
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `search` | string | Full-text search term |
| `severity` | string | Minimum severity: `Trace`, `Debug`, `Info`, `Warning`, `Error`, `Critical`, `Fatal` |
| `source` | string | Filter by source type |
| `startTime` | datetime | Start of time range (ISO 8601) |
| `endTime` | datetime | End of time range (ISO 8601) |
| `sortBy` | string | Sort field (default: `timestamp`) |
| `descending` | bool | Sort direction (default: `true`) |
| `offset` | int | Results to skip (default: 0) |
| `count` | int | Results per page (1-1000, default: 100) |

#### GET /api/v1/logs/{id}: Get single event

```bash
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:5000/api/v1/logs/01HXYZ123456789ABCDEFGHIJK
```

#### GET /api/v1/logs/stats: Storage statistics

```bash
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:5000/api/v1/logs/stats
```

Accepts optional `startTime` and `endTime`, which adjust `totalEvents`.

**Response:**
```json
{
  "totalEvents": 150000,
  "storageSizeBytes": 52428800,
  "oldestEvent": "2025-12-15T00:00:00Z",
  "newestEvent": "2026-01-15T10:30:00Z",
  "eventsBySourceType": { "generic": 100000, "syslog": 50000 },
  "eventsBySeverity": { "Info": 120000, "Error": 30000 },
  "eventsByIndex": { "main": 150000 }
}
```

#### GET /api/v1/logs/stream: Recent events

Returns up to 1,000 matching events as a JSON array, then ends. Accepts `since`, `source` and `severity`.

#### GET /api/v1/logs/stream/live: Server-Sent Events

Keeps the connection open and pushes new events by polling storage. It sends a heartbeat comment every 15 seconds and closes after 60 minutes.

```bash
curl -N -H "X-API-Key: dev-basic-key" \
  "http://localhost:5000/api/v1/logs/stream/live?index=main&filter=error&pollIntervalMs=1000"
```

---

### Query Execution

Query strings are limited to 10,000 characters. BQL syntax is described in [BQL-GUIDE.md](BQL-GUIDE.md); the language is being brought in line with that guide, so some examples may not yet behave as documented.

#### POST /api/v1/query/execute: Execute BQL query

Results are capped at 50,000 rows, and queries time out after 60 seconds. Set `useCache` to `true` to accept an in-process cached result (default TTL 30 seconds). The `X-Cache` response header reports `HIT` or `MISS`.

```bash
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "find * | where severity >= 4 | stats count by host | sort by count desc | limit 10",
    "useCache": false
  }'
```

**Response:**
```json
{
  "query": "find * | where severity >= 4 | stats count by host | sort by count desc | limit 10",
  "results": [
    { "host": "web-01", "count": 523 },
    { "host": "web-02", "count": 341 },
    { "host": "db-01", "count": 127 }
  ],
  "count": 3,
  "executionTimeMs": 87,
  "timestamp": "2026-01-15T10:35:00Z"
}
```

#### POST /api/v1/query/stream: Stream results

Same request body. Responds with `application/x-ndjson`, up to 100,000 rows.

#### POST /api/v1/query/validate: Validate BQL syntax

```bash
curl -X POST http://localhost:5000/api/v1/query/validate \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find * | where severity >= 4 | stats count by host" }'
```

**Response:**
```json
{
  "isValid": true,
  "query": "find * | where severity >= 4 | stats count by host",
  "error": null,
  "operatorCount": 3,
  "operators": ["..."]
}
```

#### POST /api/v1/query/explain: Get query execution plan

Returns the stages without running the query.

```bash
curl -X POST http://localhost:5000/api/v1/query/explain \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find errors | where severity >= 4 | stats count by host" }'
```

#### POST /api/v1/query/translate: Translate natural language to BQL

Uses a rule-based translator.

```bash
curl -X POST http://localhost:5000/api/v1/query/translate \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "text": "show me errors from the last 24 hours" }'
```

**Response fields:** `originalText`, `bqlQuery`, `success`, `confidence`, `isNaturalLanguage`.

#### POST /api/v1/query/export: Export query results

Requires `Export` (Enterprise tier). `format` is `csv` or `json` (default). `maxRows` is clamped to 1-100,000. Exports time out after 120 seconds.

```bash
curl -X POST http://localhost:5000/api/v1/query/export \
  -H "X-API-Key: dev-enterprise-key" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "find * | where severity >= 4 from last 7d",
    "format": "csv",
    "maxRows": 10000
  }'
```

#### POST /api/v1/query/fieldsummary: Field statistics

```bash
curl -X POST http://localhost:5000/api/v1/query/fieldsummary \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find * from last 1h" }'
```

#### GET /api/v1/query/suggestions: Query suggestions

Returns suggested query text for autocompletion.

#### Saved Queries

Requires `SavedQueries` (Professional or Enterprise). Run a saved query by passing its `query` text to `/api/v1/query/execute`; there is no separate execute route.

```bash
# Save a query
curl -X POST http://localhost:5000/api/v1/query/saved \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Top Error Hosts",
    "query": "find * | where severity >= 4 | stats count by host | sort by count desc | limit 10",
    "description": "Shows hosts with most errors"
  }'

# List saved queries
curl -H "X-API-Key: dev-professional-key" \
  http://localhost:5000/api/v1/query/saved
```

`GET`, `PUT` and `DELETE` are available on `/api/v1/query/saved/{id}`.

#### Search Jobs

Run a query in the background. Jobs are stored on the local instance.

```bash
# Submit (maxResults defaults to 50,000, ttlMinutes to 30)
curl -X POST http://localhost:5000/api/v1/query/jobs \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find * | stats count by host", "maxResults": 50000, "ttlMinutes": 30 }'

# Status and results
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/query/jobs/{id}
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/query/jobs/{id}/results
```

`GET /api/v1/query/jobs` lists jobs, `POST /api/v1/query/jobs/{id}/cancel` cancels one, and `DELETE /api/v1/query/jobs/{id}` removes one.

---

### Dashboards

#### CRUD Operations

Panel `type` is one of `Table`, `LineChart`, `BarChart`, `PieChart`, `AreaChart`, `SingleValue`, `Gauge`, `Map`, `Heatmap`, `Markdown`. Panel `width` and `height` are 1-12.

```bash
# Create dashboard
curl -X POST http://localhost:5000/api/v1/dashboards \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "title": "System Overview",
    "description": "System health monitoring",
    "isShared": true,
    "refreshIntervalSeconds": 30,
    "defaultTimeRange": "-24h",
    "panels": [
      {
        "title": "Error Count by Host",
        "query": "find * | where severity >= 4 | stats count by host",
        "type": "BarChart",
        "row": 0, "column": 0, "width": 6, "height": 4
      },
      {
        "title": "Events Over Time",
        "query": "find * | timechart span=1h count",
        "type": "LineChart",
        "row": 0, "column": 6, "width": 6, "height": 4
      }
    ]
  }'

# List dashboards
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/dashboards

# Get dashboard by ID
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/dashboards/{id}

# Update dashboard
curl -X PUT http://localhost:5000/api/v1/dashboards/{id} \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{ "title": "Updated Title" }'

# Delete dashboard
curl -X DELETE http://localhost:5000/api/v1/dashboards/{id} \
  -H "X-API-Key: dev-professional-key"
```

---

### Knowledge Objects

Each type below has `GET`, `PUT` and `DELETE` on `/{id}`. `GET /api/v1/knowledge/export` and `POST /api/v1/knowledge/import` move them in bulk.

#### Lookup Tables

```bash
# Create lookup. Each row is an object keyed by column name.
curl -X POST http://localhost:5000/api/v1/knowledge/lookups \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "users",
    "description": "User directory",
    "columns": ["user_id", "email", "name", "department"],
    "rows": [
      {"user_id": "U001", "email": "alice@example.com", "name": "Alice Smith", "department": "Engineering"},
      {"user_id": "U002", "email": "bob@example.com", "name": "Bob Jones", "department": "Marketing"}
    ]
  }'

# List lookups
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/knowledge/lookups

# Use in BQL
# find * | lookup users user_id OUTPUT email, name
```

#### Query Macros

```bash
# Create macro
curl -X POST http://localhost:5000/api/v1/knowledge/macros \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "severe_errors",
    "definition": "where severity >= 4",
    "description": "Filter for severe errors only"
  }'

# List macros
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/knowledge/macros
```

#### Field Aliases

```bash
# Create field alias
curl -X POST http://localhost:5000/api/v1/knowledge/aliases \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "sourceType": "syslog",
    "originalField": "hostname",
    "aliasField": "host"
  }'

# List field aliases
curl -H "X-API-Key: dev-basic-key" http://localhost:5000/api/v1/knowledge/aliases
```

---

### Scheduled Searches

Requires `Alerts` (Enterprise tier). Searches are checked every 30 seconds and send alerts through the configured channels when their condition is met. With more than one API instance, each search fires once per instance.

```bash
curl -X POST http://localhost:5000/api/v1/scheduled-searches \
  -H "X-API-Key: dev-enterprise-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Error spike",
    "query": "find * | where severity >= 4",
    "cronExpression": "*/5 * * * *",
    "enabled": true,
    "alertAction": { "threshold": 100, "severity": 1, "cooldownMinutes": 30 }
  }'
```

`GET`, `PUT` and `DELETE` are available on `/api/v1/scheduled-searches/{id}`, along with `POST .../{id}/enable` and `POST .../{id}/disable`.

### Alerts

`GET /api/v1/alerts`, `GET /api/v1/alerts/{id}` and `POST /api/v1/alerts/{id}/acknowledge` read an in-memory store that nothing writes to, so the list is empty. Scheduled searches deliver alerts through channels but don't record them here.

### Retention

`/api/v1/retention` (`GET`, and `GET`, `PUT`, `DELETE` on `/{index}`) stores per-index definitions in memory only and doesn't enforce them. Retention that actually deletes events is the global `Storage:RetentionDays` setting; see [DEPLOYMENT.md](DEPLOYMENT.md#retention).

### Field Extractions

`/api/v1/field-extractions` provides CRUD for definitions held in memory. They are lost on restart and aren't applied to queries.

### Indexes

`GET /api/v1/indexes` and `GET /api/v1/indexes/{name}` list indexes and their details. `DELETE /api/v1/indexes/{name}` requires `IndexManagement` and returns 403 for every configured key.

### Data Models

`GET /api/v1/datamodels`, `GET /api/v1/datamodels/{id}` and `POST /api/v1/datamodels/{id}/tstats` require `Search`. Creating and deleting models requires `IndexManagement` and returns 403 for every configured key.

---

### Detection

Requires `Detection` (Professional or Enterprise). These are game-moderation suites that run on demand over the events in the request body, up to 10,000 per request. Nothing runs at ingest.

```bash
curl -X POST http://localhost:5000/api/v1/detection/hackwatch \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{ "events": [ { "rawLine": "..." } ] }'
```

**Routes:** `POST /api/v1/detection/scumscan`, `/hackwatch`, `/adminabuse`, `/analyze` (runs all three), and `GET /api/v1/detection/stats`.

---

### Splunk HEC Compatibility

Cabinet implements the HEC routes below. Authenticate with `Authorization: Splunk <key>` or `X-API-Key`. The `Authorization: Splunk` form is accepted on collector routes only. Requires `Ingest`, and bodies are limited to 5 MB.

#### POST /services/collector/event: Send JSON events

`Content-Type: application/json` is required. Send one JSON object per line to batch events.

```bash
curl -X POST http://localhost:5000/services/collector/event \
  -H "Authorization: Splunk dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "event": "User login failed from 192.168.1.100",
    "time": 1768473000,
    "host": "auth-server",
    "sourcetype": "auth_log",
    "index": "main"
  }'
```

**Response:**
```json
{ "text": "Success", "code": 0 }
```

#### POST /services/collector/raw: Send raw text

Each line becomes one event. Metadata goes in the `host`, `sourcetype`, `index` and `source` query parameters.

```bash
curl -X POST "http://localhost:5000/services/collector/raw?host=web-01&sourcetype=app" \
  -H "Authorization: Splunk dev-professional-key" \
  -H "Content-Type: text/plain" \
  -d '2026-01-15 10:30:00 ERROR Connection timeout
2026-01-15 10:30:01 WARN Retry attempt 1
2026-01-15 10:30:02 INFO Connection restored'
```

#### GET /services/collector/health: HEC health check

Doesn't require authentication.

```bash
curl http://localhost:5000/services/collector/health
```

**Response:**
```json
{ "text": "HEC is healthy", "code": 17 }
```

---

### Health Endpoints

Probes, exempt from rate limits. These return plain text (`Healthy` or `Unhealthy`):

```bash
# Overall health
curl http://localhost:5000/health

# Readiness (storage reachable?)
curl http://localhost:5000/health/ready

# Liveness (healthy if the process is running)
curl http://localhost:5000/health/live
```

JSON equivalents under `/api/health`. These are subject to rate limits:

```bash
curl http://localhost:5000/api/health
curl http://localhost:5000/api/health/ready
curl http://localhost:5000/api/health/live

# Version info
curl http://localhost:5000/api/health/version

# Process metrics (requires auth)
curl -H "X-API-Key: dev-enterprise-key" http://localhost:5000/api/health/metrics

# Service status (requires auth)
curl -H "X-API-Key: dev-enterprise-key" http://localhost:5000/api/health/status
```

`GET /metrics` serves Prometheus metrics without authentication. Restrict it at the network.

---

## Common Workflows

### 1. Ingest → Query → Dashboard

```bash
# 1. Ingest logs. logs.json holds {"Logs": ["line", ...]}
curl -X POST http://localhost:5000/api/v1/logs/ingest \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d @logs.json

# 2. Query for errors
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"query": "find * | where severity >= 4 from last 1h | stats count by host"}'

# 3. Create dashboard for monitoring
curl -X POST http://localhost:5000/api/v1/dashboards \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d @dashboard.json
```

### 2. Sending from Splunk HEC clients

```bash
# Point HEC clients at Cabinet's collector routes:
# old: https://splunk:8088/services/collector/event
# new: https://cabinet.example.com/services/collector/event

# BQL accepts SPL-style stages:
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"query": "search error | stats count by host | sort -count | head 10"}'
```

Only the HEC routes above are implemented, not the rest of the Splunk API.

### 3. Enrichment with Lookups

```bash
# 1. Create lookup table
curl -X POST http://localhost:5000/api/v1/knowledge/lookups \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"name":"servers","columns":["host","team","tier"],"rows":[{"host":"web-01","team":"platform","tier":"1"},{"host":"db-01","team":"data","tier":"1"}]}'

# 2. Use in queries
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"query":"find * | lookup servers host OUTPUT team, tier | stats count by team"}'
```

## Client Examples

No client libraries are published. These examples call the API directly.

### .NET

```csharp
using var client = new HttpClient();
client.BaseAddress = new Uri("http://localhost:5000");
client.DefaultRequestHeaders.Add("X-API-Key", "your-key");

var response = await client.PostAsJsonAsync("/api/v1/query/execute", new {
    query = "find * | stats count by host"
});

var result = await response.Content.ReadFromJsonAsync<QueryResult>();
```

### Python

```python
import requests

headers = {"X-API-Key": "your-key", "Content-Type": "application/json"}

# Query
response = requests.post(
    "http://localhost:5000/api/v1/query/execute",
    headers=headers,
    json={"query": "find * | stats count by host"}
)
results = response.json()["results"]
```

### JavaScript / TypeScript

```typescript
const response = await fetch("http://localhost:5000/api/v1/query/execute", {
  method: "POST",
  headers: {
    "X-API-Key": "your-key",
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    query: "find * | stats count by host",
  }),
});

const { results, count, executionTimeMs } = await response.json();
```

## Rate Limiting

Requests pass through two limiters, both held in process memory.

1. **Per address.** Applies to all requests from an address, authenticated or not, except `/health*` and `/metrics`. Defaults to 300 requests per 60 seconds, set by `RateLimiting:PreAuth:MaxRequestsPerMinute` and `RateLimiting:PreAuth:WindowSeconds`.
2. **Per key or anonymous address.** Basic 100/min, Professional 1,000/min and Enterprise 10,000/min, hardcoded. Over-limit requests from a key are briefly queued (10, 50 and 100 requests respectively) before being rejected. Anonymous requests are limited to 100/min per address and rejected, not queued.

Because the per-address limit applies first, a high-tier client that sends everything from one address hits it first unless you raise it.

The other `RateLimiting:*` keys and `IpRateLimiting:*` are not read, even though some appear in the shipped configuration files.

`X-Forwarded-For` is ignored unless the sender is listed in `Network:TrustedProxies`.

The per-address limiter returns Problem Details with a `Retry-After` header:

```json
{
  "type": "https://httpstatuses.com/429",
  "title": "Too Many Requests",
  "status": 429,
  "detail": "Rate limit exceeded. Please retry later.",
  "retryAfter": 42
}
```

The per-key limiter returns:

```json
{
  "error": "Rate limit exceeded",
  "message": "Too many requests. Please retry later.",
  "retryAfter": 60
}
```

**Best practices:**
- Implement exponential backoff on 429 responses
- Batch log ingestion (up to 10,000 entries per request)
- Use `/api/v1/logs/stream/live` for live monitoring instead of polling
