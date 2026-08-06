#!/usr/bin/env python
"""Kimodo generation and runtime helpers used by the QuickServer supervisor.

TCP routing, Session state, and process lifecycle live in ``quickserver_cli``.
This module owns model provisioning, Kimodo generation, output conversion, and
the shared runtime checks that those services call.
"""

import json
import math
import os
from pathlib import Path
import sys
import threading
import time
from dataclasses import dataclass
from dataclasses import replace
from typing import Any

import numpy as np
from tqdm.auto import tqdm

from core import quickserver_assets as assets
from kimodo.frame_time import seconds_to_frame_count


class GenerateCancelledError(Exception):
    pass


def _resolve_cfg_text_weight(req: dict) -> float:
    text_weight = float(req.get("text_weight", 1.0))
    if not math.isfinite(text_weight) or not 0.0 <= text_weight <= 4.0:
        raise ValueError("text_weight must be in [0, 4].")
    return 2.0**text_weight


def _default_bridge_log_path(root: str) -> str:
    if not root:
        return ""
    return os.path.join(root, "log", "bridge_server.log")


def _detect_free_vram_gb(device: str | None = None) -> float:
    """Best-effort currently free accelerator memory in GiB."""
    try:
        import torch

        target = str(device or "").strip().lower()
        if target.startswith("cuda") and torch.cuda.is_available() and torch.cuda.device_count() > 0:
            index = int(target.split(":", 1)[1]) if ":" in target else 0
            free, _ = torch.cuda.mem_get_info(index)
            return float(free) / (1024 ** 3)
        if target.startswith("mps") and torch.backends.mps.is_available():
            recommended = getattr(torch.mps, "recommended_max_memory", None)
            if callable(recommended):
                allocated = getattr(torch.mps, "current_allocated_memory", lambda: 0)()
                return max(0.0, float(recommended() - allocated) / (1024 ** 3))
            return assets.KIMODO_ACCELERATOR_MIN_GB
        if target.startswith("xpu") and hasattr(torch, "xpu") and torch.xpu.is_available():
            index = int(target.split(":", 1)[1]) if ":" in target else 0
            props = torch.xpu.get_device_properties(index)
            total = getattr(props, "total_memory", 0)
            return float(total) / (1024 ** 3) if total else assets.KIMODO_ACCELERATOR_MIN_GB
    except Exception:
        pass
    return 0.0


def _detect_mps_available() -> bool:
    try:
        import torch

        return bool(torch.backends.mps.is_available())
    except Exception:
        return False


def _write_text_atomic(path: str, content: str) -> None:
    dir_path = os.path.dirname(path)
    if dir_path:
        os.makedirs(dir_path, exist_ok=True)
    tmp_path = f"{path}.tmp.{os.getpid()}"
    with open(tmp_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)
        f.flush()
        os.fsync(f.fileno())
    for _ in range(30):
        try:
            os.replace(tmp_path, path)
            return
        except PermissionError:
            time.sleep(0.05)
    # Fallback for Windows file sharing edge-cases: overwrite directly.
    for _ in range(30):
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(content)
                f.flush()
                os.fsync(f.fileno())
            try:
                os.remove(tmp_path)
            except Exception:
                pass
            return
        except PermissionError:
            time.sleep(0.05)
    raise PermissionError(f"Failed to write file after retries: {path}")


def _out(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def _log(msg: str):
    line = str(msg)
    verbose_network_log = os.environ.get("KIMODO_BRIDGE_VERBOSE_NETWORK_LOG", "").strip() == "1"
    if not verbose_network_log:
        if line.startswith("[bridge] accept "):
            return
    log_path = os.environ.get("KIMODO_BRIDGE_LOG", "")
    direct_only = os.environ.get("KIMODO_BRIDGE_LOG_DIRECT_ONLY", "").strip() == "1"
    sys.stderr.write(line + "\n")
    sys.stderr.flush()
    if direct_only:
        return
    if not log_path:
        root = os.environ.get("KIMODO_ROOT_PATH", "")
        if root:
            log_path = _default_bridge_log_path(root)
    if not log_path:
        return
    try:
        log_dir = os.path.dirname(log_path)
        if log_dir:
            os.makedirs(log_dir, exist_ok=True)
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(line + "\n")
    except Exception:
        pass


class _BridgeAssetLogger:
    def log(self, message: str) -> None:
        _log(message)


@dataclass(frozen=True)
class _BridgeProvisionPlan:
    resolved_model: assets.ResolvedModel
    models_root: Path
    using_external_models: bool
    runtime_decision: assets.TextEncoderRuntimeDecision
    text_encoder_layout: assets.TextEncoderLayoutSpec

    @property
    def encoder_route(self) -> str:
        return self.runtime_decision.encoder_route


@dataclass(frozen=True)
class _RuntimeSelfCheckResult:
    backend_profile: str
    runtime_device: str
    kernel_ok: bool
    bnb_present: bool
    bnb_ok: bool
    nf4_available: bool
    int8_accelerator_available: bool
    fp16_accelerator_available: bool
    free_vram_gb: float

def _env_flag(name: str, default: bool = False) -> bool:
    raw = os.environ.get(name, "").strip().lower()
    if not raw:
        return bool(default)
    return raw not in ("0", "false", "no", "off")


def _ensure_asset_ready(
    asset: assets.AssetSpec,
    target_dir: Path,
    logger: _BridgeAssetLogger,
    recovery_flag_dir: Path,
    download_counter: list[int],
    *,
    force_site: assets.DownloadSite | None,
    allow_download: bool,
) -> None:
    if assets.asset_is_ready(asset, target_dir):
        logger.log(f"[SKIP] {asset.label} ready: {target_dir}")
        return
    if target_dir.exists():
        logger.log(f"[INFO] {asset.label} exists but looks incomplete, refreshing: {target_dir}")
    assets.ensure_asset_present(
        asset,
        target_dir,
        logger,
        recovery_flag_dir,
        download_counter,
        force_site=force_site,
        allow_download=allow_download,
    )


def _build_bridge_provision_plan(
    kimodo_root: str,
    requested_model: str,
    *,
    runtime_profile: _RuntimeSelfCheckResult,
    encoder_free_vram_gb: float | None = None,
) -> _BridgeProvisionPlan:
    root_path = Path(kimodo_root).resolve()
    resolved_model = assets.resolve_main_model(requested_model)
    models_root, using_external_models = assets.resolve_models_root(
        root_path,
        os.environ.get("KIMODO_MODELS_ROOT"),
    )
    if encoder_free_vram_gb is None:
        motion_required_gb = assets.motion_model_min_free_vram_gb(resolved_model.local_name)
        free_vram_gb = runtime_profile.free_vram_gb
        if runtime_profile.runtime_device != "cpu" and free_vram_gb < motion_required_gb:
            raise RuntimeError(
                "GPU out of memory before model load: "
                f"{resolved_model.local_name} needs at least {motion_required_gb:g}GB free, "
                f"but only {free_vram_gb:.2f}GB is available."
            )
        encoder_free_vram_gb = max(0.0, free_vram_gb - motion_required_gb)
    runtime_decision = assets.resolve_text_encoder_runtime(
        os.environ.get("KIMODO_TEXT_ENCODER_MODE"),
        runtime_profile.runtime_device,
        encoder_free_vram_gb,
        nf4_available=runtime_profile.nf4_available,
        int8_accelerator_available=runtime_profile.int8_accelerator_available,
        fp16_accelerator_available=runtime_profile.fp16_accelerator_available,
    )
    if _env_flag("KIMODO_TEXT_ENCODER_FORCE_CPU", False):
        runtime_decision = assets.force_text_encoder_cpu(runtime_decision)
    text_encoder_layout = assets.select_text_encoder_layout_for_route(
        runtime_decision.encoder_route,
        models_root,
        runtime_decision.encoder_device,
    )
    return _BridgeProvisionPlan(
        resolved_model=resolved_model,
        models_root=models_root,
        using_external_models=using_external_models,
        runtime_decision=runtime_decision,
        text_encoder_layout=text_encoder_layout,
    )


def _apply_bridge_runtime_env(kimodo_root: str, plan: _BridgeProvisionPlan) -> None:
    root_path = Path(kimodo_root).resolve()
    source_root = root_path / "kimodo"
    if not (source_root / "pyproject.toml").is_file():
        source_root = root_path
    runtime_env = assets.build_runtime_env(
        root_dir=root_path,
        source_root=source_root,
        models_root=plan.models_root,
        text_encoder_mode=plan.runtime_decision.mode,
        encoder_device=plan.runtime_decision.encoder_device,
        encoder_route=plan.encoder_route,
        encoder_layout_id=plan.text_encoder_layout.layout_id,
    )
    assets.scrub_removed_runtime_env(os.environ)
    os.environ.update(runtime_env)


def _provision_bridge_assets(
    kimodo_root: str,
    requested_model: str,
    *,
    runtime_profile: _RuntimeSelfCheckResult,
    force_download_site: assets.DownloadSite | None = None,
    encoder_free_vram_gb: float | None = None,
) -> _BridgeProvisionPlan:
    plan = _build_bridge_provision_plan(
        kimodo_root,
        requested_model,
        runtime_profile=runtime_profile,
        encoder_free_vram_gb=encoder_free_vram_gb,
    )
    logger = _BridgeAssetLogger()
    recovery_flag_dir = Path(kimodo_root).resolve() / "archive" / "recovery_flags"
    recycle_dir = Path(kimodo_root).resolve() / "archive" / "recycle"
    allow_download = True
    download_counter = [0]

    _apply_bridge_runtime_env(kimodo_root, plan)
    if allow_download:
        plan.models_root.mkdir(parents=True, exist_ok=True)

    logger.log(
        f"[bridge] asset plan: model={plan.resolved_model.local_name} "
        f"models_root={plan.models_root} encoder_route={plan.encoder_route} "
        f"encoder_device={plan.runtime_decision.encoder_device} "
        f"encoder_layout={plan.text_encoder_layout.layout_id} "
        f"external_models_root={plan.using_external_models}"
    )
    encoder_primary_dir, encoder_peft_dir = assets.resolve_text_encoder_layout_paths(
        plan.text_encoder_layout,
        plan.models_root,
    )
    logger.log(
        f"[INFO] Text encoder layout selected: {plan.text_encoder_layout.layout_id} "
        f"label={plan.text_encoder_layout.label} primary={encoder_primary_dir}"
        + (f" peft={encoder_peft_dir}" if encoder_peft_dir is not None else "")
    )

    main_asset = assets.AssetSpec(
        label="main model",
        local_dir_name=plan.resolved_model.local_name,
        modelscope_repo=plan.resolved_model.modelscope_repo,
        huggingface_repo=plan.resolved_model.huggingface_repo,
    )
    main_dir = assets.local_model_dir(plan.models_root, plan.resolved_model)
    _ensure_asset_ready(
        main_asset,
        main_dir,
        logger,
        recovery_flag_dir,
        download_counter,
        force_site=force_download_site,
        allow_download=allow_download,
    )

    encoder_assets = list(plan.text_encoder_layout.download_assets)

    for encoder_asset in encoder_assets:
        _ensure_asset_ready(
            encoder_asset,
            plan.models_root / encoder_asset.local_dir_name,
            logger,
            recovery_flag_dir,
            download_counter,
            force_site=force_download_site,
            allow_download=allow_download,
        )

    if assets.should_inject_once(
        recovery_flag_dir,
        "model_missing_after_download",
        "KIMODO_TEST_INJECT_MODEL_MISSING_AFTER_DOWNLOAD_ONCE",
    ):
        logger.log(f"[TEST] Injected model-missing-once by archiving downloaded asset dir: {main_dir}")
        assets.archive_path(main_dir, recycle_dir)

    logger.log(
        f"[bridge] asset plan complete: model={plan.resolved_model.local_name} "
        f"encoder_route={plan.encoder_route} encoder_layout={plan.text_encoder_layout.layout_id} "
        f"downloads={download_counter[0]}"
    )
    return plan


def _rotation_mats_to_quat_wxyz(rot_mats: np.ndarray) -> np.ndarray:
    m = rot_mats.astype(np.float32, copy=False).reshape(-1, 3, 3)
    q = np.zeros((m.shape[0], 4), dtype=np.float32)

    tr = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    mask_t = tr > 0.0
    if np.any(mask_t):
        s = np.sqrt(tr[mask_t] + 1.0) * 2.0
        q[mask_t, 0] = 0.25 * s
        q[mask_t, 1] = (m[mask_t, 2, 1] - m[mask_t, 1, 2]) / s
        q[mask_t, 2] = (m[mask_t, 0, 2] - m[mask_t, 2, 0]) / s
        q[mask_t, 3] = (m[mask_t, 1, 0] - m[mask_t, 0, 1]) / s

    mask_x = (~mask_t) & (m[:, 0, 0] > m[:, 1, 1]) & (m[:, 0, 0] > m[:, 2, 2])
    if np.any(mask_x):
        s = np.sqrt(1.0 + m[mask_x, 0, 0] - m[mask_x, 1, 1] - m[mask_x, 2, 2]) * 2.0
        q[mask_x, 0] = (m[mask_x, 2, 1] - m[mask_x, 1, 2]) / s
        q[mask_x, 1] = 0.25 * s
        q[mask_x, 2] = (m[mask_x, 0, 1] + m[mask_x, 1, 0]) / s
        q[mask_x, 3] = (m[mask_x, 0, 2] + m[mask_x, 2, 0]) / s

    mask_y = (~mask_t) & (~mask_x) & (m[:, 1, 1] > m[:, 2, 2])
    if np.any(mask_y):
        s = np.sqrt(1.0 + m[mask_y, 1, 1] - m[mask_y, 0, 0] - m[mask_y, 2, 2]) * 2.0
        q[mask_y, 0] = (m[mask_y, 0, 2] - m[mask_y, 2, 0]) / s
        q[mask_y, 1] = (m[mask_y, 0, 1] + m[mask_y, 1, 0]) / s
        q[mask_y, 2] = 0.25 * s
        q[mask_y, 3] = (m[mask_y, 1, 2] + m[mask_y, 2, 1]) / s

    mask_z = (~mask_t) & (~mask_x) & (~mask_y)
    if np.any(mask_z):
        s = np.sqrt(1.0 + m[mask_z, 2, 2] - m[mask_z, 0, 0] - m[mask_z, 1, 1]) * 2.0
        q[mask_z, 0] = (m[mask_z, 1, 0] - m[mask_z, 0, 1]) / s
        q[mask_z, 1] = (m[mask_z, 0, 2] + m[mask_z, 2, 0]) / s
        q[mask_z, 2] = (m[mask_z, 1, 2] + m[mask_z, 2, 1]) / s
        q[mask_z, 3] = 0.25 * s

    norm = np.linalg.norm(q, axis=1, keepdims=True)
    norm[norm < 1e-8] = 1.0
    q = q / norm
    return q.reshape(*rot_mats.shape[:-2], 4)


def _resolve_skeleton_for_joint_count(skeleton, num_joints: int):
    if skeleton is None:
        return None
    if hasattr(skeleton, "somaskel77") and int(num_joints) == 77:
        return skeleton.somaskel77
    if hasattr(skeleton, "somaskel30") and int(num_joints) == 30:
        return skeleton.somaskel30
    return skeleton


def _extract_local_rot_mats(model, output, sample_index: int):
    local_rot = None

    if output.get("local_rot_mats") is not None:
        candidate = output["local_rot_mats"][sample_index]
        if candidate is not None:
            local_rot = np.asarray(candidate, dtype=np.float32)

    if local_rot is None and output.get("global_rot_mats") is not None:
        try:
            from kimodo.skeleton import global_rots_to_local_rots
            import torch

            global_rot = np.asarray(output["global_rot_mats"][sample_index], dtype=np.float32)
            num_joints = int(np.asarray(output["posed_joints"]).shape[2])
            skeleton = _resolve_skeleton_for_joint_count(model.skeleton, num_joints)
            if skeleton is not None:
                global_rot_t = torch.from_numpy(global_rot)
                if hasattr(skeleton, "joint_parents") and isinstance(skeleton.joint_parents, torch.Tensor):
                    global_rot_t = global_rot_t.to(skeleton.joint_parents.device)
                local_rot_t = global_rots_to_local_rots(global_rot_t, skeleton)
                local_rot = local_rot_t.detach().cpu().numpy().astype(np.float32, copy=False)
        except Exception as exc:
            _out({"status": "progress", "message": f"rotation fallback failed: {exc}"})

    return local_rot


def _extract_flat_local_rot_quats(model, output, sample_index: int):
    local_rot = _extract_local_rot_mats(model, output, sample_index)
    if local_rot is None:
        return None

    q_wxyz = _rotation_mats_to_quat_wxyz(local_rot)
    return q_wxyz.reshape(-1).tolist()


def _extract_local_rot_quats_array(model, output, sample_index: int):
    local_rot = _extract_local_rot_mats(model, output, sample_index)
    if local_rot is None:
        return None

    q_wxyz = _rotation_mats_to_quat_wxyz(local_rot).astype(np.float32, copy=False)
    return q_wxyz.reshape(-1)


def _parents_and_names(model, num_joints: int):
    parents = None
    names = None
    skel = _resolve_skeleton_for_joint_count(getattr(model, "skeleton", None), num_joints)
    if skel is not None:
        if hasattr(skel, "joint_parents"):
            try:
                jp = skel.joint_parents
                if hasattr(jp, "detach"):
                    jp = jp.detach().cpu().tolist()
                elif hasattr(jp, "cpu"):
                    jp = jp.cpu().tolist()
                parents = [int(x) for x in jp]
            except Exception:
                parents = None
        if hasattr(skel, "bone_order_names"):
            try:
                names = [str(x) for x in list(skel.bone_order_names)]
            except Exception:
                names = None

    if not parents or len(parents) != num_joints:
        parents = [-1] + [i for i in range(num_joints - 1)]
    if not names or len(names) != num_joints:
        names = [f"joint_{i}" for i in range(num_joints)]
    return parents, names


def _load_constraints(constraints_json: str, model, horizon_frames: int | None = None):
    if not constraints_json:
        return []

    from kimodo.constraints import load_constraints_lst

    text = constraints_json.strip() if isinstance(constraints_json, str) else constraints_json
    if not isinstance(text, str) or not text:
        return []
    if text[0] not in ("[", "{"):
        raise ValueError(
            "constraints_json must be inline JSON (array/object). File path input is no longer supported."
        )

    try:
        parsed = json.loads(text)
    except Exception as ex:
        raise ValueError(f"Invalid inline constraints_json payload: {ex}") from ex

    if isinstance(parsed, dict):
        parsed = [parsed]
    if not isinstance(parsed, list):
        raise ValueError("constraints_json inline payload must be JSON array/object.")
    if any(isinstance(item, dict) and item.get("type") == "clip" for item in parsed):
        raise ValueError("Clip constraints are supported only by ARDY models.")

    root_target = None
    plain_constraints = []
    for item in parsed:
        if isinstance(item, dict) and item.get("type") == "root2d_target":
            from core.ardy_backend import _parse_root_2d_target

            root_target = _parse_root_2d_target(item)
            continue
        plain_constraints.append(item)

    if root_target is not None:
        from core.ardy_backend import _plan_root_2d_target

        fps = float(model.fps)
        target_horizon = (
            max(1, int(horizon_frames))
            if horizon_frames is not None
            else max(1, seconds_to_frame_count(5.0, fps))
        )
        if root_target.arrival_frame is not None:
            root_target = replace(
                root_target,
                arrival_frame=min(root_target.arrival_frame, target_horizon - 1),
            )
        planned_target = _plan_root_2d_target(
            root_target,
            (0.0, 0.0),
            (0.0, 0.0),
            -1,
            fps,
            target_horizon,
            extend_prediction_to_horizon=True,
        )
        if planned_target is not None:
            if "global_root_heading" in planned_target:
                planned_target["global_root_heading"] = [
                    [math.cos(float(angle)), math.sin(float(angle))]
                    for angle in planned_target["global_root_heading"]
                ]
            plain_constraints.append(planned_target)

    return load_constraints_lst(plain_constraints, model.skeleton)


def _restore_kimodo_output_origin(output: dict, transform, model) -> dict:
    if transform is None:
        return output

    translation, yaw = transform
    x, z = (float(value) for value in translation.detach().cpu())
    angle = float(yaw.detach().cpu())
    cos, sin = np.cos(angle), np.sin(angle)
    rotation_3d = np.asarray(((cos, 0.0, sin), (0.0, 1.0, 0.0), (-sin, 0.0, cos)), dtype=np.float32)
    rotation_2d = np.asarray(((cos, sin), (-sin, cos)), dtype=np.float32)
    offset = np.asarray((x, 0.0, z), dtype=np.float32)

    for name in ("posed_joints", "root_positions", "smooth_root_pos"):
        if output.get(name) is not None:
            output[name] = np.asarray(output[name]) @ rotation_3d.T + offset
    if output.get("global_root_heading") is not None:
        output["global_root_heading"] = np.asarray(output["global_root_heading"]) @ rotation_2d
    if output.get("global_rot_mats") is not None:
        output["global_rot_mats"] = rotation_3d @ np.asarray(output["global_rot_mats"])
    if output.get("local_rot_mats") is not None:
        local_rotations = np.asarray(output["local_rot_mats"]).copy()
        root_index = int(getattr(getattr(model, "skeleton", None), "root_idx", 0))
        local_rotations[..., root_index, :, :] = rotation_3d @ local_rotations[..., root_index, :, :]
        output["local_rot_mats"] = local_rotations
    return output


@dataclass
class UnityMotionJsonResult:
    num_frames: int
    num_joints: int
    fps: int
    joint_names: list[str]
    joint_parents: list[int]
    joints: list[float]
    local_rot_quats: list[float] | None
    texts: list[str]
    skeleton: str

    @classmethod
    def from_model_output(cls, model: Any, output: dict, prompt: str, sample_index: int = 0):
        sample_joints = np.asarray(output["posed_joints"][sample_index], dtype=np.float32)
        flat_joints = sample_joints.reshape(-1).tolist()
        joint_count = int(sample_joints.shape[1])
        parents, joint_names = _parents_and_names(model, joint_count)
        local_rot_quats = _extract_flat_local_rot_quats(model, output, sample_index)
        return cls(
            num_frames=int(sample_joints.shape[0]),
            num_joints=joint_count,
            fps=int(model.fps),
            joint_names=joint_names,
            joint_parents=parents,
            joints=flat_joints,
            local_rot_quats=local_rot_quats,
            texts=[prompt],
            skeleton=getattr(getattr(model, "skeleton", None), "name", "unknown"),
        )

    def to_compact_json(self) -> str:
        payload = {
            "num_frames": self.num_frames,
            "num_joints": self.num_joints,
            "fps": self.fps,
            "joint_names": self.joint_names,
            "joint_parents": self.joint_parents,
            "joints": self.joints,
            "local_rot_quats": self.local_rot_quats,
            "texts": self.texts,
            "skeleton": self.skeleton,
        }
        return json.dumps(payload, separators=(",", ":"))


def _resolve_bridge_output_format() -> str:
    raw = os.environ.get("KIMODO_BRIDGE_OUTPUT_FORMAT", "json_compact").strip().lower()
    return raw if raw in ("json_compact", "bvh", "kmb_v1") else "json_compact"


def _resolve_requested_output_format(req: dict | None = None) -> str:
    if isinstance(req, dict):
        raw = str(req.get("output_format", "") or "").strip().lower()
        if raw in ("json_compact", "bvh", "kmb_v1"):
            return raw
    return _resolve_bridge_output_format()


def _resolve_bridge_bvh_standard_tpose() -> bool:
    return _env_flag("KIMODO_BRIDGE_BVH_STANDARD_TPOSE", False)


def _detect_xpu_available() -> bool:
    try:
        import torch

        return bool(hasattr(torch, "xpu") and torch.xpu.is_available())
    except Exception:
        return False


def _probe_device_kernel(device: str) -> bool:
    try:
        import torch

        target = torch.device(device)
        value = torch.zeros(8, device=target, dtype=torch.float32)
        (value + 1).sum().item()
        if target.type == "cuda":
            torch.cuda.synchronize(target)
        elif target.type == "xpu" and hasattr(torch, "xpu") and hasattr(torch.xpu, "synchronize"):
            torch.xpu.synchronize()
        return True
    except Exception:
        return False


def _probe_bitsandbytes(device: str) -> tuple[bool, bool, bool]:
    try:
        import importlib.util

        if importlib.util.find_spec("bitsandbytes") is None:
            return False, False, False

        import bitsandbytes as bnb  # type: ignore
        _ = getattr(bnb, "__version__", "")
    except Exception:
        return True, False, False

    if not str(device or "").lower().startswith("cuda"):
        return True, False, False

    nf4_ok = False
    int8_ok = False
    try:
        import torch
        from bitsandbytes.nn import Linear4bit  # type: ignore

        layer = Linear4bit(8, 8, bias=False, compute_dtype=torch.float16, quant_type="nf4").to(device)
        layer(torch.ones((1, 8), device=device, dtype=torch.float16)).sum().item()
        nf4_ok = True
    except Exception:
        nf4_ok = False

    try:
        import torch
        from bitsandbytes.nn import Linear8bitLt  # type: ignore

        layer = Linear8bitLt(8, 8, bias=False, has_fp16_weights=False).to(device)
        layer(torch.ones((1, 8), device=device, dtype=torch.float16)).sum().item()
        int8_ok = True
    except Exception:
        int8_ok = False
    return True, nf4_ok, int8_ok


def _probe_fp16_kernel(device: str) -> bool:
    try:
        import torch

        target = torch.device(device)
        left = torch.ones((8, 8), device=target, dtype=torch.float16)
        right = torch.ones((8, 8), device=target, dtype=torch.float16)
        (left @ right).sum().item()
        if target.type == "cuda":
            torch.cuda.synchronize(target)
        elif target.type == "xpu" and hasattr(torch, "xpu") and hasattr(torch.xpu, "synchronize"):
            torch.xpu.synchronize()
        return True
    except Exception:
        return False


def _runtime_self_check(requested_device: str | None) -> _RuntimeSelfCheckResult:
    requested = str(requested_device or "").strip().lower()
    simulated_vram_gb: float | None = None
    raw_simulated_vram = (
        os.environ.get("KIMODO_SIMULATE_FREE_VRAM_GB", "").strip()
        or os.environ.get("KIMODO_SIMULATE_VRAM_GB", "").strip()
    )
    if raw_simulated_vram:
        try:
            simulated_vram_gb = max(0.0, float(raw_simulated_vram))
        except Exception:
            pass

    candidates: list[tuple[str, str]] = []
    if requested:
        if requested == "cpu":
            candidates = [("cpu", "cpu")]
        elif requested.startswith("cuda"):
            candidates = [("cuda", requested if ":" in requested else "cuda:0")]
        elif requested.startswith("mps"):
            candidates = [("mps", "mps")]
        elif requested.startswith("xpu"):
            candidates = [("xpu", requested if ":" in requested else "xpu:0")]
        else:
            candidates = [("generic_gpu", requested)]
    else:
        candidates = [
            ("cuda", "cuda:0"),
            ("mps", "mps"),
            ("xpu", "xpu:0"),
        ]

    selected_profile = "cpu"
    selected_device = "cpu"
    kernel_ok = False

    for profile, device in candidates:
        if profile == "cuda":
            try:
                import torch

                if not torch.cuda.is_available():
                    continue
            except Exception:
                continue
        elif profile == "mps" and not _detect_mps_available():
            continue
        elif profile == "xpu" and not _detect_xpu_available():
            continue

        if _probe_device_kernel(device):
            selected_profile = profile
            selected_device = device
            kernel_ok = True
            break

    free_vram_gb = (
        simulated_vram_gb
        if simulated_vram_gb is not None
        else _detect_free_vram_gb(selected_device)
    )
    if simulated_vram_gb == 0.0:
        selected_profile = "cpu"
        selected_device = "cpu"
        kernel_ok = False
    bnb_present, nf4_bnb_ok, int8_bnb_ok = _probe_bitsandbytes(selected_device)
    nf4_available = selected_profile == "cuda" and kernel_ok and bnb_present and nf4_bnb_ok
    int8_accelerator_available = (
        selected_profile == "cuda" and kernel_ok and bnb_present and int8_bnb_ok
    )
    fp16_accelerator_available = kernel_ok and selected_profile != "cpu" and _probe_fp16_kernel(selected_device)
    return _RuntimeSelfCheckResult(
        backend_profile=selected_profile,
        runtime_device=selected_device,
        kernel_ok=kernel_ok,
        bnb_present=bnb_present,
        bnb_ok=nf4_bnb_ok,
        nf4_available=nf4_available,
        int8_accelerator_available=int8_accelerator_available,
        fp16_accelerator_available=fp16_accelerator_available,
        free_vram_gb=free_vram_gb,
    )


def _build_generate_response(model: Any, output: dict, prompt: str, sample_index: int = 0) -> dict:
    output_format = _resolve_bridge_output_format()
    motion_data = UnityMotionJsonResult.from_model_output(model, output, prompt, sample_index=sample_index)
    if output_format != "bvh":
        return {
            "status": "done",
            "output_format": "json_compact",
            "motion_json_compact": motion_data.to_compact_json(),
        }

    from kimodo.exports.bvh import motion_to_bvh

    sample_joints = np.asarray(output["posed_joints"][sample_index], dtype=np.float32)
    num_joints = int(sample_joints.shape[1])
    skeleton = _resolve_skeleton_for_joint_count(getattr(model, "skeleton", None), num_joints)
    if skeleton is None:
        raise ValueError(f"Cannot resolve skeleton for BVH export with joint_count={num_joints}.")

    local_rot_mats = _extract_local_rot_mats(model, output, sample_index)
    if local_rot_mats is None:
        raise ValueError("BVH export requires local rotations, but none were available in model output.")

    import torch

    root_idx = int(getattr(skeleton, "root_idx", 0))
    local_rot_mats_t = torch.as_tensor(local_rot_mats, dtype=torch.float32)
    root_positions = torch.as_tensor(sample_joints[:, root_idx, :], dtype=torch.float32)
    bvh_text = motion_to_bvh(
        local_rot_mats_t,
        root_positions,
        skeleton=skeleton,
        fps=float(model.fps),
        standard_tpose=_resolve_bridge_bvh_standard_tpose(),
    )
    return {
        "status": "done",
        "output_format": "bvh",
        "motion_bvh": bvh_text,
    }


def _build_generate_flatbuffer_payload(model: Any, output: dict, sample_index: int = 0) -> bytes:
    import flatbuffers

    from core.protocol.generated import MotionPacket

    sample_joints = np.asarray(output["posed_joints"][sample_index], dtype=np.float32)
    if sample_joints.ndim != 3 or sample_joints.shape[2] < 3:
        raise ValueError(f"Unexpected posed_joints shape for flatbuffer export: {sample_joints.shape!r}")
    if not np.isfinite(sample_joints).all():
        raise ValueError("FlatBuffer posed_joints contains NaN or Infinity.")

    num_frames = int(sample_joints.shape[0])
    num_joints = int(sample_joints.shape[1])
    joint_parents, joint_names = _parents_and_names(model, num_joints)
    root_joint_index = 0
    for index, parent in enumerate(joint_parents):
        if int(parent) < 0:
            root_joint_index = index
            break

    root_positions = np.asarray(sample_joints[:, root_joint_index, :], dtype=np.float32).reshape(-1)
    local_rot_quats = _extract_local_rot_quats_array(model, output, sample_index)
    if local_rot_quats is None or int(local_rot_quats.size) == 0:
        raise ValueError("FlatBuffer export requires local_rot_quats, but none were available in model output.")
    if not np.isfinite(local_rot_quats).all():
        raise ValueError("FlatBuffer local_rot_quats contains NaN or Infinity.")
    quat_norms = np.linalg.norm(local_rot_quats.reshape(-1, 4), axis=1)
    if np.any(quat_norms < 1e-6):
        raise ValueError("FlatBuffer local_rot_quats contains a zero-length quaternion.")

    foot_contacts = np.asarray(output.get("foot_contacts", []))
    if foot_contacts.ndim == 3:
        foot_contacts = foot_contacts[sample_index]
    if foot_contacts.size:
        if foot_contacts.ndim != 2:
            raise ValueError(
                f"FlatBuffer foot_contacts must have shape ({num_frames}, 4); got {foot_contacts.shape!r}."
            )
        if foot_contacts.shape[1] == 6:
            foot_contacts = foot_contacts[:, [0, 1, 3, 4]]
        if foot_contacts.shape != (num_frames, 4):
            raise ValueError(
                f"FlatBuffer foot_contacts must have shape ({num_frames}, 4); got {foot_contacts.shape!r}."
            )
        foot_contacts = np.ascontiguousarray((foot_contacts >= 0.5).astype(np.uint8)).reshape(-1)

    builder = flatbuffers.Builder(
        max(1024, int(local_rot_quats.size * 4 + root_positions.size * 4 + foot_contacts.size + 512))
    )

    model_name_offset = builder.CreateString(str(getattr(model, "name", "") or ""))
    joint_name_offsets = [builder.CreateString(str(name or "")) for name in joint_names]
    MotionPacket.StartJointNamesVector(builder, len(joint_name_offsets))
    for joint_name_offset in reversed(joint_name_offsets):
        builder.PrependUOffsetTRelative(joint_name_offset)
    joint_names_offset = builder.EndVector()
    joint_parents_offset = builder.CreateNumpyVector(np.asarray(joint_parents, dtype=np.int32))
    root_positions_offset = builder.CreateNumpyVector(root_positions)
    local_rot_quats_offset = builder.CreateNumpyVector(local_rot_quats)
    foot_contacts_offset = builder.CreateNumpyVector(foot_contacts) if foot_contacts.size else None

    MotionPacket.Start(builder)
    MotionPacket.AddVersion(builder, 1)
    MotionPacket.AddFps(builder, float(model.fps))
    MotionPacket.AddNumFrames(builder, num_frames)
    MotionPacket.AddNumJoints(builder, num_joints)
    MotionPacket.AddJointNames(builder, joint_names_offset)
    MotionPacket.AddJointParents(builder, joint_parents_offset)
    MotionPacket.AddRootPositions(builder, root_positions_offset)
    MotionPacket.AddLocalRotQuats(builder, local_rot_quats_offset)
    MotionPacket.AddModelName(builder, model_name_offset)
    if foot_contacts_offset is not None:
        MotionPacket.AddFootContacts(builder, foot_contacts_offset)
    packet = MotionPacket.End(builder)
    builder.Finish(packet, file_identifier=b"KMB1")
    return bytes(builder.Output())


def _write_json_line(file, payload: dict) -> None:
    file.write((json.dumps(payload) + "\n").encode("utf-8"))
    file.flush()


def _write_kmb_generate_response(file, payload: bytes) -> None:
    header = {
        "status": "done",
        "output_format": "kmb_v1",
        "byte_length": len(payload),
    }
    file.write((json.dumps(header) + "\n").encode("utf-8"))
    file.write(payload)
    file.flush()


def _make_cancelable_progress_bar(cancel_event: threading.Event):
    def _progress_bar(iterable):
        progress = tqdm(iterable, ascii=" =O")
        try:
            for item in progress:
                if cancel_event.is_set():
                    raise GenerateCancelledError("Generation canceled.")
                yield item
            if cancel_event.is_set():
                raise GenerateCancelledError("Generation canceled.")
        finally:
            progress.close()

    return _progress_bar


def _generation_segment_frames(num_frames: int, fps: float, max_duration_seconds: float = 10.0) -> list[int]:
    """Split long Kimodo requests into equal, transition-connected model segments."""
    if num_frames < 1:
        raise ValueError(f"num_frames must be positive, got {num_frames}.")
    max_frames = max(1, seconds_to_frame_count(max_duration_seconds, fps))
    segment_count = math.ceil(num_frames / max_frames)
    base_frames, extra_frames = divmod(num_frames, segment_count)
    return [base_frames + (1 if index < extra_frames else 0) for index in range(segment_count)]


def _run_generate(
    req: dict,
    model,
    cancel_event: threading.Event | None = None,
    emit_progress: bool = True,
):
    from kimodo.tools import seed_everything

    prompt = str(req.get("prompt", "A person walks forward.")).strip()
    if not prompt.endswith("."):
        prompt += "."

    duration = float(req.get("duration", 5.0))
    seed = req.get("seed", None)
    diffusion_steps = int(req.get("diffusion_steps", 100))
    cfg_text_weight = _resolve_cfg_text_weight(req)
    if seed is not None:
        seed_everything(int(seed))

    num_frames = max(1, seconds_to_frame_count(duration, model.fps))
    segment_frames = _generation_segment_frames(num_frames, model.fps)
    constraints = _load_constraints(req.get("constraints_json", ""), model, horizon_frames=num_frames)
    from kimodo.constraints import normalize_constraints_to_anchor

    constraint_origin = normalize_constraints_to_anchor(constraints)
    progress_bar = _make_cancelable_progress_bar(cancel_event or threading.Event())
    if emit_progress:
        _out({"status": "progress", "message": f"Running diffusion ({diffusion_steps} steps)..."})
    if emit_progress and len(segment_frames) > 1:
        _out({"status": "progress", "message": f"Generating {len(segment_frames)} continuous segments..."})

    output = model(
        [prompt] * len(segment_frames),
        segment_frames,
        constraint_lst=constraints,
        num_denoising_steps=diffusion_steps,
        cfg_weight=[cfg_text_weight, 2.0],
        num_samples=1,
        # Keep the original single-request path intact; for long requests the
        # same built-in transition path simply receives multiple segments.
        multi_prompt=True,
        num_transition_frames=5,
        post_processing=True,
        return_numpy=True,
        progress_bar=progress_bar,
    )
    output = _restore_kimodo_output_origin(output, constraint_origin, model)
    if cancel_event is not None and cancel_event.is_set():
        raise GenerateCancelledError("Generation canceled.")
    return output, prompt


def _generate(req: dict, model, cancel_event: threading.Event | None = None):
    output, prompt = _run_generate(req, model, cancel_event)

    _out(_build_generate_response(model, output, prompt, sample_index=0))


