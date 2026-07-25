#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." >/dev/null 2>&1 && pwd)"
LAUNCHER="${ROOT_DIR}/run_server.sh"
PORT_FILE="${ROOT_DIR}/serverport"
BRIDGE_LOG="${ROOT_DIR}/log/bridge_server.log"
PID_FILE="${ROOT_DIR}/log/example_run_server_tpose.pid"
STARTUP_TIMEOUT_SEC=1800
EXIT_GRACE_SEC=15
TASK_ID="example_tpose"

if [[ ! -f "${LAUNCHER}" ]]; then
  echo "[ERROR] run_server.sh not found: ${LAUNCHER}"
  exit 1
fi

mkdir -p "${ROOT_DIR}/log"
rm -f "${PORT_FILE}" "${PID_FILE}"

echo "[EXAMPLE] ROOT_DIR=${ROOT_DIR}"
echo "[EXAMPLE] Launching QuickServer T-pose example..."

bash "${LAUNCHER}" >/dev/null 2>&1 &
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

resolve_python() {
  if command -v python3 >/dev/null 2>&1; then
    command -v python3
    return 0
  fi
  if command -v python >/dev/null 2>&1; then
    command -v python
    return 0
  fi
  return 1
}

PYTHON_BIN="$(resolve_python || true)"
if [[ -z "${PYTHON_BIN}" ]]; then
  echo "[ERROR] python3/python is required for example generate client."
  exit 1
fi

send_quit() {
  "${PYTHON_BIN}" - "${HOST}" "${PORT}" <<'PY' >/dev/null 2>&1 || true
import socket
import sys
with socket.create_connection((sys.argv[1], int(sys.argv[2])), timeout=3.0) as conn:
    conn.sendall(b'{"cmd":"quit"}\n')
PY
}

run_generate() {
  "${PYTHON_BIN}" - "${HOST}" "${PORT}" "${TASK_ID}" <<'PY'
import json
import socket
import sys

host = sys.argv[1]
port = int(sys.argv[2])
task_id = sys.argv[3]
request = {
    "cmd": "generate",
    "task_id": task_id,
    "prompt": "tpose",
    "duration": 1.0,
    "diffusion_steps": 20,
    "output_format": "kmb_v1",
    "constraints_json": "",
    "seed": 42,
}
with socket.create_connection((host, port), timeout=30.0) as conn:
    conn.sendall((json.dumps(request) + "\n").encode("utf-8"))
    file = conn.makefile("rb")
    header_line = file.readline()
    if not header_line:
        raise RuntimeError("No response header from server.")
    header = json.loads(header_line.decode("utf-8").strip())
    if header.get("status") != "done":
        raise RuntimeError(f"Generate failed: {header}")
    remaining = int(header.get("byte_length") or 0)
    while remaining > 0:
        chunk = file.read(min(8192, remaining))
        if not chunk:
            raise RuntimeError("Generate payload truncated.")
        remaining -= len(chunk)
print("[EXAMPLE] generate complete.")
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
    echo "[EXAMPLE] endpoint ready: ${HOST}:${PORT}"
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
  if (( WAIT_SEC >= STARTUP_TIMEOUT_SEC )); then
    echo "[ERROR] startup endpoint did not appear within ${STARTUP_TIMEOUT_SEC}s."
    dump_logs
    exit 1
  fi
done

if ! run_generate; then
  dump_logs
  exit 1
fi

send_quit
for _ in $(seq 1 30); do
  if ! kill -0 "${WRAPPER_PID}" >/dev/null 2>&1; then
    echo "[OK] QuickServer T-pose example passed."
    exit 0
  fi
  sleep 1
done

echo "[WARN] wrapper still running after quit request; exiting example anyway."
echo "[OK] QuickServer T-pose example passed."
exit 0
