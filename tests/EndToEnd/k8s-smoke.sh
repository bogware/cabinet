#!/usr/bin/env bash
# Deploys k8s/ to a throwaway kind cluster and checks that it really works: both replicas become
# ready, events can be stored and read back through the Service, and they survive losing a pod.
# It needs Docker, kind and kubectl.
set -euo pipefail

cd "$(dirname "$0")/../.."
cluster="cabinet-smoke"
image="ghcr.io/bogware/cabinet:smoke"
key="$(openssl rand -hex 32)"
forward_pid=""

cleanup() {
  [ -n "$forward_pid" ] && kill "$forward_pid" 2>/dev/null || true
  kind delete cluster --name "$cluster" >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "== Building the image"
docker build -q -t "$image" -f Dockerfile.api . >/dev/null

echo "== Creating the cluster"
kind create cluster --name "$cluster" --wait 120s >/dev/null
kind load docker-image "$image" --name "$cluster" >/dev/null

echo "== Applying the manifests"
kubectl create namespace cabinet >/dev/null
kubectl -n cabinet create secret generic cabinet-secrets \
  --from-literal=postgres-password="$(openssl rand -hex 24)" \
  --from-literal=api-key-0="$key" >/dev/null
overlay="$(mktemp -d)"
trap 'rm -rf "$overlay"; cleanup' EXIT
cp k8s/*.yaml "$overlay/"
sed -i 's/newTag: latest/newTag: smoke/' "$overlay/kustomization.yaml"
kubectl apply -k "$overlay" >/dev/null

echo "== Waiting for it to be ready"
kubectl -n cabinet rollout status statefulset/postgres --timeout=240s
kubectl -n cabinet rollout status deployment/cabinet-api --timeout=300s
ready="$(kubectl -n cabinet get deployment cabinet-api -o jsonpath='{.status.readyReplicas}')"
echo "ready replicas: $ready"
[ "$ready" = "2" ]

forward() {
  [ -n "$forward_pid" ] && kill "$forward_pid" 2>/dev/null || true
  kubectl -n cabinet port-forward service/cabinet-api 58080:80 >/dev/null 2>&1 &
  forward_pid=$!
  for _ in $(seq 1 30); do
    curl -fs http://127.0.0.1:58080/health/ready >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

api() {
  # kubectl port-forward sometimes drops a connection, which says nothing about the service
  curl -fsS --retry 5 --retry-all-errors --retry-delay 1 -H "X-API-Key: $key" -H "Content-Type: application/json" "$@"
}

echo "== Storing and reading events"
forward
for i in $(seq 1 20); do
  # With its own ID, so a retried request can't store it twice
  id="$(printf '01SM0KE000000000000000%04d' "$i")"
  api http://127.0.0.1:58080/services/collector/event -d "{\"id\":\"$id\",\"event\":\"smoke event $i\"}" >/dev/null
done
api http://127.0.0.1:58080/api/v1/query/execute -d '{"query":"find * | stats count"}' | grep -q '"count":20'
[ "$(curl -fsS --retry 5 --retry-all-errors --retry-delay 1 http://127.0.0.1:58080/ | grep -ci '<html')" -gt 0 ]

echo "== Losing a pod"
pod="$(kubectl -n cabinet get pods -l component=api -o jsonpath='{.items[0].metadata.name}')"
kubectl -n cabinet delete pod "$pod" --wait=false >/dev/null
sleep 5
kubectl -n cabinet rollout status deployment/cabinet-api --timeout=240s
forward
api http://127.0.0.1:58080/api/v1/query/execute -d '{"query":"find * | stats count"}' | grep -q '"count":20'

echo "== Background work has one leader"
sleep 8
leaders=0
for pod in $(kubectl -n cabinet get pods -l component=api -o name); do
  # Not grep -q: closing the pipe early would fail kubectl, and with it the pipeline
  if [ "$(kubectl -n cabinet logs "$pod" | grep -c "now the leader")" -gt 0 ]; then
    leaders=$((leaders + 1))
  fi
done
echo "pods that have led: $leaders"
[ "$leaders" -ge 1 ]

echo "PASS: deployed, served, and survived losing a pod"
