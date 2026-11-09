# Changelog

All notable changes to Cabinet are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [2.3.0] - 2026-11-09

### Added

- Settings, Storage shows where your data is: the database file's full path, or the PostgreSQL host and database, with its size, event count and time span.
- Move storage from the dashboard, the API (`/api/v1/storage`) or the Windows installer, with progress you can watch and cancel:
  - to another file or drive, copying the SQLite database whole;
  - to PostgreSQL, creating the database if asked, copying events, dashboards, lookups, macros, saved and scheduled searches, keys and history while Cabinet keeps running;
  - or to a new, empty database.
  Ingest pauses for a few seconds at the switch, answering 503 with `Retry-After`, and agents resend. Counts are checked at both ends before switching, and the old database is never changed.
- The Windows installer installs a local PostgreSQL (17, signature checked, answering only this machine) and uses it by default for new installs; `-Storage postgresql` moves an existing install to it.

### Changed

- New Windows installs use PostgreSQL unless `-Storage sqlite` is given. Existing installs keep their storage.

### Security

- The settings file the dashboard writes, which can hold database passwords and the update token, is now readable only by administrators and the service account. It used to inherit `Program Files` permissions, readable by every user on the machine.

## [2.2.0] - 2026-11-09

This release includes everything planned for 2.1.0, which was never published.

### Added

- Search, rebuilt:
  - A time picker with presets, "last N minutes, hours, days or weeks" and exact ranges.
  - A timeline of matching events, with hover; drag across it or click a bar to zoom in, and zoom back out.
  - A fields sidebar: each field's coverage, distinct values, top values with shares, and min, max and mean for numbers, with one-click filtering on or excluding any value.
  - Expandable events: filter or exclude by any field, and pin fields to show on every event.
  - Highlighting of what you searched for, and query syntax highlighting.
  - Autocomplete for stages, functions, aggregates and your own field names.
  - Recent searches; an index picker.
  - A sortable table, chart and JSON views, and adjustable page size.
  - Links that reopen a search; CSV and JSON export.
  - Notes when a search was computed in the database, cut off by a limit, or approximate.
- The dashboard's Settings screen:
  - Every server setting in groups, each with its description, default, units, allowed range and where its value comes from.
  - Editing with checks before saving, reset to default, and a restart button when a change needs one.
  - An overview of where logs are stored, their size, the events held and free disk.
  - API keys made, listed and revoked in the dashboard, each shown once when created.
  - Browser preferences, including how many events a search loads.
- The query API takes `startTime`, `endTime` and `limit`, and says when the database computed a result (`pushedDown`).
- Every server setting described and editable at `/api/v1/settings`, for the dashboard's Settings screen: grouped, with units, limits, allowed values, the value in effect and its source. Changes are checked, saved atomically to a file outside the program folder, audited, and marked when they wait for a restart, which `/api/v1/settings/restart` does for a service.
- Limits that were fixed in code are settings under `Limits:*`, defaulting to their old values: request sizes, search, stream and export time limits and caps, query length, regular expression time, backtest bounds, alert history size, backup spacing, and background intervals. So is the anonymous rate limit (`RateLimiting:Anonymous:MaxRequestsPerMinute`).
- `/api/v1/settings/storage`: where events are stored, how much, and how much disk is left.
- `/api/v1/keys`: list, make and revoke API keys. Keys and revocations persist in the metadata database and reach every instance.
- Updating from the dashboard (Settings > Updates), the CLI (`cabinet update`, `cabinet update apply`) and the API (`/api/v1/updates`). The server downloads the release, checks its SHA-256, and swaps itself in through a separate updater that keeps settings, backs up the old version and restores it if the new one doesn't start. Agents get the matching build from their server and restart on it.
- `install-server.ps1 -UpdateToken`, for a private repository's releases.
- Agent inputs take `DeleteDeliveredAfterDays`, which deletes files the server has stored in full once they've been quiet that long. `install-agent.sh --writable` lets the service delete there.

### Fixed

- Event times from SQLite had no time zone in JSON, so the dashboard showed them shifted by the browser's offset from UTC (six hours in Chicago).
- BQL now reads dotted names such as `metric.name` and `service.name` as fields. Before, they matched nothing in `where` and failed to parse after `by`, so OpenTelemetry attributes couldn't be filtered or grouped on.
- `install-server.ps1 -Https` failed on an existing install with "Cannot convert value ... to type SwitchParameter": a variable shared the `-Https` switch's name, which PowerShell doesn't tell apart by case.
- Upgrading the service set its path through `sc.exe`, which Windows PowerShell's quoting could break; it's now set directly.

## [2.0.2] - 2026-11-08

### Added

- `install-server.ps1 -Https` serves HTTPS with a self-signed certificate it creates, for agents on other networks, and prints the fingerprint for them to pin. Plain HTTP then answers only the server itself.
- Agents pin a server certificate with `Output:CertificateSha256`, so a self-signed certificate is safe without a domain name or certificate authority.

### Fixed

- Requests from the server itself are no longer redirected to HTTPS, so the dashboard opened there doesn't land on a certificate warning.

## [2.0.1] - 2026-11-08

Found by installing on Windows Server and reading live Left 4 Dead 2 servers.

### Added

- Agent inputs take `IgnoreOlderThanHours`, to skip old history, and `CloseInactiveSeconds` (default 300), which closes quiet files and reopens them from their checkpoint when they change.
- The agent sends its time zone, and times written without an offset are read in it.
- `install-server.ps1` creates API keys (`-IngestKeys`), opens the firewall only to `-AllowFrom`, turns on the game parsers with `-Gaming`, sets retention, and restarts the service after a crash. Upgrades keep settings and keys.
- `install-agent.sh` takes `--config`, `--groups`, `--binary`, `--source-type` and `--index`, finds the binary in the release archive, and starts the service.
- The Left 4 Dead 2 parser reads the full player format (character, state, health, position), both sides of a kill, ban length and who banned, map, world and connection events, and flags the restart-for-update notice as a warning.

### Fixed

- As a Windows service, the API looked for its settings, dashboard and logs in `C:\Windows\System32`, so the dashboard returned 404.
- `install-server.ps1` couldn't find the binaries in the release archive, created no API keys, and opened the firewall only on Domain and Private networks.
- The agent's service couldn't read files under `/home`.
- The agent read every file a pattern matched, however old, and never closed them.
- Left 4 Dead 2 and SourceMod times were read as UTC, so events from servers in other zones were hours off.
- Words in game chat, such as "lag" or "error", raised severity or added tags, and every special infected bot leaving was tagged as an admin action.
- SourceMod's console was recorded as a Steam ID, and the plugin named in "Blaming:" was lost.
- `agent-chaos.sh` stopped working when Compose began requiring `CABINET_AGENT_API_KEY`.

## [2.0.0] - 2026-11-07

Upgrading from 1.0: settings under `Distributed:*` and the SQLite RBAC database are gone (see Removed), the Compose agent needs its own `CABINET_AGENT_API_KEY`, and changing detection rules, data models, knowledge objects and field extractions now needs the capability on every index.

### Added

- `stats` and `timechart` run inside the database when they can: as SQL on PostgreSQL and SQLite, and as aggregations for counts on OpenSearch. Answers are identical either way, which a test checks on every backend. Counting a million events by host takes a quarter of a second on PostgreSQL instead of 13. See `docs/BENCHMARKS.md`.
- OTLP traces at `POST /v1/traces` and metrics at `POST /v1/metrics`, stored as searchable events in the `traces` and `metrics` indexes.
- Field extractions apply at search time, including to events already stored.
- `POST /api/v1/rules/backtest` shows how often a rule would have fired on stored events, to tune it before turning it on.
- Nine more built-in detection rules: SSH key added, shadow file read, shell history cleared, Windows admin group change, account lockout, Kerberoasting, bursts of 5xx responses, AWS root use and access key creation.
- Detection counts and cooldowns are shared by all instances on PostgreSQL, so a threshold counts events wherever they arrive and a rule fires once.
- Rate limits are shared out among running instances, which send heartbeats.
- Encryption key rotation: values record which key wrote them, and old keys go in `Encryption:PreviousKeyFilePaths` or `CABINET_ENCRYPTION_PREVIOUS_KEYS`.
- API key expiry with `ApiKeys:Keys:N:ExpiresAt`.
- The `Ingest` key tier, which can send events but not read them, for agents.
- `Audit:HmacKey`, which makes the audit chain's hashes HMACs.
- `benchmarks/Cabinet.LoadTest` and `tests/EndToEnd/soak.sh`, which run several instances under sustained load while killing them and check every acknowledged event is stored once. A manual Soak workflow runs it with the backup drill.

- Detection rules that run on every event at ingest, with thresholds, cooldowns and grouping, at `/api/v1/rules`. 20 built-in security rules, off until turned on.
- OTLP logs at `POST /v1/logs`, in protobuf and JSON.
- Alert history that's actually written, from detections and scheduled searches.
- The dashboard draws line, stacked area and bar charts and single values, with hover, keyboard access and a data table. Dashboards have a panel editor, a grid and auto-refresh, and there's a Detections page.
- Accessibility: real buttons for navigation, skip link, labelled controls, dialogs that trap focus and close on Escape, keyboard-usable tabs and rows, visible focus, reduced motion, and a phone layout.
- SQLite backups through `/api/v1/backups`, with `--restore`, and a PostgreSQL drill that runs in CI.
- Per-index retention, enforced hourly.
- Field encryption with `Encryption:*`, and masking by role with `Masking:Rules`, on every read and in the raw line too.
- `GET /api/v1/audit` and `/api/v1/audit/verify`. Audit records live in the metadata database.
- OTLP trace export with `Telemetry:OtlpEndpoint`.
- Working Kubernetes manifests with a kustomization and a PostgreSQL pod, tested on kind.
- Prometheus alert rules, and CI checks for them and for the manifests.
- `RateLimiting:Tiers:*`, and a `Serilog` configuration section.

- Queries read every matching event, a page at a time. `stats`, `timechart`, `chart`, `top`, `rare` and filters hold what they compute, not the events, and `sort` with `head` holds only the top rows.
- Per-query limits (`Query:Limits`) and concurrency limits (`Query:Concurrency`, 429 when exceeded). A query over a limit stops with an explanation or is marked `truncated` or `approximate`. A regular expression that keeps timing out fails the query instead of using its whole time limit.
- `benchmarks/Cabinet.Benchmarks` and published figures in `docs/BENCHMARKS.md`.
- Per-index permissions, enforced for every read and write, including subsearches, joins, macros and search jobs.
- The Admin API key tier, with `UserManagement`. `PUT /api/v1/roles/{id}` now works, and `GET /api/v1/roles/me` returns the caller's principal.
- OIDC tokens get the role and index permissions their claims map to. `Sso:AutoProvision=false` is enforced.
- Several API instances can share a PostgreSQL database, with one at a time running background work. With OpenSearch, use `Storage:MetadataPostgresConnectionString`.
- Saved principals, alert history, field extractions, search jobs and data models persist in the metadata database.

- RFC 5424 syslog with structured data, TLS syslog (`Syslog:TlsPort`), and RFC 6587 octet-counted framing.
- systemd (`Type=notify`) and Windows service integration for the API.
- `Authorization: Splunk <key>` authentication on `/services/collector/*` routes, alongside `X-API-Key`.
- `Network:TrustedProxies`, the list of proxies allowed to set `X-Forwarded-For`.
- `Storage:MetadataConnectionString`, a local SQLite database for metadata that has no store on the primary backend.
- Postgres-backed stores for dashboards, knowledge objects, saved queries and schedules.
- `api-spec.json`, generated from the API and checked by a test. Regenerate with `CABINET_UPDATE_API_SPEC=1 dotnet test --filter ApiSpec_MatchesTheRunningApi`.
- Apache-2.0 `LICENSE` file.
- `SECURITY.md`.
- CI runs the Postgres integration tests, boots the API against a real database, validates the compose file, and runs tests in a non-UTC time zone.

### Changed

- `/api/v1/query/stream` requires `Export`. Dashboards and field extractions require capabilities for writing, and retention and alert acknowledgement require them at all.
- API keys get IDs derived from the key, which stay the same across restarts.
- `Enterprise` keys get `IndexManagement`.
- PostgreSQL ingest uses `COPY`, and reads use an index on time and ID rather than sorting every match.
- `pollIntervalMs` on live streams has a minimum of 500.
- Every ingest route parses lines the same way: REST, HEC `/event` and `/raw`, so the agent too. Events get severity, fields and the time in the line. A HEC event's `time` is used only when the line has none. A source type that isn't a parser's stays as a label.
- Timestamps: ISO 8601 with `T` or an offset, log4j commas, nginx error log dates and syslog dates are recognized. Times without an offset are UTC. A syslog date with no year that would be in the future is from last year.
- Severity: words match whole words, and mentions like `0 errors`, `failed=0` or `no errors` don't count. A declared level beats words in the message. In JSON, only the level field counts.
- JSON: epochs in seconds to nanoseconds are read, times without an offset are UTC, nested objects flatten to dotted fields such as `log.level`, and `source` no longer overwrites `Host`. `critical` maps to `Critical`, not `Fatal`.
- `key=value` pairs become fields under their own name with numbers kept as numbers, replacing `kv_<key>`.
- Prose with commas is no longer detected as CSV.
- Syslog events use the `Syslog` source type, and files with syslog lines are detected. The listener's `message` field is now `Message`.
- The syslog listener stops the server if it can't bind its ports, retries failed batches, stores what's queued at shutdown, and makes TCP senders wait rather than dropping their messages.
- Storage ignores an event whose ID already exists, on every backend. HEC events can supply a stable `id`.
- Agent registry files from earlier versions aren't compatible, so tracked files are read again once after upgrading.
- Compose keeps the agent's spool on its data volume.
- `now()` returns Unix seconds, as documented. `strptime`, `relative_time` and `mktime` also return Unix seconds.
- Unknown functions, unknown aggregates and invalid regular expressions are query errors rather than silent nulls.
- `Tags` in query results is an array rather than a comma-separated string. CSV exports join multi-value fields with semicolons.
- Text such as `1,2` or `$5` no longer counts as a number.
- Game log parsers and moderation detection moved to the optional `Cabinet.Extras.Gaming` extension, off by default. Set `Gaming:Enabled=true` to use them. `/api/v1/detection/*` returns 404 otherwise.
- Parsers run in a fixed order: JSON, CEF, Apache, Nginx, CSV, gaming parsers if enabled, Generic. Ordinary lines mentioning words like "Steam" are no longer claimed by a game parser, and the API now parses Apache and Nginx logs.
- `stats count` output column is now `count`, as documented, rather than `count()`.
- A bare `... | stats count` uses the backend's count, so it isn't limited by the 100,000 event read limit. Other queries that hit that limit set `truncated: true` in the response and the `X-Result-Truncated` header.
- Moved to .NET 10, with package versions declared centrally in `Directory.Packages.props`. Restore now audits transitive packages.
- **Retention no longer deletes at 30 days by default.** `Storage:RetentionDays` is honored: 0, the default, keeps everything, and a positive value purges at startup and hourly.
- A backend that is still unreachable after retries now fails startup with a non-zero exit code, as do other fatal startup errors.
- The default Production configuration starts on SQLite.
- The API image runs as the built-in non-root `app` user (uid 1654) on the Ubuntu-based .NET 10 base image.
- CI publishes the API image to `ghcr.io/bogware/cabinet`.
- Repository references point at `bogware/cabinet`.
- Superseded CI runs on the same branch are cancelled.
- Documentation now states what works, what is partial and what is not implemented. Unsupported performance figures were removed.

### Removed

- `AlertEngine`, `IAlertAction`, `AlertRule` and `SendAlertWithRuleAsync`, which nothing used. Detections and scheduled searches share one path.
- `ArchiveBeforeDelete` and `MaxSizeBytes` on retention policies, which did nothing.
- Redis, along with `Distributed:*` settings, the `distributed` setup wizard mode and the Compose service. Nothing read from it. PostgreSQL now does leader election.
- `SqliteRbacService`, and the `cabinet-rbac.db` file with it. Roles saved in it aren't carried over.
- The `source` parameter of `DELETE /api/v1/logs`. It was ignored, so a request meant for one source could purge everything.
- MySQL and DynamoDB storage providers and the legacy PostgreSQL provider, along with the CLI `backup` and `centralize` commands and the compose MySQL profile.
- CLI `report` command, which simulated work and printed sample data.
- Multi-tenancy code. Cabinet serves one organization per deployment.
- SQLite fallback for unreachable backends, including `Storage:FallbackType`.
- Redis caching of API key validation, including `Distributed:Cache:ApiKeyTtlSeconds`. Keys are validated locally on every instance.
- `api-spec.yaml`, `docker-compose.dev.yml` and the root `Dockerfile`.

### Fixed

- OpenSearch rejected events whose custom fields changed type from one event to another. `fields.*` is now mapped as keyword.
- A detection rule's regular expression could time out the first time it ran under load.
- `RateLimiting:Tiers:Basic` had no effect.
- The dashboard no longer offers a development key by default, and keeps the key for the tab unless asked to remember it. Script moved out of the page, so the content security policy forbids inline script.
- Dashboards listed zero panels, and the Alerts page's columns didn't match the API.
- Cron: an update could save an invalid expression, a huge step could hang the scheduler, `1,5/2` wasn't understood, and an expression could run twice in a minute or miss one during a slow check. A slow search no longer delays others.
- Scheduled searches counted at most 10,000 results.
- Webhooks now retry on 429.
- `/query/execute` returns `eventsScanned`.
- Backups could copy a SQLite database mid-write. They now use SQLite's backup API, with an integrity check, and verification opens the database rather than only decompressing it.
- `Backup:AutoBackupEnabled` on another backend crashed startup. It's now a warning.
- A corrupt backup manifest was overwritten, losing older checksums.
- The audit hash left out the description and source address, so those could be changed unnoticed.
- The default `appsettings.json` deleted events older than 30 days, though the docs said it kept everything. It now keeps everything.
- Log files stopped being written after 100 MB in a day. They now roll over.
- The encryption service wrote a key file even when off, and instances starting together could fail on it.
- Trivy findings now fail the Docker job, as they appeared to.
- Text search means the same on every backend: every whole word, ignoring case. It used to be a phrase on SQLite, stemmed words on PostgreSQL, any word on OpenSearch and a substring in memory.
- SQLite's full-text index wrote no rowid, so searches after a purge could return the wrong events or none. It's rebuilt on upgrade.
- `GET /api/v1/logs` with `source` or `severity` returned nothing, and its `X-Total-Count` ignored filters.
- Live streams lost events that shared a timestamp, and events that arrived with an older time than one already sent, which agents' batches often do.
- `/query/execute` never returned its `truncated` flag.
- The query cache could show one caller's results to another with different access.
- Search jobs could be read, cancelled or deleted by anyone, were left Running forever after a crash, and could run twice.
- `sort` threw on a field holding both numbers and text.
- A long chain of `not` could overflow the stack and kill the server.
- `logfmt` `host=` now sets the event's host.
- Readiness counted every event, every 10 seconds. `/metrics` runs its aggregates at most once a minute.
- The agent no longer loses events in an outage or crash. Checkpoints move only when events are accepted or spooled, the spool replays periodically instead of only at startup, and a rejected batch no longer blocks the rest. A 401 or 403 no longer drops events.
- The agent no longer creates duplicates. Growing small files no longer look rotated, and events carry a stable ID that the server stores once.
- The agent reads lines written just before a rotation, doesn't re-read rotated files that a pattern also matches, doesn't split partial lines or multi-line events across polls, bounds line length and spool size, restarts failed harvesters, and supports `**` and wildcards in any path segment.
- A partial OpenSearch bulk failure now returns an error, so the sender retries instead of losing the failed events.
- Brought BQL in line with its guide. Every documented query now runs as a test, with results checked for each function and stage. Notable fixes: `.` concatenation, `replace` with a regex, `printf`, `strftime`, `strptime`, `relative_time`, `mktime`, `ctime`, `dur2sec`, `mvfilter`, `regex field="pattern"`, `top N x by y`, `transaction startswith/endswith`, `chart` cross-tabs, `timechart` functions other than sum, avg, min and max, in-place `convert`, and `fieldsummary` type and stdev.
- Fields named like severity levels, such as `error`, are no longer read as numbers outside severity comparisons.
- Group keys no longer collide when values contain `|`.
- PostgreSQL instances starting together on a new database could crash creating the schema. Schema creation now takes a lock.
- OpenSearch queries raise an error when shards fail instead of returning partial results.
- BQL field names now match case-insensitively in every stage, so `stats count by host`, `fields`, `dedup`, `top` and `rename` find the fields ingestion stores. `source_type`, `raw_line` and `level` are aliases, and `message` always has a value.
- Filters and sorts after a stage such as `head`, `eval` or `stats` now apply to that stage's output. They were moved ahead of it, giving wrong results.
- `OR` conditions, repeated filters and `severity > n` no longer lose rows or match the wrong ones.
- Numeric JSON fields compare and aggregate as numbers on SQLite.
- `search status=500`, `search a=1 OR b=2` and `search NOT a=1` now filter on fields. Bare values are strings.
- `host` and `source_type` equality is case-insensitive on every backend, matching the rest of the query language.
- OpenSearch queries returned nothing through the API: they exceeded the result window and the failure was logged as an empty result. Reads now page with `search_after`, and failed requests raise errors.
- Failed SQLite and OpenSearch batch writes were reported as successfully ingested. Ingest now reports the stored count and returns an error on failure. SQLite no longer uses shared cache, which could make open readers fail writes.
- The agent image could not start because its entry point named a file that isn't published.
- PostgreSQL and OpenSearch deployments crashed at startup because metadata stores received the primary backend's connection string. `docker-compose.yml` was also unparsable after the MySQL removal.
- The API container health check called `wget`, which the base image doesn't include. It now uses `curl`.
- The CLI image could not be built or started, ran as root, and pointed at the wrong API port.
- L4D2, SourceMod and SteamCMD timestamps without an offset are read as UTC on every host. SourceMod timestamps previously failed to parse, so those events took the ingest time.
- A packaged install could not start, because Production configuration required a PostgreSQL connection string it didn't provide.
- Fatal startup errors exited with code 0, so service managers treated a crash as a clean exit.
- An idle syslog sender could pin a thread past the idle timeout.
- `peek` reverse reads could use a partially filled buffer.
- `Cabinet.AWS` and `Cabinet.Alerts` are now in the solution, so they build in the selected configuration.

### Security

- Fork pull requests no longer run on the self-hosted CI runners.
- Alert history no longer copies log lines, which bypassed encryption and masking, and shows each caller only alerts about indexes they can read.
- Detection rules, data models, knowledge objects, field extractions and backups need their capability on every index, since they act on all of them. Retention changes need `IndexManagement` on that index, and are audited.
- Searching under field masking matches the masked text, so it can't reveal whether a hidden value is present.
- OIDC tokens can't supply Cabinet's own claims, such as `CabinetRole` or `IndexPermission`.
- Limits on OTLP value nesting and decompressed size, on nested function calls in queries, and on `printf` precision, each of which could exhaust memory or the stack.
- `/metrics` matches only its exact path, so a proxy rule blocking it can't be bypassed with different case.
- Manual backups can be taken at most every five minutes.
- The agent installer makes its settings file, which holds the API key, readable only by root.
- Closed a stored XSS in the dashboard. Crafted hostnames or macro parameters could run script in an analyst's session and read the API key from local storage.
- Compose refuses to start until every secret in `.env` is set, Redis requires a password, Postgres and Redis are no longer published to the host, and the monitoring and LocalStack ports bind to localhost.
- Capability checks deny principals without a `ClientId`. They previously passed, granting every capability to such principals, including OIDC tokens without a subject.
- Forged Redis entries can no longer grant access, because API key validation no longer reads Redis.
- `X-Forwarded-For` is ignored unless the sender is a trusted proxy, so clients can no longer forge an address to evade rate limits or falsify audit records.
- Anonymous requests are limited per address (100 per minute) and rejected rather than queued. Health endpoints are exempt, so anonymous floods can't starve health probes.
- Metric labels are built from route templates and known methods, which stops unbounded label growth and label injection before authentication.
- Tightened the Content Security Policy with `connect-src`, `object-src`, `base-uri` and `form-action`.
- Third-party GitHub Actions are pinned to commit SHAs, and `trivy-action` moved to v0.36.0 after its older tags were compromised (GHSA-69fq-xp46-6x23). This repository was not exposed.
- Updated dependencies clear the System.Text.Json and SQLite advisories, and removing MySqlConnector clears its advisory.
