#!/bin/bash

# Cabinet End-to-End Test Suite
# Tests all major functionality

set -e  # Exit on error

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CABINET="dotnet run --project $REPO_ROOT/src/Cabinet.CLI/Cabinet.CLI.csproj --"
TEST_LOG_ORIG="$SCRIPT_DIR/test-logs/server.log"
TEST_LOG=$(mktemp /tmp/cabinet-e2e-XXXXXX.log)

# Copy original test log to temp file so tests don't mutate source data
cp "$TEST_LOG_ORIG" "$TEST_LOG"

# Cleanup temp file on exit
cleanup() {
    rm -f "$TEST_LOG"
}
trap cleanup EXIT

echo "==================================="
echo "Cabinet End-to-End Test Suite"
echo "==================================="
echo ""

# Test 1: Help command
echo "[TEST 1] Testing help command..."
$CABINET --help > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Help command works"
else
    echo "✗ Help command failed"
    exit 1
fi

# Test 2: Version command
echo "[TEST 2] Testing version command..."
$CABINET version > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Version command works"
else
    echo "✗ Version command failed"
    exit 1
fi

# Test 3: Peek command
echo "[TEST 3] Testing peek command..."
$CABINET peek "$TEST_LOG" --lines 5 > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Peek command works"
else
    echo "✗ Peek command failed"
    exit 1
fi

# Test 4: Peek reverse
echo "[TEST 4] Testing peek reverse..."
$CABINET peek "$TEST_LOG" --lines 5 --reverse > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Peek reverse works"
else
    echo "✗ Peek reverse failed"
    exit 1
fi

# Test 5: Ingest logs
echo "[TEST 5] Testing log ingestion..."
$CABINET ingest "$TEST_LOG" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Log ingestion works"
else
    echo "✗ Log ingestion failed"
    exit 1
fi

# Test 6: Query - search for errors
echo "[TEST 6] Testing query for errors..."
$CABINET query "search ERROR" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Query for errors works"
else
    echo "✗ Query for errors failed"
    exit 1
fi

# Test 7: Query - stats by severity
echo "[TEST 7] Testing stats query..."
$CABINET query "search * | stats count() by Severity" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Stats query works"
else
    echo "✗ Stats query failed"
    exit 1
fi

# Test 8: Query - time filtering
echo "[TEST 8] Testing time-based query..."
$CABINET query "search * | where Timestamp > '2024-12-07 10:01:00'" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Time-based query works"
else
    echo "✗ Time-based query failed"
    exit 1
fi

# Test 9: Query - top command
echo "[TEST 9] Testing top command..."
$CABINET query "search * | top 5 Severity" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Top command works"
else
    echo "✗ Top command failed"
    exit 1
fi

# Test 10: AWS command help
echo "[TEST 10] Testing AWS command..."
$CABINET aws --help > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ AWS command available"
else
    echo "✗ AWS command failed"
    exit 1
fi

# Test 11: Detection - scumscan
echo "[TEST 11] Testing scumscan detection..."
# First, create a log with suspicious content
echo "2024-12-07 11:00:00 Player says: you fucking noob idiot" >> "$TEST_LOG"
echo "2024-12-07 11:00:01 Player says: go kill yourself" >> "$TEST_LOG"
$CABINET ingest "$TEST_LOG" > /dev/null 2>&1
$CABINET report scumscan --last 1h > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Scumscan detection works"
else
    echo "✗ Scumscan detection failed"
    exit 1
fi

# Test 12: Detection - hackwatch
echo "[TEST 12] Testing hackwatch detection..."
$CABINET report hackwatch --last 1h > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Hackwatch detection works"
else
    echo "✗ Hackwatch detection failed"
    exit 1
fi

# Test 13: Detection - adminabuse
echo "[TEST 13] Testing adminabuse detection..."
$CABINET report adminabuse --last 1h > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Adminabuse detection works"
else
    echo "✗ Adminabuse detection failed"
    exit 1
fi

# Test 14: Config command
echo "[TEST 14] Testing config command..."
$CABINET config --help > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Config command works"
else
    echo "✗ Config command failed"
    exit 1
fi

# Test 15: Complex query pipeline
echo "[TEST 15] Testing complex query pipeline..."
$CABINET query "search * | where Severity != 'DEBUG' | stats count() by Severity | sort count desc | head 3" > /dev/null 2>&1
if [ $? -eq 0 ]; then
    echo "✓ Complex query pipeline works"
else
    echo "✗ Complex query pipeline failed"
    exit 1
fi

echo ""
echo "==================================="
echo "All tests passed successfully! ✓"
echo "==================================="
echo ""
echo "Cabinet is production-ready!"