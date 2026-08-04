#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$ROOT_DIR/core/integration_test_suite.py"

resolve_python() {
  if command -v python3 >/dev/null 2>&1; then
    echo "python3"
    return 0
  fi
  if command -v python >/dev/null 2>&1; then
    echo "python"
    return 0
  fi
  return 1
}

PYTHON_EXE="$(resolve_python || true)"
if [[ -z "${PYTHON_EXE:-}" ]]; then
  echo "[ERROR] Could not find a host Python interpreter." >&2
  exit 1
fi

if [[ $# -gt 0 ]]; then
  exec "$PYTHON_EXE" "$SCRIPT" "$@"
fi

echo
echo "Kimodo QuickServer Integration Tests"
echo "===================================="
echo
"$PYTHON_EXE" "$SCRIPT" --list
echo
read -r -p "Select test id(s), range:T15-T20, tag:<name>, or testfull (empty = testfull): " TEST_SELECTION
TEST_SELECTION="${TEST_SELECTION:-testfull}"

if [[ "${TEST_SELECTION,,}" == "testfull" ]]; then
  exec "$PYTHON_EXE" "$SCRIPT" --full
fi

if [[ "$TEST_SELECTION" == tag:* ]]; then
  exec "$PYTHON_EXE" "$SCRIPT" --tag "${TEST_SELECTION#tag:}"
fi

if [[ "$TEST_SELECTION" == range:* ]]; then
  RANGE_VALUE="${TEST_SELECTION#range:}"
  START_CASE="${RANGE_VALUE%-*}"
  END_CASE="${RANGE_VALUE#*-}"
  exec "$PYTHON_EXE" "$SCRIPT" --range "$START_CASE" "$END_CASE"
fi

if [[ "$TEST_SELECTION" == *,* ]] || [[ "$TEST_SELECTION" == *" "* ]]; then
  exec "$PYTHON_EXE" "$SCRIPT" --cases "$TEST_SELECTION"
fi

exec "$PYTHON_EXE" "$SCRIPT" --case "$TEST_SELECTION"
