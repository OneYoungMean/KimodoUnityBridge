from __future__ import annotations

import argparse
import contextlib
import gc
import json
import os
from collections import deque
from itertools import count
import io
from pathlib import Path
import socket
import threading
import time
import sys
from typing import Any

from . import bridge_server as bridge_runtime_helpers
from . import quickserver_assets as assets
from .quickserver_setup import ProjectPaths, SetupLogger, discover_project_paths


SUPERVISOR_LOG_FILE_NAME = "bridge_server.log"
DEFAULT_TASK_ID_PREFIX = "task"


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Kimodo QuickServer supervisor")
    subparsers = parser.add_subparsers(dest="action", required=True)

    run_parser = subparsers.add_parser("run")
    run_parser.add_argument("--output", choices=("console", "file"), default="console")
    run_parser.add_argument("--log")
    run_parser.add_argument("--watchpid", type=int, default=0)
    run_parser.add_argument("--force-setup", action="store_true")

    # Legacy CLI compatibility only. Runtime semantics now come from TCP generate requests.
    run_parser.add_argument("--model", default=assets.DEFAULT_MODEL_NAME)
    run_parser.add_argument("--highvram", action="store_true")
    run_parser.add_argument("--models-root")
    run_parser.add_argument("--device")
    run_parser.add_argument("--force-hf-download", action="store_true")
    run_parser.add_argument("--venv")
    run_parser.add_argument("--unlock-stale", action="store_true")
    run_parser.add_argument("--force", action="store_true")
    return parser


def _prepare_logger(
    paths: ProjectPaths,
    output_mode: str,
    log_path: str | None,
    default_name: str,
    append: bool = False,
) -> SetupLogger:
    final_log_path = Path(log_path).resolve() if log_path else paths.log_dir / default_name
    paths.log_dir.mkdir(parents=True, exist_ok=True)
    return SetupLogger(output_mode, final_log_path, append=append)


class _TeeTextStream(io.TextIOBase):
    def __init__(self, primary, secondary):
        self._primary = primary
        self._secondary = secondary

    @property
    def encoding(self):
        return getattr(self._primary, "encoding", "utf-8")

    def write(self, s):
        text = "" if s is None else str(s)
        self._primary.write(text)
        self._secondary.write(text)
        return len(text)

    def flush(self):
        self._primary.flush()
        self._secondary.flush()

    def isatty(self):
        return bool(getattr(self._primary, "isatty", lambda: False)())


@contextlib.contextmanager
def _redirect_process_output(paths: ProjectPaths, output_mode: str, log_path: str | None, default_name: str):
    final_log_path = Path(log_path).resolve() if log_path else paths.log_dir / default_name
    final_log_path.parent.mkdir(parents=True, exist_ok=True)

    with final_log_path.open("a", encoding="utf-8", newline="\n") as log_stream:
        original_stdout = sys.stdout
        original_stderr = sys.stderr
        tee_stdout = _TeeTextStream(original_stdout, log_stream) if str(output_mode or "").strip().lower() == "console" else log_stream
        tee_stderr = _TeeTextStream(original_stderr, log_stream) if str(output_mode or "").strip().lower() == "console" else log_stream
        sys.stdout = tee_stdout
        sys.stderr = tee_stderr
        try:
            yield final_log_path
        finally:
            try:
                sys.stdout.flush()
            except Exception:
                pass
            try:
                sys.stderr.flush()
            except Exception:
                pass
            sys.stdout = original_stdout
            sys.stderr = original_stderr


def _pid_is_running(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        import ctypes.wintypes

        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, int(pid))
        if not handle:
            return False
        try:
            code = ctypes.wintypes.DWORD()
            if not ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return int(code.value) == 259
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    return Path(f"/proc/{pid}").exists()


def _bool_value(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    text = str(value).strip().lower()
    if not text:
        return default
    return text in {"1", "true", "yes", "on"}


def _remove_file(path: Path) -> None:
    try:
        if path.exists():
            path.unlink()
    except Exception:
        pass


def _read_serverport(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except Exception:
        return {}

    data: dict[str, str] = {}
    for line in lines:
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip().lower()] = value.strip()
    return data


def _can_connect(host: str, port: int, timeout_seconds: float = 1.0) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout_seconds):
            return True
    except OSError:
        return False


def _try_reuse_existing_supervisor(serverport_path: Path, logger: SetupLogger) -> bool:
    data = _read_serverport(serverport_path)
    host = str(data.get("host") or "").strip()
    port_text = str(data.get("port") or "").strip()
    if not host or not port_text:
        return False

    try:
        port = int(port_text)
    except ValueError:
        _remove_file(serverport_path)
        return False

    if port <= 0:
        _remove_file(serverport_path)
        return False

    pid_text = str(data.get("pid") or "").strip()
    if pid_text:
        try:
            if not _pid_is_running(int(pid_text)):
                _remove_file(serverport_path)
                return False
        except ValueError:
            _remove_file(serverport_path)
            return False

    if not _can_connect(host, port):
        _remove_file(serverport_path)
        return False

    logger.log(f"[INFO] Reusing active quickserver_cli at {host}:{port}")
    return True


def _write_serverport(path: Path, host: str, port: int, state_name: str) -> None:
    bridge_runtime_helpers._write_text_atomic(
        str(path),
        "\n".join(
            [
                f"{host}:{port}",
                f"host={host}",
                f"port={port}",
                f"state={state_name}",
                f"pid={os.getpid()}",
                "",
            ]
        ),
    )


def _build_signature(config: dict[str, Any]) -> str:
    return "|".join(
        [
            f"model={config['model']}",
            f"highvram={int(bool(config['highvram']))}",
            f"force_cpu={int(bool(config['force_cpu']))}",
            f"models_root={config['models_root']}",
            f"force_hf_download={int(bool(config['force_hf_download']))}",
            f"simulate_vram_gb={config['simulate_vram_gb']}",
        ]
    )


def _normalize_runtime_config(req: dict[str, Any], defaults: dict[str, Any]) -> dict[str, Any]:
    model = str(req.get("model") or defaults.get("model") or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME
    models_root = str(req.get("models_root") or defaults.get("models_root") or "").strip()
    raw_device = str(req.get("device") or defaults.get("device") or "").strip().lower()
    force_cpu = _bool_value(req.get("force_cpu"), _bool_value(defaults.get("force_cpu")))
    if raw_device == "cpu":
        force_cpu = True

    return {
        "model": model,
        "highvram": _bool_value(req.get("highvram"), _bool_value(defaults.get("highvram"))),
        "force_cpu": force_cpu,
        "models_root": models_root,
        "force_hf_download": _bool_value(req.get("force_hf_download"), _bool_value(defaults.get("force_hf_download"))),
        "simulate_vram_gb": int(req.get("simulate_vram_gb") or defaults.get("simulate_vram_gb") or 0),
    }


def _unload_runtime_model(state: dict[str, Any], logger: SetupLogger) -> None:
    model = state.get("model")
    if model is None:
        return

    logger.log("[INFO] Releasing current Kimodo runtime.")
    state["model"] = None
    state["fps"] = 30
    state["runtime_signature"] = ""
    state["runtime_config"] = None
    state["resolved_model_name"] = ""
    state["runtime_device"] = ""

    try:
        del model
    except Exception:
        pass

    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except Exception:
        pass


def _ensure_runtime(
    state: dict[str, Any],
    config: dict[str, Any],
    kimodo_root: str,
    logger: SetupLogger,
) -> dict[str, Any]:
    signature = _build_signature(config)
    existing_signature = str(state.get("runtime_signature") or "")
    if existing_signature == signature and state.get("model") is not None:
        return {
            "model": state["resolved_model_name"],
            "device": state["runtime_device"],
            "fps": int(state["fps"]),
            "signature": signature,
            "reused": True,
        }

    if state.get("model") is not None:
        _unload_runtime_model(state, logger)

    if config["highvram"]:
        os.environ["KIMODO_HIGHVRAM"] = "1"
    else:
        os.environ.pop("KIMODO_HIGHVRAM", None)

    if config["models_root"]:
        os.environ["KIMODO_MODELS_ROOT"] = config["models_root"]
    else:
        os.environ.pop("KIMODO_MODELS_ROOT", None)

    if int(config["simulate_vram_gb"] or 0) > 0:
        os.environ["KIMODO_SIMULATE_VRAM_GB"] = str(int(config["simulate_vram_gb"]))
    else:
        os.environ.pop("KIMODO_SIMULATE_VRAM_GB", None)

    requested_device = "cpu" if config["force_cpu"] else None
    runtime_profile = bridge_runtime_helpers._runtime_self_check(requested_device)
    os.environ["KIMODO_RUNTIME_BACKEND_PROFILE"] = runtime_profile.backend_profile
    os.environ["KIMODO_RUNTIME_DEVICE"] = runtime_profile.runtime_device

    logger.log(
        "[INFO] Preparing runtime: "
        f"model={config['model']} highvram={config['highvram']} force_cpu={config['force_cpu']} "
        f"models_root={config['models_root'] or '<default>'} device={runtime_profile.runtime_device}"
    )

    force_download_site = assets.DownloadSite.HUGGINGFACE if config["force_hf_download"] else None
    plan = bridge_runtime_helpers._provision_bridge_assets(
        kimodo_root,
        config["model"],
        runtime_profile=runtime_profile,
        force_download_site=force_download_site,
    )

    from kimodo.bridge.bridge_load_model import load_bridge_model

    resolved_model_name = plan.resolved_model.local_name
    model = load_bridge_model(
        resolved_model_name,
        models_root=plan.models_root,
        device=runtime_profile.runtime_device,
    )

    state["model"] = model
    state["fps"] = int(model.fps)
    state["runtime_signature"] = signature
    state["runtime_config"] = dict(config)
    state["resolved_model_name"] = resolved_model_name
    state["runtime_device"] = runtime_profile.runtime_device
    logger.log(
        f"[INFO] Runtime ready: model={resolved_model_name} device={runtime_profile.runtime_device} fps={int(model.fps)}"
    )
    return {
        "model": resolved_model_name,
        "device": runtime_profile.runtime_device,
        "fps": int(model.fps),
        "signature": signature,
        "reused": False,
    }


def _execute_generate(task_request: dict[str, Any], model: Any, cancel_event: threading.Event) -> tuple[dict[str, Any], bytes | None]:
    from kimodo.tools import seed_everything

    prompt = str(task_request.get("prompt", "A person walks forward.")).strip()
    if not prompt.endswith("."):
        prompt += "."

    duration = float(task_request.get("duration", 5.0))
    seed = task_request.get("seed")
    diffusion_steps = int(task_request.get("diffusion_steps", 100))
    constraints_json = task_request.get("constraints_json", "")

    if seed is not None:
        seed_everything(int(seed))

    num_frames = max(1, int(duration * float(model.fps)))
    constraints = bridge_runtime_helpers._load_constraints(constraints_json, model)
    progress_bar = bridge_runtime_helpers._make_cancelable_progress_bar(cancel_event)

    output = model(
        [prompt],
        [num_frames],
        constraint_lst=constraints,
        num_denoising_steps=diffusion_steps,
        num_samples=1,
        multi_prompt=True,
        num_transition_frames=5,
        post_processing=True,
        return_numpy=True,
        progress_bar=progress_bar,
    )
    if cancel_event.is_set():
        raise bridge_runtime_helpers.GenerateCancelledError("Generation canceled.")

    output_format = bridge_runtime_helpers._resolve_requested_output_format(task_request)
    if output_format == "flatbuf_motion_v1":
        payload = bridge_runtime_helpers._build_generate_flatbuffer_payload(model, output, sample_index=0)
        return {
            "status": "done",
            "output_format": "flatbuf_motion_v1",
            "byte_length": len(payload),
        }, payload

    return bridge_runtime_helpers._build_generate_response(model, output, prompt, sample_index=0), None


def _build_streaming_status_message(server_state: str, queue_index: int, task_id: str) -> tuple[str, str]:
    normalized = str(server_state or "").strip().lower()
    if normalized == "loading_runtime":
        return "loading", f"Preparing runtime for task '{task_id}'..."
    if normalized == "generating":
        if queue_index > 0:
            return "queued", f"Task '{task_id}' waiting in queue. queue_index={queue_index}"
        return "progress", f"Generating task '{task_id}'..."
    if queue_index > 0:
        return "queued", f"Task '{task_id}' waiting in queue. queue_index={queue_index}"
    return "progress", f"Task '{task_id}' is still running..."


def _attach_task_id(payload: dict[str, Any], task_id: str) -> dict[str, Any]:
    result = dict(payload or {})
    normalized_task_id = str(task_id or "").strip()
    if normalized_task_id:
        result["task_id"] = normalized_task_id
        result["id"] = normalized_task_id
    return result


def _write_protocol_message(file, writer_lock: threading.Lock, payload: dict[str, Any], binary_payload: bytes | None = None) -> None:
    with writer_lock:
        bridge_runtime_helpers._write_json_line(file, payload)
        if binary_payload:
            file.write(binary_payload)
            file.flush()


def _run_supervisor(args: argparse.Namespace, root_dir: str, logger: SetupLogger) -> int:
    host = "127.0.0.1"
    kimodo_root = str(Path(root_dir).resolve())
    serverport_path = Path(kimodo_root) / "serverport"
    idle_timeout_seconds = max(0, int(float(os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600"))))

    os.environ["KIMODO_ROOT_PATH"] = kimodo_root
    os.environ["KIMODO_BRIDGE_LOG"] = str((Path(kimodo_root) / "log" / SUPERVISOR_LOG_FILE_NAME).resolve())

    if _try_reuse_existing_supervisor(serverport_path, logger):
        return 0

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((host, 0))
    server.listen(16)
    host, port = server.getsockname()
    _write_serverport(serverport_path, host, int(port), "boot")
    logger.log(f"[INFO] quickserver_cli listening on {host}:{port}")

    state: dict[str, Any] = {
        "shutdown": False,
        "owner_pid": max(0, int(args.watchpid or 0)),
        "last_activity": time.time(),
        "default_config": {
            "model": str(args.model or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME,
            "highvram": bool(args.highvram),
            "force_cpu": str(args.device or "").strip().lower() == "cpu",
            "models_root": str(args.models_root or "").strip(),
            "force_hf_download": bool(args.force_hf_download),
            "simulate_vram_gb": 0,
        },
        "runtime_signature": "",
        "runtime_config": None,
        "resolved_model_name": "",
        "runtime_device": "",
        "model": None,
        "fps": 30,
        "queue": deque(),
        "tasks": {},
        "active_task_id": "",
        "active_cancel_event": None,
        "active_command_count": 0,
        "server_state": "boot",
        "task_counter": count(1),
    }
    state_lock = threading.Lock()
    queue_changed = threading.Condition(state_lock)
    runtime_gate = threading.Lock()

    def touch_activity() -> None:
        with state_lock:
            state["last_activity"] = time.time()

    def publish_state(state_name: str) -> None:
        state["server_state"] = state_name
        _write_serverport(serverport_path, host, int(port), state_name)

    def begin_command() -> None:
        with state_lock:
            state["active_command_count"] = int(state.get("active_command_count") or 0) + 1
            state["last_activity"] = time.time()

    def end_command() -> None:
        with state_lock:
            state["active_command_count"] = max(0, int(state.get("active_command_count") or 0) - 1)
            state["last_activity"] = time.time()

    def request_shutdown(reason: str) -> None:
        logger.log(f"[INFO] Supervisor shutdown requested: {reason}")
        with state_lock:
            if state["shutdown"]:
                return
            state["shutdown"] = True
            active_cancel_event = state.get("active_cancel_event")
            if active_cancel_event is not None:
                active_cancel_event.set()
            pending_tasks = list(state["tasks"].values())
            for task in pending_tasks:
                task["response"] = {"status": "cancelled", "message": "Server shutting down."}
                task["binary"] = None
                task["event"].set()
            state["tasks"].clear()
            state["queue"].clear()
            queue_changed.notify_all()

        try:
            server.close()
        except Exception:
            pass

    def lifecycle_monitor_loop() -> None:
        while True:
            time.sleep(1.0)
            with state_lock:
                if state["shutdown"]:
                    return
                owner_pid = int(state.get("owner_pid") or 0)
                idle_seconds = time.time() - float(state.get("last_activity") or 0.0)
                runtime_loaded = state.get("model") is not None
                work_in_flight = bool(state["queue"]) or bool(state.get("active_task_id")) or int(state.get("active_command_count") or 0) > 0

            if owner_pid > 0 and not _pid_is_running(owner_pid):
                request_shutdown(f"owner pid {owner_pid} exited")
                return

            if idle_timeout_seconds > 0 and not work_in_flight and idle_seconds >= idle_timeout_seconds:
                request_shutdown(f"idle timeout reached ({int(idle_seconds)}s)")
                return

            if not work_in_flight and runtime_loaded and idle_seconds >= max(30, idle_timeout_seconds // 2 if idle_timeout_seconds > 0 else 300):
                with runtime_gate:
                    with state_lock:
                        if (
                            state["model"] is not None
                            and not state["queue"]
                            and not state["active_task_id"]
                            and int(state.get("active_command_count") or 0) == 0
                        ):
                            _unload_runtime_model(state, logger)

    def worker_loop() -> None:
        while True:
            with queue_changed:
                while not state["shutdown"] and not state["queue"]:
                    queue_changed.wait(timeout=0.5)
                if state["shutdown"]:
                    return
                task = state["queue"].popleft()
                task_id = task["task_id"]
                cancel_event = task["cancel_event"]
                state["active_task_id"] = task_id
                state["active_cancel_event"] = cancel_event
                publish_state("generating")

            try:
                with state_lock:
                    runtime_config = dict(task["runtime_config"])
                with runtime_gate:
                    publish_state("loading_runtime")
                    _ensure_runtime(state, runtime_config, kimodo_root, logger)
                    publish_state("generating")
                response, binary_payload = _execute_generate(task["request"], state["model"], cancel_event)
            except bridge_runtime_helpers.GenerateCancelledError as exc:
                response = {"status": "cancelled", "message": str(exc)}
                binary_payload = None
            except Exception as exc:
                response = {
                    "status": "error",
                    "message": str(exc),
                }
                binary_payload = None
                logger.log(f"[ERROR] Generate task {task_id} failed: {exc}")
            finally:
                with queue_changed:
                    task["state"] = str(response.get("status") or "done")
                    task["status_message"] = str(response.get("message") or "")
                    task["response"] = _attach_task_id(response, task_id)
                    task["binary"] = binary_payload
                    task["event"].set()
                    state["tasks"].pop(task_id, None)
                    state["active_task_id"] = ""
                    state["active_cancel_event"] = None
                    state["last_activity"] = time.time()
                    publish_state("idle")
                    queue_changed.notify_all()

    threading.Thread(target=lifecycle_monitor_loop, daemon=True).start()
    threading.Thread(target=worker_loop, daemon=True).start()

    def resolve_request_task_id(request: dict[str, Any]) -> str:
        raw_task_id = str(request.get("task_id") or request.get("id") or "").strip()
        if raw_task_id:
            return raw_task_id

        sequence = next(state["task_counter"])
        return f"{DEFAULT_TASK_ID_PREFIX}-{int(time.time() * 1000)}-{sequence}"

    def select_cancel_target_locked() -> dict[str, Any] | None:
        if state["queue"]:
            return state["queue"][0]

        active_task_id = str(state.get("active_task_id") or "")
        if active_task_id:
            return state["tasks"].get(active_task_id)

        return None

    def cancel_task(task_id: str) -> dict[str, Any]:
        with queue_changed:
            resolved_task = None
            normalized_task_id = str(task_id or "").strip()
            if normalized_task_id:
                resolved_task = state["tasks"].get(normalized_task_id)
            else:
                resolved_task = select_cancel_target_locked()

            task = resolved_task
            if task is None:
                return {"status": "idle", "message": "No cancellable task found."}

            resolved_task_id = str(task["task_id"])

            if state.get("active_task_id") == resolved_task_id:
                task["cancel_event"].set()
                task["state"] = "cancelling"
                task["status_message"] = f"Cancellation requested for '{resolved_task_id}'."
                return _attach_task_id(
                    {"status": "cancelling", "message": task["status_message"]},
                    resolved_task_id)

            try:
                state["queue"].remove(task)
            except ValueError:
                pass
            task["state"] = "cancelled"
            task["status_message"] = f"Task '{resolved_task_id}' was removed from queue."
            task["response"] = _attach_task_id(
                {"status": "cancelled", "message": task["status_message"]},
                resolved_task_id)
            task["binary"] = None
            task["event"].set()
            state["tasks"].pop(resolved_task_id, None)
            return _attach_task_id(
                {"status": "cancelled", "message": task["status_message"]},
                resolved_task_id)

    def stream_task_to_client(task: dict[str, Any], file, writer_lock: threading.Lock) -> None:
        task_id = str(task["task_id"] or "")
        last_stream_status = ""
        last_stream_message = ""
        last_stream_time = 0.0

        try:
            while True:
                if task["event"].wait(timeout=0.5):
                    break

                with state_lock:
                    if state["shutdown"]:
                        break
                    current_state = str(state.get("server_state") or "")
                    active_task_id = str(state.get("active_task_id") or "")
                    queue_snapshot = list(state.get("queue") or [])

                queue_index = -1
                for index, queued_task in enumerate(queue_snapshot):
                    if queued_task is task:
                        queue_index = index + 1
                        break

                if active_task_id and active_task_id != task_id and queue_index < 0:
                    queue_index = 1

                if str(task.get("state") or "") == "cancelling":
                    stream_status = "cancelling"
                    stream_message = str(task.get("status_message") or f"Cancellation requested for '{task_id}'.")
                else:
                    stream_status, stream_message = _build_streaming_status_message(current_state, queue_index, task_id)

                now = time.time()
                should_emit = (
                    stream_status != last_stream_status
                    or stream_message != last_stream_message
                    or (now - last_stream_time) >= 2.0
                )
                if should_emit:
                    _write_protocol_message(
                        file,
                        writer_lock,
                        _attach_task_id(
                            {
                                "status": stream_status,
                                "message": stream_message,
                            },
                            task_id))
                    last_stream_status = stream_status
                    last_stream_message = stream_message
                    last_stream_time = now

            response = task.get("response")
            if response is None:
                response = _attach_task_id({"status": "cancelled", "message": "Server shutting down."}, task_id)
            binary_payload = task.get("binary")
            _write_protocol_message(file, writer_lock, response, binary_payload)
        except Exception:
            return

    def client_worker(conn: socket.socket, addr: tuple[str, int]) -> None:
        with conn:
            file = conn.makefile("rwb")
            writer_lock = threading.Lock()
            while True:
                try:
                    line = file.readline()
                except (ConnectionResetError, BrokenPipeError, OSError):
                    return
                if not line:
                    return

                try:
                    request = json.loads(line.decode("utf-8").strip())
                except Exception as exc:
                    _write_protocol_message(file, writer_lock, {"status": "error", "message": f"Bad JSON: {exc}"})
                    continue

                touch_activity()
                cmd = str(request.get("cmd", "") or "").strip().lower()

                try:
                    if cmd == "generate":
                        task_id = resolve_request_task_id(request)
                        request["task_id"] = task_id
                        request["id"] = task_id

                        with queue_changed:
                            active_config = _normalize_runtime_config(request, state.get("default_config") or {})
                            state["default_config"] = dict(active_config)
                            owner_pid = int(request.get("owner_pid") or 0)
                            if owner_pid > 0:
                                state["owner_pid"] = owner_pid
                            if task_id in state["tasks"]:
                                _write_protocol_message(
                                    file,
                                    writer_lock,
                                    _attach_task_id(
                                        {"status": "error", "message": f"Duplicate task_id '{task_id}'."},
                                        task_id))
                                continue

                            task = {
                                "task_id": task_id,
                                "request": dict(request),
                                "runtime_config": dict(active_config),
                                "cancel_event": threading.Event(),
                                "event": threading.Event(),
                                "response": None,
                                "binary": None,
                                "state": "queued",
                                "status_message": f"Task '{task_id}' waiting in queue.",
                            }
                            state["tasks"][task_id] = task
                            state["queue"].append(task)
                            queue_changed.notify_all()

                        threading.Thread(target=stream_task_to_client, args=(task, file, writer_lock), daemon=True).start()
                    elif cmd == "cancel":
                        task_id = str(request.get("task_id") or request.get("id") or "").strip()
                        _write_protocol_message(file, writer_lock, cancel_task(task_id))
                    elif cmd == "quit":
                        _write_protocol_message(file, writer_lock, {"status": "bye"})
                        request_shutdown("quit command")
                        return
                    else:
                        _write_protocol_message(file, writer_lock, {"status": "error", "message": f"Unknown cmd: {cmd!r}"})
                except Exception as exc:
                    logger.log(f"[ERROR] Command '{cmd}' failed: {exc}")
                    _write_protocol_message(file, writer_lock, {"status": "error", "message": str(exc)})

    try:
        publish_state("boot")
        while True:
            with state_lock:
                if state["shutdown"]:
                    break
            try:
                conn, addr = server.accept()
            except OSError:
                with state_lock:
                    if state["shutdown"]:
                        break
                raise
            threading.Thread(target=client_worker, args=(conn, addr), daemon=True).start()
    finally:
        with runtime_gate:
            with state_lock:
                _unload_runtime_model(state, logger)
        _remove_file(serverport_path)
        try:
            server.close()
        except Exception:
            pass

    return 0


def main(argv: list[str] | None = None, *, root_dir: str | None = None, source_root: str | None = None) -> int:
    del source_root

    parser = _build_parser()
    args = parser.parse_args(list(sys.argv[1:] if argv is None else argv))
    paths = discover_project_paths(root_dir)
    with _redirect_process_output(paths, args.output, args.log, SUPERVISOR_LOG_FILE_NAME):
        with _prepare_logger(paths, "file", args.log, SUPERVISOR_LOG_FILE_NAME, append=True) as logger:
            return _run_supervisor(args, str(paths.root_dir), logger)


if __name__ == "__main__":
    raise SystemExit(main())
