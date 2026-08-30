#!/usr/bin/env bash
set -euo pipefail

JOBS="${1:-10000}"
API="${2:-${FORGE_API:-http://localhost:5171}}"
PARALLEL="${3:-50}"
QUEUE="default"

echo "Forge Benchmark"
echo "  Jobs:     $JOBS"
echo "  API:      $API"
echo "  Parallel: $PARALLEL"
echo "  Queue:    $QUEUE"
echo ""

submit_one() {
    curl -sS -X POST "$1/jobs" \
        -H "Content-Type: application/json" \
        -d "{\"jobType\":\"NoOp\",\"payload\":{},\"queue\":\"$2\"}" \
        -o /dev/null
}
export -f submit_one

echo -n "Submitting $JOBS jobs ($PARALLEL at a time)... "
SUBMIT_START=$(date +%s%N)

seq 1 "$JOBS" | xargs -P "$PARALLEL" -I{} bash -c "submit_one '$API' '$QUEUE'"

SUBMIT_END=$(date +%s%N)
SUBMIT_MS=$(( (SUBMIT_END - SUBMIT_START) / 1000000 ))
SUBMIT_RATE=$(( JOBS * 1000 / SUBMIT_MS ))
echo "done."
echo "  Submit time: ${SUBMIT_MS}ms (~${SUBMIT_RATE} submissions/sec)"
echo ""

# Truncate the table first since we already have 10000 from the previous run
echo "Note: truncating jobs table before drain measurement..."
docker exec forge-postgres psql -U forge -d forge \
    -c "TRUNCATE TABLE jobs" > /dev/null 2>&1 || true

echo -n "Submitting fresh batch for drain measurement... "
SUBMIT_START2=$(date +%s%N)
seq 1 "$JOBS" | xargs -P "$PARALLEL" -I{} bash -c "submit_one '$API' '$QUEUE'"
SUBMIT_END2=$(date +%s%N)
echo "done."
echo ""

echo -n "Waiting for drain"
DRAIN_START=$(date +%s%N)

while true; do
    PENDING=$(docker exec forge-postgres psql -U forge -d forge \
        -t -c "SELECT COUNT(*) FROM jobs WHERE status IN ('queued','running') AND queue = '$QUEUE'" \
        2>/dev/null | tr -d ' \r\n')

    if [ "$PENDING" = "0" ]; then
        break
    fi

    echo -n " $PENDING"
    sleep 1
done

DRAIN_END=$(date +%s%N)
DRAIN_MS=$(( (DRAIN_END - DRAIN_START) / 1000000 ))
echo " done."
echo ""

SUCCEEDED=$(docker exec forge-postgres psql -U forge -d forge \
    -t -c "SELECT COUNT(*) FROM jobs WHERE status = 'succeeded' AND queue = '$QUEUE'" \
    2>/dev/null | tr -d ' \r\n')

FAILED=$(docker exec forge-postgres psql -U forge -d forge \
    -t -c "SELECT COUNT(*) FROM jobs WHERE status IN ('failed','dead') AND queue = '$QUEUE'" \
    2>/dev/null | tr -d ' \r\n')

# Integer arithmetic only — jobs/sec rounded down
THROUGHPUT=$(( SUCCEEDED * 1000 / DRAIN_MS ))

echo "==============================="
echo "Results"
echo "  Drain time:  ${DRAIN_MS}ms"
echo "  Succeeded:   $SUCCEEDED"
echo "  Failed/Dead: $FAILED"
echo "  Throughput:  ~${THROUGHPUT} jobs/sec"
echo "==============================="
echo ""
echo "README line:"
echo "  $JOBS NoOp jobs, 1 worker: ~${THROUGHPUT} jobs/sec (${DRAIN_MS}ms drain)"
