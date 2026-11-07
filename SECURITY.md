# Security Policy

## Reporting a vulnerability

Report vulnerabilities privately through GitHub: <https://github.com/bogware/cabinet/security/advisories/new>

Please don't open a public issue for a suspected vulnerability. Include the affected component, steps to reproduce, and the impact you expect.

## Supported versions

Only the latest `main` is supported. Fixes are not backported to earlier releases.

## Deployment guidance

- **Terminate TLS at a reverse proxy.** The API container listens on plain HTTP. List the proxy in `Network:TrustedProxies`; otherwise `X-Forwarded-For` is ignored and every client appears to have the proxy's address.
- **Restrict `/metrics` at the network.** It is unauthenticated, as are `/health`, `/health/ready` and `/health/live`.
- **Treat API keys as secrets.** Generate them with `openssl rand -hex 32` and pass them through environment variables or a secret store. The configuration value is the raw key, which the API hashes in memory.
- **Keep Postgres off public networks.** The Compose stack doesn't publish them to the host.
- **Protect the data directory and database.** Log contents are stored as ingested, apart from fields you configure for encryption, and returned as stored, apart from fields you configure for masking. See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md). Use disk or database encryption as well.
- **Syslog has no TLS or authentication.** It is off by default. Enable it only on a trusted network.
- **Keep audit chain heads elsewhere.** `/api/v1/audit/verify` detects changes and removals, but someone with write access to the database could rewrite the whole chain. The heads in the log output are what would catch that, so ship your logs to separate storage.
