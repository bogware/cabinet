# Security Policy

## Reporting a vulnerability

Report vulnerabilities privately through GitHub: <https://github.com/bogware/cabinet/security/advisories/new>

Please don't open a public issue for a suspected vulnerability. Include the affected component and version, steps to reproduce, and the impact you expect. You'll get an acknowledgement within a few days, and we'll agree a disclosure date with you once there's a fix. Reports that turn out to be valid are credited in the advisory unless you'd rather not be named.

In scope: the server, dashboard, CLI, agent, installers and the official container image, as shipped. Out of scope: deployments that ignore the guidance below (for example, a server reachable from the internet with default development keys), and denial of service by an authenticated user with the rights to run expensive searches, which query limits bound rather than prevent.

## Supported versions

Security fixes go into the latest release. Earlier releases don't get backported fixes; update to the latest, which the dashboard and `cabinet` CLI can do for you.

## Deployment guidance

- **Terminate TLS at a reverse proxy.** The API container listens on plain HTTP. List the proxy in `Network:TrustedProxies`; otherwise `X-Forwarded-For` is ignored and every client appears to have the proxy's address.
- **Require a key for `/metrics`** with `Metrics:RequireKey` (on for new installs), and list your Prometheus server in `Metrics:AllowFrom` or give it a key as a bearer token. `/health`, `/health/ready` and `/health/live` stay open, so restrict them at the network if their answers matter to you.
- **Treat API keys as secrets.** Generate them with `openssl rand -hex 32` and pass them through environment variables or a secret store. The configuration value is the raw key, which the API hashes in memory.
- **Keep Postgres off public networks.** The Compose stack doesn't publish them to the host.
- **Protect the data directory and database.** Log contents are stored as ingested, apart from fields you configure for encryption, and returned as stored, apart from fields you configure for masking. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md). Use disk or database encryption as well.
- **Syslog has no TLS or authentication.** It is off by default. Enable it only on a trusted network.
- **Keep audit chain heads elsewhere.** `/api/v1/audit/verify` detects changes and removals, but someone with write access to the database could rewrite the whole chain. The heads in the log output are what would catch that, so ship your logs to separate storage.
