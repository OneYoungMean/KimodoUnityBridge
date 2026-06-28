#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
ROOT_DIR="${SCRIPT_DIR}"
UV_INSTALL_TIMEOUT_SEC=600
UV_PROBE_TIMEOUT_SEC=1
UV_VERSION="0.11.25"
UV_SELECTED_NAME=""
UV_SELECTED_URL=""
UV_SELECTED_MS=""

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
  local artifact=""
  local github_url=""
  local ustc_url=""
  local tmp_dir=""
  mkdir -p "${uv_dir}"
  UV_SELECTED_NAME=""
  UV_SELECTED_URL=""
  UV_SELECTED_MS=""
  if ! command -v curl >/dev/null 2>&1; then
    echo "[ERROR] Could not auto-install uv: curl is required."
    return 1
  fi
  artifact="$(resolve_uv_artifact)" || return 1
  github_url="https://github.com/astral-sh/uv/releases/download/${UV_VERSION}/${artifact}"
  ustc_url="https://mirrors.ustc.edu.cn/github-release/astral-sh/uv/${UV_VERSION}/${artifact}"
  probe_uv_candidate "github" "${github_url}"
  probe_uv_candidate "ustc" "${ustc_url}"
  if [[ -z "${UV_SELECTED_URL}" ]]; then
    echo "[ERROR] Failed to choose a uv download source."
    return 1
  fi
  echo "[INFO] Selected uv source: ${UV_SELECTED_NAME}"
  tmp_dir="$(mktemp -d 2>/dev/null || mktemp -d -t kimodo-uv)"
  trap 'rm -rf "${tmp_dir}"' RETURN
  run_with_timeout "${UV_INSTALL_TIMEOUT_SEC}" curl -L --fail --silent --show-error --output "${tmp_dir}/${artifact}" "${UV_SELECTED_URL}" || return 1
  if [[ "${artifact}" == *.zip ]]; then
    if ! command -v unzip >/dev/null 2>&1; then
      echo "[ERROR] unzip is required to install uv from ${artifact}."
      return 1
    fi
    unzip -oq "${tmp_dir}/${artifact}" -d "${tmp_dir}" >/dev/null
  else
    tar -xzf "${tmp_dir}/${artifact}" -C "${tmp_dir}"
  fi
  if [[ -f "${tmp_dir}/uv" ]]; then
    cp -f "${tmp_dir}/uv" "${uv_dir}/uv"
    chmod +x "${uv_dir}/uv"
  fi
  if [[ -f "${tmp_dir}/uvx" ]]; then
    cp -f "${tmp_dir}/uvx" "${uv_dir}/uvx"
    chmod +x "${uv_dir}/uvx"
  fi
  if [[ -f "${tmp_dir}/uvw" ]]; then
    cp -f "${tmp_dir}/uvw" "${uv_dir}/uvw"
    chmod +x "${uv_dir}/uvw"
  fi
  trap - RETURN
  rm -rf "${tmp_dir}"
  echo "[INFO] Download uv complete."
}

resolve_uv_artifact() {
  local os_name=""
  local arch_name=""
  os_name="$(uname -s)"
  arch_name="$(uname -m)"
  case "${os_name}" in
    Darwin)
      case "${arch_name}" in
        arm64|aarch64) echo "uv-aarch64-apple-darwin.tar.gz" ;;
        x86_64) echo "uv-x86_64-apple-darwin.tar.gz" ;;
        *) echo "[ERROR] Unsupported macOS architecture: ${arch_name}" ; return 1 ;;
      esac
      ;;
    Linux)
      case "${arch_name}" in
        aarch64|arm64) echo "uv-aarch64-unknown-linux-gnu.tar.gz" ;;
        x86_64|amd64) echo "uv-x86_64-unknown-linux-gnu.tar.gz" ;;
        *) echo "[ERROR] Unsupported Linux architecture: ${arch_name}" ; return 1 ;;
      esac
      ;;
    *)
      echo "[ERROR] Unsupported platform for uv auto-install: ${os_name}"
      return 1
      ;;
  esac
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
  local url="$2"
  local code=""
  local elapsed_ms=""
  local time_total=""
  local probe_result=""
  probe_result="$(curl -I -L -sS -o /dev/null -w '%{http_code} %{time_total}' --max-time "${UV_PROBE_TIMEOUT_SEC}" "${url}" || true)"
  code="$(awk '{print $1}' <<<"${probe_result}")"
  time_total="$(awk '{print $2}' <<<"${probe_result}")"
  elapsed_ms="$(awk -v t="${time_total:-0}" 'BEGIN { printf "%d", t * 1000 + 0.5 }')"

  if [[ "${code}" == 2* || "${code}" == 3* ]]; then
    echo "[PROBE] uv ${name}: ok, ${elapsed_ms} ms, ${url}"
    if [[ -z "${UV_SELECTED_URL}" || -z "${UV_SELECTED_MS}" || "${elapsed_ms}" -lt "${UV_SELECTED_MS}" ]]; then
      UV_SELECTED_NAME="${name}"
      UV_SELECTED_URL="${url}"
      UV_SELECTED_MS="${elapsed_ms}"
    fi
  else
    echo "[PROBE] uv ${name}: failed, ${elapsed_ms} ms, status=${code:-000}, ${url}"
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
