#!/usr/bin/env bash
#
# Compares the latest BenchmarkDotNet JSON report against thresholds/thresholds.json. Exits 0
# when every benchmark is within its threshold, non-zero when at least one regresses. Wired into
# the daily-benchmarks workflow — the workflow's failure surfaces as a GitHub notification on the
# runs page (default email to repo subscribers; Slack/Discord/etc. via repo-level integrations).
#
# Input:  benchmarks/PaymentGateway.Api.Benchmarks/BenchmarkDotNet.Artifacts/results/*-report.json
# Output: one line per benchmark + a final summary
#
# BenchmarkDotNet's JSON report has the structure:
#   { "Benchmarks": [ { "DisplayInfo": { "Method": "<fully.qualified.Name>" },
#                       "Statistics": { "Mean": <nanoseconds>, ... } } ... ] }

set -euo pipefail

REPORT_DIR="benchmarks/PaymentGateway.Api.Benchmarks/BenchmarkDotNet.Artifacts/results"
THRESHOLDS_FILE="benchmarks/thresholds.json"

if [ ! -d "$REPORT_DIR" ]; then
  echo "::error::Benchmark report directory not found: $REPORT_DIR"
  exit 1
fi

# Most recent *-report.json
REPORT=$(ls -1t "$REPORT_DIR"/*-report.json 2>/dev/null | head -1 || true)
if [ -z "$REPORT" ]; then
  echo "::error::No BenchmarkDotNet JSON report found in $REPORT_DIR"
  exit 1
fi

echo "Checking thresholds against $(basename "$REPORT")"
echo ""

if ! command -v jq >/dev/null 2>&1; then
  echo "::error::jq is required for the threshold check (apt-get install jq on bare runners)"
  exit 1
fi

# Iterate benchmarks; for each, look up its threshold and compare Mean (ns) against the limit.
# Uses bash process substitution so the loop variable comes from jq's output rather than stdin
# (lets us keep the script flow straightforward with `while read`).
FAILED=0
CHECKED=0
MISSING=0
while IFS=$'\t' read -r method meanNs; do
  CHECKED=$((CHECKED + 1))

  # Read the threshold for this method (object-key lookup via jq — fails with "null" if absent).
  threshold=$(jq -r --arg m "$method" '.thresholds[$m].meanNs // empty' "$THRESHOLDS_FILE")

  if [ -z "$threshold" ]; then
    MISSING=$((MISSING + 1))
    echo "  ⚠  $method: no threshold configured (meanNs=${meanNs})"
    continue
  fi

  # Compare integers (both are nanoseconds, no floats to worry about).
  if [ "$meanNs" -gt "$threshold" ]; then
    FAILED=$((FAILED + 1))
    echo "  ✗  $method: meanNs=${meanNs} > threshold=${threshold}  (regression)"
  else
    pct=$(( meanNs * 100 / threshold ))
    echo "  ✓  $method: meanNs=${meanNs} ≤ threshold=${threshold}  (~${pct}% of budget)"
  fi
done < <(jq -r '.Benchmarks[] | [.DisplayInfo.Method, (.Statistics.Mean | floor)] | @tsv' "$REPORT")

echo ""
echo "Checked: $CHECKED benchmarks, Missing-threshold: $MISSING, Over-threshold: $FAILED"

if [ "$FAILED" -gt 0 ]; then
  echo "::error::$FAILED benchmark(s) regressed above threshold. Open the run's artifacts for the full report."
  exit 1
fi
