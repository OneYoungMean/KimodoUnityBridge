from __future__ import annotations

import json
import math
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

try:
    from .flowhmr_memory import device_for_free_vram_gb
    from .flowhmr_memory import options_for_free_vram_gb
    from .flowhmr_assets import ensure_flowhmr_assets
    from .kimodo_runtime import _detect_free_vram_gb
except ImportError:  # direct test/module imports
    from flowhmr_memory import device_for_free_vram_gb
    from flowhmr_memory import options_for_free_vram_gb
    from flowhmr_assets import ensure_flowhmr_assets
    from kimodo_runtime import _detect_free_vram_gb

VIDEO_EXTENSIONS = {".mp4", ".mov", ".m4v", ".avi", ".mkv", ".webm"}
PICTURE_EXTENSIONS = {".png", ".jpg", ".jpeg"}
MAX_MEDIA_BYTES = 2 * 1024**3
MAX_KMB_BYTES = 256 * 1024**2
WORKER_TIMEOUT_SECONDS = 1800


def _resolve_free_vram_gb(simulate_free_vram_gb: float | None = None) -> float:
    if simulate_free_vram_gb is not None:
        try:
            value = float(simulate_free_vram_gb)
        except (TypeError, ValueError) as exc:
            raise ValueError("simulate_free_vram_gb must be a finite value greater than or equal to 0.") from exc
        if not math.isfinite(value) or value < 0.0:
            raise ValueError("simulate_free_vram_gb must be a finite value greater than or equal to 0.")
        return max(0.0, value)

    simulated_vram = os.environ.get("KIMODO_SIMULATE_FREE_VRAM_GB", "").strip()
    if simulated_vram:
        try:
            value = max(0.0, float(simulated_vram))
            if math.isfinite(value):
                return value
        except ValueError:
            pass
    return _detect_free_vram_gb("cuda")


def _resolve_flowhmr_device(free_vram_gb: float, configured_device: str = "") -> str:
    configured = str(configured_device or "").strip()
    if configured:
        return configured
    try:
        import torch

        if torch.cuda.is_available():
            return device_for_free_vram_gb(free_vram_gb)
    except Exception:
        pass
    return "cpu"


def analyze_media(kind: str, media_path: str, person: str | None = None,
                  enable_camera: bool = False,
                  simulate_free_vram_gb: float | None = None) -> tuple[dict[str, Any], bytes]:
    normalized_kind = str(kind or "").strip().lower()
    if normalized_kind not in {"video", "picture", "auto"}:
        raise ValueError("Analysis kind must be 'video', 'picture', or 'auto'.")
    if person not in (None, "left", "right"):
        raise ValueError("person must be 'left' or 'right'.")
    if person is not None:
        raise ValueError("FlowHMR currently selects a person automatically; manual left/right selection is not supported.")
    media = Path(str(media_path or "")).expanduser().resolve(strict=True)
    if not media.is_file():
        raise ValueError("media_path must point to a file.")
    if normalized_kind == "auto":
        if media.suffix.lower() in VIDEO_EXTENSIONS:
            normalized_kind = "video"
        elif media.suffix.lower() in PICTURE_EXTENSIONS:
            normalized_kind = "picture"
        else:
            raise ValueError(f"Unsupported media file extension: {media.suffix or '<none>'}.")
    extensions = VIDEO_EXTENSIONS if normalized_kind == "video" else PICTURE_EXTENSIONS
    if media.suffix.lower() not in extensions:
        raise ValueError(f"Unsupported {normalized_kind} file extension: {media.suffix or '<none>'}.")
    if enable_camera and normalized_kind != "video":
        raise ValueError("enable_camera is supported only for video media.")
    if media.stat().st_size > MAX_MEDIA_BYTES:
        raise ValueError(f"Media file exceeds the {MAX_MEDIA_BYTES}-byte limit.")

    runtime_root = Path(__file__).resolve().parents[1]
    flowhmr_root = Path(os.environ.get("KIMODO_FLOWHMR_ROOT") or runtime_root / "vendor" / "FlowHMR").expanduser().resolve()
    flowhmr_models = Path(os.environ.get("KIMODO_FLOWHMR_MODELS_ROOT") or runtime_root / "models" / "FlowHMR").expanduser().resolve()
    checkpoint = ensure_flowhmr_assets(flowhmr_root, skeleton_only=True, model_root=flowhmr_models)
    configured_python = os.environ.get("KIMODO_FLOWHMR_PYTHON", "").strip()
    if configured_python:
        python = Path(configured_python).expanduser().resolve(strict=True)
    else:
        default_flowhmr_python = runtime_root / ".flowhmr-venv" / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
        if not default_flowhmr_python.is_file():
            raise RuntimeError(
                f"FlowHMR Python was not found at {default_flowhmr_python}. Run QuickServer setup or set KIMODO_FLOWHMR_PYTHON."
            )
        python = default_flowhmr_python
    if not python.is_file():
        raise RuntimeError("FlowHMR Python was not found. Set KIMODO_FLOWHMR_PYTHON to its environment Python executable.")

    worker = Path(__file__).with_name("mocap_worker.py")
    # Respect the same simulated free-VRAM setting used by QuickServer model
    # selection, so FlowHMR chooses matching camera/SAM settings in tests and
    # constrained deployments.
    free_vram_gb = _resolve_free_vram_gb(simulate_free_vram_gb)
    memory_options = options_for_free_vram_gb(free_vram_gb)
    device = _resolve_flowhmr_device(
        free_vram_gb, os.environ.get("KIMODO_FLOWHMR_DEVICE", ""))
    command = [sys.executable, str(worker), "--kind", normalized_kind,
               "--media", str(media), "--flowhmr-root", str(flowhmr_root),
               "--checkpoint", str(checkpoint), "--flowhmr-python", str(python),
               "--assets-root", str(flowhmr_models),
               "--device", device, "--vggt-interval", str(memory_options.vggt_interval),
               "--sam-chunk-size", str(memory_options.sam_chunk_size)]
    if memory_options.static_camera and not enable_camera:
        command.append("--static-camera")
    if enable_camera:
        command.append("--enable-camera")
    environment = dict(os.environ)
    try:
        completed = subprocess.run(command, cwd=str(runtime_root), env=environment, capture_output=True,
                                   text=True, timeout=WORKER_TIMEOUT_SECONDS, check=False)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"FlowHMR analysis exceeded {WORKER_TIMEOUT_SECONDS} seconds.") from exc
    if completed.returncode != 0:
        diagnostic = (completed.stderr or completed.stdout or "worker exited without diagnostics").strip()
        raise RuntimeError(f"FlowHMR analysis failed: {diagnostic[-4000:]}")
    try:
        result = json.loads(completed.stdout)
        payload = bytes.fromhex(result.pop("_binary_payload"))
    except (TypeError, ValueError, KeyError, json.JSONDecodeError) as exc:
        raise RuntimeError("FlowHMR worker returned an invalid KMB response.") from exc
    if (not isinstance(result, dict) or result.get("status") != "done" or
            result.get("output_format") != "kmb_attachments_v1" or not payload or len(payload) > MAX_KMB_BYTES):
        raise RuntimeError("FlowHMR worker returned an incomplete or oversized KMB motion.")
    if int(result.get("byte_length", -1)) != len(payload):
        raise RuntimeError("FlowHMR worker KMB byte length does not match its manifest.")
    return result, payload


def error_code_for_exception(exc: Exception) -> str:
    message = str(exc).lower()
    if (
        "flowhmr python" in message
        or "flowhmr source" in message
        or "checkpoint is missing" in message
    ):
        return "mocap_not_configured"
    if isinstance(exc, FileNotFoundError):
        return "media_not_found"
    if isinstance(exc, ValueError):
        return "invalid_mocap_request"
    return "mocap_analysis_failed"
