"""Writes numbered lines while killing the API and agent, then checks for loss and duplicates.

Run through agent-chaos.sh, which starts the compose stack. Exits non-zero if any line was lost or
stored twice.
"""
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request

API = "http://127.0.0.1:5000"
KEY = os.environ["CABINET_API_KEY"]
LOG_DIR = "logs"
LOG = os.path.join(LOG_DIR, "chaos.log")
TOTAL = 30000
RATE_PER_SECOND = 400


def docker(*args):
    return subprocess.run(["docker", *args], capture_output=True, text=True, timeout=120)


def count(query):
    body = json.dumps({"query": query}).encode()
    request = urllib.request.Request(
        API + "/api/v1/query/execute", body, {"X-API-Key": KEY, "Content-Type": "application/json"}
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)["results"][0]["count"]


written = 0
rotations = 0


def writer():
    global written, rotations
    os.makedirs(LOG_DIR, exist_ok=True)
    number = 0
    while number < TOTAL:
        with open(LOG, "a", encoding="utf-8", newline="\n") as handle:
            for _ in range(RATE_PER_SECOND // 10):
                if number >= TOTAL:
                    break
                handle.write(f"chaosline seq={number:07d} payload=xxxxxxxxxxxxxxxxxxxxxxxx\n")
                number += 1
        written = number
        time.sleep(0.1)

        # Rotate by rename partway through, the way logrotate does.
        if number in (8000, 16000, 24000):
            rotations += 1
            os.replace(LOG, os.path.join(LOG_DIR, f"chaos-{rotations}.txt"))


thread = threading.Thread(target=writer, daemon=True)
started = time.time()
thread.start()


def wait_for(target, label):
    while written < target:
        time.sleep(0.2)
    print(f"[{time.time() - started:5.1f}s] at {written} lines: {label}", flush=True)


wait_for(4000, "killing the API (hard)")
docker("kill", "cabinet-api")
time.sleep(8)
print(f"[{time.time() - started:5.1f}s] restarting the API", flush=True)
docker("start", "cabinet-api")

wait_for(10000, "killing the agent (SIGKILL)")
docker("kill", "cabinet-agent")
time.sleep(5)
print(f"[{time.time() - started:5.1f}s] restarting the agent", flush=True)
docker("start", "cabinet-agent")

wait_for(18000, "restarting Postgres")
docker("restart", "cabinet-postgres")

wait_for(22000, "killing both the API and the agent")
docker("kill", "cabinet-api")
docker("kill", "cabinet-agent")
time.sleep(6)
docker("start", "cabinet-api")
docker("start", "cabinet-agent")

thread.join()
print(f"[{time.time() - started:5.1f}s] all {written} lines written, waiting for delivery", flush=True)

deadline = time.time() + 240
stored = -1
last_change = time.time()
while time.time() < deadline:
    try:
        current = count("find chaosline | stats count")
    except Exception as error:  # the API may still be restarting
        print("  query failed:", repr(error)[:90], flush=True)
        time.sleep(3)
        continue
    if current != stored:
        stored = current
        last_change = time.time()
        print(f"  stored {stored}", flush=True)
    if stored >= TOTAL and time.time() - last_change > 45:
        break
    time.sleep(3)

distinct = count('find chaosline | rex "seq=(?<seq>[0-9]+)" | stats dc(seq) as count')
print(f"written={TOTAL} stored={stored} distinct={distinct}")
print(f"lost={TOTAL - distinct} duplicates={stored - distinct}")

# Which lines? A contiguous range points at a cause; scattered lines point elsewhere.
body = json.dumps({"query": 'find chaosline | rex "seq=(?<seq>[0-9]+)" | fields seq'}).encode()
request = urllib.request.Request(API + "/api/v1/query/execute", body, {"X-API-Key": KEY, "Content-Type": "application/json"})
with urllib.request.urlopen(request, timeout=60) as response:
    seen = [int(row["seq"]) for row in json.load(response)["results"]]
counts = {}
for value in seen:
    counts[value] = counts.get(value, 0) + 1


def ranges(values):
    values = sorted(values)
    out = []
    for value in values:
        if out and out[-1][1] == value - 1:
            out[-1][1] = value
        else:
            out.append([value, value])
    return out


print("missing ranges:", ranges(v for v in range(TOTAL) if v not in counts)[:20])
print("duplicated ranges:", ranges(v for v, n in counts.items() if n > 1)[:20])

# For the first duplicated line: which file and event IDs produced each copy?
dupes = sorted(v for v, n in counts.items() if n > 1)
if dupes:
    for seq in (dupes[0], dupes[-1]):
        q = f'find "seq={seq:07d}" | fields Id, SourceFile, Timestamp'
        body = json.dumps({"query": q}).encode()
        request = urllib.request.Request(API + "/api/v1/query/execute", body, {"X-API-Key": KEY, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=60) as response:
            print("duplicate sources", seq, json.load(response)["results"])

sys.exit(0 if TOTAL == distinct == stored else 1)
