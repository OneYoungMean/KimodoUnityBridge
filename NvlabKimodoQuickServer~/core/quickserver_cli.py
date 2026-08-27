from __future__ import annotations

import argparse
import contextlib
import errno
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

from . import kimodo_runtime as runtime_helpers
from . import ardy_backend
from . import animation_analysis
from . import quickserver_assets as assets
from core.protocol.kmb_motion import (
    MAX_KMB_BYTES,
    attachment_payload,
    clip_slice,
    encode_kmb1,
    parse_constraints,
    parse_kmb1,
)
from kimodo.frame_time import seconds_to_frame_count
from .quickserver_setup import ProjectPaths, SetupLogger, discover_project_paths


SUPERVISOR_LOG_FILE_NAME = "bridge_server.log"
DEFAULT_TASK_ID_PREFIX = "task"
DEFAULT_RUNTIME_IDLE_UNLOAD_SEC = 900


def _build_protocol_help(kimodo_root: str = "") -> dict[str, Any]:
    return {
        "protocol": "kimodo-quickserver-tcp",
        "server_version": _read_quickserver_version(kimodo_root) if kimodo_root else "unknown",
        "commands": [
            {
                "cmd": "help",
                "description": "Return this built-in protocol reference without loading a model.",
            },
            {
                "cmd": "runtime.list_models",
                "description": "List every supported model and text encoder configuration for this server and device.",
            },
            {
                "cmd": "session.open",
                "description": "Open an isolated TCP generation session.",
            },
            {
                "cmd": "session.close",
                "description": "Close the current TCP generation session.",
            },
            {
                "cmd": "generate",
                "description": "Queue motion generation, or analyze KMB ClipConstraints when analysis_option.analysis_only is true. Choose model and text_encoder_mode from runtime.list_models for motion generation.",
                "fields": [
                    "prompt", "model", "text_encoder_mode", "duration", "constraints_json",
                    "kmb_attachments", "attachment_byte_length", "analysis_option",
                    "timeline_segments", "ardy_history_weight", "ardy_playback_reserve_seconds",
                ],
            },
            {
                "cmd": "cancel",
                "description": "Cancel a queued or running generation by task_id.",
            },
            {
                "cmd": "quit",
                "description": "Stop the QuickServer process.",
            },
        ],
    }


def _build_model_configurations(
    kimodo_root: str,
    default_config: dict[str, Any],
    runtime_profile: Any,
) -> dict[str, Any]:
    models_root, _ = assets.resolve_models_root(kimodo_root, default_config.get("models_root"))
    free_vram_gb = max(0.0, float(getattr(runtime_profile, "free_vram_gb", 0.0) or 0.0))
    detected_device = str(getattr(runtime_profile, "runtime_device", "cpu") or "cpu")
    detected_device = detected_device if free_vram_gb >= assets.MOTION_MODEL_MIN_FREE_GB else "cpu"
    nf4_available = bool(getattr(runtime_profile, "nf4_available", False)) and detected_device != "cpu"
    int8_available = bool(getattr(runtime_profile, "int8_accelerator_available", False)) and detected_device != "cpu"
    fp16_available = bool(getattr(runtime_profile, "fp16_accelerator_available", False)) and detected_device != "cpu"
    default_model = str(default_config.get("model") or assets.DEFAULT_MODEL_NAME)
    default_spec = assets.resolve_model_spec(default_model)
    if default_spec is not None:
        default_model = default_spec.model_name
    else:
        model_path = Path(default_model).expanduser()
        if model_path.is_file() and model_path.name.lower() == "config.yaml":
            model_path = model_path.parent
        default_model = (
            str(model_path.resolve())
            if model_path.is_dir() and (model_path / "config.yaml").is_file()
            else assets.resolve_main_model(default_model).local_name
        )
    default_encoder = assets.normalize_text_encoder_mode(default_config.get("text_encoder_mode"))
    configs: list[dict[str, Any]] = []
    for spec in assets.ALL_MODEL_SPECS:
        model_name = spec.model_name
        encoder_budget_gb = max(0.0, free_vram_gb - assets.motion_model_min_free_vram_gb(model_name))
        for text_encoder_mode in (
            assets.TEXT_ENCODER_MODE_HIGH_PERFORMANCE,
            assets.TEXT_ENCODER_MODE_HIGH_PRECISION,
        ):
            decision = assets.resolve_text_encoder_runtime(
                text_encoder_mode,
                detected_device,
                encoder_budget_gb,
                nf4_available=nf4_available,
                int8_accelerator_available=int8_available,
                fp16_accelerator_available=fp16_available,
            )
            configs.append(
                {
                    "model": model_name,
                    "backend": spec.backend,
                    "source_fps": spec.source_fps,
                    "joint_count": spec.joint_count,
                    "max_diffusion_steps": spec.max_diffusion_steps,
                    "default_diffusion_steps": spec.default_diffusion_steps,
                    "horizon_frames": spec.horizon_frames,
                    "frames_per_token": spec.frames_per_token,
                    "max_context_frames": spec.max_context_frames,
                    "motion_rep_fingerprint": spec.motion_rep_fingerprint,
                    "supports_streaming": spec.supports_streaming,
                    "supports_timeline_segments": spec.supports_timeline_segments,
                    "text_encoder_model": text_encoder_mode,
                    "runtime_device": decision.motion_device,
                    "text_encoder_route": decision.encoder_route,
                    "text_encoder_device": decision.encoder_device,
                    "available": True,
                    "default": model_name == default_model and text_encoder_mode == default_encoder,
                }
            )
    return {
        "status": "done",
        "models_root": str(models_root),
        "configs": configs,
        "default": {
            "model": default_model,
            "text_encoder_model": default_encoder,
        },
    }


def _publish_cancelled_task_to_client(task: dict[str, Any], message: str) -> None:
    response = {
        "status": "cancelled",
        "message": message,
        "task_id": str(task.get("task_id") or ""),
    }
    request_id = str(task.get("request_id") or "")
    if request_id:
        response["request_id"] = request_id
    task["response"] = response
    task["binary"] = None
    task["event"].set()


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
    try:
        # Signal 0 performs a portable POSIX existence/permission check without
        # sending a signal. macOS has no /proc filesystem.
        os.kill(int(pid), 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError as exc:
        return exc.errno == errno.EPERM
    return True


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
    runtime_helpers._write_text_atomic(
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


def _release_bootstrap_lock(root_dir: str) -> None:
    """Release the launcher lock after publishing the supervisor endpoint."""
    try:
        (Path(root_dir).resolve() / ".bootstrap.lock").unlink(missing_ok=True)
    except OSError:
        pass


def _read_quickserver_version(root_dir: str) -> str:
    try:
        package_path = Path(root_dir).resolve() / "package.json"
        version = str(json.loads(package_path.read_text(encoding="utf-8")).get("version") or "").strip()
        return version or "unknown"
    except Exception:
        return "unknown"


def _build_signature(config: dict[str, Any]) -> str:
    return "|".join(
        [
            f"model={config['model']}",
            f"text_encoder_mode={config['text_encoder_mode']}",
            f"models_root={config['models_root']}",
            f"force_hf_download={int(bool(config['force_hf_download']))}",
            f"simulate_free_vram_gb={config['simulate_free_vram_gb']}",
            f"force_text_encoder_cpu={int(bool(config.get('_force_text_encoder_cpu')))}",
        ]
    )


def _build_model_worker_key(config: dict[str, Any]) -> str:
    return "|".join(
        [
            f"model={config['model']}",
            f"models_root={config['models_root']}",
            f"simulate_free_vram_gb={config['simulate_free_vram_gb']}",
        ]
    )


def _build_task_worker_key(config: dict[str, Any], session_id: str) -> str:
    profile = assets.resolve_motion_model_profile(config["model"])
    if profile is not None and profile.backend == "ardy":
        return f"ardy_session={session_id}"
    return _build_model_worker_key(config)


def _build_text_encoder_signature(config: dict[str, Any]) -> str:
    return f"text_encoder_mode={config['text_encoder_mode']}"


def _build_text_encoder_execution_key(config: dict[str, Any]) -> tuple[str, str]:
    profile = assets.resolve_motion_model_profile(config["model"])
    backend = str(getattr(profile, "backend", "kimodo") or "kimodo")
    return _build_text_encoder_signature(config), backend


class _TextEncoderExecutionGate:
    def __init__(self) -> None:
        self._condition = threading.Condition()
        self._active_key: tuple[str, str] | None = None
        self._active_count = 0

    def acquire(self, key: tuple[str, str]) -> None:
        with self._condition:
            while self._active_count > 0 and self._active_key != key:
                self._condition.wait()
            self._active_key = key
            self._active_count += 1

    def release(self, key: tuple[str, str]) -> None:
        with self._condition:
            if self._active_count <= 0 or self._active_key != key:
                raise RuntimeError("TextEncoder execution gate was released by a non-owner.")
            self._active_count -= 1
            if self._active_count == 0:
                self._active_key = None
                self._condition.notify_all()


def _normalize_runtime_config(req: dict[str, Any], defaults: dict[str, Any]) -> dict[str, Any]:
    removed_keys = [key for key in ("highvram", "force_cpu") if key in req]
    if removed_keys:
        raise ValueError(
            "Removed generate fields are not supported: "
            + ", ".join(removed_keys)
            + ". Use text_encoder_mode and simulate_free_vram_gb."
        )
    model = str(req.get("model") or defaults.get("model") or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME
    models_root = str(req.get("models_root") or defaults.get("models_root") or "").strip()
    raw_simulated_vram = req.get("simulate_free_vram_gb", defaults.get("simulate_free_vram_gb"))
    simulated_vram_gb = None
    if raw_simulated_vram is not None and str(raw_simulated_vram).strip() != "":
        simulated_vram_gb = float(raw_simulated_vram)
        if not math.isfinite(simulated_vram_gb) or simulated_vram_gb < 0.0:
            raise ValueError("simulate_free_vram_gb must be a finite value greater than or equal to 0.")

    return {
        "model": model,
        "text_encoder_mode": assets.normalize_text_encoder_mode(
            req.get("text_encoder_mode")
            if "text_encoder_mode" in req
            else defaults.get("text_encoder_mode")
        ),
        "models_root": models_root,
        "force_hf_download": _bool_value(req.get("force_hf_download"), _bool_value(defaults.get("force_hf_download"))),
        "simulate_free_vram_gb": simulated_vram_gb,
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
    _release_accelerator_cache()


def _clear_shared_text_encoder_state(state: dict[str, Any]) -> Any:
    encoder = state.get("shared_text_encoder")
    runtimes = [state["active_runtime"]] + list((state.get("ardy_runtimes") or {}).values()) + [
        session.get("ardy_runtime") or {} for session in state["sessions"].values()
    ] + list(state["retired_runtimes"])
    for runtime in runtimes:
        model = runtime.get("model")
        if model is not None and getattr(model, "text_encoder", None) is encoder:
            model.text_encoder = None
    state["shared_text_encoder"] = None
    state["shared_text_encoder_signature"] = ""
    state["shared_text_encoder_decision"] = None
    state["shared_text_encoder_models_root"] = ""
    state["active_text_encoder_signature"] = ""
    return encoder


def _release_accelerator_cache() -> None:
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


def _replace_text_encoder(
    model: Any,
    config: dict[str, Any],
    decision: assets.TextEncoderRuntimeDecision,
    kimodo_root: str,
    logger: SetupLogger,
    cancel_event: threading.Event | None = None,
) -> None:
    assets.raise_if_download_cancelled(cancel_event)
    from kimodo.model.load_model import _select_text_encoder_conf
    from kimodo.model.loading import DEFAULT_TEXT_ENCODER_URL, get_env_var, instantiate_from_dict

    models_root, _ = assets.resolve_models_root(kimodo_root, config["models_root"])
    layout = assets.select_text_encoder_layout_for_route(
        decision.encoder_route,
        models_root,
        decision.encoder_device,
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
            text_encoder_mode=decision.mode,
            encoder_device=decision.encoder_device,
            encoder_route=decision.encoder_route,
            encoder_layout_id=layout.layout_id,
        )
    )
    recovery_flag_dir = Path(kimodo_root).resolve() / "archive" / "recovery_flags"
    force_site = assets.DownloadSite.HUGGINGFACE if config["force_hf_download"] else None
    download_counter = [0]
    for encoder_asset in layout.download_assets:
        assets.ensure_asset_present(
            encoder_asset,
            models_root / encoder_asset.local_dir_name,
            logger,
            recovery_flag_dir,
            download_counter,
            force_site=force_site,
            cancel_event=cancel_event,
        )
    assets.raise_if_download_cancelled(cancel_event)
    old_encoder = getattr(model, "text_encoder", None)
    new_encoder = instantiate_from_dict(
        _select_text_encoder_conf(
            get_env_var("TEXT_ENCODER_URL", DEFAULT_TEXT_ENCODER_URL),
            decision.encoder_device,
        )
    )
    model.text_encoder = new_encoder
    del old_encoder
    _release_accelerator_cache()
    logger.log(
        f"[INFO] Text encoder rerouted after motion load: route={decision.encoder_route} "
        f"device={decision.encoder_device} free_vram={decision.effective_free_vram_gb:.2f}GB"
    )


def _text_encoder_placement(encoder: Any) -> tuple[str, str]:
    if encoder is None:
        return "", ""
    device = str(getattr(encoder, "target_device", "cpu") or "cpu").lower()
    if encoder.__class__.__name__ == "LLM2VecInt8Encoder":
        return assets.ENCODER_ROUTE_INT8, "cpu"
    if bool(getattr(encoder, "accelerator_int8", False)):
        return assets.ENCODER_ROUTE_INT8, device
    route = (
        assets.ENCODER_ROUTE_NF4
        if bool(getattr(encoder, "accelerator_nf4", False))
        or assets.NF4_LOCAL_DIR.lower() in str(getattr(encoder, "custom_dir", "")).lower()
        else assets.ENCODER_ROUTE_FP16
    )
    return route, device


def _refresh_encoder_route_after_motion_load(
    model: Any,
    config: dict[str, Any],
    current: assets.TextEncoderRuntimeDecision,
    runtime_profile: Any,
    kimodo_root: str,
    logger: SetupLogger,
    cancel_event: threading.Event | None = None,
) -> assets.TextEncoderRuntimeDecision:
    if runtime_profile.runtime_device == "cpu" or config["simulate_free_vram_gb"] is not None:
        return current
    free_vram_gb = runtime_helpers._detect_free_vram_gb(runtime_profile.runtime_device)
    updated = assets.resolve_text_encoder_runtime(
        config["text_encoder_mode"],
        runtime_profile.runtime_device,
        free_vram_gb,
        nf4_available=runtime_profile.nf4_available,
        int8_accelerator_available=runtime_profile.int8_accelerator_available,
        fp16_accelerator_available=runtime_profile.fp16_accelerator_available,
    )
    if config.get("_force_text_encoder_cpu"):
        updated = assets.force_text_encoder_cpu(updated)
    if (updated.encoder_route, updated.encoder_device) != _text_encoder_placement(getattr(model, "text_encoder", None)):
        _replace_text_encoder(model, config, updated, kimodo_root, logger, cancel_event)
    return updated


def _fallback_runtime_text_encoder_to_cpu(
    runtime: dict[str, Any],
    config: dict[str, Any],
    kimodo_root: str,
    logger: SetupLogger,
    cancel_event: threading.Event | None = None,
) -> bool:
    decision = runtime.get("text_encoder_decision")
    if decision is None or decision.encoder_device == "cpu":
        return False
    fallback = assets.force_text_encoder_cpu(decision)
    logger.log("[WARN] Text encoder accelerator OOM; retrying once with the encoder on CPU.")
    _replace_text_encoder(runtime["model"], config, fallback, kimodo_root, logger, cancel_event)
    runtime["text_encoder_decision"] = fallback
    _release_accelerator_cache()
    return True

def _ensure_runtime(
    state: dict[str, Any],
    config: dict[str, Any],
    kimodo_root: str,
    logger: SetupLogger,
    text_encoder: Any = None,
    text_encoder_decision: assets.TextEncoderRuntimeDecision | None = None,
    cancel_event: threading.Event | None = None,
) -> dict[str, Any]:
    assets.raise_if_download_cancelled(cancel_event)
    signature = _build_signature(config)
    existing_signature = str(state.get("runtime_signature") or "")
    same_runtime = existing_signature == signature and state.get("model") is not None
    if (
        same_runtime
        and getattr(state["model"], "text_encoder", None) is not None
    ):
        return {
            "model": state["resolved_model_name"],
            "device": state["runtime_device"],
            "fps": int(state["fps"]),
            "signature": signature,
            "reused": True,
        }

    if state.get("model") is not None and not same_runtime:
        _unload_runtime_model(state, logger)

    os.environ["KIMODO_TEXT_ENCODER_MODE"] = config["text_encoder_mode"]

    if config["models_root"]:
        os.environ["KIMODO_MODELS_ROOT"] = config["models_root"]
    else:
        os.environ.pop("KIMODO_MODELS_ROOT", None)

    if config["simulate_free_vram_gb"] is not None:
        os.environ["KIMODO_SIMULATE_FREE_VRAM_GB"] = str(config["simulate_free_vram_gb"])
    else:
        os.environ.pop("KIMODO_SIMULATE_FREE_VRAM_GB", None)
    os.environ.pop("KIMODO_SIMULATE_VRAM_GB", None)

    runtime_profile = runtime_helpers._runtime_self_check(
        str(state.get("runtime_device") or "") if same_runtime else None
    )
    motion_required_gb = 0.0 if same_runtime else assets.motion_model_min_free_vram_gb(config["model"])
    free_vram_gb = runtime_profile.free_vram_gb
    if runtime_profile.runtime_device != "cpu" and free_vram_gb < motion_required_gb:
        logger.log(
            "[WARN] Motion runtime does not fit in current free VRAM; loading it on CPU: "
            f"model={config['model']} required={motion_required_gb:g}GB free={free_vram_gb:.2f}GB"
        )
        runtime_profile = runtime_helpers._runtime_self_check("cpu")
        free_vram_gb = runtime_profile.free_vram_gb
    encoder_free_vram_gb = max(0.0, free_vram_gb - motion_required_gb)
    runtime_decision = assets.resolve_text_encoder_runtime(
        config["text_encoder_mode"],
        runtime_profile.runtime_device,
        encoder_free_vram_gb,
        nf4_available=runtime_profile.nf4_available,
        int8_accelerator_available=runtime_profile.int8_accelerator_available,
        fp16_accelerator_available=runtime_profile.fp16_accelerator_available,
    )
    if config.get("_force_text_encoder_cpu"):
        runtime_decision = assets.force_text_encoder_cpu(runtime_decision)
        os.environ["KIMODO_TEXT_ENCODER_FORCE_CPU"] = "1"
    else:
        os.environ.pop("KIMODO_TEXT_ENCODER_FORCE_CPU", None)
    if text_encoder is not None and _text_encoder_placement(text_encoder) != (
        runtime_decision.encoder_route,
        runtime_decision.encoder_device,
    ):
        logger.log("[INFO] Shared text encoder placement no longer fits the current free-VRAM budget; rebuilding it.")
        del text_encoder
        text_encoder = None
        _release_accelerator_cache()
    os.environ["KIMODO_RUNTIME_BACKEND_PROFILE"] = runtime_profile.backend_profile
    os.environ["KIMODO_RUNTIME_DEVICE"] = runtime_decision.motion_device

    logger.log(
        "[INFO] Preparing runtime: "
        f"model={config['model']} text_encoder_mode={config['text_encoder_mode']} "
        f"models_root={config['models_root'] or '<default>'} "
            f"free_vram={free_vram_gb:.2f}GB motion_reserve={motion_required_gb:g}GB "
            f"encoder_budget={encoder_free_vram_gb:.2f}GB motion_device={runtime_decision.motion_device} "
            f"encoder_route={runtime_decision.encoder_route} encoder_device={runtime_decision.encoder_device}"
    )

    if same_runtime:
        _replace_text_encoder(state["model"], config, runtime_decision, kimodo_root, logger, cancel_event)
        state["text_encoder_decision"] = runtime_decision
        logger.log("[INFO] Text encoder ready; reusing current motion runtime.")
        return {
            "model": state["resolved_model_name"],
            "device": state["runtime_device"],
            "fps": int(state["fps"]),
            "signature": signature,
            "reused": True,
        }

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
                cancel_event=cancel_event,
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
            text_encoder=text_encoder,
            cancel_event=cancel_event,
            logger=logger,
        )
        runtime_decision = _refresh_encoder_route_after_motion_load(
            model,
            config,
            runtime_decision,
            runtime_profile,
            kimodo_root,
            logger,
            cancel_event,
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
    plan = runtime_helpers._provision_bridge_assets(
        kimodo_root,
        config["model"],
        runtime_profile=runtime_profile,
        force_download_site=force_download_site,
        encoder_free_vram_gb=encoder_free_vram_gb,
        cancel_event=cancel_event,
        )

    from core.bridge_load_model import load_bridge_model

    resolved_model_name = plan.resolved_model.local_name
    model = load_bridge_model(
        resolved_model_name,
        models_root=plan.models_root,
        device=plan.runtime_decision.motion_device,
        text_encoder=text_encoder,
    )
    runtime_decision = _refresh_encoder_route_after_motion_load(
        model,
        config,
        plan.runtime_decision,
        runtime_profile,
        kimodo_root,
        logger,
        cancel_event,
    )

    state["model"] = model
    state["fps"] = int(model.fps)
    state["runtime_signature"] = signature
    state["runtime_config"] = dict(config)
    state["resolved_model_name"] = resolved_model_name
    state["runtime_device"] = runtime_decision.motion_device
    state["motion_profile"] = None
    state["text_encoder_decision"] = runtime_decision
    logger.log(
        f"[INFO] Runtime ready: model={resolved_model_name} device={runtime_decision.motion_device} fps={int(model.fps)}"
    )
    return {
        "model": resolved_model_name,
        "device": runtime_decision.motion_device,
        "fps": int(model.fps),
        "signature": signature,
        "reused": False,
    }


def _execute_generate(
    task_request: dict[str, Any],
    model: Any,
    cancel_event: threading.Event,
    progress=None,
    attachments: tuple[bytes, ...] = (),
) -> tuple[dict[str, Any], bytes | None]:
    if cancel_event.is_set():
        raise runtime_helpers.GenerateCancelledError("Generation canceled.")
    if "loop" in task_request:
        raise ValueError("The generate.loop protocol field has been removed.")

    generate_kwargs = {"emit_progress": False}
    if attachments:
        generate_kwargs["attachments"] = attachments
    output, prompt = runtime_helpers._run_generate(
        task_request,
        model,
        cancel_event,
        **generate_kwargs,
    )
    return runtime_helpers._finalize_generation_result(task_request, model, output, prompt)


def _build_streaming_status_message(
    server_state: str,
    queue_index: int,
    task_id: str,
    task_message: str = "",
) -> tuple[str, str]:
    normalized = str(server_state or "").strip().lower()
    detail = str(task_message or "").strip()
    if queue_index > 0:
        return "queued", f"Task '{task_id}' waiting in queue. queue_index={queue_index}"
    if normalized == "loading_runtime":
        if detail and not detail.startswith("Task '"):
            return "loading", detail
        return "loading", "Preparing motion runtime..."
    if normalized == "generating":
        if detail and not detail.startswith("Task '"):
            return "progress", detail
        return "progress", "Generating motion..."
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
                "effective_free_vram_gb": decision.effective_free_vram_gb,
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


def _is_encoder_oom(error: Exception) -> bool:
    if not _is_accelerator_oom(error):
        return False
    try:
        import torch

        trace = error.__traceback__
        while trace is not None:
            frame = trace.tb_frame
            module_name = str(frame.f_globals.get("__name__") or "")
            if module_name.startswith("kimodo.model.llm2vec") or module_name == "kimodo.model.llm2vec_int8":
                return True
            trace = trace.tb_next
        return not bool(torch.cuda.is_available())
    except Exception:
        return False


def _write_protocol_message(file, writer_lock: threading.Lock, payload: dict[str, Any], binary_payload: bytes | None = None) -> None:
    with writer_lock:
        runtime_helpers._write_json_line(file, payload)
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


def _read_kmb_attachments(file, request: dict[str, Any]) -> tuple[bytes, ...]:
    total = int(request.get("attachment_byte_length") or 0)
    manifest = request.get("kmb_attachments") or []
    if total == 0 and not manifest:
        return ()
    if total <= 0 or total > MAX_KMB_BYTES:
        raise ardy_backend.ArdyBackendError(
            f"attachment_byte_length must be in [1, {MAX_KMB_BYTES}]."
        )
    if not isinstance(manifest, list):
        raise ardy_backend.ArdyBackendError("kmb_attachments must be an array.")
    payload = _read_exact(file, total)
    if len(payload) != total:
        raise ardy_backend.ArdyBackendError(
            f"Generate attachments ended after {len(payload)} of {total} bytes."
        )
    result: list[bytes] = []
    expected_offset = 0
    for index, item in enumerate(manifest):
        if not isinstance(item, dict) or int(item.get("index", -1)) != index:
            raise ardy_backend.ArdyBackendError("KMB attachment indices must be contiguous and zero-based.")
        offset = int(item.get("offset", -1))
        length = int(item.get("byte_length", 0))
        if offset != expected_offset or length <= 0 or offset + length > total:
            raise ardy_backend.ArdyBackendError("KMB attachment offsets or lengths are invalid.")
        result.append(payload[offset : offset + length])
        expected_offset += length
    if expected_offset != total:
        raise ardy_backend.ArdyBackendError("KMB attachment manifest does not cover the binary request payload.")
    return tuple(result)


def _is_analysis_only_request(request: dict[str, Any]) -> bool:
    options = request.get("analysis_option")
    return isinstance(options, dict) and options.get("analysis_only") is True


def _execute_analysis_only(
    request: dict[str, Any],
    attachments: tuple[bytes, ...],
) -> tuple[dict[str, Any], bytes]:
    options = request.get("analysis_option")
    if not isinstance(options, dict) or options.get("analysis_only") is not True:
        raise ardy_backend.ArdyBackendError("analysis_option.analysis_only must be true.")

    clips: list[dict[str, Any]] = []
    manifest: list[dict[str, Any]] = []
    output = bytearray()
    for item in parse_constraints(request.get("constraints_json"), ardy_backend.ArdyBackendError):
        if item.get("type") != "clip":
            continue
        payload = attachment_payload(item, attachments, ardy_backend.ArdyBackendError)
        motion = parse_kmb1(payload, ardy_backend.ArdyBackendError)
        start, end = clip_slice(item, motion, ardy_backend.ArdyBackendError)
        dense_clip = encode_kmb1(motion, start, end)
        offset = len(output)
        output.extend(dense_clip)
        manifest.append(
            {
                "index": len(manifest),
                "offset": offset,
                "byte_length": len(dense_clip),
                "start_frame": 0,
                "end_frame_exclusive": end - start,
            }
        )
        clips.append(
            {
                "root_positions": motion.root_positions[start:end],
                "local_rot_quats": motion.local_rot_quats[start:end],
                "joint_names": list(motion.joint_names),
                "joint_parents": list(motion.joint_parents),
                "foot_contacts": motion.foot_contacts[start:end] if motion.foot_contacts is not None else None,
                "model_name": motion.model_name,
                "fps": motion.fps,
            }
        )

    if not clips:
        raise ardy_backend.ArdyBackendError("analysis_only requires one or more KMB ClipConstraints.")
    if len(output) > MAX_KMB_BYTES:
        raise ardy_backend.ArdyBackendError(
            f"analysis_only KMB output exceeds the {MAX_KMB_BYTES}-byte limit."
        )
    return (
        {
            "status": "done",
            "output_format": "kmb_attachments_v1",
            "byte_length": len(output),
            "kmb_attachments": manifest,
            "analysis": animation_analysis.build_clip_constraint_analysis(clips, options),
        },
        bytes(output),
    )


def _run_supervisor(args: argparse.Namespace, root_dir: str, logger: SetupLogger) -> int:
    host = "127.0.0.1"
    kimodo_root = str(Path(root_dir).resolve())
    serverport_path = Path(kimodo_root) / "serverport"
    idle_timeout_seconds = max(0, int(float(os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600"))))
    runtime_idle_unload_seconds = max(
        30,
        int(float(os.environ.get("KIMODO_RUNTIME_IDLE_UNLOAD_SEC", str(DEFAULT_RUNTIME_IDLE_UNLOAD_SEC)))),
    )
    default_session_id = "session:default"

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
    logger.log(f"[INFO] Kimodo QuickServer version: {_read_quickserver_version(kimodo_root)}")
    logger.log(f"[INFO] quickserver_cli listening on {host}:{port}")
    _release_bootstrap_lock(kimodo_root)
    logger.log("[INFO] Bootstrap lock released after QuickServer became ready.")
    logger.log(f"[INFO] ARDY cross-Session batch size: {ardy_backend.ARDY_BATCH_SIZE}")

    default_config = {
        "model": str(args.model or assets.DEFAULT_MODEL_NAME).strip() or assets.DEFAULT_MODEL_NAME,
        "text_encoder_mode": assets.normalize_text_encoder_mode(args.text_encoder_mode),
        "models_root": str(args.models_root or "").strip(),
        "force_hf_download": bool(args.force_hf_download),
        "simulate_free_vram_gb": 0.0 if str(args.device or "").strip().lower() == "cpu" else None,
    }

    def new_session(session_id: str, *, explicit: bool, connection_id: str = "") -> dict[str, Any]:
        return {
            "session_id": session_id,
            "explicit": explicit,
            "connection_id": connection_id,
            "default_config": dict(default_config),
            "queue": deque(),
            "active": None,
            "ardy_runtime": {},
            "ardy_stream": None,
            "ardy_stream_signature": "",
            "ardy_registered": False,
            "ready": False,
            "closed": False,
        }

    state: dict[str, Any] = {
        "shutdown": False,
        "watch_pid": max(0, int(args.watchpid or 0)),
        "last_activity": time.time(),
        "active_runtime": {},
        "ardy_runtimes": {},
        "shared_text_encoder": None,
        "shared_text_encoder_signature": "",
        "shared_text_encoder_decision": None,
        "shared_text_encoder_models_root": "",
        "active_text_encoder_signature": "",
        "active_model_worker_key": "",
        "model_workers": {},
        "sessions": {default_session_id: new_session(default_session_id, explicit=False)},
        "ready_model_workers": deque(),
        "tasks": {},
        "retired_runtimes": deque(),
        "active_task_id": "",
        "active_worker_count": 0,
        "ardy_session_count": 0,
        "active_command_count": 0,
        "server_state": "boot",
        "task_counter": count(1),
        "session_counter": count(1),
        "connection_counter": count(1),
    }
    state_lock = threading.Lock()
    queue_changed = threading.Condition(state_lock)
    runtime_gate = threading.Lock()
    non_ardy_generation_gate = threading.Lock()
    # ponytail: mixed backends/modes serialize while the server owns one shared TextEncoder slot.
    # Replace this gate with keyed resident encoders only if mixed-mode parallelism becomes necessary.
    text_encoder_execution_gate = _TextEncoderExecutionGate()
    publish_lock = threading.Lock()
    task_context = threading.local()
    ardy_backend.set_inference_session_count(0)

    def capture_task_runtime_progress(message: str) -> None:
        text = str(message or "").strip()
        if not text.startswith(
            (
                "[INFO] Preparing runtime:",
                "[INFO] Shared text encoder mode changed",
                "[INFO] ARDY reusing Kimodo text encoder:",
                "[INFO] Runtime ready:",
                "[INFO] Text encoder",
                "[OK] FP16 text encoder",
                "[OK] INT8 text encoder",
                "[OK] NF4 text encoder",
                "[STEP] Downloading",
                "[DOWNLOAD]",
            )
        ):
            return
        task = state["tasks"].get(str(getattr(task_context, "task_id", "") or ""))
        if task is not None:
            task["status_message"] = text

    logger.on_log = capture_task_runtime_progress

    def retire_session_locked(session: dict[str, Any]) -> None:
        stream = session.get("ardy_stream")
        if stream is not None:
            stream.close()
        session["ardy_stream"] = None
        session["ardy_stream_signature"] = ""
        session["ardy_runtime"] = {}
        if session.get("ardy_registered"):
            session["ardy_registered"] = False
            state["ardy_session_count"] = max(0, int(state["ardy_session_count"]) - 1)
            ardy_backend.set_inference_session_count(state["ardy_session_count"])
        state["sessions"].pop(session["session_id"], None)
        state["model_workers"].pop(f"ardy_session={session['session_id']}", None)

    def register_ardy_session_locked(session: dict[str, Any]) -> None:
        if session.get("ardy_registered"):
            return
        session["ardy_registered"] = True
        state["ardy_session_count"] = int(state["ardy_session_count"]) + 1
        ardy_backend.set_inference_session_count(state["ardy_session_count"])

    def bind_shared_text_encoder(runtime: dict[str, Any], encoder_signature: str) -> None:
        model = runtime.get("model")
        encoder = getattr(model, "text_encoder", None) if model is not None else None
        if encoder is None:
            return
        decision = runtime.get("text_encoder_decision")
        models_root = str((runtime.get("runtime_config") or {}).get("models_root") or "")
        with state_lock:
            runtimes = [state["active_runtime"]] + list(state["ardy_runtimes"].values()) + [
                session.get("ardy_runtime") or {} for session in state["sessions"].values()
            ]
            state["shared_text_encoder"] = encoder
            state["shared_text_encoder_signature"] = encoder_signature
            state["shared_text_encoder_decision"] = decision
            state["shared_text_encoder_models_root"] = models_root
            state["active_text_encoder_signature"] = encoder_signature
            for candidate in runtimes:
                candidate_model = candidate.get("model")
                if candidate_model is not None:
                    candidate_model.text_encoder = encoder
                    candidate["text_encoder_decision"] = decision

    def release_shared_text_encoder() -> None:
        with state_lock:
            encoder = _clear_shared_text_encoder_state(state)
        if encoder is not None:
            del encoder
            _release_accelerator_cache()

    def touch_activity() -> None:
        with state_lock:
            state["last_activity"] = time.time()

    def publish_state(state_name: str) -> None:
        with publish_lock:
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

    def get_model_worker_locked(worker_key: str) -> dict[str, Any]:
        worker = state["model_workers"].get(worker_key)
        if worker is None:
            worker = {"worker_key": worker_key, "ready_sessions": deque(), "ready": False}
            state["model_workers"][worker_key] = worker
        return worker

    def mark_session_ready_locked(session: dict[str, Any]) -> None:
        if session["ready"]:
            return
        active = session.get("active")
        if active is not None and str(active.get("state") or "") in ("running", "cancelling"):
            return
        if state["shutdown"] or session["closed"]:
            if active is None or not active["cancel_event"].is_set():
                return
            task = active
        elif active is None and not session["queue"]:
            return
        else:
            task = active or session["queue"][0]
        session["ready"] = True
        worker = get_model_worker_locked(task["model_worker_key"])
        worker["ready_sessions"].append(session["session_id"])
        if not worker["ready"]:
            worker["ready"] = True
            if state["active_model_worker_key"] == worker["worker_key"]:
                state["ready_model_workers"].appendleft(worker["worker_key"])
            else:
                state["ready_model_workers"].append(worker["worker_key"])
        queue_changed.notify_all()

    def finish_task_locked(
        session: dict[str, Any],
        task: dict[str, Any],
        response: dict[str, Any] | None = None,
        binary_payload: bytes | None = None,
    ) -> None:
        final_response = response or {"status": "cancelled", "message": "Task closed."}
        task["response"] = attach_request_id(
            _attach_task_id(final_response, task["task_id"]),
            task["request_id"],
        )
        task["binary"] = binary_payload
        task["event"].set()
        task_status = str((response or {}).get("status") or ("cancelled" if task["cancel_event"].is_set() else "closed"))
        message = str((response or {}).get("message") or task.get("status_message") or "Task closed.")
        task["state"] = task_status
        state["tasks"].pop(task["task_id"], None)
        if session.get("active") is task:
            session["active"] = None
        state["active_task_id"] = ""
        if session["closed"] and not session["queue"]:
            retire_session_locked(session)
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
            retire_session_locked(session)

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
                    mark_session_ready_locked(session)
            queue_changed.notify_all()

        try:
            server.close()
        except Exception:
            pass

    def drain_retired_runtimes() -> None:
        with state_lock:
            retired = list(state["retired_runtimes"])
            state["retired_runtimes"].clear()
        if not retired:
            return
        with runtime_gate:
            for runtime in retired:
                _unload_runtime_model(runtime, logger)

    def lifecycle_monitor_loop() -> None:
        while True:
            time.sleep(1.0)
            drain_retired_runtimes()
            with state_lock:
                if state["shutdown"]:
                    return
                watch_pid = int(state.get("watch_pid") or 0)
                idle_seconds = time.time() - float(state.get("last_activity") or 0.0)
                runtime_loaded = state["active_runtime"].get("model") is not None or any(
                    runtime.get("model") is not None for runtime in state["ardy_runtimes"].values()
                )
                work_in_flight = any(
                    session["queue"]
                    or session.get("active") is not None
                    or session.get("ardy_stream") is not None
                    for session in state["sessions"].values()
                ) or int(state.get("active_command_count") or 0) > 0

            if watch_pid > 0 and not _pid_is_running(watch_pid):
                request_shutdown(f"watch pid {watch_pid} exited")
                return

            if idle_timeout_seconds > 0 and not work_in_flight and idle_seconds >= idle_timeout_seconds:
                request_shutdown(f"idle timeout reached ({int(idle_seconds)}s)")
                return

            if not work_in_flight and runtime_loaded and idle_seconds >= runtime_idle_unload_seconds:
                with runtime_gate:
                    with state_lock:
                        if not any(
                            session["queue"] or session.get("active") is not None
                            for session in state["sessions"].values()
                        ) and int(state.get("active_command_count") or 0) == 0:
                            runtimes = [state["active_runtime"]] + list(state["ardy_runtimes"].values())
                            state["ardy_runtimes"] = {}
                            for session in state["sessions"].values():
                                session["ardy_runtime"] = {}
                            state["shared_text_encoder"] = None
                            state["shared_text_encoder_signature"] = ""
                            state["shared_text_encoder_decision"] = None
                            state["shared_text_encoder_models_root"] = ""
                            state["active_text_encoder_signature"] = ""
                            state["active_model_worker_key"] = ""
                        else:
                            runtimes = []
                    for runtime in runtimes:
                        _unload_runtime_model(runtime, logger)

    def get_runtime(
        session: dict[str, Any],
        runtime_config: dict[str, Any],
        cancel_event: threading.Event | None = None,
    ) -> dict[str, Any]:
        with runtime_gate:
            shared_encoder = state.get("shared_text_encoder")
            shared_signature = str(state.get("shared_text_encoder_signature") or "")
            shared_decision = state.get("shared_text_encoder_decision")
            requested_models_root = str(runtime_config.get("models_root") or "").strip()
            if requested_models_root:
                runtime_config["models_root"] = str(
                    assets.resolve_models_root(kimodo_root, requested_models_root)[0]
                )
            else:
                runtime_config["models_root"] = str(assets.default_models_root(kimodo_root))
            signature = _build_signature(runtime_config)
            encoder_signature = _build_text_encoder_signature(runtime_config)
            profile = assets.resolve_motion_model_profile(runtime_config["model"])
            is_ardy = profile is not None and profile.backend == "ardy"
            if is_ardy:
                runtime = state["ardy_runtimes"].setdefault(signature, {})
                session["ardy_runtime"] = runtime
            else:
                runtime = state["active_runtime"]
            if shared_encoder is not None and shared_signature != encoder_signature:
                logger.log(
                    "[INFO] Shared text encoder mode changed; releasing the previous encoder: "
                    f"old={shared_signature or '<unknown>'} new={encoder_signature}."
                )
                release_shared_text_encoder()
                shared_encoder = None
                shared_decision = None
            if runtime.get("model") is not None and runtime.get("runtime_signature") == signature:
                expected_decision = runtime.get("text_encoder_decision")
                if (
                    shared_encoder is not None
                    and expected_decision is not None
                    and _text_encoder_placement(shared_encoder) == (
                        expected_decision.encoder_route,
                        expected_decision.encoder_device,
                    )
                ):
                    runtime["model"].text_encoder = shared_encoder
                if getattr(runtime["model"], "text_encoder", None) is not None:
                    runtime["model"]._kimodo_runtime_signature = signature
                    state["active_model_worker_key"] = _build_task_worker_key(
                        runtime_config, session["session_id"]
                    )
                    return runtime
            _ensure_runtime(
                runtime,
                runtime_config,
                kimodo_root,
                logger,
                text_encoder=shared_encoder,
                text_encoder_decision=shared_decision,
                cancel_event=cancel_event,
            )
            runtime["model"]._kimodo_runtime_signature = signature
            bind_shared_text_encoder(runtime, encoder_signature)
            state["active_model_worker_key"] = _build_task_worker_key(
                runtime_config, session["session_id"]
            )
            return runtime

    def run_one_shot(
        task: dict[str, Any],
        runtime: dict[str, Any],
        session: dict[str, Any],
    ) -> tuple[dict[str, Any], bytes | None]:
        def report_progress(message: str) -> None:
            task["status_message"] = str(message or "").strip()

        def execute() -> tuple[dict[str, Any], bytes | None]:
            profile = runtime.get("motion_profile")
            if profile is None or profile.backend != "ardy":
                return _execute_generate(
                    task["request"],
                    runtime["model"],
                    task["cancel_event"],
                    report_progress,
                    tuple(task.get("attachments") or ()),
                )
            signature = str(runtime.get("runtime_signature") or "")
            existing = session.get("ardy_stream")
            existing_signature = str(session.get("ardy_stream_signature") or "")
            fixed_length = "duration" in task["request"]
            if existing is not None and existing_signature != signature and not fixed_length:
                raise ardy_backend.ArdyBackendError(
                    "An active ARDY Session cannot change its model or runtime configuration."
                )
            if fixed_length:
                session["ardy_stream"] = None
                session["ardy_stream_signature"] = ""
            stream, response, payload = ardy_backend.execute_stream_generate(
                existing,
                task["request"],
                tuple(task.get("attachments") or ()),
                runtime["model"],
                profile,
                task["cancel_event"],
                kimodo_root,
                progress=report_progress,
            )
            session["ardy_stream"] = stream
            session["ardy_stream_signature"] = signature if stream is not None else ""
            return response, payload

        try:
            return execute()
        except Exception as exc:
            if not _is_encoder_oom(exc) or not _fallback_runtime_text_encoder_to_cpu(
                runtime,
                task["runtime_config"],
                kimodo_root,
                logger,
                task["cancel_event"],
            ):
                raise
            bind_shared_text_encoder(
                runtime, _build_text_encoder_signature(task["runtime_config"])
            )
            return execute()

    def worker_loop() -> None:
        while True:
            with queue_changed:
                while not state["shutdown"] and not state["ready_model_workers"]:
                    queue_changed.wait(timeout=0.5)
                if state["shutdown"] and not state["ready_model_workers"]:
                    return
                worker_key = state["ready_model_workers"].popleft()
                model_worker = state["model_workers"].get(worker_key)
                if model_worker is None:
                    continue
                model_worker["ready"] = False
                if not model_worker["ready_sessions"]:
                    continue
                session_id = model_worker["ready_sessions"].popleft()
                if model_worker["ready_sessions"]:
                    model_worker["ready"] = True
                    if state["active_model_worker_key"] == worker_key:
                        state["ready_model_workers"].appendleft(worker_key)
                    else:
                        state["ready_model_workers"].append(worker_key)
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
                if str(task.get("state") or "") in ("running", "cancelling"):
                    continue
                task["state"] = "running"
                task_id = task["task_id"]
                state["active_task_id"] = task_id
                state["active_worker_count"] = int(state.get("active_worker_count") or 0) + 1
                publish_state("generating")

            task_context.task_id = task_id
            try:
                def execute_task() -> tuple[dict[str, Any], bytes | None]:
                    encoder_key = _build_text_encoder_execution_key(task["runtime_config"])
                    text_encoder_execution_gate.acquire(encoder_key)
                    try:
                        publish_state("loading_runtime")
                        task["status_message"] = "Preparing motion runtime..."
                        if task["cancel_event"].is_set():
                            raise runtime_helpers.GenerateCancelledError("Generation canceled.")
                        try:
                            runtime = get_runtime(session, task["runtime_config"], task["cancel_event"])
                        except assets.DownloadCancelledError as exc:
                            raise runtime_helpers.GenerateCancelledError(str(exc)) from exc
                        if task["cancel_event"].is_set():
                            raise runtime_helpers.GenerateCancelledError("Generation canceled.")
                        task["status_message"] = "Generating motion..."
                        publish_state("generating")
                        task_response, task_binary = run_one_shot(task, runtime, session)
                        return _attach_runtime_metadata(
                            task_response,
                            runtime.get("text_encoder_decision"),
                        ), task_binary
                    finally:
                        text_encoder_execution_gate.release(encoder_key)

                if _is_analysis_only_request(task["request"]):
                    task["status_message"] = "Analyzing KMB ClipConstraints..."
                    response, binary_payload = _execute_analysis_only(
                        task["request"],
                        tuple(task.get("attachments") or ()),
                    )
                else:
                    profile = assets.resolve_motion_model_profile(task["runtime_config"]["model"])
                    if profile is not None and profile.backend == "ardy":
                        response, binary_payload = execute_task()
                    else:
                        with non_ardy_generation_gate:
                            response, binary_payload = execute_task()
            except runtime_helpers.GenerateCancelledError as exc:
                response = {"status": "cancelled", "message": str(exc)}
                binary_payload = None
            except assets.DownloadCancelledError as exc:
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
                if _is_accelerator_oom(exc):
                    response = {
                        "status": "error",
                        "error_code": "gpu_out_of_memory",
                        "message": "GPU memory is exhausted; the task was stopped before publishing animation data.",
                    }
                    _release_accelerator_cache()
                else:
                    response = {
                        "status": "error",
                        "message": str(exc),
                    }
                binary_payload = None
                logger.log(f"[ERROR] Generate task {task_id} failed: {exc}")
            finally:
                task_context.task_id = ""
            with queue_changed:
                if task["cancel_event"].is_set():
                    response = {"status": "cancelled", "message": "Generation canceled."}
                    binary_payload = None
                finish_task_locked(session, task, response, binary_payload)
                state["last_activity"] = time.time()
                state["active_worker_count"] = max(0, int(state.get("active_worker_count") or 0) - 1)
                publish_state("generating" if state["active_worker_count"] else "idle")
                queue_changed.notify_all()

    threading.Thread(target=lifecycle_monitor_loop, daemon=True).start()
    worker_threads = [
        threading.Thread(target=worker_loop, name=f"KimodoGenerate-{index + 1}", daemon=True)
        for index in range(ardy_backend.ARDY_BATCH_SIZE)
    ]
    for worker_thread in worker_threads:
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
                _publish_cancelled_task_to_client(task, task["status_message"])
                return _attach_task_id(
                    {
                        "status": "done",
                        "cancel_status": "cancelling",
                        "message": task["status_message"],
                    },
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
                {
                    "status": "done",
                    "cancel_status": "cancelled",
                    "message": task["status_message"],
                },
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
                    stream_status, stream_message = _build_streaming_status_message(
                        current_state,
                        queue_index,
                        task_id,
                        str(task.get("status_message") or ""),
                    )

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

                        if cmd == "help":
                            reply({"status": "done", **_build_protocol_help(kimodo_root)})
                        elif cmd == "runtime.list_models":
                            runtime_profile = runtime_helpers._runtime_self_check(None)
                            list_config = _normalize_runtime_config(request, session["default_config"])
                            reply(_build_model_configurations(kimodo_root, list_config, runtime_profile))
                        elif cmd == "session.open":
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
                        elif cmd == "generate":
                            attachments = _read_kmb_attachments(file, request)
                            task_id = resolve_request_task_id(request)
                            request["task_id"] = task_id

                            with queue_changed:
                                requested_profile = assets.resolve_motion_model_profile(
                                    str(request.get("model") or session["default_config"].get("model") or "")
                                )
                                is_ardy_request = requested_profile is not None and requested_profile.backend == "ardy"
                                if task_id in state["tasks"]:
                                    reply(
                                        _attach_task_id(
                                            {"status": "error", "message": f"Duplicate task_id '{task_id}'."},
                                            task_id))
                                    continue
                                if not is_ardy_request and len(session["queue"]) + (
                                    1 if session.get("active") is not None else 0
                                ) >= 32:
                                    reply({
                                        "status": "error",
                                        "error_code": "session_queue_full",
                                        "message": "Session Generate queue limit is 32.",
                                    })
                                    continue
                                active_config = _normalize_runtime_config(request, session["default_config"])
                                session["default_config"] = dict(active_config)
                                if is_ardy_request:
                                    register_ardy_session_locked(session)
                                if is_ardy_request and "duration" not in request:
                                    while session["queue"]:
                                        superseded = session["queue"].popleft()
                                        finish_task_locked(
                                            session,
                                            superseded,
                                            {
                                                "status": "cancelled",
                                                "message": (
                                                    f"Task '{superseded['task_id']}' was superseded by newer "
                                                    f"ARDY Generate '{task_id}'."
                                                ),
                                            },
                                        )

                                task = {
                                    "task_id": task_id,
                                    "session_id": bound_session_id,
                                    "connection_id": connection_id,
                                    "request_id": request_id,
                                    "request": dict(request),
                                    "attachments": attachments,
                                    "runtime_config": dict(active_config),
                                    "model_worker_key": _build_task_worker_key(
                                        active_config, bound_session_id
                                    ),
                                    "cancel_event": threading.Event(),
                                    "event": threading.Event(),
                                    "response": None,
                                    "binary": None,
                                    "state": "queued",
                                    "status_message": f"Task '{task_id}' waiting in queue.",
                                }
                                state["tasks"][task_id] = task
                                session["queue"].append(task)
                                mark_session_ready_locked(session)

                            threading.Thread(target=stream_task_to_client, args=(task, file, writer_lock), daemon=True).start()
                        elif cmd == "cancel":
                            task_id = str(request.get("task_id") or request.get("id") or "").strip()
                            cancel_response = cancel_task(session, task_id)
                            reply(cancel_response)
                            if cancel_response.get("cancel_status") == "cancelling":
                                with queue_changed:
                                    mark_session_ready_locked(session)
                        elif cmd == "quit":
                            reply({"status": "done", "session_id": default_session_id, "server_closing": True})
                            request_shutdown("quit command")
                            return
                        else:
                            reply({"status": "error", "message": f"Unknown cmd: {cmd!r}"})
                    except ardy_backend.ArdyBackendError as exc:
                        logger.log(f"[ERROR] Command '{cmd}' failed: {exc}")
                        reply({"status": "error", "error_code": exc.code, "message": str(exc)})
                    except Exception as exc:
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
        for worker_thread in worker_threads:
            worker_thread.join()
        with state_lock:
            for session in state["sessions"].values():
                stream = session.get("ardy_stream")
                if stream is not None:
                    stream.close()
                session["ardy_stream"] = None
            runtimes = [state["active_runtime"]] + list(state["ardy_runtimes"].values()) + list(
                state["retired_runtimes"]
            )
            state["ardy_runtimes"] = {}
            state["retired_runtimes"].clear()
            state["shared_text_encoder"] = None
            state["shared_text_encoder_signature"] = ""
            state["shared_text_encoder_decision"] = None
            state["shared_text_encoder_models_root"] = ""
        with runtime_gate:
            seen: set[int] = set()
            for runtime in runtimes:
                if id(runtime) in seen:
                    continue
                seen.add(id(runtime))
                _unload_runtime_model(runtime, logger)
        # The next launcher validates and removes stale endpoints. Do not
        # delete here: a replacement supervisor may already own this path.
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
