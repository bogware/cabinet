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
- **Protect the data directory and database.** Encryption at rest and field masking are not implemented, so log contents are stored and returned as ingested. Use disk or database encryption if you need it.
- **Syslog has no TLS or authentication.** It is off by default. Enable it only on a trusted network.
- **Don't rely on the audit log as tamper-proof evidence.** It is hash-chained in the log output only, with no verifier. Ship it to separate storage if you need stronger guarantees.
