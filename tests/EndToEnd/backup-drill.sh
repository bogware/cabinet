#!/usr/bin/env bash
# A PostgreSQL backup and restore drill: store events, dump, drop the database, restore, and check
# that the events and shared state are all back. It needs Docker and the API built in Release.
# (The SQLite drill runs as a test: BackupDrill_RestoresEverythingToAFreshInstall.)
set -euo pipefail

cd "$(dirname "$0")/../.."
root="$(pwd)"
# Unique, since CI runners are shared
name="cabinet-drill-pg-$$-$RANDOM"
api_port=$((20000 + RANDOM % 20000))
key="drill-admin-key"
work="$(mktemp -d)"
api_pid=""

cleanup() {
  [ -n "$api_pid" ] && kill "$api_pid" 2>/dev/null || true
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

start_api() {
  (
    cd "$work"
    ASPNETCORE_URLS="http://127.0.0.1:$api_port" \
    Storage__Type=PostgreSQL \
    Storage__ConnectionString="Host=127.0.0.1;Port=$port;Database=cabinet;Username=cabinet;Password=drill" \
    Storage__RetentionDays=0 \
    ApiKeys__Keys__0__Key="$key" \
    ApiKeys__Keys__0__Tier=Admin \
    dotnet "$root/src/Cabinet.API/bin/Release/net10.0/Cabinet.API.dll" >"$work/api.log" 2>&1 &
    echo $! >"$work/api.pid"
  )
  api_pid="$(cat "$work/api.pid")"
  for _ in $(seq 1 120); do
    curl -fs "http://127.0.0.1:$api_port/health/ready" >/dev/null 2>&1 && return 0
    sleep 1
  done
  cat "$work/api.log"
  echo "The API didn't become ready" >&2
  exit 1
}

stop_api() {
  kill "$api_pid" 2>/dev/null || true
  wait "$api_pid" 2>/dev/null || true
  api_pid=""
}

api() {
  curl -fsS -H "X-API-Key: $key" -H "Content-Type: application/json" "$@"
}

count_events() {
  api "http://127.0.0.1:$api_port/api/v1/query/execute" -d '{"query":"find * | stats count"}' \
    | sed -E 's/.*"results":\[\{"count":([0-9]+).*/\1/'
}

echo "== Starting PostgreSQL"
docker run -d --name "$name" -e POSTGRES_USER=cabinet -e POSTGRES_PASSWORD=drill -e POSTGRES_DB=cabinet \
  -p "127.0.0.1::5432" postgres:16-alpine >/dev/null
port="$(docker port "$name" 5432/tcp | head -1 | sed -E 's/.*:([0-9]+)$/\1/')"
for _ in $(seq 1 60); do
  MSYS_NO_PATHCONV=1 docker exec "$name" pg_isready -U cabinet -d cabinet >/dev/null 2>&1 && break
  sleep 1
done
sleep 2

echo "== Storing events and state"
start_api
for i in $(seq 1 200); do
  api "http://127.0.0.1:$api_port/services/collector/event" -d "{\"event\":\"drill event $i\"}" >/dev/null
done
api -X PUT "http://127.0.0.1:$api_port/api/v1/retention/main" -d '{"maxAgeDays":42}' >/dev/null
before="$(count_events)"
echo "events before: $before"
[ "$before" = "200" ]

echo "== Backing up"
MSYS_NO_PATHCONV=1 docker exec "$name" pg_dump -U cabinet -d cabinet -Fc -f /tmp/cabinet.dump
stop_api

echo "== Losing the database"
MSYS_NO_PATHCONV=1 docker exec "$name" dropdb -U cabinet --force cabinet
MSYS_NO_PATHCONV=1 docker exec "$name" createdb -U cabinet cabinet

echo "== Restoring"
MSYS_NO_PATHCONV=1 docker exec "$name" pg_restore -U cabinet -d cabinet --exit-on-error /tmp/cabinet.dump

echo "== Checking"
start_api
after="$(count_events)"
echo "events after: $after"
fail() { echo "FAIL: $1"; echo "$2"; tail -n 40 "$work/api.log"; exit 1; }
[ "$after" = "$before" ] || fail "event count changed" "$before before, $after after"
retention=$(api "http://127.0.0.1:$api_port/api/v1/retention/main") || fail "reading retention" ""
grep -q '"maxAgeDays":42' <<<"$retention" || fail "retention policy not restored" "$retention"
found=$(api "http://127.0.0.1:$api_port/api/v1/query/execute" -d '{"query":"find \"drill\" | head 1"}') || fail "search request" ""
grep -q 'drill event' <<<"$found" || fail "search didn't find the drill event" "$found"
verify=$(api "http://127.0.0.1:$api_port/api/v1/audit/verify") || fail "audit verify request" ""
grep -q '"valid":true' <<<"$verify" || fail "audit chain not intact" "$verify"
echo "PASS: restored $after events, shared state and an intact audit log"
