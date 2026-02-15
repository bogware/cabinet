# Cabinet API Integration Guide

This guide covers integrating with Cabinet's REST API, including authentication, endpoint reference, request/response formats, and common workflows.

## Base URL

```
http://localhost:8080/api/v1
```

Production: `https://your-cabinet-host/api/v1`

## Authentication

All API endpoints (except health checks) require authentication via the `X-API-Key` header:

```bash
curl -H "X-API-Key: your-api-key" http://localhost:8080/api/v1/logs/stats
```

### API Key Tiers

| Tier | Rate Limit | Capabilities |
|------|-----------|--------------|
| Basic | 100 req/min | Search, basic query |
| Professional | 1,000 req/min | Search, query, ingest, export |
| Enterprise | 10,000 req/min | All capabilities |

### Development Keys

For local development (when `ASPNETCORE_ENVIRONMENT=Development`):

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
  "timestamp": "2024-12-15T10:30:00Z"
}
```

### Error Responses (RFC 7807 Problem Details)

```json
{
  "status": 400,
  "title": "Bad Request",
  "detail": "Query exceeds maximum length of 10000 characters",
  "instance": "/api/v1/query/execute",
  "extensions": {
    "requestId": "abc-123"
  }
}
```

### HTTP Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 400 | Invalid request (validation error) |
| 401 | Missing or invalid API key |
| 403 | Insufficient permissions (RBAC) |
| 404 | Resource not found |
| 429 | Rate limit exceeded |
| 500 | Internal server error |

## Endpoints

### Log Ingestion

#### POST /api/v1/logs — Ingest log events

```bash
curl -X POST http://localhost:8080/api/v1/logs \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '[
    {
      "rawLine": "2024-12-15 10:30:00 ERROR Connection timeout from 192.168.1.1",
      "sourceType": "application",
      "host": "web-01",
      "severity": 4,
      "timestamp": "2024-12-15T10:30:00Z",
      "index": "main",
      "tags": ["error", "network"],
      "fields": {
        "ip": "192.168.1.1",
        "duration_ms": 5000
      }
    }
  ]'
```

**Response:**
```json
{
  "totalReceived": 1,
  "totalIngested": 1,
  "errors": []
}
```

**Limits:** Max 10,000 events per request.

#### GET /api/v1/logs — Query logs (simple)

```bash
# Basic query with pagination
curl -H "X-API-Key: dev-basic-key" \
  "http://localhost:8080/api/v1/logs?search=error&page=1&pageSize=50"

# With filters
curl -H "X-API-Key: dev-basic-key" \
  "http://localhost:8080/api/v1/logs?search=error&severity=4&sourceType=application&startTime=2024-12-14T00:00:00Z&endTime=2024-12-15T00:00:00Z"
```

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `search` | string | Full-text search term |
| `severity` | int | Minimum severity (0-6) |
| `sourceType` | string | Filter by source type |
| `host` | string | Filter by host |
| `startTime` | datetime | Start of time range (ISO 8601) |
| `endTime` | datetime | End of time range (ISO 8601) |
| `page` | int | Page number (default: 1) |
| `pageSize` | int | Results per page (1-1000, default: 50) |

#### GET /api/v1/logs/{id} — Get single event

```bash
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:8080/api/v1/logs/01HXYZ123456789ABCDEFG
```

#### GET /api/v1/logs/stats — Storage statistics

```bash
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:8080/api/v1/logs/stats
```

**Response:**
```json
{
  "totalEvents": 150000,
  "oldestEvent": "2024-11-15T00:00:00Z",
  "newestEvent": "2024-12-15T10:30:00Z",
  "estimatedSizeBytes": 52428800,
  "eventsBySourceType": {
    "application": 80000,
    "syslog": 50000,
    "generic": 20000
  }
}
```

#### GET /api/v1/logs/stream — Server-Sent Events stream

```bash
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:8080/api/v1/logs/stream
```

Returns SSE (Server-Sent Events) format. Events streamed as they arrive.

---

### Query Execution

#### POST /api/v1/query/execute — Execute BQL query

```bash
curl -X POST http://localhost:8080/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "find * | where severity >= 4 | stats count by host | sort by count desc | limit 10",
    "maxResults": 1000,
    "timeRange": "24h"
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
  "timestamp": "2024-12-15T10:35:00Z"
}
```

**Limits:** Max 10,000 character query. Max 100,000 results.

#### POST /api/v1/query/validate — Validate BQL syntax

```bash
curl -X POST http://localhost:8080/api/v1/query/validate \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find * | where severity >= 4 | stats count by host" }'
```

**Response (valid):**
```json
{ "isValid": true, "message": "Query is valid" }
```

**Response (invalid):**
```json
{ "isValid": false, "message": "Unknown stage: 'foo' at position 23" }
```

#### POST /api/v1/query/explain — Get query execution plan

```bash
curl -X POST http://localhost:8080/api/v1/query/explain \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find errors | where severity >= 4 | stats count by host" }'
```

#### POST /api/v1/query/translate — Translate natural language to BQL

```bash
curl -X POST http://localhost:8080/api/v1/query/translate \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "text": "show me errors from the last 24 hours" }'
```

**Response:**
```json
{
  "originalText": "show me errors from the last 24 hours",
  "translatedQuery": "find * | where severity >= 4 from last 24h",
  "confidence": 0.85
}
```

#### POST /api/v1/query/export — Export query results

```bash
curl -X POST http://localhost:8080/api/v1/query/export \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "find * | where severity >= 4 from last 7d",
    "format": "csv",
    "maxRows": 10000
  }'
```

#### POST /api/v1/query/fieldsummary — Field statistics

```bash
curl -X POST http://localhost:8080/api/v1/query/fieldsummary \
  -H "X-API-Key: dev-basic-key" \
  -H "Content-Type: application/json" \
  -d '{ "query": "find * from last 1h" }'
```

#### Saved Queries

```bash
# Save a query
curl -X POST http://localhost:8080/api/v1/query/saved \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Top Error Hosts",
    "query": "find * | where severity >= 4 | stats count by host | sort by count desc | limit 10",
    "description": "Shows hosts with most errors"
  }'

# List saved queries
curl -H "X-API-Key: dev-basic-key" \
  http://localhost:8080/api/v1/query/saved

# Execute saved query
curl -X POST http://localhost:8080/api/v1/query/saved/{id}/execute \
  -H "X-API-Key: dev-basic-key"
```

---

### Dashboards

#### CRUD Operations

```bash
# Create dashboard
curl -X POST http://localhost:8080/api/v1/dashboards \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "title": "System Overview",
    "description": "Real-time system health monitoring",
    "isShared": true,
    "refreshIntervalSeconds": 30,
    "defaultTimeRange": "24h",
    "panels": [
      {
        "title": "Error Count by Host",
        "query": "find * | where severity >= 4 | stats count by host",
        "visualizationType": "bar",
        "row": 0, "column": 0, "width": 6, "height": 4
      },
      {
        "title": "Events Over Time",
        "query": "find * | timechart span=1h count",
        "visualizationType": "line",
        "row": 0, "column": 6, "width": 6, "height": 4
      }
    ]
  }'

# List dashboards
curl -H "X-API-Key: dev-basic-key" http://localhost:8080/api/v1/dashboards

# Get dashboard by ID
curl -H "X-API-Key: dev-basic-key" http://localhost:8080/api/v1/dashboards/{id}

# Update dashboard
curl -X PUT http://localhost:8080/api/v1/dashboards/{id} \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{ "title": "Updated Title", ... }'

# Delete dashboard
curl -X DELETE http://localhost:8080/api/v1/dashboards/{id} \
  -H "X-API-Key: dev-professional-key"
```

---

### Knowledge Objects

#### Lookup Tables

```bash
# Create lookup
curl -X POST http://localhost:8080/api/v1/knowledge/lookups \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "users",
    "description": "User directory",
    "columns": ["user_id", "email", "name", "department"],
    "rows": [
      ["U001", "alice@example.com", "Alice Smith", "Engineering"],
      ["U002", "bob@example.com", "Bob Jones", "Marketing"]
    ]
  }'

# List lookups
curl -H "X-API-Key: dev-basic-key" http://localhost:8080/api/v1/knowledge/lookups

# Use in BQL
# find * | lookup users user_id OUTPUT email, name
```

#### Query Macros

```bash
# Create macro
curl -X POST http://localhost:8080/api/v1/knowledge/macros \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "severe_errors",
    "definition": "where severity >= 4",
    "description": "Filter for severe errors only"
  }'

# List macros
curl -H "X-API-Key: dev-basic-key" http://localhost:8080/api/v1/knowledge/macros
```

#### Field Aliases

```bash
# Create field alias
curl -X POST http://localhost:8080/api/v1/knowledge/field-aliases \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "sourceType": "syslog",
    "originalField": "hostname",
    "aliasField": "host"
  }'

# List field aliases
curl -H "X-API-Key: dev-basic-key" http://localhost:8080/api/v1/knowledge/field-aliases
```

---

### Detection

#### Run Detection Suites

```bash
curl -X POST http://localhost:8080/api/v1/detection/analyze \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "suite": "hackwatch",
    "maxEvents": 10000,
    "options": {
      "minSeverity": 3
    }
  }'
```

**Available suites:** `hackwatch` (cheating), `scumscan` (toxicity), `adminabuse` (admin misconduct)

---

### Splunk HEC Compatibility

Cabinet supports Splunk's HTTP Event Collector (HEC) protocol for drop-in compatibility:

#### POST /services/collector/event — Send JSON events

```bash
curl -X POST http://localhost:8080/services/collector/event \
  -H "Authorization: Splunk dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{
    "event": "User login failed from 192.168.1.100",
    "time": 1702640400,
    "host": "auth-server",
    "sourcetype": "auth_log",
    "index": "main"
  }'
```

**Response:**
```json
{ "text": "Success", "code": 0 }
```

#### POST /services/collector/raw — Send raw text

```bash
curl -X POST http://localhost:8080/services/collector/raw \
  -H "Authorization: Splunk dev-professional-key" \
  -H "Content-Type: text/plain" \
  -d '2024-12-15 10:30:00 ERROR Connection timeout
2024-12-15 10:30:01 WARN Retry attempt 1
2024-12-15 10:30:02 INFO Connection restored'
```

#### GET /services/collector/health — HEC health check

```bash
curl http://localhost:8080/services/collector/health
```

**Response:**
```json
{ "text": "HEC is healthy", "code": 0 }
```

---

### Health Endpoints

All health endpoints are **unauthenticated** (except `/health/metrics` and `/health/status`):

```bash
# Basic health
curl http://localhost:8080/health
# {"status":"Healthy"}

# Readiness (storage connected?)
curl http://localhost:8080/health/ready

# Liveness (always 200)
curl http://localhost:8080/health/live

# Version info
curl http://localhost:8080/health/version
# {"version":"1.0.0","environment":"Production"}

# Detailed metrics (requires auth)
curl -H "X-API-Key: dev-enterprise-key" http://localhost:8080/health/metrics

# Service status (requires auth)
curl -H "X-API-Key: dev-enterprise-key" http://localhost:8080/health/status
```

---

## Common Workflows

### 1. Ingest → Query → Alert

```bash
# 1. Ingest logs
curl -X POST http://localhost:8080/api/v1/logs \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d @logs.json

# 2. Query for errors
curl -X POST http://localhost:8080/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"query": "find * | where severity >= 4 from last 1h | stats count by host"}'

# 3. Create dashboard for monitoring
curl -X POST http://localhost:8080/api/v1/dashboards \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d @dashboard.json
```

### 2. Migrate from Splunk

```bash
# Splunk HEC events work with Cabinet unchanged
# Just point your Splunk forwarders to Cabinet:
# old: https://splunk:8088/services/collector/event
# new: http://cabinet:8080/services/collector/event

# Splunk SPL queries work in Cabinet:
curl -X POST http://localhost:8080/api/v1/query/execute \
  -H "X-API-Key: dev-professional-key" \
  -H "Content-Type: application/json" \
  -d '{"query": "search error | stats count by host | sort -count | head 10"}'
```

### 3. Enrichment with Lookups

```bash
# 1. Create lookup table
curl -X POST http://localhost:8080/api/v1/knowledge/lookups \
  -H "X-API-Key: dev-professional-key" \
  -d '{"name":"servers","columns":["host","team","tier"],"rows":[["web-01","platform","1"],["db-01","data","1"]]}'

# 2. Use in queries
curl -X POST http://localhost:8080/api/v1/query/execute \
  -d '{"query":"find * | lookup servers host OUTPUT team, tier | stats count by team"}'
```

## SDK / Client Libraries

### .NET

```csharp
using var client = new HttpClient();
client.BaseAddress = new Uri("http://localhost:8080");
client.DefaultRequestHeaders.Add("X-API-Key", "your-key");

var response = await client.PostAsJsonAsync("/api/v1/query/execute", new {
    query = "find * | stats count by host",
    maxResults = 100
});

var result = await response.Content.ReadFromJsonAsync<QueryResult>();
```

### Python

```python
import requests

headers = {"X-API-Key": "your-key", "Content-Type": "application/json"}

# Query
response = requests.post(
    "http://localhost:8080/api/v1/query/execute",
    headers=headers,
    json={"query": "find * | stats count by host"}
)
results = response.json()["results"]
```

### JavaScript / TypeScript

```typescript
const response = await fetch("http://localhost:8080/api/v1/query/execute", {
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

When rate limited, the API returns `429 Too Many Requests`:

```json
{
  "status": 429,
  "title": "Too Many Requests",
  "detail": "Rate limit exceeded. Please retry after the rate limit window resets."
}
```

**Best practices:**
- Implement exponential backoff on 429 responses
- Batch log ingestion (up to 10,000 events per request)
- Use streaming endpoints for real-time monitoring instead of polling
- Consider upgrading API key tier if regularly hitting limits
