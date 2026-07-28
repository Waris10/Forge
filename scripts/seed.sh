#!/usr/bin/env bash
# Seed Forge with realistic test data for dashboard verification.
# Hits the API; everything flows through the real submit -> enqueue path.

set -euo pipefail

API="${FORGE_API:-http://localhost:5171}"

echo "Seeding against $API"

submit() {
  local payload="$1"
  curl -sS -X POST "$API/jobs" \
    -H "Content-Type: application/json" \
    -d "$payload" \
    -o /dev/null -w "%{http_code} "
}

echo -n "  20 NoOp jobs: "
for i in $(seq 1 20); do
  submit '{"jobType":"NoOp","payload":{}}'
done
echo

echo -n "  3 Slow jobs (60s each): "
for i in $(seq 1 3); do
  submit '{"jobType":"Slow","payload":{}}'
done
echo

echo -n "  30 Flaky jobs (successRate=0.3): "
for i in $(seq 1 30); do
  submit '{"jobType":"Flaky","payload":{"successRate":0.3}}'
done
echo

echo -n "  5 delayed NoOp jobs (10s delay): "
for i in $(seq 1 5); do
  submit '{"jobType":"NoOp","payload":{},"delaySeconds":10}'
done
echo

echo -n "  2 unknown-type jobs (will fail terminally): "
for i in $(seq 1 2); do
  submit '{"jobType":"DoesNotExist","payload":{}}'
done
echo

echo "Done. Roughly:"
echo "  ~20 succeeded (NoOp), 3 running/succeeded (Slow), ~9 succeeded/~5 dead (Flaky retries)"
echo "  ~5 scheduled briefly, 2 failed (unknown jobType)"