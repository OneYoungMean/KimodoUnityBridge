from __future__ import annotations

import argparse
import contextlib
import gc
import json
import os
from collections import deque
from itertools import count
import io
import math
from pathlib import Path
import secrets
import socket
import threading
import time
import sys
from typing import Any

from . import bridge_server as bridge_runtime_helpers
from . import ardy_backend
from . import animation_handles
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

    # Defaults only; TCP generate requests own runtime semantics.
    run_parser.add_argument("--model", default=assets.DEFAULT_MODEL_NAME)
    run_parser.add_argument(
        "--text-encoder-mode",
        choices=(assets.TEXT_ENCODER_MODE_HIGH_PERFORMANCE, assets.TEXT_ENCODER_MODE_HIGH_PRECISION),
        default=assets.DEFAULT_TEXT_ENCODER_MODE,
    )
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
            f"text_encoder_mode={config['text_encoder_mode']}",
            f"models_root={config['models_root']}",
            f"force_hf_download={int(bool(config['force_hf_download']))}",
            f"simulate_vram_gb={config['simulate_vram_gb']}",
        ]
    )


def _normalize_runtime_config(req: dict[str, Any], defaults: dict[str, Any]) -> dict[str, Any]:
    removed_keys = [key for key in ("highvram", "force_cpu") if key in req]
    if removed_keys:
        raise ValueError(
            "Removed generate fields are not supported: "
            + ", ".join(removed_keys)
            + ". Use text_encoder_mode and simulate_vram_gb."
        )
    model = str(req.get("model") or defaults.get("model") or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME
    models_root = str(req.get("models_root") or defaults.get("models_root") or "").strip()
    raw_simulated_vram = (
        req.get("simulate_vram_gb")
        if "simulate_vram_gb" in req
        else defaults.get("simulate_vram_gb")
    )
    simulated_vram_gb = None
    if raw_simulated_vram is not None and str(raw_simulated_vram).strip() != "":
        simulated_vram_gb = float(raw_simulated_vram)
        if not math.isfinite(simulated_vram_gb) or simulated_vram_gb < 0.0:
            raise ValueError("simulate_vram_gb must be a finite value greater than or equal to 0.")

    return {
        "model": model,
        "text_encoder_mode": assets.normalize_text_encoder_mode(
            req.get("text_encoder_mode")
            if "text_encoder_mode" in req
            else defaults.get("text_encoder_mode")
        ),
        "models_root": models_root,
        "force_hf_download": _bool_value(req.get("force_hf_download"), _bool_value(defaults.get("force_hf_download"))),
        "simulate_vram_gb": simulated_vram_gb,
    }


def _unload_runtime_model(state: dict[str, Any], logger: SetupLogger) -> None:
    model = state.get("model")
    if model is None:
        return

    logger.log("[INFO] Releasing current motion runtime.")
    state["model"] = None
    state["fps"] = 30
    state["runtime_signature"] = ""
    state["runtime_config"] = None
    state["resolved_model_name"] = ""
    state["runtime_device"] = ""
    state["motion_profile"] = None
    state["text_encoder_decision"] = None

    try:
        del model
    except Exception:
        pass

    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
        if torch.backends.mps.is_available():
            torch.mps.empty_cache()
        if hasattr(torch, "xpu") and torch.xpu.is_available() and hasattr(torch.xpu, "empty_cache"):
            torch.xpu.empty_cache()
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

    os.environ["KIMODO_TEXT_ENCODER_MODE"] = config["text_encoder_mode"]

    if config["models_root"]:
        os.environ["KIMODO_MODELS_ROOT"] = config["models_root"]
    else:
        os.environ.pop("KIMODO_MODELS_ROOT", None)

    if config["simulate_vram_gb"] is not None:
        os.environ["KIMODO_SIMULATE_VRAM_GB"] = str(config["simulate_vram_gb"])
    else:
        os.environ.pop("KIMODO_SIMULATE_VRAM_GB", None)

    runtime_profile = bridge_runtime_helpers._runtime_self_check(None)
    runtime_decision = assets.resolve_text_encoder_runtime(
        config["text_encoder_mode"],
        runtime_profile.runtime_device,
        runtime_profile.total_vram_gb,
        nf4_available=runtime_profile.nf4_available,
        int8_accelerator_available=runtime_profile.int8_accelerator_available,
        fp16_accelerator_available=runtime_profile.fp16_accelerator_available,
    )
    if config.get("_force_text_encoder_cpu"):
        runtime_decision = assets.force_text_encoder_cpu(runtime_decision)
        os.environ["KIMODO_TEXT_ENCODER_FORCE_CPU"] = "1"
    else:
        os.environ.pop("KIMODO_TEXT_ENCODER_FORCE_CPU", None)
    os.environ["KIMODO_RUNTIME_BACKEND_PROFILE"] = runtime_profile.backend_profile
    os.environ["KIMODO_RUNTIME_DEVICE"] = runtime_decision.motion_device

    logger.log(
        "[INFO] Preparing runtime: "
        f"model={config['model']} text_encoder_mode={config['text_encoder_mode']} "
        f"models_root={config['models_root'] or '<default>'} "
        f"motion_device={runtime_decision.motion_device} encoder_route={runtime_decision.encoder_route} "
        f"encoder_device={runtime_decision.encoder_device} vram={runtime_decision.effective_vram_gb:g}GB"
    )

    motion_profile = assets.resolve_motion_model_profile(config["model"])
    if motion_profile is not None and motion_profile.backend == "ardy":
        models_root, _ = assets.resolve_models_root(kimodo_root, config["models_root"])
        encoder_route = runtime_decision.encoder_route
        encoder_layout = assets.select_text_encoder_layout_for_route(
            encoder_route,
            models_root,
            runtime_decision.encoder_device,
        )
        source_root = Path(kimodo_root).resolve() / "kimodo"
        if not (source_root / "pyproject.toml").is_file():
            source_root = Path(kimodo_root).resolve()
        assets.scrub_removed_runtime_env(os.environ)
        os.environ.update(
            assets.build_runtime_env(
                root_dir=kimodo_root,
                source_root=source_root,
                models_root=models_root,
                text_encoder_mode=runtime_decision.mode,
                encoder_device=runtime_decision.encoder_device,
                encoder_route=encoder_route,
                encoder_layout_id=encoder_layout.layout_id,
            )
        )
        download_counter = [0]
        recovery_flag_dir = Path(kimodo_root).resolve() / "archive" / "recovery_flags"
        force_download_site = assets.DownloadSite.HUGGINGFACE if config["force_hf_download"] else None
        for encoder_asset in encoder_layout.download_assets:
            assets.ensure_asset_present(
                encoder_asset,
                models_root / encoder_asset.local_dir_name,
                logger,
                recovery_flag_dir,
                download_counter,
                force_site=force_download_site,
            )
        logger.log(
            f"[INFO] ARDY reusing Kimodo text encoder: route={encoder_route} "
            f"layout={encoder_layout.layout_id} models_root={models_root} downloads={download_counter[0]}"
        )
        runtime_config = dict(config)
        runtime_config["models_root"] = str(models_root)
        model = ardy_backend.load_runtime(
            motion_profile,
            runtime_config,
            kimodo_root,
            runtime_decision.motion_device,
        )
        state["model"] = model
        state["fps"] = int(motion_profile.source_fps)
        state["runtime_signature"] = signature
        state["runtime_config"] = dict(config)
        state["resolved_model_name"] = motion_profile.model_name
        state["runtime_device"] = runtime_decision.motion_device
        state["motion_profile"] = motion_profile
        state["text_encoder_decision"] = runtime_decision
        logger.log(
            f"[INFO] Runtime ready: model={motion_profile.model_name} "
            f"device={runtime_decision.motion_device} fps={motion_profile.source_fps:g}"
        )
        return {
            "model": motion_profile.model_name,
            "device": runtime_decision.motion_device,
            "fps": int(motion_profile.source_fps),
            "signature": signature,
            "reused": False,
        }

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
        device=plan.runtime_decision.motion_device,
    )

    state["model"] = model
    state["fps"] = int(model.fps)
    state["runtime_signature"] = signature
    state["runtime_config"] = dict(config)
    state["resolved_model_name"] = resolved_model_name
    state["runtime_device"] = plan.runtime_decision.motion_device
    state["motion_profile"] = None
    state["text_encoder_decision"] = plan.runtime_decision
    logger.log(
        f"[INFO] Runtime ready: model={resolved_model_name} device={plan.runtime_decision.motion_device} fps={int(model.fps)}"
    )
    return {
        "model": resolved_model_name,
        "device": plan.runtime_decision.motion_device,
        "fps": int(model.fps),
        "signature": signature,
        "reused": False,
    }


def _execute_generate(
    task_request: dict[str, Any],
    model: Any,
    cancel_event: threading.Event,
    motion_profile: assets.MotionModelProfile | None,
    spool: animation_handles.AnimationHandleStore,
    kimodo_root: str,
) -> tuple[dict[str, Any], bytes | None]:
    if motion_profile is not None and motion_profile.backend == "ardy":
        return ardy_backend.execute_generate(
            task_request,
            model,
            motion_profile,
            cancel_event,
            spool,
            kimodo_root,
        )

    from kimodo.tools import seed_everything

    prompt = str(task_request.get("prompt", "A person walks forward.")).strip()
    if not prompt.endswith("."):
        prompt += "."

    duration = float(task_request.get("duration", 5.0))
    seed = task_request.get("seed")
    diffusion_steps = int(task_request.get("diffusion_steps", 100))
    cfg_text_weight = bridge_runtime_helpers._resolve_cfg_text_weight(task_request)
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
        cfg_weight=[cfg_text_weight, 2.0],
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
    if output_format in {"flatbuf_motion_v1", "kmb_handle_v1"}:
        payload = bridge_runtime_helpers._build_generate_flatbuffer_payload(model, output, sample_index=0)
        if output_format == "kmb_handle_v1":
            handle_info = spool.publish(payload, description=prompt)
            return {
                "status": "done",
                "output_format": output_format,
                "byte_length": 0,
                "clip_handle": handle_info["handle"],
                "handle_info": handle_info,
            }, None
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
    return result


def _attach_runtime_metadata(
    payload: dict[str, Any],
    decision: assets.TextEncoderRuntimeDecision | None,
) -> dict[str, Any]:
    result = dict(payload or {})
    if decision is not None:
        result.update(
            {
                "text_encoder_mode": decision.mode,
                "text_encoder_route": decision.encoder_route,
                "text_encoder_device": decision.encoder_device,
                "text_encoder_reason": decision.reason,
                "effective_vram_gb": decision.effective_vram_gb,
            }
        )
    return result


def _is_accelerator_oom(error: Exception) -> bool:
    try:
        import torch

        if isinstance(error, torch.cuda.OutOfMemoryError):
            return True
    except Exception:
        pass
    message = str(error or "").lower()
    return "out of memory" in message and any(name in message for name in ("cuda", "mps", "xpu", "gpu"))


def _write_protocol_message(file, writer_lock: threading.Lock, payload: dict[str, Any], binary_payload: bytes | None = None) -> None:
    with writer_lock:
        bridge_runtime_helpers._write_json_line(file, payload)
        if binary_payload:
            file.write(binary_payload)
            file.flush()


def _read_exact(file, byte_length: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < byte_length:
        chunk = file.read(byte_length - len(chunks))
        if not chunk:
            break
        chunks.extend(chunk)
    return bytes(chunks)


def _run_supervisor(args: argparse.Namespace, root_dir: str, logger: SetupLogger) -> int:
    host = "127.0.0.1"
    kimodo_root = str(Path(root_dir).resolve())
    serverport_path = Path(kimodo_root) / "serverport"
    idle_timeout_seconds = max(0, int(float(os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600"))))
    default_session_id = "session:default"
    session_queue_limit = 32

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

    default_config = {
        "model": str(args.model or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME,
        "text_encoder_mode": assets.normalize_text_encoder_mode(args.text_encoder_mode),
        "models_root": str(args.models_root or "").strip(),
        "force_hf_download": bool(args.force_hf_download),
        "simulate_vram_gb": 0.0 if str(args.device or "").strip().lower() == "cpu" else None,
    }

    def new_session(session_id: str, *, explicit: bool, connection_id: str = "") -> dict[str, Any]:
        return {
            "session_id": session_id,
            "explicit": explicit,
            "connection_id": connection_id,
            "default_config": dict(default_config),
            "queue": deque(),
            "active": None,
            "ready": False,
            "closed": False,
        }

    state: dict[str, Any] = {
        "shutdown": False,
        "owner_pid": max(0, int(args.watchpid or 0)),
        "last_activity": time.time(),
        "runtimes": {},
        "sessions": {default_session_id: new_session(default_session_id, explicit=False)},
        "ready_sessions": deque(),
        "tasks": {},
        "active_task_id": "",
        "active_command_count": 0,
        "server_state": "boot",
        "task_counter": count(1),
        "session_counter": count(1),
        "connection_counter": count(1),
        "ardy_spool": animation_handles.create_store(),
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

    def attach_request_id(payload: dict[str, Any], request_id: str) -> dict[str, Any]:
        result = dict(payload or {})
        if request_id:
            result["request_id"] = request_id
        return result

    def mark_session_ready_locked(session: dict[str, Any]) -> None:
        if session["ready"]:
            return
        active = session.get("active")
        if state["shutdown"] or session["closed"]:
            if active is None or not active["cancel_event"].is_set():
                return
            session["ready"] = True
            state["ready_sessions"].append(session["session_id"])
            queue_changed.notify_all()
            return
        if active is None and not session["queue"]:
            return
        if active is not None and active.get("stream_handle"):
            if not active["cancel_event"].is_set() and state["ardy_spool"].stream_is_full(active["stream_handle"]):
                return
        session["ready"] = True
        state["ready_sessions"].append(session["session_id"])
        queue_changed.notify_all()

    def finish_task_locked(
        session: dict[str, Any],
        task: dict[str, Any],
        response: dict[str, Any] | None = None,
        binary_payload: bytes | None = None,
    ) -> None:
        stream_context = task.get("stream_context")
        if stream_context is not None:
            stream_context.close()
        stream_handle = str(task.get("stream_handle") or "")
        if stream_handle:
            state["ardy_spool"].close_stream(stream_handle, notify=False)
        state["ardy_spool"].unpin(task.get("pinned_handles") or ())
        if not task.get("response_sent"):
            final_response = response or {"status": "cancelled", "message": "Task closed."}
            task["response"] = attach_request_id(
                _attach_task_id(final_response, task["task_id"]),
                task["request_id"],
            )
            task["binary"] = binary_payload
            task["event"].set()
        task["state"] = str((response or {}).get("status") or "closed")
        state["tasks"].pop(task["task_id"], None)
        if session.get("active") is task:
            session["active"] = None
        state["active_task_id"] = ""
        if session["closed"] and not session["queue"]:
            state["sessions"].pop(session["session_id"], None)
        else:
            mark_session_ready_locked(session)

    def close_session_locked(session: dict[str, Any], reason: str) -> None:
        session["closed"] = True
        while session["queue"]:
            task = session["queue"].popleft()
            task["state"] = "cancelled"
            finish_task_locked(session, task, {"status": "cancelled", "message": reason})
        active = session.get("active")
        if active is not None:
            active["cancel_event"].set()
            active["state"] = "cancelling"
            mark_session_ready_locked(session)
        else:
            state["sessions"].pop(session["session_id"], None)

    def request_shutdown(reason: str) -> None:
        logger.log(f"[INFO] Supervisor shutdown requested: {reason}")
        with state_lock:
            if state["shutdown"]:
                return
            state["shutdown"] = True
            for session in list(state["sessions"].values()):
                session["closed"] = True
                while session["queue"]:
                    task = session["queue"].popleft()
                    finish_task_locked(
                        session,
                        task,
                        {"status": "cancelled", "message": "Server shutting down."},
                    )
                if session.get("active") is not None:
                    session["active"]["cancel_event"].set()
                    if not session["ready"]:
                        session["ready"] = True
                        state["ready_sessions"].append(session["session_id"])
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
                runtime_loaded = any(runtime.get("model") is not None for runtime in state["runtimes"].values())
                work_in_flight = any(
                    session["queue"] or session.get("active") is not None
                    for session in state["sessions"].values()
                ) or int(state.get("active_command_count") or 0) > 0

            if owner_pid > 0 and not _pid_is_running(owner_pid):
                request_shutdown(f"owner pid {owner_pid} exited")
                return

            if idle_timeout_seconds > 0 and not work_in_flight and idle_seconds >= idle_timeout_seconds:
                request_shutdown(f"idle timeout reached ({int(idle_seconds)}s)")
                return

            if not work_in_flight and runtime_loaded and idle_seconds >= max(30, idle_timeout_seconds // 2 if idle_timeout_seconds > 0 else 300):
                with runtime_gate:
                    with state_lock:
                        if not any(
                            session["queue"] or session.get("active") is not None
                            for session in state["sessions"].values()
                        ) and int(state.get("active_command_count") or 0) == 0:
                            for runtime in state["runtimes"].values():
                                _unload_runtime_model(runtime, logger)
                            state["runtimes"].clear()

    def get_runtime(runtime_config: dict[str, Any]) -> dict[str, Any]:
        signature = _build_signature(runtime_config)
        with runtime_gate:
            runtime = state["runtimes"].get(signature)
            if runtime is None:
                runtime = {}
                _ensure_runtime(runtime, runtime_config, kimodo_root, logger)
                state["runtimes"][signature] = runtime
            return runtime

    def wake_session(session_id: str) -> None:
        with queue_changed:
            session = state["sessions"].get(session_id)
            if session is not None:
                mark_session_ready_locked(session)

    def run_one_shot(task: dict[str, Any], runtime: dict[str, Any]) -> tuple[dict[str, Any], bytes | None]:
        fallback_reason = ""
        try:
            return _execute_generate(
                task["request"],
                runtime["model"],
                task["cancel_event"],
                runtime.get("motion_profile"),
                state["ardy_spool"],
                kimodo_root,
            )
        except Exception as generation_error:
            decision = runtime.get("text_encoder_decision")
            if decision is not None and decision.encoder_device != "cpu" and _is_accelerator_oom(generation_error):
                fallback_reason = str(generation_error)
            else:
                raise
        logger.log("[WARN] Accelerator OOM; retrying with the text encoder on CPU. " + fallback_reason)
        fallback_config = dict(task["runtime_config"])
        fallback_config["_force_text_encoder_cpu"] = True
        with runtime_gate:
            _unload_runtime_model(runtime, logger)
            _ensure_runtime(runtime, fallback_config, kimodo_root, logger)
        return _execute_generate(
            task["request"],
            runtime["model"],
            task["cancel_event"],
            runtime.get("motion_profile"),
            state["ardy_spool"],
            kimodo_root,
        )

    def worker_loop() -> None:
        while True:
            with queue_changed:
                while not state["shutdown"] and not state["ready_sessions"]:
                    queue_changed.wait(timeout=0.5)
                if state["shutdown"] and not state["ready_sessions"]:
                    return
                session_id = state["ready_sessions"].popleft()
                session = state["sessions"].get(session_id)
                if session is None:
                    continue
                session["ready"] = False
                task = session.get("active")
                if task is None:
                    if not session["queue"]:
                        continue
                    task = session["queue"].popleft()
                    session["active"] = task
                task_id = task["task_id"]
                state["active_task_id"] = task_id
                publish_state("generating")

            try:
                publish_state("loading_runtime")
                runtime = get_runtime(task["runtime_config"])
                task["runtime"] = runtime
                publish_state("generating")
                profile = runtime.get("motion_profile")
                is_ardy_stream = (
                    profile is not None
                    and profile.backend == "ardy"
                    and bridge_runtime_helpers._resolve_requested_output_format(task["request"]) == "kmb_handle_v1"
                )

                if is_ardy_stream and task.get("stream_context") is None:
                    stream_context = ardy_backend.ArdyStreamGenerator(
                        task["request"],
                        runtime["model"],
                        profile,
                        state["ardy_spool"],
                        kimodo_root,
                    )
                    capacity_frames = ardy_backend.resolve_stream_capacity_frames(task["request"], profile)
                    joint_parents, joint_names = bridge_runtime_helpers._parents_and_names(
                        runtime["model"],
                        len(runtime["model"].motion_rep.skeleton.bone_order_names),
                    )
                    handle_info = state["ardy_spool"].create_stream(
                        task_id=task_id,
                        session_id=session_id,
                        capacity_frames=capacity_frames,
                        horizon_frames=int(profile.horizon_frames),
                        fps=float(profile.source_fps),
                        model_name=profile.model_name,
                        joint_names=joint_names,
                        joint_parents=joint_parents,
                        motion_rep_fingerprint=profile.motion_rep_fingerprint,
                        description=stream_context.prompt,
                        serializer=lambda output, model=runtime["model"]: bridge_runtime_helpers._build_generate_flatbuffer_payload(
                            model, output, sample_index=0
                        ),
                        cancel=lambda event=task["cancel_event"], sid=session_id: (
                            event.set(),
                            wake_session(sid),
                        ),
                        resume=lambda sid=session_id: wake_session(sid),
                    )
                    task["stream_context"] = stream_context
                    task["stream_handle"] = handle_info["handle"]
                    task["state"] = "streaming"
                    task["response"] = attach_request_id(
                        _attach_task_id(
                            _attach_runtime_metadata(
                                {
                                    "status": "done",
                                    "output_format": "kmb_handle_v1",
                                    "byte_length": 0,
                                    "clip_handle": handle_info["handle"],
                                    "handle_info": handle_info,
                                    "motion_rep_fingerprint": profile.motion_rep_fingerprint,
                                    "resolved_seed": stream_context.resolved_seed,
                                },
                                runtime.get("text_encoder_decision"),
                            ),
                            task_id,
                        ),
                        task["request_id"],
                    )
                    task["response_sent"] = True
                    task["event"].set()
                    with queue_changed:
                        if task["cancel_event"].is_set():
                            finish_task_locked(session, task)
                        else:
                            mark_session_ready_locked(session)
                        publish_state("idle")
                    continue

                if is_ardy_stream:
                    if task["cancel_event"].is_set():
                        with queue_changed:
                            finish_task_locked(session, task)
                            publish_state("idle")
                        continue
                    output = task["stream_context"].generate_horizon()
                    if task["cancel_event"].is_set():
                        with queue_changed:
                            finish_task_locked(session, task)
                            publish_state("idle")
                        continue
                    state["ardy_spool"].append_stream(task["stream_handle"], output)
                    with queue_changed:
                        state["last_activity"] = time.time()
                        mark_session_ready_locked(session)
                        publish_state("idle")
                    continue

                response, binary_payload = run_one_shot(task, runtime)
                response = _attach_runtime_metadata(response, runtime.get("text_encoder_decision"))
            except bridge_runtime_helpers.GenerateCancelledError as exc:
                response = {"status": "cancelled", "message": str(exc)}
                binary_payload = None
            except ardy_backend.ArdyBackendError as exc:
                response = {
                    "status": "error",
                    "error_code": exc.code,
                    "message": str(exc),
                }
                binary_payload = None
            except Exception as exc:
                response = {
                    "status": "error",
                    "message": str(exc),
                }
                binary_payload = None
                logger.log(f"[ERROR] Generate task {task_id} failed: {exc}")
            with queue_changed:
                finish_task_locked(session, task, response, binary_payload)
                state["last_activity"] = time.time()
                publish_state("idle")
                queue_changed.notify_all()

    threading.Thread(target=lifecycle_monitor_loop, daemon=True).start()
    worker_thread = threading.Thread(target=worker_loop, daemon=True)
    worker_thread.start()

    def resolve_request_task_id(request: dict[str, Any]) -> str:
        raw_task_id = str(request.get("task_id") or request.get("id") or "").strip()
        if raw_task_id:
            return raw_task_id

        sequence = next(state["task_counter"])
        return f"{DEFAULT_TASK_ID_PREFIX}-{int(time.time() * 1000)}-{sequence}"

    def cancel_task(session: dict[str, Any], task_id: str) -> dict[str, Any]:
        with queue_changed:
            normalized_task_id = str(task_id or "").strip()
            if normalized_task_id:
                resolved_task = state["tasks"].get(normalized_task_id)
                if resolved_task is not None and resolved_task["session_id"] != session["session_id"]:
                    resolved_task = None
            else:
                resolved_task = session.get("active") or (session["queue"][0] if session["queue"] else None)

            task = resolved_task
            if task is None:
                return {"status": "idle", "message": "No cancellable task found."}

            resolved_task_id = str(task["task_id"])

            if session.get("active") is task:
                task["cancel_event"].set()
                task["state"] = "cancelling"
                task["status_message"] = f"Cancellation requested for '{resolved_task_id}'."
                mark_session_ready_locked(session)
                return _attach_task_id(
                    {"status": "cancelling", "message": task["status_message"]},
                    resolved_task_id)

            try:
                session["queue"].remove(task)
            except ValueError:
                pass
            task["state"] = "cancelled"
            task["status_message"] = f"Task '{resolved_task_id}' was removed from queue."
            finish_task_locked(
                session,
                task,
                {"status": "cancelled", "message": task["status_message"]},
            )
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
                    session = state["sessions"].get(task["session_id"])
                    active_task_id = str(((session or {}).get("active") or {}).get("task_id") or "")
                    queue_snapshot = list((session or {}).get("queue") or [])

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
                        attach_request_id(
                            _attach_task_id(
                                {
                                    "status": stream_status,
                                    "message": stream_message,
                                },
                                task_id),
                            task["request_id"],
                        ))
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
        connection_id = f"connection:{next(state['connection_counter'])}"
        bound_session_id = default_session_id
        with conn:
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            file = conn.makefile("rwb")
            writer_lock = threading.Lock()
            try:
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
                    request_id = str(request.get("request_id") or "").strip()

                    def reply(payload: dict[str, Any], binary_payload: bytes | None = None) -> None:
                        _write_protocol_message(
                            file,
                            writer_lock,
                            attach_request_id(payload, request_id),
                            binary_payload,
                        )

                    try:
                        with state_lock:
                            session = state["sessions"].get(bound_session_id)
                        if session is None:
                            raise ValueError("The TCP Session is closed.")

                        if cmd == "session.open":
                            if bound_session_id != default_session_id:
                                reply({"status": "done", "session_id": bound_session_id})
                                continue
                            session_id = f"session:{next(state['session_counter'])}-{secrets.token_urlsafe(12)}"
                            with queue_changed:
                                state["sessions"][session_id] = new_session(
                                    session_id,
                                    explicit=True,
                                    connection_id=connection_id,
                                )
                            bound_session_id = session_id
                            reply({"status": "done", "session_id": session_id})
                        elif cmd == "session.close":
                            if bound_session_id == default_session_id:
                                reply({"status": "done", "session_id": default_session_id, "server_closing": True})
                                request_shutdown("default session closed")
                            else:
                                with queue_changed:
                                    close_session_locked(session, "Session closed.")
                                reply({"status": "done", "session_id": bound_session_id})
                            return
                        elif cmd == "animation.upload":
                            upload_started = time.monotonic()
                            conn.settimeout(3.0)
                            if str(request.get("format") or "") != "flatbuf_motion_v1":
                                raise animation_handles.AnimationHandleError(
                                    "animation.upload requires format='flatbuf_motion_v1'."
                                )
                            byte_length = int(request.get("byte_length") or 0)
                            if byte_length <= 0 or byte_length > animation_handles.MAX_KMB_BYTES:
                                raise animation_handles.AnimationHandleError(
                                    f"animation.upload byte_length must be in [1, {animation_handles.MAX_KMB_BYTES}]."
                                )
                            payload = _read_exact(file, byte_length)
                            conn.settimeout(None)
                            if payload is None or len(payload) != byte_length:
                                raise animation_handles.AnimationHandleError(
                                    f"animation.upload ended after {len(payload or b'')} of {byte_length} bytes."
                                )
                            info = state["ardy_spool"].publish(
                                payload,
                                description=str(request.get("description") or ""),
                                motion_rep_fingerprint=str(request.get("motion_rep_fingerprint") or ""),
                            )
                            if time.monotonic() - upload_started > 3.0:
                                state["ardy_spool"].release(info["handle"])
                                raise animation_handles.AnimationHandleError("animation.upload exceeded the 3 second limit.")
                            reply({"status": "done", "output_format": "kmb_handle_v1", "handle_info": info})
                        elif cmd == "animation.info":
                            info = state["ardy_spool"].info(str(request.get("handle") or ""))
                            if info.get("is_stream") and info.get("session_id") != bound_session_id:
                                raise animation_handles.AnimationHandleNotFoundError("Animation stream belongs to another Session.")
                            reply({"status": "done", "handle_info": info})
                        elif cmd == "animation.download":
                            handle = str(request.get("handle") or "")
                            info = state["ardy_spool"].info(handle)
                            if info.get("is_stream") and info.get("session_id") != bound_session_id:
                                raise animation_handles.AnimationHandleNotFoundError("Animation stream belongs to another Session.")
                            payload, info = state["ardy_spool"].download(handle)
                            reply(
                                {
                                    "status": "done",
                                    "output_format": "flatbuf_motion_v1",
                                    "byte_length": len(payload),
                                    "handle_info": info,
                                },
                                payload,
                            )
                        elif cmd == "animation.release":
                            handle = str(request.get("handle") or "")
                            info = state["ardy_spool"].info(handle)
                            if info.get("is_stream") and info.get("session_id") != bound_session_id:
                                raise animation_handles.AnimationHandleNotFoundError("Animation stream belongs to another Session.")
                            expected_instance = str(request.get("server_instance_id") or "")
                            if expected_instance and expected_instance != state["ardy_spool"].server_instance_id:
                                raise animation_handles.AnimationHandleNotFoundError(
                                    "Animation handle belongs to a different QuickServer instance."
                                )
                            released = state["ardy_spool"].release(handle)
                            reply({"status": "done", "released": released, "handle": handle})
                        elif cmd == "generate":
                            task_id = resolve_request_task_id(request)
                            request["task_id"] = task_id

                            with queue_changed:
                                if len(session["queue"]) + (1 if session.get("active") is not None else 0) >= session_queue_limit:
                                    reply({
                                        "status": "error",
                                        "error_code": "session_queue_full",
                                        "message": f"Session Generate queue limit is {session_queue_limit}.",
                                    })
                                    continue
                                active_config = _normalize_runtime_config(request, session["default_config"])
                                session["default_config"] = dict(active_config)
                                owner_pid = int(request.get("owner_pid") or 0)
                                if owner_pid > 0:
                                    state["owner_pid"] = owner_pid
                                if task_id in state["tasks"]:
                                    reply(
                                        _attach_task_id(
                                            {"status": "error", "message": f"Duplicate task_id '{task_id}'."},
                                            task_id))
                                    continue

                                pinned_handles = state["ardy_spool"].pin(
                                    ardy_backend.extract_handle_refs(request.get("constraints_json", ""))
                                )
                                task = {
                                    "task_id": task_id,
                                    "session_id": bound_session_id,
                                    "connection_id": connection_id,
                                    "request_id": request_id,
                                    "request": dict(request),
                                    "runtime_config": dict(active_config),
                                    "cancel_event": threading.Event(),
                                    "event": threading.Event(),
                                    "response": None,
                                    "binary": None,
                                    "state": "queued",
                                    "status_message": f"Task '{task_id}' waiting in queue.",
                                    "pinned_handles": pinned_handles,
                                    "response_sent": False,
                                    "stream_context": None,
                                    "stream_handle": "",
                                }
                                state["tasks"][task_id] = task
                                session["queue"].append(task)
                                mark_session_ready_locked(session)

                            threading.Thread(target=stream_task_to_client, args=(task, file, writer_lock), daemon=True).start()
                        elif cmd == "cancel":
                            task_id = str(request.get("task_id") or request.get("id") or "").strip()
                            reply(cancel_task(session, task_id))
                        elif cmd == "quit":
                            reply({"status": "done", "session_id": default_session_id, "server_closing": True})
                            request_shutdown("legacy quit command")
                            return
                        else:
                            reply({"status": "error", "message": f"Unknown cmd: {cmd!r}"})
                    except animation_handles.AnimationHandleError as exc:
                        conn.settimeout(None)
                        logger.log(f"[ERROR] Command '{cmd}' failed: {exc}")
                        reply({"status": "error", "error_code": exc.code, "message": str(exc)})
                    except Exception as exc:
                        conn.settimeout(None)
                        logger.log(f"[ERROR] Command '{cmd}' failed: {exc}")
                        reply({"status": "error", "message": str(exc)})
            finally:
                with queue_changed:
                    session = state["sessions"].get(bound_session_id)
                    if session is not None and session["explicit"]:
                        close_session_locked(session, "TCP connection closed.")
                    elif session is not None:
                        for task in list(session["queue"]):
                            if task["connection_id"] == connection_id:
                                session["queue"].remove(task)
                                finish_task_locked(
                                    session,
                                    task,
                                    {"status": "cancelled", "message": "Submitting TCP connection closed."},
                                )
                        active = session.get("active")
                        if active is not None and active["connection_id"] == connection_id:
                            active["cancel_event"].set()
                            mark_session_ready_locked(session)

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
        worker_thread.join()
        with runtime_gate:
            for runtime in list(state["runtimes"].values()):
                _unload_runtime_model(runtime, logger)
            state["runtimes"].clear()
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
