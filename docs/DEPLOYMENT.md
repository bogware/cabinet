# Cabinet Deployment Guide

This guide covers deploying Cabinet in production environments.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Deployment Options](#deployment-options)
- [Docker Deployment](#docker-deployment)
- [Kubernetes Deployment](#kubernetes-deployment)
- [Configuration Reference](#configuration-reference)
- [Storage Backend Setup](#storage-backend-setup)
- [Security Hardening](#security-hardening)
- [Monitoring & Observability](#monitoring--observability)
- [Backup & Recovery](#backup--recovery)
- [Scaling Guide](#scaling-guide)
- [Troubleshooting](#troubleshooting)
- [Runbooks](#runbooks)

## Prerequisites

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| CPU | 2 cores | 4+ cores |
| RAM | 2 GB | 8+ GB |
| Disk | 20 GB SSD | 100+ GB SSD |
| .NET Runtime | 10.0 | 10.0 (latest patch) |
| Docker | 20.10+ | 24.0+ |
| Kubernetes | 1.25+ | 1.28+ |

### Storage Backend Requirements

| Backend | Additional Requirements |
|---------|----------------------|
| SQLite | None (embedded) |
| PostgreSQL | PostgreSQL 14+ with 2+ GB RAM |
| OpenSearch | OpenSearch 2.x cluster with 4+ GB RAM per node |
| Redis (distributed) | Redis 7.0+ with 512+ MB RAM |

## Deployment Options

### Option 1: Docker Compose (Simplest)

Best for: Small teams, single-server deployments, evaluation.

```bash
# Production with PostgreSQL
docker-compose up -d

# With monitoring (Grafana + Prometheus)
docker-compose --profile monitoring up -d
```

### Option 2: Kubernetes (Production)

Best for: Teams, high availability, auto-scaling, multi-tenant.

### Option 3: Bare Metal / VM

Best for: Air-gapped environments, maximum control.

```bash
dotnet publish src/Cabinet.API -c Release -o /opt/cabinet
cd /opt/cabinet && dotnet Cabinet.API.dll
```

## Docker Deployment

### Quick Start

```bash
# Clone and start
git clone https://github.com/bogware/cabinet.git
cd cabinet
docker-compose up -d

# Verify
curl http://localhost:8080/health
# Expected: {"status":"Healthy"}
```

### Production docker-compose.yml

```yaml
# Use the included docker-compose.yml with these overrides:
# docker-compose.override.yml
services:
  cabinet-api:
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Storage__Type=PostgreSQL
      - Storage__ConnectionString=Host=postgres;Port=5432;Database=cabinet;Username=cabinet;Password=${DB_PASSWORD}
      - ApiKeys__Keys__0__Key=${API_KEY_HASH}
      - ApiKeys__Keys__0__ClientName=production-client
      - ApiKeys__Keys__0__Tier=Professional
    restart: unless-stopped
    deploy:
      resources:
        limits:
          cpus: '4'
          memory: 4G
        reservations:
          cpus: '1'
          memory: 1G
```

### Environment Variables

```bash
# Required for production
ASPNETCORE_ENVIRONMENT=Production
Storage__Type=PostgreSQL              # or OpenSearch
Storage__ConnectionString=<connection-string>

# API Keys (at least one required)
ApiKeys__Keys__0__Key=<sha256-hash-of-key>
ApiKeys__Keys__0__ClientName=<client-name>
ApiKeys__Keys__0__Tier=Professional   # Basic, Professional, or Enterprise

# Optional: Distributed mode
Distributed__Enabled=true
Distributed__RedisConnectionString=redis:6379
Distributed__InstanceId=${HOSTNAME}

# Optional: Encryption at rest
Encryption__Enabled=true
CABINET_ENCRYPTION_KEY=<base64-encoded-32-byte-key>

# Optional: Syslog listener
Syslog__Enabled=true
Syslog__TcpPort=1514
Syslog__UdpPort=1514
```

### Generating API Key Hashes

```bash
# Generate a random API key
API_KEY=$(openssl rand -hex 32)
echo "API Key (give to client): $API_KEY"

# Generate SHA-256 hash (store this in config)
echo -n "$API_KEY" | sha256sum | cut -d' ' -f1
```

## Kubernetes Deployment

### Prerequisites

```bash
# Verify cluster access
kubectl cluster-info
kubectl get nodes

# Create namespace
kubectl create namespace cabinet
```

### Step-by-Step Deployment

#### 1. Create Secrets

```bash
# Database password
kubectl create secret generic cabinet-secrets \
  --namespace cabinet \
  --from-literal=db-password='<your-db-password>' \
  --from-literal=api-key-hash='<sha256-hash>' \
  --from-literal=encryption-key='<base64-key>'

# Docker registry credentials (if private registry)
kubectl create secret docker-registry regcred \
  --namespace cabinet \
  --docker-server=<registry> \
  --docker-username=<user> \
  --docker-password=<password>
```

#### 2. Apply Manifests

```bash
# Apply all manifests
kubectl apply -f k8s/

# Verify deployment
kubectl -n cabinet get pods
kubectl -n cabinet get svc
kubectl -n cabinet get ingress
```

#### 3. Verify Health

```bash
# Port-forward for testing
kubectl -n cabinet port-forward svc/cabinet-api 8080:80

# Test health endpoint
curl http://localhost:8080/health

# Test with API key
curl -H "X-API-Key: <your-key>" http://localhost:8080/api/v1/logs/stats
```

### Kubernetes Manifests

The `k8s/` directory contains:

| File | Purpose |
|------|---------|
| `deployment.yaml` | API deployment (2 replicas, rolling update, probes) |
| `service.yaml` | ClusterIP service + ConfigMap + Namespace |
| `hpa.yaml` | Horizontal Pod Autoscaler (CPU/memory based) |
| `pdb.yaml` | Pod Disruption Budget (min 1 available) |
| `network-policy.yaml` | Network isolation (ingress + egress rules) |
| `ingress.yaml` | External access with TLS termination |
| `pvc.yaml` | Persistent storage for data and logs |

## Configuration Reference

### Storage Configuration

```json
{
  "Storage": {
    "Type": "PostgreSQL",
    "ConnectionString": "Host=localhost;Port=5432;Database=cabinet;Username=cabinet;Password=secret",
    "RetentionDays": 90,
    "MaxEvents": 10000000,
    "IndexPrefix": "cabinet-logs"
  }
}
```

### Rate Limiting

```json
{
  "RateLimiting": {
    "Basic": { "PermitLimit": 100, "Window": "00:01:00" },
    "Professional": { "PermitLimit": 1000, "Window": "00:01:00", "AutoReplenishment": true },
    "Enterprise": { "PermitLimit": 10000, "Window": "00:01:00", "QueueLimit": 100 }
  }
}
```

### Distributed Mode

```json
{
  "Distributed": {
    "Enabled": true,
    "RedisConnectionString": "redis:6379,abortConnect=false",
    "InstanceId": "cabinet-pod-1",
    "LeaderElection": {
      "Enabled": true,
      "LeaseTimeSeconds": 30
    },
    "Cache": {
      "ApiKeyTtlSeconds": 60
    }
  }
}
```

### Encryption at Rest

```json
{
  "Encryption": {
    "Enabled": true,
    "KeySource": "environment",
    "KeyRotationDays": 90
  }
}
```

Key source options:
- `environment` — Read from `CABINET_ENCRYPTION_KEY` env var
- `file` — Read from file path specified in `KeyFilePath`

### RBAC Configuration

```json
{
  "Rbac": {
    "Enabled": true,
    "DefaultRole": "Viewer",
    "Users": [
      {
        "ApiKeyHash": "<hash>",
        "Role": "Admin",
        "IndexPermissions": [
          { "Pattern": "*", "Capabilities": ["Search", "Ingest", "Export"] }
        ]
      }
    ]
  }
}
```

## Security Hardening

### Production Checklist

- [ ] Set `ASPNETCORE_ENVIRONMENT=Production`
- [ ] Configure real API keys (not dev keys)
- [ ] Enable HTTPS (TLS 1.2+)
- [ ] Set restrictive CORS origins
- [ ] Enable RBAC with least-privilege roles
- [ ] Configure audit logging retention
- [ ] Enable encryption at rest
- [ ] Set PII masking rules for sensitive fields
- [ ] Remove default/dev API keys from config
- [ ] Configure rate limiting per tier
- [ ] Set request size limits (default 50MB)
- [ ] Enable response compression (disable over HTTPS if BREACH is a concern)
- [ ] Run as non-root user (UID 1000 in Docker)
- [ ] Use read-only root filesystem where possible
- [ ] Mount secrets from K8s Secrets, not ConfigMaps

### TLS Configuration

```bash
# Generate self-signed cert (dev only)
dotnet dev-certs https --export-path ./cert.pfx --password <password>

# Production: Use cert-manager in K8s or load balancer TLS termination
```

## Monitoring & Observability

### Health Endpoints

| Endpoint | Purpose | Auth Required |
|----------|---------|---------------|
| `GET /health` | Basic health status | No |
| `GET /health/ready` | Readiness (storage connected) | No |
| `GET /health/live` | Liveness (always 200) | No |
| `GET /health/version` | API version info | No |
| `GET /health/metrics` | Memory, GC, request stats | Yes |
| `GET /health/status` | Detailed service health | Yes |

### Structured Logging

Cabinet uses Serilog with structured JSON output. Configure sinks in `appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console", "Args": { "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}" } },
      { "Name": "File", "Args": { "path": "logs/cabinet-.log", "rollingInterval": "Day", "retainedFileCountLimit": 30, "fileSizeLimitBytes": 104857600 } }
    ]
  }
}
```

### Key Metrics to Monitor

| Metric | Warning | Critical |
|--------|---------|----------|
| API response time (p95) | >500ms | >2000ms |
| Error rate (5xx) | >1% | >5% |
| Storage latency | >100ms | >500ms |
| Memory usage | >70% limit | >90% limit |
| CPU usage | >70% | >90% |
| Active connections | >80% pool | >95% pool |
| Queue depth (rate limiter) | >50% | >80% |

## Backup & Recovery

### SQLite Backup

```bash
# Online backup (safe while the server is writing)
sqlite3 cabinet.db ".backup /backups/cabinet-backup.db"
```

### PostgreSQL Backup

```bash
# pg_dump (recommended)
pg_dump -h localhost -U cabinet -d cabinet -F c -f /backups/cabinet-$(date +%Y%m%d).dump

# Restore
pg_restore -h localhost -U cabinet -d cabinet /backups/cabinet-20240115.dump
```

### OpenSearch Backup

```bash
# Register snapshot repository
curl -X PUT "http://opensearch:9200/_snapshot/cabinet-backups" -H 'Content-Type: application/json' -d '{
  "type": "fs",
  "settings": { "location": "/backups/opensearch" }
}'

# Create snapshot
curl -X PUT "http://opensearch:9200/_snapshot/cabinet-backups/snapshot-$(date +%Y%m%d)"
```

### Recovery Procedures

See [Runbooks](#runbook-disaster-recovery) below.

## Scaling Guide

### Vertical Scaling

| Load | CPU | RAM | Storage |
|------|-----|-----|---------|
| <1K events/sec | 2 cores | 2 GB | 50 GB SSD |
| 1-10K events/sec | 4 cores | 8 GB | 200 GB SSD |
| 10-50K events/sec | 8 cores | 16 GB | 1 TB SSD |
| 50K+ events/sec | 16 cores | 32 GB | Distributed storage |

### Horizontal Scaling (Kubernetes)

```yaml
# HPA scales based on CPU/memory
# Minimum: 2 replicas (HA)
# Maximum: 10 replicas (adjust based on load)

# Distributed mode required for >1 replica
Distributed:
  Enabled: true
  RedisConnectionString: redis:6379
```

### Storage Scaling Path

```
Development:    SQLite (single file, zero config)
     ↓
Small Team:     PostgreSQL (single node, connection pooling)
     ↓
Growth:         PostgreSQL + table partitioning by month
     ↓
Enterprise:     OpenSearch cluster (2+ nodes, sharded indices)
     ↓
Large Scale:    OpenSearch + hot/warm/cold tiering + S3 archival
```

## Troubleshooting

### Common Issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Connection refused` on port 8080 | API not started or wrong port | Check `docker-compose ps` or `kubectl get pods` |
| `401 Unauthorized` | Invalid API key | Verify key hash matches config |
| `429 Too Many Requests` | Rate limit exceeded | Upgrade tier or wait for window reset |
| `503 Service Unavailable` | Storage backend down | Check PostgreSQL/OpenSearch health |
| Slow queries (>5s) | Large result set or missing index | Add `\| limit 1000` or check storage indexes |
| High memory usage | Large query results in memory | Reduce query limit, check for unbounded queries |
| Leader election flapping | Redis connectivity issues | Check Redis connectivity and lease time |

### Debug Mode

```bash
# Enable debug logging
ASPNETCORE_ENVIRONMENT=Development
Serilog__MinimumLevel__Default=Debug

# Check API health details
curl -H "X-API-Key: <key>" http://localhost:8080/health/status
```

## Runbooks

### Runbook: Deployment

```
1. Build Docker image:
   docker build -t cabinet-api:v1.1.0 -f Dockerfile.api .

2. Push to registry:
   docker tag cabinet-api:v1.1.0 <registry>/cabinet-api:v1.1.0
   docker push <registry>/cabinet-api:v1.1.0

3. Update K8s deployment:
   kubectl -n cabinet set image deployment/cabinet-api cabinet-api=<registry>/cabinet-api:v1.1.0

4. Monitor rollout:
   kubectl -n cabinet rollout status deployment/cabinet-api

5. Verify health:
   curl http://<ingress-host>/health

6. If issues, rollback:
   kubectl -n cabinet rollout undo deployment/cabinet-api
```

### Runbook: Disaster Recovery

```
1. Assess damage:
   - Check pod status: kubectl -n cabinet get pods
   - Check storage: kubectl -n cabinet exec <pod> -- curl localhost:8080/health/status

2. If storage corrupted:
   a. Stop API pods: kubectl -n cabinet scale deployment/cabinet-api --replicas=0
   b. Restore from backup (see Backup & Recovery section)
   c. Restart: kubectl -n cabinet scale deployment/cabinet-api --replicas=2

3. If data loss:
   a. Identify last good backup
   b. Restore to new database
   c. Update connection string in ConfigMap
   d. Restart pods

4. Post-incident:
   - Review audit logs for root cause
   - Document incident and resolution
   - Update backup frequency if needed
```

### Runbook: Scaling Up

```
1. Monitor current load:
   curl -H "X-API-Key: <key>" http://localhost:8080/health/metrics

2. If CPU > 80% sustained:
   kubectl -n cabinet scale deployment/cabinet-api --replicas=<current+2>
   # Or let HPA handle it (if configured)

3. If storage latency > 500ms:
   a. Check backend health (PostgreSQL connections, OpenSearch cluster)
   b. Consider upgrading storage tier
   c. Add read replicas if supported

4. If memory > 90%:
   a. Check for unbounded queries (add limits)
   b. Increase pod memory limits
   c. Review query patterns for optimization
```

### Runbook: API Key Rotation

```
1. Generate new key:
   NEW_KEY=$(openssl rand -hex 32)
   NEW_HASH=$(echo -n "$NEW_KEY" | sha256sum | cut -d' ' -f1)

2. Add new key to config (keep old key active):
   kubectl -n cabinet edit secret cabinet-secrets
   # Add new key hash alongside old one

3. Distribute new key to clients

4. Monitor for old key usage in audit logs

5. After all clients migrated, remove old key:
   kubectl -n cabinet edit secret cabinet-secrets
   # Remove old key hash

6. Restart pods to pick up config change:
   kubectl -n cabinet rollout restart deployment/cabinet-api
```
