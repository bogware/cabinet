# Changelog

All notable changes to Cabinet are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- Messages about unsigned releases name 3.1.0, the first signed release, where they said 3.0.1, which was never released.

## [3.1.1] - 2026-11-13

### Security

- On Windows, the Cabinet engine's folder kept the access it inherited, so under C:\ProgramData every user on the machine could read its events and its metadata database: key hashes, the user directory, and the keys that sign dashboard sessions, enough to forge a sign-in. The server now makes that folder, and the folder of settings saved from the dashboard (which can hold secrets, and sat under Program Files), readable only by its own account, SYSTEM and Administrators, at every start. `Storage:SecureFolders` turns this off. Linux services were already private through their data folder and umask.

## [3.1.0] - 2026-11-13

Enterprise sign-in and signed releases. Upgrading: dashboard sessions end once, since their keys now live in the metadata database; update the server before its agents, because 3.1 agents install only signed builds, which a 3.1 server offers; and on Linux, rerun the installer (or reinstall the package) so the service gets its new data-folder settings.

### Added

- **A new look: "Platinum".** Light and calm, with a Graphite dark theme that follows the system. A five-group sidebar (Ask, Watch, Respond, Data, Admin) replaces the 27-item list; every page says what it is for, every control is labelled in words, and each view has one default action. Self-hosted Atkinson Hyperlegible fonts, so nothing is fetched from elsewhere.
- **Search shows how it read you**: "Understood as" splits the query into steps, each in code and in words, and clicking one edits it. The result view is chosen from the question (ranked bars, a chart over time, a table or events) and says why; events open in an inspector with Only and Hide per field, a timeline you can drag to zoom, and a summary of the fields in the results. Ask next, About this result, and Save question, Add to dashboard and Alert me when beside every result.
- **First-run Setup Assistant**: on a server with no events, administrators are walked through choosing a source (agent, Splunk HEC, OpenTelemetry or syslog) with the exact install command, and it moves on by itself when the first event arrives.
- **Dashboards** say what each panel measures, compare big numbers with the period before, and add stacked bars; **detection rules** sit beside the ATT&CK coverage matrix, with the biggest gap and a one-click backtest; **cases** read as a timeline with an ADD field for notes and in-case searches; **settings** are staged until Apply, and each says what it does, its default and where it is set. **Live Tail** filters in plain words, shows events per second, and pauses with Space.
- **Entra ID and Cognito presets** for single sign-on (`Sso:Oidc:Provider`): from the tenant, or the region and user pool, Cabinet works out the authority, accepts Entra's v1 and v2 tokens for that tenant only (never `common` or `organizations`), knows users by their Entra object ID (the one SCIM uses, where `sub` differs per app), maps app roles (`roles`) and groups, warns when Entra leaves groups out of a token, accepts Cognito access tokens by their `client_id`, and reads groups from `cognito:groups`. Tested against a real Entra tenant and Cognito user pool.
- **Signed releases**: every archive, package and install script in a release has a `.sig`, an ECDSA P-256 / SHA-256 signature made by the release workflow, plus a signed `SHA256SUMS`. The public key is `build/release-signing.pub.pem`; DEPLOYMENT.md and SECURITY.md show how to check a download with `openssl`.
- **One-step releases**: the manual Release workflow tests a commit, builds every artifact from it with the version stamped in, signs them, tags the commit and publishes the release, as a pre-release for versions like `3.1.0-rc.1`.
- Local accounts: people can sign in to the dashboard with a user name and password kept in Cabinet. Passwords are hashed with PBKDF2-HMAC-SHA256 (600,000 iterations, `Accounts:PasswordIterations`) and must meet `Accounts:PasswordMinLength` (12) without containing the user name or email or being a common password. Repeated failures are slowed per account and per address and lock the account (`Accounts:LockoutThreshold`, `Accounts:LockoutMinutes`).
- Multi-factor sign-in for local accounts with any authenticator app (TOTP, RFC 6238), with ten single-use recovery codes. `Accounts:RequireMfa` makes it compulsory for everyone or for a role and above. Administrators can reset a password (to a temporary one that must be changed), reset MFA, unlock and sign someone out everywhere.
- A user directory, shared by every instance on every backend: users, groups, and the role and index permissions each group gives (the highest role wins; permissions combine). Managed on the new Users page, at `/api/v1/users` and `/api/v1/groups`, and from **My account** for one's own password and authenticator. Every change is audited.
- Single sign-on users (OIDC, SAML, LDAP and OIDC bearer tokens) are matched to directory users by external ID or sign-in name: a user deactivated in the directory is refused whatever the identity provider says, and directory groups give roles. Deactivating, resetting or signing out a directory user ends their sessions on the next request.
- SCIM 2.0 provisioning at `/scim/v2`, tested against the requests Microsoft Entra ID and Okta send, authenticated by SCIM tokens made on the Users page (`/api/v1/scim-tokens`) that work only there. New settings: `Accounts:*` and `Limits:ScimMaxRequestMB`.
- `/auth/providers` says which OIDC provider is configured, and the dashboard labels its sign-in button for Microsoft or AWS.

### Changed

- `index=` in search text, or `where index=`, narrows what storage reads to that index, ignoring case as the search does; on the Cabinet engine only that index's files are read. A search of all time for one index used to read every event.
- The CLI's remote commands (`query --server`, `rules`, `update`) trust a server's self-signed certificate by its fingerprint in `CABINET_CERT_SHA256`, as agents do, and `cabinet setup --mode agent` writes it into the agent's settings.
- `/metrics` limits wrong keys: after 20 in ten minutes from one address, that address is refused without its key being checked. A request from this machine that carries a forwarding header (from a local reverse proxy) no longer counts as local.
- OIDC bearer tokens keep their claims' own names (`sub`, `roles`, `groups`), as the dashboard's sign-in already did. Role mappings on the long Microsoft claim URIs need the short names.
- CI runs only when started by hand (`gh workflow run ci.yml --ref <branch>`); nothing runs on pushes, pull requests, tags or a schedule. Releases are made by running the Release workflow rather than pushing a tag. See docs/CI.md.
- The Docker image carries the release's version, and is tagged with it, `sha-<commit>`, and for a release `<major>.<minor>` and `latest`; pushes to `main` no longer publish an image.
- Updating logs the full running version and the release's tag, as in "Updating from 3.0.0-rc.4 to v3.0.1", where it used to drop the pre-release label from both.
- ASP.NET Data Protection keys, which protect session cookies and now authenticator secrets, are kept in the metadata database, so sessions survive restarts and work across instances. Existing dashboard sessions end once on upgrade.
- LDAP sign-in is slowed after repeated failures, like local accounts, and user names typed at sign-in are cleaned of control characters and shortened before they reach the audit log.

### Security

- Linux services write only under their data folder (/var/lib/cabinet): logs (`CABINET_LOG_DIR`), dashboard settings (`CABINET_SETTINGS_FILE`) and update downloads now go there, and the program belongs to root, so a compromised service can't replace its own program. Dashboard settings and the storage move used to fail on Linux installs, whose sandbox made the settings folder read-only; the installer moves an existing settings file across.
- `/etc/cabinet/appsettings.json`, which can hold a database password, is readable by root and the service only, no longer by every user.
- The agent list keeps each key's reports apart, with at most 1,000 agents per key, so one ingest key can't rewrite another's agents or fill the list.
- The server checks the publisher's signature on a release archive before unpacking it, as well as its SHA-256, and refuses an unsigned or wrongly signed release. Releases before 3.0.1 aren't signed, so install those with the installer. `Updates:AllowUnsigned` accepts unsigned releases, for development builds only, and is logged as a warning.
- Agents check the publisher's signature over the agent program their server offers, with the key built into the agent, so a server that has been taken over can't give agents a program the publisher didn't sign. The server passes the signature (`cabinet-agent-<platform>.sig` in the release) with its offer; agents before 3.0.1 ignore it. Agents from 3.0.1 don't update from servers before 3.0.1, which send no signature. `Agent:Update:AllowUnsigned` is the development-only escape hatch.
- Workflows ask for the least permissions per job, pass inputs to scripts only through checked environment variables, and only the release job sees the signing key, which it removes when it's done.

### Fixed

- The Linux installer upgrades in place when run from a newer release: it stops the service, copies the new build and starts it again. It used to keep the old program if one was installed, and copying a release by hand over an install replaced the server's settings with the release's defaults, through the settings link.
- The Linux installer copies everything published with the server, including SQLite's native library, which it left out.
- The alert log went to a `cabinet` folder in the program's folder when the service account has no home, as Linux services don't; it goes beside the server's logs.
- `scripts/build-release.sh` had a syntax error and couldn't run.
- A leader whose lease loop stalled (a starved thread pool or a hung connection) kept counting itself the leader, beside whichever instance took the lock next. Leadership now lapses unless confirmed within 20 seconds, and confirming asks PostgreSQL whether this session still holds the lock rather than whether any query answers.
- The Cabinet engine stored list-valued fields (the IP addresses and quoted strings the generic parser finds) as the text "System.Collections.Generic.List`1[System.String]"; it stores them as JSON, as the other backends do.
- A search could be cached without a write made in the same clock tick as it started, so it showed one event too few until the cache expired.


## [3.0.0] - 2026-11-13

It has breaking changes to search semantics and single sign-on, listed under Changed.

**License:** from 3.0, Cabinet is licensed under the AGPL-3.0, except the forwarding agent, which stays Apache-2.0. Running Cabinet for your own organization carries no obligations; offering a modified server to others over a network means offering them the modified source. Earlier releases remain Apache-2.0.

### Added

- **Ask in plain words**: the search box, CLI and API read questions in any word order into BQL, offline: times (*since yesterday*, *between 2pm and 4pm*), levels, hosts (*on web servers*), fields compared every which way (*status is at least 500*, *over 1000 ms*, *404s*, *5xx*), counts and measures (*how many unique users per day*, *p95 latency per host*), grouping, charts over time, most and least common values, columns, sorting and limits. Field names come from your data, typos are fixed, and what ran is always shown in BQL and in words with one-click alternatives. A question it can't read isn't run; it comes back with choices and the AI assistant. Strict BQL and SPL run as written, and saved searches, alerts and rules never reinterpret.
- **Search help in the dashboard**: a live preview of what will run, problems underlined where they are with one-click fixes, suggestions of values from your data, a Build panel to pick a search, and starter questions fitted to your hosts and fields. API: `POST /query/understand`, `GET /query/complete`, `GET /query/examples`, `GET /query/catalog`, and `exact` on `/query/execute`.
- **`cabinet query`** reads plain words too, printing what it understood to stderr; `--exact` runs the query as typed.
- **Move to the Cabinet engine from the dashboard**: Settings, Storage copies events, settings, rules, keys and history from SQLite or PostgreSQL into the engine while Cabinet keeps running, then switches, leaving the old database as it was to switch back to. The engine can be moved off the same way.
- **Staged agent rollouts**: `Updates:AgentHosts` lists which agents take the server's agent version, such as a test machine first.
- **`/metrics` can require a key** (`Metrics:RequireKey`, on for new installs): any key above Ingest, as `X-API-Key` or a bearer token, except from the server itself and `Metrics:AllowFrom`.
- **Release candidates**: tags like `v3.0.0-rc.1` publish as pre-releases, which update checks never offer, and agents on one move to the final release.
- **Search the archive where it is**: `POST /api/v1/archive/search` runs BQL, SPL or plain words over an index's archived days, reading only the days in range, with the caller's access and masking, and no rehydrating.
- **ClickHouse storage** (`Storage:Type=ClickHouse`): events in a ClickHouse server over its HTTP interface, every value a typed parameter and every time exact to the tick, with counts, timecharts, `top` and `rare` computed in ClickHouse. It passes the same cross-backend suites as the others, and CI tests it against ClickHouse 24.8.
- **The Cabinet engine** (`Storage:Type=Cabinet`): Cabinet's own storage for one server, with no database to run. Events go to a write-ahead log, synced before they're acknowledged, then into compressed column segments with per-block summaries and word and ID filters, merged in the background. On a million events it counts everything in 0.1 ms, counts by host in about 0.1 s and computes an average and 95th percentile by host in about 0.2 s, against 4.6 s and 14 s on SQLite, stores 120,000 events a second from one writer, and takes 50 MB where SQLite takes 838 MB. Every `stats` and `timechart` aggregate runs in the engine, percentiles exactly. See BENCHMARKS.md.
- **Search syntax as in SPL**: OR, AND, NOT or a leading `-`, parentheses, `"quoted phrases"` that must appear in order, `wild*cards` that start at a word, and `field=value` terms (with `!=`, `<`, `>` and wildcards) mixed with words, as in `find error OR host=web-1`. Storage still does the AND of plain words; the rest runs in the pipeline. `earliest=-1d@d latest=now` in a search sets its time range, and `@`, `:` and `#` are allowed in searches, so `find alice@example.com` works.
- **stats functions**: `values`, `list`, `first`, `last`, `earliest`, `latest`, `range` and `mode`, and any aggregate of an expression, as in `count(eval(status >= 500))`. **eval functions**: `in()`, `cidrmatch()`, `mvzip()`, `mvrange()` and `json_keys()`.
- **`cluster`**: groups lines into patterns with the Drain algorithm, one row per pattern with its count and a sample, or `labelonly=true` to label every event.
- **`iplocation`**: City, Region, Country, lat and lon from a MaxMind GeoLite2 or GeoIP2 City database you point `GeoIp:DatabasePath` at.
- **Charts**: dashboard panels can be pie, gauge, heatmap or map (points on a latitude and longitude grid).
- **Live tail**: highlight words, pause, and pause automatically on a highlighted line, with events kept while paused.
- **Browser sign-in with OIDC, SAML or LDAP**: the dashboard signs people in with their organization's account (OIDC authorization code with PKCE, SAML 2.0 as service provider, or an LDAP or Active Directory bind), mapping groups to roles through `Sso:RoleMappings`. The session cookie is HttpOnly and SameSite=Strict, and cookie requests that change anything need the page's own header.
- **AI investigator** (off by default): ask a question; a language model answers by running searches with your access and shows every search it ran. Local first: by default it uses an OpenAI-compatible server such as Ollama; Claude is optional. What the model sees is masked as for a Viewer, at most 50 rows per search and 8 searches per question, and every question and search is audited.
- **Anomaly rules**: spikes against a learned baseline, something going quiet, a value never seen before, and a kind of log line never seen before, checked as each window ends.
- **Connectors**: Slack, Teams, Discord, PagerDuty, Opsgenie, Jira, ServiceNow, email and webhooks, used by detection rules' "Send to connector" action, with secrets write-only.
- **Sigma import and ATT&CK coverage**: Sigma rules convert to detection rules (selections, filters, value modifiers, `1 of`/`all of`, and `count() by`), added turned off. The ATT&CK Coverage page shows which techniques the rules cover and where the gaps are.
- **Cases**: investigations that gather alerts, events, searches and notes, with a timeline that shows each reader only the events they may read.
- **Archive and rehydrate**: retention can archive an index's events to a directory or S3 before deleting them (the retention dialog's "Archive Before Delete" now works), and archived events can be brought back into an index to search.
- **Detection rules as code**: rules as YAML files with tests in them; `cabinet rules test`, `cabinet rules push` and `cabinet rules pull`.
- **Ingest pipeline**: rules that drop, sample (by event ID, so resends agree), route or redact events before they're stored or detected, with how much they kept out of storage.
- **Agent inputs**: the systemd journal, Windows event logs (EventData values become fields, as Sigma rules expect) and Kubernetes container logs (CRI and Docker formats, with pod, namespace and container fields), all checkpointed only once a batch is durable.
- **Scheduled reports**: searches emailed on a schedule as HTML with a PDF copy.
- **Player moderation** (gaming extension): player profiles with chat, toxicity, admin history and a suggested action with the SourceMod command to type. Cabinet never runs RCON commands.
- `cabinet query --server` runs a query on a Cabinet server, with the key from `CABINET_API_KEY`.

### Changed

- API keys are compared with every stored key in constant time.
- **Breaking: `SSO:AutoProvision` is off by default.** Someone no role mapping matches can't sign in, where before they got `SSO:DefaultRole`, which let every account the identity provider or directory knows search every index. Turn it on to keep the old behavior.
- Dashboard sign-ins end after `SSO:MaxSessionHours` (24) however much they're used, so a role taken away at the identity provider takes effect. Everyone signs in again once after upgrading.
- Reading cases needs Search on every index, since notes and searches can quote any of them, and adding to a case needs Alerts.
- Ingest keys no longer have the system health view: the agent list with hosts and versions, and index retention.
- Agents update themselves only over https with the server's certificate verified or pinned. Over http, or with `VerifySsl` off and no pin, they keep their version and log why.
- A server running a release candidate is offered the newest release, candidates included.
- `/api/health/version` gives the version as released, such as `3.0.0-rc.3`.
- Installers: the Windows agent installer requires a key (it fell back to a development key), takes `-CertificateSha256`, and makes its settings and spool readable by administrators only; the Linux agent installer takes `--pin`; the systemd units set a umask, so spooled copies of logs aren't readable by other users; the Windows server installer locks down its database and backup folders.
- **Breaking: `/query/translate` is gone**, with the rule-based translator. `/query/understand` replaces it. The query engine no longer guesses at English: interactive searches are read before they reach it, and saved searches, alerts and rules run exactly as written.
- **Breaking: comparisons with a missing value are false**, for `!=` too, as in SPL: `where x != "a"` drops events without `x`. Use `isnull()` to find those.
- **Breaking: `stats ... by` leaves out events missing a by-field**, as in SPL, both in the pipeline and when storage aggregates.
- **Breaking: quoted search text is a phrase**, matching its words in order, as in SPL, rather than its words anywhere.
- **Breaking: a subsearch, join, multisearch leg, macro, lookup or unknown stage that fails is an error** instead of quietly giving a different answer. Subsearch rows mean "all of these fields", OR'd across rows, as in SPL.
- `if`, `case`, `validate` and `coalesce` evaluate only the branch they take, and `"false"` and `"0"` from a log are false in a condition.
- `find error host=web` searches for both instead of for the text `host=web`. timechart uses `_time` when eval or bin changed it. `where _time` comparisons narrow what storage reads.
- Detection actions run off the ingest path on a bounded queue, each with a timeout, so a slow webhook can't hold up ingest.
- Ingest into an index is refused unless the caller may ingest there, even without a principal. Event IDs a sender supplies are kept apart per sending key.
- `PUT /api/v1/rules/{id}` adds a rule under a new ID instead of answering 404.
- OpenSearch indexes get one shard, replicas only with several nodes, and a 10,000 field limit, all settings; several node URLs share requests.
- PostgreSQL retention and index deletes run 10,000 rows at a time.

### Fixed

- A query with a very long chain of operators (`eval x=1+1+...`) or search text nested thousands of parentheses deep overflowed the stack and stopped the server. Expressions deeper than 1,000 and search parentheses deeper than 100 are now a parse error; use `IN (...)` for long lists of values.
- `LIKE`, search wildcards and phrases match without backtracking, so a padded log line can't make them slow. A slow regular expression in `match()`, `replace()` or `mvfind()` fails the query instead of quietly not matching; in a detection rule it's counted and logged, and the configured timeouts apply.
- Plain-word questions compared every word with every field name letter by letter, so a huge field name from a log and a huge word took hundreds of megabytes. Long words aren't fuzzy-matched, and field names over 128 characters stay out of the field list.
- Cabinet engine: index names differing only in case shared a folder on Windows and macOS; a retention purge under steady ingest never finished and blocked merges, and rewrote segments while holding up writes and searches; a damaged segment stopped the server starting instead of being set aside; a merge could close a segment under a slow search; and events without an index showed none until written to a segment.
- With the gaming extension on, a log line of many unclosed brackets took the SourceMod parser seconds to minutes of CPU. Parser regular expressions on log text all have timeouts or don't backtrack, including multi-line merge patterns from settings.
- A small Sigma file could take tens of gigabytes through YAML aliases, or stop the server through deep nesting. Aliases are refused, and nesting, size and the number of conditions are limited.
- Archive folders: index names that a folder can't hold as they are (such as `.` or `a b`) were folded together, so one index's archive could hold another's, and rehydrate didn't check which index each event came from. Such names are refused, and rehydrate and archive search skip other indexes' events.
- ClickHouse field filters matched field names only in their stored case, and missed values with accents or `< > +`, unlike the other backends.
- Moving off the Cabinet engine opened a second engine on the running one's folder, which replayed and deleted its write-ahead log. The move now reads through the running engine.
- A write to the engine's log cancelled or failed partway left half a record, and recovery dropped every batch after it. Records are written whole, and the log is cut back after a failed write.
- A release candidate and its release were downloaded into the same folder, so updating to the release could reinstall the candidate, and agents could be offered it in a loop.
- Outbound calls (webhook actions, connectors, the settings webhook) went through a configured proxy, which reached private addresses the guard would refuse, and NAT64 and 6to4 addresses weren't counted as private.
- A sign-in return path with a tab or line break in it could send people to another site after signing in.
- The HTTP Event Collector read one event per line, so events sent back to back (`{...}{...}`), as Splunk accepts and its logging libraries send, were refused.
- The pre-authentication rate limit (300 requests a minute per address) counted requests with a valid key, so a busy forwarder on one address was throttled. Only requests that end unauthenticated count now; an address over the limit is still refused before its key is looked at.
- Every downloaded update was kept, a few hundred megabytes each: 3.5 GB after nine updates on one server. Only the running version and the newest download stay.
- Agent packages were downloaded again by every agent that asked at once, and after a failure on every request.
- Whole numbers in fields read back from PostgreSQL and OpenSearch came back as decimals (4 as 4.0), since a conditional boxed them as `double`.
- Webhook actions and connectors can't reach private, loopback or link-local addresses unless allowed, and never follow redirects. Actions see events masked as for a Viewer, and an action that writes an event needs ingest on that index. Rule changes are audited.
- Data-model acceleration divided by zero for buckets under an hour, read at most 100,000 events silently, and instances that weren't the leader overwrote its summaries.
- The leader lease loop ended on an unexpected error and never led again, and a hung connection kept leadership.
- The query cache was never invalidated by writes, and had no size cap. Searches answered from it are audited.
- streamstats without a window rescanned every earlier row for each row. Stages after the first stateful one ignored the query's limits. A number too large for `head` crashed the query.
- CloudWatch timestamps were read in the server's time zone.
- The webhook channel logged URLs, which often hold tokens.

## [2.5.4] - 2026-11-11

### Fixed

- The PostgreSQL search memory (2.5.3) is now set on the database (`ALTER DATABASE ... SET work_mem`) instead of per connection, because a plain `SET` is wiped when a pooled connection is reused, so broad searches fell back to slow after the first query.
- The Windows in-app updater no longer rolls back a good update. A large single-file service can take longer than Windows' 30-second service-control start timeout to come up; the updater treated that timeout as a failure and restored the previous version. It now checks the health endpoint to decide, and starts the service again if the first start reports a timeout.

## [2.5.3] - 2026-11-11

### Changed

- PostgreSQL searches now run each connection with a higher `work_mem` (default 256 MB, set by `Storage:PostgresWorkMemMb`). This is the real fix for slow broad searches: with the default 4 MB, the match bitmap turned lossy and Postgres rechecked by recomputing the text vector for every row on each matched page, so a search over a large result set took close to a minute. With enough memory the bitmap stays exact and there's no recheck.
- This replaces the stored search-vector column added in 2.5.0–2.5.2, which needed a long one-time backfill on existing data for no real gain over the `work_mem` fix (both end up reading the same heap). New databases no longer add that column, trigger or background fill. A database that already has the column from 2.5.0–2.5.2 is unaffected and the column can be dropped; searches use the raw-line index as before.

## [2.5.2] - 2026-11-11

### Fixed

- The background fill of the PostgreSQL search column now walks the table by primary key, so each row is read once. It had searched for unfilled rows from the start of the table every batch, which grew slower as the fill progressed. On a large table this is the difference between finishing in minutes and taking hours.

## [2.5.1] - 2026-11-11

### Fixed

- The background fill of the PostgreSQL search column (2.5.0) stopped on the first batch on a large table, because filling a batch and building the index take longer than the default command timeout. Those commands now run without a timeout, in smaller batches, on a single connection, and the coordinating lock is always released. Searches kept working through the old path throughout.

## [2.5.0] - 2026-11-11

### Changed

- PostgreSQL full-text search is much faster on large datasets. It used to recompute each line's search vector for every matching row, so a broad search over tens of millions of events could take close to a minute and time out. The vector is now stored in a column, kept current by a trigger, and indexed, so the search reads the index directly.
  - New databases use it from the first event.
  - A database upgraded with existing events fills the column in the background on the first start after upgrading, one bounded batch at a time, and searches keep working through the old path until it's done. Only one instance runs the fill, and it resumes if interrupted. When it finishes, searches switch over and the old index is dropped to reclaim space.

## [2.4.0] - 2026-11-11

### Added

- Detection rules can now carry **actions** that run when they fire, configurable in the dashboard (Detections) or through the API. A rule with no actions notifies as before. The actions are:
  - **notify** — through every configured channel.
  - **event** — write a new event into an index (default `detections`), searchable like any log and able to feed dashboards or even other detections. Extra fields are templated.
  - **webhook** — POST the firing as JSON to a URL.
  Each action can override severity and template its message and fields with `{rule} {group} {count} {host} {event_id} {line}` and more. Emitted events are written below the detection layer, so a detection can raise an event without re-triggering detection.

## [2.3.6] - 2026-11-10

### Added

- `cabinet-agent import` sends logs that already exist, once: the history an agent leaves out (older than `IgnoreOlderThanHours`), or any folder, file or glob from any machine. Each event's ID comes from the host, the file's name and first line, and the line's position, so importing again stores nothing twice, and files the running agent reads are left out. It reads one file at a time at a set rate (`--rate`, default 2000 events a second), and `--dry-run` shows what it would send.

## [2.3.5] - 2026-11-10

### Fixed

- `replace` wrote `\1` literally instead of the first captured group, the form Splunk users write; only .NET's `$1` worked. Both work now, and `\\` is a backslash.

## [2.3.4] - 2026-11-09

This release includes everything planned for 2.3.3, which was never published.

### Fixed

- On Linux, the agent couldn't read a log file while the program writing it held a lock on it, as Source game servers do, so lines arrived only once that program closed the file: a map late on a busy server, and up to a day late for a daily log. The agent no longer takes file locks, since it only reads. Found on the L4D2 servers, where two of three game servers' logs were arriving the next morning.
- The agent now warns when a file stays unopenable for a minute, instead of retrying silently.

## [2.3.2] - 2026-11-09

### Fixed

- `min` and `max` of a time, such as `stats max(_time) by host`, compared times as text, so 9:59 PM came after 10:16 PM, and returned them as text. They now compare times and return a time.
- Searching for a value with a hyphen without quotes, such as `find host=game-dev`, found nothing, because the search was rebuilt as `host = l4d2 - dev`. Search text now keeps the spacing as typed.
- Running the Windows installer again without `-AllowFrom` removed the firewall rule, cutting off every agent. It now keeps the addresses already allowed.
- The PostgreSQL user the Windows installer creates couldn't create databases, so "Create the database" in Settings, Storage failed on it.
- After moving storage, the Windows installer now records the new database in its own settings too, so Cabinet can't fall back to the old one if the dashboard's settings file goes missing.

## [2.3.1] - 2026-11-09

This release includes everything planned for 2.3.0, which was never published.

### Fixed

- Several instances starting together with encryption on and no key file yet could each create their own key on Linux, leaving events that the other instances couldn't decrypt. Exactly one instance now creates the key, and the others use it.

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
