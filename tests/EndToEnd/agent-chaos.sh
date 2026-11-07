#!/bin/bash
# Runs the agent chaos test against the real compose stack: 30,000 lines written while the API, the
# agent and Postgres are killed and files rotate. Passes only if every line is stored exactly once.
#
# It found three bugs that unit tests didn't, all timing-dependent, so run it several times:
#   ./tests/EndToEnd/agent-chaos.sh 5
#
# Uses port 5000 and ./logs, and removes the stack's volumes when done.
set -u
cd "$(dirname "$0")/../.."

runs="${1:-1}"
project=cabinet-chaos
export CABINET_API_KEY=chaos-test-key
export CABINET_DB_PASSWORD=chaos-test-db
export CABINET_REDIS_PASSWORD=chaos-test-redis
export CABINET_GRAFANA_PASSWORD=chaos-test-grafana
export PYTHONUNBUFFERED=1

cleanup() {
  docker compose -p "$project" down -v > /dev/null 2>&1
  rm -rf logs
}
trap cleanup EXIT

docker compose -p "$project" build cabinet-api cabinet-agent cabinet-cli || exit 1

failures=0
for run in $(seq 1 "$runs"); do
  echo "=== Run $run of $runs"
  cleanup
  mkdir -p logs
  docker compose -p "$project" up -d || exit 1

  for _ in $(seq 1 60); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' cabinet-api 2>/dev/null)" = healthy ] && break
    sleep 2
  done

  python3 tests/EndToEnd/agent-chaos.py || failures=$((failures + 1))
done

echo "$failures of $runs runs failed"
[ "$failures" -eq 0 ]
