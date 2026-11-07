#!/usr/bin/env bash
# Soak test: two API instances on one PostgreSQL database under steady ingest and queries, with
# instances killed and restarted along the way. Passes if every acknowledged event is stored once,
# few requests fail, and memory doesn't keep growing. Needs Docker and a Release build.
#
#   ./tests/EndToEnd/soak.sh [duration] [events per second] [seconds between kills, 0 for none]
#   ./tests/EndToEnd/soak.sh 2h 2000 600
set -euo pipefail

cd "$(dirname "$0")/../.."
root="$(pwd)"
duration="${1:-10m}"
rate="${2:-1000}"
kill_every="${3:-300}"
name="cabinet-soak-pg-$$-$RANDOM"
key="soak-$(date +%s)-$RANDOM"
work="$(mktemp -d)"
pids=("" "")
ports=($((20000 + RANDOM % 10000)) $((30000 + RANDOM % 10000)))
chaos_pid=""

cleanup() {
  [ -n "$chaos_pid" ] && kill "$chaos_pid" 2>/dev/null || true
  # From the files: instances restarted by the chaos loop got their IDs in a subshell
  for file in "$work"/api*.pid; do [ -f "$file" ] && kill "$(cat "$file")" 2>/dev/null || true; done
  docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== Starting PostgreSQL"
docker run -d --name "$name" -e POSTGRES_USER=cabinet -e POSTGRES_PASSWORD=soak -e POSTGRES_DB=cabinet \
  -p "127.0.0.1::5432" postgres:16-alpine >/dev/null
db_port="$(docker port "$name" 5432/tcp | head -1 | sed -E 's/.*:([0-9]+)$/\1/')"
for _ in $(seq 1 60); do
  MSYS_NO_PATHCONV=1 docker exec "$name" pg_isready -U cabinet -d cabinet >/dev/null 2>&1 && break
  sleep 1
done
sleep 2

start_api() {
  local n="$1"
  mkdir -p "$work/api$n"
  (
    cd "$work/api$n"
    ASPNETCORE_URLS="http://127.0.0.1:${ports[$n]}" \
    Storage__Type=PostgreSQL \
    Storage__ConnectionString="Host=127.0.0.1;Port=$db_port;Database=cabinet;Username=cabinet;Password=soak;Maximum Pool Size=50" \
    Storage__RetentionDays=0 \
    ApiKeys__Keys__0__Key="$key" \
    ApiKeys__Keys__0__Tier=Admin \
    RateLimiting__PreAuth__MaxRequestsPerMinute=1000000 \
    RateLimiting__Tiers__Enterprise=1000000 \
    Query__Concurrency__MaxConcurrentPerClient=64 \
    dotnet "$root/src/Cabinet.API/bin/Release/net10.0/Cabinet.API.dll" >>"$work/api$n.log" 2>&1 &
    echo $! >"$work/api$n.pid"
  )
  pids[$n]="$(cat "$work/api$n.pid")"
  for _ in $(seq 1 120); do
    curl -fs "http://127.0.0.1:${ports[$n]}/health/ready" >/dev/null 2>&1 && return 0
    sleep 1
  done
  tail -20 "$work/api$n.log"
  echo "Instance $n didn't become ready" >&2
  exit 1
}

echo "== Starting two instances"
start_api 0
start_api 1

if [ "$kill_every" -gt 0 ]; then
  (
    n=0
    while true; do
      sleep "$kill_every"
      echo "== Killing instance $n"
      kill -9 "$(cat "$work/api$n.pid")" 2>/dev/null || true
      sleep 15
      echo "== Restarting instance $n"
      start_api "$n"
      n=$((1 - n))
    done
  ) &
  chaos_pid=$!
fi

echo "== Running load for $duration at $rate events/s"
status=0
dotnet run -c Release --project benchmarks/Cabinet.LoadTest -- \
  --urls "http://127.0.0.1:${ports[0]},http://127.0.0.1:${ports[1]}" \
  --key "$key" --duration "$duration" --rate "$rate" --output "$work/report.md" || status=$?

[ -n "$chaos_pid" ] && kill "$chaos_pid" 2>/dev/null || true
chaos_pid=""

echo "== Leadership"
grep -h "now the leader" "$work"/api*.log | wc -l | xargs echo "times an instance took the lead:"
cp "$work/report.md" ./soak-report.md 2>/dev/null || true
exit "$status"
