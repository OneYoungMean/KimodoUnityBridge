#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
UV_INSTALL_TIMEOUT_SEC=600

resolve_uv_bin() {
  if [[ -n "${KIMODO_UV_BIN:-}" ]]; then
    echo "${KIMODO_UV_BIN}"
  elif [[ -x "${ROOT_DIR}/program/exe/uv/uv" ]]; then
    echo "${ROOT_DIR}/program/exe/uv/uv"
  elif [[ -x "${ROOT_DIR}/program/exe/uv/uv.exe" ]]; then
    echo "${ROOT_DIR}/program/exe/uv/uv.exe"
  elif command -v uv >/dev/null 2>&1; then
    command -v uv
  else
    echo ""
  fi
}

install_uv_locally() {
  local uv_dir="${ROOT_DIR}/program/exe/uv"
  local selected_name=""
  local selected_script=""
  local selected_github_base=""
  local best_ms=""
  mkdir -p "${uv_dir}"
  probe_uv_candidate "official" "https://astral.sh/uv/install.sh" ""
  probe_uv_candidate "release-mirror" "https://releases.astral.sh/github/uv/releases/latest/download/uv-installer.sh" "https://releases.astral.sh/github"
  if [[ -z "${selected_script}" ]]; then
    echo "[ERROR] Failed to choose a uv installer source."
    return 1
  fi
  echo "[INFO] Selected uv source: ${selected_name}"
  if command -v curl >/dev/null 2>&1; then
    if [[ -n "${selected_github_base}" ]]; then
      run_with_timeout "${UV_INSTALL_TIMEOUT_SEC}" sh -c "curl -LsSf \"${selected_script}\" | env UV_UNMANAGED_INSTALL=\"${uv_dir}\" UV_INSTALLER_GITHUB_BASE_URL=\"${selected_github_base}\" sh" || return 1
    else
      run_with_timeout "${UV_INSTALL_TIMEOUT_SEC}" sh -c "curl -LsSf \"${selected_script}\" | env UV_UNMANAGED_INSTALL=\"${uv_dir}\" sh" || return 1
    fi
    return
  fi
  if command -v wget >/dev/null 2>&1; then
    if [[ -n "${selected_github_base}" ]]; then
      run_with_timeout "${UV_INSTALL_TIMEOUT_SEC}" sh -c "wget -qO- \"${selected_script}\" | env UV_UNMANAGED_INSTALL=\"${uv_dir}\" UV_INSTALLER_GITHUB_BASE_URL=\"${selected_github_base}\" sh" || return 1
    else
      run_with_timeout "${UV_INSTALL_TIMEOUT_SEC}" sh -c "wget -qO- \"${selected_script}\" | env UV_UNMANAGED_INSTALL=\"${uv_dir}\" sh" || return 1
    fi
    return
  fi
  echo "[ERROR] Could not auto-install uv: curl or wget is required."
  return 1
}

run_with_timeout() {
  local timeout_sec="$1"
  shift
  "$@" &
  local cmd_pid=$!
  (
    sleep "${timeout_sec}"
    kill -TERM "${cmd_pid}" >/dev/null 2>&1 || true
    sleep 2
    kill -KILL "${cmd_pid}" >/dev/null 2>&1 || true
  ) &
  local watchdog_pid=$!
  local rc=0
  if wait "${cmd_pid}"; then
    rc=0
  else
    rc=$?
  fi
  kill -TERM "${watchdog_pid}" >/dev/null 2>&1 || true
  wait "${watchdog_pid}" 2>/dev/null || true
  if [[ "${rc}" -eq 143 || "${rc}" -eq 137 ]]; then
    echo "[ERROR] uv automatic installation timed out after ${timeout_sec} seconds."
    echo "[ERROR] Please install uv manually, or place uv under: ${ROOT_DIR}/program/exe/uv"
    return 1
  fi
  if [[ "${rc}" -ne 0 ]]; then
    echo "[ERROR] uv automatic installation failed."
    echo "[ERROR] Please install uv manually, or place uv under: ${ROOT_DIR}/program/exe/uv"
    return "${rc}"
  fi
  return 0
}

probe_uv_candidate() {
  local name="$1"
  local script_url="$2"
  local github_base="$3"
  local code=""
  local elapsed_ms=""
  local started_s=""
  local ended_s=""

  if command -v curl >/dev/null 2>&1; then
    started_s="$(date +%s)"
    code="$(curl -I -L -sS -o /dev/null -w '%{http_code}' --max-time 3 "${script_url}" || true)"
    ended_s="$(date +%s)"
    elapsed_ms="$(( (ended_s - started_s) * 1000 ))"
  elif command -v wget >/dev/null 2>&1; then
    started_s="$(date +%s)"
    if wget -q --spider --server-response --timeout=3 "${script_url}" >/dev/null 2>&1; then
      code="200"
    else
      code="000"
    fi
    ended_s="$(date +%s)"
    elapsed_ms="$(( (ended_s - started_s) * 1000 ))"
  else
    echo "[ERROR] Could not probe uv source: curl or wget is required."
    return
  fi

  if [[ "${code}" == 2* || "${code}" == 3* ]]; then
    echo "[PROBE] uv ${name}: ok, ${elapsed_ms} ms, ${script_url}"
    if [[ -z "${selected_script}" || -z "${best_ms}" || "${elapsed_ms}" -lt "${best_ms}" ]]; then
      selected_name="${name}"
      selected_script="${script_url}"
      selected_github_base="${github_base}"
      best_ms="${elapsed_ms}"
    fi
  else
    echo "[PROBE] uv ${name}: failed, ${elapsed_ms} ms, status=${code}, ${script_url}"
  fi
}

prompt_install_missing_tools() {
  local missing=("$@")
  local answer=""
  echo "[ERROR] Missing required command-line tool(s): ${missing[*]}"
  read -r -p "Would you like Kimodo QuickServer to try installing them now? [Y/N] " answer
  case "${answer}" in
    Y|y|Yes|YES|yes)
      for tool in "${missing[@]}"; do
        case "${tool}" in
          uv)
            install_uv_locally || return 1
            ;;
        esac
      done
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

UV_BIN="$(resolve_uv_bin)"

if [[ -n "${KIMODO_TEST_VENV_PATH:-}" ]]; then
  echo "[ERROR] KIMODO_TEST_VENV_PATH has been removed. Use KIMODO_VENV_PATH."
  exit 1
fi
if [[ -n "${KIMODO_TEST_SETUP_DEVICE:-}" ]]; then
  echo "[ERROR] KIMODO_TEST_SETUP_DEVICE has been removed. Use KIMODO_SETUP_DEVICE."
  exit 1
fi
if [[ -n "${KIMODO_CPU_TEXT_ENCODER:-}" ]]; then
  echo "[ERROR] KIMODO_CPU_TEXT_ENCODER has been removed. QuickServer now auto-selects the local INT8 text encoder route."
  exit 1
fi
if [[ -n "${CHECKPOINT_DIR:-}" ]]; then
  echo "[ERROR] CHECKPOINT_DIR has been removed. Use KIMODO_MODELS_ROOT."
  exit 1
fi

MISSING_TOOLS=()
if [[ -z "${UV_BIN}" || ! -x "${UV_BIN}" ]]; then
  MISSING_TOOLS+=("uv")
fi

if [[ "${#MISSING_TOOLS[@]}" -gt 0 ]]; then
  prompt_install_missing_tools "${MISSING_TOOLS[@]}" || exit 1
  UV_BIN="$(resolve_uv_bin)"
fi

if [[ -z "${UV_BIN}" || ! -x "${UV_BIN}" ]]; then
  echo "[ERROR] uv is still unavailable after installation attempt."
  exit 1
fi

ARGS=("$@")
HAS_VENV_ARG=0
for arg in "${ARGS[@]}"; do
  if [[ "${arg}" == "--venv" ]]; then
    HAS_VENV_ARG=1
    break
  fi
done
if [[ -n "${KIMODO_VENV_PATH:-}" && "${HAS_VENV_ARG}" -eq 0 ]]; then
  ARGS+=("--venv" "${KIMODO_VENV_PATH}")
fi

exec "${UV_BIN}" run --python 3.12 --no-project python "${ROOT_DIR}/quickserver.py" "${ARGS[@]}"
