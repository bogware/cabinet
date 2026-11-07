# Changelog

All notable changes to Cabinet are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

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

- MySQL and DynamoDB storage providers and the legacy PostgreSQL provider, along with the CLI `backup` and `centralize` commands and the compose MySQL profile.
- CLI `report` command, which simulated work and printed sample data.
- Multi-tenancy code. Cabinet serves one organization per deployment.
- SQLite fallback for unreachable backends, including `Storage:FallbackType`.
- Redis caching of API key validation, including `Distributed:Cache:ApiKeyTtlSeconds`. Keys are validated locally on every instance.
- `api-spec.yaml`, `docker-compose.dev.yml` and the root `Dockerfile`.

### Fixed

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
