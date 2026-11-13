# Features

What Cabinet does today, and how finished each part is. **Available** means it's tested and documented; **Partial** and **Working** say what's missing in the notes.

| Feature | Status | Notes |
|---------|--------|-------|
| REST ingest | Available | `POST /api/v1/logs/ingest`. Events go to index `main`. |
| Splunk HEC ingest | Available | `/services/collector/event` and `/raw`. |
| OpenTelemetry logs | Available | OTLP over HTTP at `/v1/logs`, in protobuf or JSON. Retried exports are stored once. |
| Syslog listener | Working | RFC 3164 and RFC 5424 over UDP, TCP and TLS. Off by default, and Compose doesn't publish the port. |
| Forwarding agent | Available | Files, Kubernetes container logs, the systemd journal and Windows event logs. Retries, a disk spool and checkpoints that follow delivery, so an outage or crash doesn't lose lines, and re-sent lines are stored once. See the limits below. |
| Ingest pipeline | Available | Drop, sample, route or redact events before they're stored, with how much was kept out. |
| Storage backends | Available | The Cabinet engine (Cabinet's own columnar store, the fastest on one server), SQLite, PostgreSQL, OpenSearch, ClickHouse, and in-memory for tests. |
| BQL query engine | Available | SPL search syntax (OR, NOT, phrases, wildcards, field terms, `earliest=`), `stats` functions including `values`, `earliest`, `mode` and `count(eval())`, `cluster` for log patterns, and `iplocation`. Every example in the guide runs as a test. Filters, `stats`, `timechart`, `chart`, `top`, `rare` and `sort | head` read every matching event without holding them in memory. Stages that need all their rows at once hold up to 100,000 and flag results as `truncated` beyond that. See [benchmarks](BENCHMARKS.md). |
| Plain-English search | Available | Questions in any word order ("errors on web servers since yesterday by host", "p95 latency per endpoint", "top 5 slowest pages") read into BQL offline, with typos fixed, what it understood shown in BQL and in words, and choices when it isn't sure. Strict BQL and SPL run as written; saved searches, alerts and rules never reinterpret. |
| Web dashboard | Available | Search with charts, dashboards with a panel editor and auto-refresh, detections, and keyboard and screen reader support. Served from the API outside the Development environment. |
| Search in the dashboard | Available | "Understood as" with one-click alternatives and fixes, problems underlined as you type, suggestions of commands, fields and values from your data, a Build panel to pick a search, starter questions fitted to your data, time picker, timeline with drag-to-zoom, fields sidebar with one-click filters, shareable links, CSV and JSON export. |
| Settings in the dashboard | Available | Every server setting described and editable, with defaults, limits and where each value comes from; API keys; restart. |
| Importing existing logs | Available | `cabinet-agent import` sends a folder, file or glob once, from any machine, or the history an agent skips. Repeating it stores nothing twice. |
| Detection actions | Available | When a rule fires: notify, write a searchable `detections` event, call a webhook, or send to a connector (Slack, Teams, Discord, PagerDuty, Opsgenie, Jira, ServiceNow, email) — off the ingest path, templated and configurable per rule. |
| Sigma and ATT&CK | Available | Import Sigma rules; see which ATT&CK techniques the rules cover. |
| Detection rules as code | Available | YAML rule files with tests; `cabinet rules test`, `push` and `pull`. |
| Anomaly rules | Available | Spikes against a learned baseline, silence, new values and new kinds of line. |
| Cases | Available | Alerts, events, searches and notes on one investigation timeline. |
| AI investigator | Available | Off by default. Answers questions by running searches with the asker's access, on a local model (Ollama or any OpenAI-compatible server) or Claude. |
| Scheduled reports | Available | Searches emailed on a schedule as HTML with a PDF. |
| Storage management | Available | Where the database is, its size and span; move to another file or drive, switch to PostgreSQL, or start empty, from the dashboard, API or Windows installer, with ingest paused only for the switch. |
| Dashboards, saved queries, lookups, macros, field aliases | Available | REST APIs. |
| Async search jobs | Available | Private to their owner, and shared by all instances on PostgreSQL. |
| Scheduled searches | Available | Cron-based, run by one instance. Require an Enterprise-tier key. |
| Alert channels | Available | Console, file, email, and webhook with generic, Slack, Discord, Teams and PagerDuty formats. |
| Alert history | Available | `/api/v1/alerts`, kept in the metadata database, from both detections and scheduled searches. |
| API key authentication | Available | `X-API-Key` header, with Ingest, Basic, Professional, Enterprise and Admin tiers, and optional expiry. |
| Roles and capabilities per endpoint | Available | A test fails if an endpoint has no capability check. |
| Per-index RBAC | Available | Grants limit a principal to some indexes for every read and write, including subsearches and search jobs. |
| Index and user management routes | Available | `IndexManagement` from the Enterprise tier, `UserManagement` from the Admin tier. |
| OIDC | Available | Bearer tokens for API clients, and dashboard sign-in with the authorization code flow, with claims mapped to roles and index grants. |
| SAML, LDAP | Available | Dashboard sign-in. |
| Rate limiting | Available | Per address and per key, shared out among running instances. Queries and streams also have concurrency limits. |
| Audit log | Available | Hash-chained records in the database, optionally keyed with HMAC, searchable and checkable through `/api/v1/audit`. |
| Global retention | Available | `Storage:RetentionDays`. 0, the default, keeps everything. |
| Per-index retention | Available | `/api/v1/retention`, applied hourly by one instance. |
| Log parsers | Available | JSON, CEF, Apache, Nginx, CSV, Generic. |
| Data models | Available | Pre-aggregated summaries for `tstats`. |
| Field extractions | Available | Regular expressions applied at search time, to events already stored too. |
| Gaming extension | Partial | Optional, off by default. Set `Gaming:Enabled` to add L4D2, SourceMod and SteamCMD parsers, on-demand moderation detection at `/api/v1/detection/*`, and player profiles with suggested moderation actions (never run by Cabinet). |
| Security detection rules | Available | Rules run on every event at ingest, with thresholds and cooldowns shared by all instances. 29 built-in rules for SSH, sudo, Linux persistence, Windows, Kerberos, web attacks and AWS, off until turned on. Add your own, and backtest any rule against stored events before turning it on. |
| Health and Prometheus metrics | Available | `/health`, `/health/ready`, `/health/live`, `/metrics`. |
| AWS CloudWatch and CloudTrail | Available | CLI only (`cabinet aws`). |
| Backup and restore | Available | Verified SQLite backups with `--restore`, and a PostgreSQL drill. Both run in CI. OpenSearch uses snapshots and the Cabinet engine file-system snapshots, which aren't drill-tested yet. |
| Kubernetes manifests | Available | `k8s/`, tested on a kind cluster by `tests/EndToEnd/k8s-smoke.sh`. The included PostgreSQL pod is for trying it, not for production. |
| Encryption at rest | Available | AES-256-GCM on configured fields, or whole lines, with key rotation. Those values can't be searched in storage. |
| PII and field masking | Available | Applied to every read by role, in fields and in the raw line. |
| Archive, search and rehydrate | Available | Retention can archive events to a directory or S3 first. Archived days can be searched where they are (`/archive/search`, up to 500,000 events a search), or brought back into an index. Hot/warm tiering isn't implemented. |
| Charts | Available | Line, area, bar, pie, single value, gauge, heatmap, map and table. |
| Multiple API instances | Available | On PostgreSQL, sharing all state. One instance at a time runs background work. |
| OpenTelemetry | Available | Receives OTLP logs, traces and metrics, in protobuf or JSON, as searchable events. Exports its own request, query and ingest spans. |
| Server-side multi-line merging | Not implemented | The agent merges multi-line events; the API does not. |
| Prometheus alert rules | Available | `prometheus-rules.yml`. |

## Known limitations

- **The built-in detections are a starting point.** They're tested against sample lines of common formats, not against your traffic, so expect to tune them.
- **Multiple instances need PostgreSQL.** With OpenSearch, set `Storage:MetadataPostgresConnectionString`. SQLite and in-memory storage serve one instance. The query cache is per instance.
- **Only some aggregates run in storage outside the Cabinet engine.** On the Cabinet engine, a leading `stats`, `timechart`, `top` or `rare` runs where the data is, percentiles exactly. Elsewhere, a query whose first stage after its filters is `stats` or `timechart`, over built-in or JSON fields, runs as SQL on PostgreSQL and SQLite, and counts run as OpenSearch aggregations. Anything else, such as `top`, `rare`, `stats` after `eval` or `rex`, or percentiles on SQLite, reads every event through the API process. `stats count by f | sort -count | head 10` is a pushed-down `top 10 f`. See [docs/BENCHMARKS.md](BENCHMARKS.md).
- **Agent delivery has edge cases.** Lines are lost if a file is deleted before they're read, for example during a long agent outage. If the registry file is lost, files are read again from the start under new event IDs. See [docs/DEPLOYMENT.md](DEPLOYMENT.md#agent-behaviour).
- **The audit log is tamper-evident, not tamper-proof.** Changes, removals and truncation show up in `/api/v1/audit/verify`, but someone with database access could rewrite every record. Chain heads also go to the log output, which helps if you keep it elsewhere. Reading and verifying are slow on very large logs, since they load every record.
- **API keys** are looked up by SHA-256 hash in a dictionary. The comparison is not constant-time.
- **`/metrics` is unauthenticated.** Restrict it at the network. See [SECURITY.md](../SECURITY.md).
- **Rate limits are shared out, not counted centrally.** The tier limits default to 100, 1,000 and 10,000 requests per minute (`RateLimiting:Tiers:*`), and a per-address limit of 300 per minute applies to all non-health requests (`RateLimiting:PreAuth:*`). With several instances, each enforces its share, so a client sent mostly to one instance is limited early.
- **Soak testing has run for hours, not days.** `tests/EndToEnd/soak.sh` runs two instances under load while killing them; see [docs/BENCHMARKS.md](BENCHMARKS.md). Run the Soak workflow before relying on long uptimes.
