# Cabinet Docker Deployment Guide

Cabinet can be deployed using Docker for both development and production environments. The solution includes both CLI and API components that can work independently or together.

## Quick Start

### 1. Basic Deployment
```bash
# Build and start all services
docker-compose up -d

# View logs
docker-compose logs -f

# Access the API
curl http://localhost:5000/health
```

### 2. Using the CLI in Docker
```bash
# Interactive CLI session
docker-compose run --rm cabinet-cli

# Run specific command
docker-compose run --rm cabinet-cli query "search ERROR | head 10"

# Ingest logs from host
docker-compose run --rm -v /path/to/logs:/logs cabinet-cli ingest /logs/*.log
```

## Architecture

### Services

#### Cabinet API (`cabinet-api`)
- REST API service exposing all Cabinet functionality
- Runs on port 5000 (HTTP) and 5001 (HTTPS)
- Supports API key authentication with tier-based rate limiting
- Integrates with PostgreSQL, MySQL, and DynamoDB

#### Cabinet CLI (`cabinet-cli`)
- Command-line interface for Cabinet operations
- Can run in local mode (direct file access) or remote mode (API client)
- Configured for remote mode by default in Docker

#### PostgreSQL (`postgres`)
- Primary database for log storage and queries
- Port: 5432
- Credentials: cabinet/cabinet123

#### Redis (`redis`)
- Caching and rate limiting
- Port: 6379

#### Optional Services

##### MySQL (`mysql`)
- Alternative database option
- Enable with: `docker-compose --profile with-mysql up`

##### Monitoring Stack
- Grafana (port 3000) - Visualization
- Prometheus (port 9090) - Metrics collection
- Enable with: `docker-compose --profile monitoring up`

##### LocalStack
- AWS service emulation for development
- Enable with: `docker-compose --profile development up`

## Configuration

### Environment Variables

#### API Configuration
```yaml
ASPNETCORE_ENVIRONMENT: Production
Storage__Type: SQLite|PostgreSQL|MySQL
Database__Providers__0__Enabled: true
ApiKeys__Keys__0__Key: your-api-key
```

#### CLI Configuration
```yaml
CABINET_MODE: local|remote
CABINET_API_URL: http://cabinet-api
CABINET_API_KEY: your-api-key
```

### Volumes

- `cabinet-data`: Persistent data storage
- `cabinet-logs`: Application logs
- `postgres-data`: PostgreSQL data
- `redis-data`: Redis persistence

## Deployment Scenarios

### 1. Development Environment
```bash
# Start with all development tools
docker-compose --profile development --profile monitoring up
```

### 2. Production Environment
```bash
# Use production compose file
docker-compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

### 3. CLI-Only Mode
```bash
# Build CLI image
docker build -f Dockerfile.cli -t cabinet-cli .

# Run locally
docker run -it --rm \
  -v $(pwd)/logs:/logs \
  -v ~/.cabinet:/root/.cabinet \
  cabinet-cli
```

### 4. API-Only Mode
```bash
# Build API image
docker build -f Dockerfile.api -t cabinet-api .

# Run API
docker run -d -p 5000:80 \
  -e ApiKeys__Keys__0__Key=test-key \
  cabinet-api
```

## API Authentication

The API uses API key authentication. Default keys in development:
- `demo-basic-key`: Basic tier (100 req/min)
- `demo-professional-key`: Professional tier (1000 req/min)
- `demo-enterprise-key`: Enterprise tier (10000 req/min)

Example API usage:
```bash
# Health check
curl http://localhost:5000/health

# Ingest logs
curl -X POST http://localhost:5000/api/v1/logs/ingest \
  -H "X-API-Key: demo-enterprise-key" \
  -H "Content-Type: application/json" \
  -d '{"logs": ["[2024-01-01] ERROR: Test log"]}'

# Query logs
curl -X POST http://localhost:5000/api/v1/query/execute \
  -H "X-API-Key: demo-enterprise-key" \
  -H "Content-Type: application/json" \
  -d '{"query": "search ERROR | head 10"}'
```

## Health Checks

All services include health checks:
```bash
# Check API health
curl http://localhost:5000/health

# Check readiness
curl http://localhost:5000/health/ready

# Check liveness
curl http://localhost:5000/health/live
```

## Scaling

### Horizontal Scaling
```bash
# Scale API instances
docker-compose up -d --scale cabinet-api=3
```

### Load Balancing
Add an nginx or HAProxy container for load balancing multiple API instances.

## Monitoring

### Grafana Dashboards
1. Access Grafana at http://localhost:3000
2. Login: admin/admin123
3. Import Cabinet dashboards from `/grafana/dashboards`

### Prometheus Metrics
- API metrics: http://localhost:5000/metrics
- Prometheus UI: http://localhost:9090

## Troubleshooting

### View Logs
```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f cabinet-api
```

### Debug Mode
```bash
# Enable debug logging
docker-compose run -e ASPNETCORE_ENVIRONMENT=Development cabinet-api
```

### Reset Data
```bash
# Stop and remove volumes
docker-compose down -v

# Restart fresh
docker-compose up -d
```

## Security Considerations

1. **Change default passwords** in production
2. **Use secrets management** for API keys
3. **Enable HTTPS** with proper certificates
4. **Restrict network access** using firewall rules
5. **Regular updates** of base images

## Performance Tuning

### Database Optimization
```sql
-- Create indexes for common queries
CREATE INDEX idx_logs_timestamp ON logs(timestamp);
CREATE INDEX idx_logs_severity ON logs(severity);
```

### Redis Configuration
```yaml
redis:
  command: redis-server --maxmemory 2gb --maxmemory-policy allkeys-lru
```

## Backup and Recovery

### Backup
```bash
# Backup PostgreSQL
docker exec cabinet-postgres pg_dump -U cabinet cabinet > backup.sql

# Backup volumes
docker run --rm -v cabinet-data:/data -v $(pwd):/backup \
  alpine tar czf /backup/cabinet-data.tar.gz /data
```

### Restore
```bash
# Restore PostgreSQL
docker exec -i cabinet-postgres psql -U cabinet cabinet < backup.sql

# Restore volumes
docker run --rm -v cabinet-data:/data -v $(pwd):/backup \
  alpine tar xzf /backup/cabinet-data.tar.gz -C /
```

## Support

For issues or questions:
- GitHub Issues: https://github.com/yourusername/cabinet/issues
- Documentation: https://cabinet.io/docs