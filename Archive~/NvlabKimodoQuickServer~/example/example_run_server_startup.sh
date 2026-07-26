#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
LAUNCHER="${ROOT_DIR}/run_server.sh"
PORT_FILE="${ROOT_DIR}/serverport"
BRIDGE_LOG="${ROOT_DIR}/log/bridge_server.log"
PID_FILE="${ROOT_DIR}/log/example_run_server_startup.pid"
STARTUP_TIMEOUT_SEC=1800
EXIT_GRACE_SEC=15

if [[ ! -f "${LAUNCHER}" ]]; then
  echo "[ERROR] run_server.sh not found: ${LAUNCHER}"
  exit 1
fi

mkdir -p "${ROOT_DIR}/log"
rm -f "${PORT_FILE}" "${PID_FILE}"

echo "[EXAMPLE] ROOT_DIR=${ROOT_DIR}"
echo "[EXAMPLE] Launching QuickServer startup example..."

bash "${LAUNCHER}" > /dev/null 2>&1 &
WRAPPER_PID=$!
printf '%s\n' "${WRAPPER_PID}" > "${PID_FILE}"

read_serverport() {
  [[ -f "${PORT_FILE}" ]] || return 1
  local endpoint
  endpoint="$(head -n 1 "${PORT_FILE}" 2>/dev/null | tr -d '\r')"
  [[ "${endpoint}" == *:* ]] || return 1
  HOST="${endpoint%:*}"
  PORT="${endpoint##*:}"
  [[ -n "${HOST}" && -n "${PORT}" ]] || return 1
  return 0
}

read_endpoint_from_log() {
  [[ -f "${BRIDGE_LOG}" ]] || return 1
  local endpoint
  endpoint="$(grep -m1 'quickserver_cli listening on ' "${BRIDGE_LOG}" | sed -E 's/.*quickserver_cli listening on ([^ ]+)/\1/' | tr -d '\r' || true)"
  [[ "${endpoint}" == *:* ]] || return 1
  HOST="${endpoint%:*}"
  PORT="${endpoint##*:}"
  [[ -n "${HOST}" && -n "${PORT}" ]] || return 1
  return 0
}

send_quit() {
  local python_bin=""
  if command -v python3 >/dev/null 2>&1; then
    python_bin="$(command -v python3)"
  elif command -v python >/dev/null 2>&1; then
    python_bin="$(command -v python)"
  else
    return 0
  fi

  "${python_bin}" - "${HOST}" "${PORT}" <<'PY' >/dev/null 2>&1 || true
import socket
import sys

host = sys.argv[1]
port = int(sys.argv[2])
with socket.create_connection((host, port), timeout=3.0) as conn:
    conn.sendall(b'{"cmd":"quit"}\n')
PY
}

dump_logs() {
  if [[ -f "${BRIDGE_LOG}" ]]; then
    echo "[DIAG] tail: ${BRIDGE_LOG}"
    tail -n 40 "${BRIDGE_LOG}" || true
  fi
  if [[ -f "${ROOT_DIR}/log/setup.log" ]]; then
    echo "[DIAG] tail: ${ROOT_DIR}/log/setup.log"
    tail -n 40 "${ROOT_DIR}/log/setup.log" || true
  fi
}

WAIT_SEC=0
WRAPPER_EXITED=0
WRAPPER_EXIT_AT=0
HOST=""
PORT=""
while true; do
  if read_serverport || read_endpoint_from_log; then
    echo "[OK] QuickServer startup ready: ${HOST}:${PORT}"
    send_quit
    break
  fi

  if ! kill -0 "${WRAPPER_PID}" >/dev/null 2>&1; then
    if [[ "${WRAPPER_EXITED}" -eq 0 ]]; then
      WRAPPER_EXITED=1
      WRAPPER_EXIT_AT="${WAIT_SEC}"
    fi
    if (( WAIT_SEC - WRAPPER_EXIT_AT >= EXIT_GRACE_SEC )); then
      echo "[ERROR] run_server.sh exited before startup endpoint became available."
      dump_logs
      exit 1
    fi
  fi

  sleep 1
  WAIT_SEC=$((WAIT_SEC + 1))
  if (( WAIT_SEC % 10 == 0 )); then
    if [[ "${WRAPPER_EXITED}" -eq 1 ]]; then
      echo "[EXAMPLE] waiting startup endpoint... ${WAIT_SEC}/${STARTUP_TIMEOUT_SEC}s (wrapper exited, waiting for bridge handoff)"
    else
      echo "[EXAMPLE] waiting startup endpoint... ${WAIT_SEC}/${STARTUP_TIMEOUT_SEC}s"
    fi
  fi
  if (( WAIT_SEC >= STARTUP_TIMEOUT_SEC )); then
    echo "[ERROR] startup endpoint did not appear within ${STARTUP_TIMEOUT_SEC}s."
    dump_logs
    exit 1
  fi
done

for _ in $(seq 1 30); do
  if ! kill -0 "${WRAPPER_PID}" >/dev/null 2>&1; then
    exit 0
  fi
  sleep 1
done

echo "[WARN] wrapper still running after quit request; exiting example anyway."
exit 0
