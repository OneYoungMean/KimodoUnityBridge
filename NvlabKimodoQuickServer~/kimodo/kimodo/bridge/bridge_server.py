#!/usr/bin/env python
"""
Kimodo Unity Bridge Server

Persistent process for Unity Editor:
- Loads kimodo model once
- Listens on TCP socket for newline-delimited JSON requests
- Responds with newline-delimited JSON over the same TCP connection
"""

import argparse
import json
import os
from pathlib import Path
import socket
import sys
import threading
import time
import traceback
from dataclasses import dataclass
from typing import Any

import numpy as np
from tqdm.auto import tqdm

from kimodo.bridge import quickserver_assets as assets


class GenerateCancelledError(Exception):
    pass


def _default_bridge_log_path(root: str) -> str:
    if not root:
        return ""
    return os.path.join(root, "log", "bridge_server.log")


def _detect_total_vram_gb() -> float:
    """Total VRAM of cuda:0 in GiB, or 0.0 when no usable CUDA device exists."""
    try:
        import torch

        if torch.cuda.is_available() and torch.cuda.device_count() > 0:
            props = torch.cuda.get_device_properties(0)
            return float(props.total_memory) / (1024 ** 3)
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
    highvram: bool
    encoder_route: str
    text_encoder_layout: assets.TextEncoderLayoutSpec


@dataclass(frozen=True)
class _RuntimeSelfCheckResult:
    backend_profile: str
    runtime_device: str
    kernel_ok: bool
    bnb_present: bool
    bnb_ok: bool
    nf4_available: bool
    total_vram_gb: float


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
) -> _BridgeProvisionPlan:
    root_path = Path(kimodo_root).resolve()
    resolved_model = assets.resolve_main_model(requested_model)
    models_root, using_external_models = assets.resolve_models_root(
        root_path,
        os.environ.get("KIMODO_MODELS_ROOT"),
    )
    highvram = _env_flag("KIMODO_HIGHVRAM", False)
    if runtime_profile.backend_profile == "cpu":
        encoder_route = assets.ENCODER_ROUTE_INT8
    elif highvram:
        encoder_route = assets.ENCODER_ROUTE_FP16
    elif runtime_profile.nf4_available:
        encoder_route = assets.ENCODER_ROUTE_NF4
    else:
        encoder_route = assets.ENCODER_ROUTE_INT8
    text_encoder_layout = assets.select_text_encoder_layout_for_route(encoder_route, models_root)
    return _BridgeProvisionPlan(
        resolved_model=resolved_model,
        models_root=models_root,
        using_external_models=using_external_models,
        highvram=highvram,
        encoder_route=encoder_route,
        text_encoder_layout=text_encoder_layout,
    )


def _apply_bridge_runtime_env(kimodo_root: str, plan: _BridgeProvisionPlan) -> None:
    root_path = Path(kimodo_root).resolve()
    source_root = root_path / "kimodo"
    if not (source_root / "pyproject.toml").is_file():
        source_root = root_path
    target_encoder_device = "cpu" if plan.encoder_route == assets.ENCODER_ROUTE_INT8 else os.environ.get("KIMODO_RUNTIME_DEVICE", "")
    runtime_env = assets.build_runtime_env(
        root_dir=root_path,
        source_root=source_root,
        models_root=plan.models_root,
        highvram=plan.highvram,
        hints=assets.normalize_runtime_hints(target_encoder_device or None),
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
) -> _BridgeProvisionPlan:
    plan = _build_bridge_provision_plan(
        kimodo_root,
        requested_model,
        runtime_profile=runtime_profile,
    )
    logger = _BridgeAssetLogger()
    recovery_flag_dir = Path(kimodo_root).resolve() / "archive" / "recovery_flags"
    recycle_dir = Path(kimodo_root).resolve() / "archive" / "recycle"
    allow_download = not plan.using_external_models
    download_counter = [0]

    _apply_bridge_runtime_env(kimodo_root, plan)
    if allow_download:
        plan.models_root.mkdir(parents=True, exist_ok=True)

    logger.log(
        f"[bridge] asset plan: model={plan.resolved_model.local_name} "
        f"models_root={plan.models_root} encoder_route={plan.encoder_route} "
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


def _load_constraints(constraints_json: str, model):
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

    return load_constraints_lst(parsed, model.skeleton)


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
    return raw if raw in ("json_compact", "bvh", "flatbuf_motion_v1") else "json_compact"


def _resolve_requested_output_format(req: dict | None = None) -> str:
    if isinstance(req, dict):
        raw = str(req.get("output_format", "") or "").strip().lower()
        if raw in ("json_compact", "bvh", "flatbuf_motion_v1"):
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


def _probe_bitsandbytes() -> tuple[bool, bool]:
    try:
        import importlib.util

        if importlib.util.find_spec("bitsandbytes") is None:
            return False, False

        import bitsandbytes as bnb  # type: ignore

        _ = getattr(bnb, "__version__", "")
        from bitsandbytes.nn import Linear4bit  # type: ignore

        _ = Linear4bit
        return True, True
    except Exception:
        return True, False


def _runtime_self_check(requested_device: str | None) -> _RuntimeSelfCheckResult:
    requested = str(requested_device or "").strip().lower()
    total_vram_gb = _detect_total_vram_gb()

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

    bnb_present, bnb_ok = _probe_bitsandbytes()
    nf4_available = selected_profile == "cuda" and kernel_ok and bnb_present and bnb_ok
    return _RuntimeSelfCheckResult(
        backend_profile=selected_profile,
        runtime_device=selected_device,
        kernel_ok=kernel_ok,
        bnb_present=bnb_present,
        bnb_ok=bnb_ok,
        nf4_available=nf4_available,
        total_vram_gb=total_vram_gb,
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

    from kimodo.bridge.protocol.generated import MotionPacket

    sample_joints = np.asarray(output["posed_joints"][sample_index], dtype=np.float32)
    if sample_joints.ndim != 3 or sample_joints.shape[2] < 3:
        raise ValueError(f"Unexpected posed_joints shape for flatbuffer export: {sample_joints.shape!r}")

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

    builder = flatbuffers.Builder(max(1024, int(local_rot_quats.size * 4 + root_positions.size * 4 + 512)))

    model_name_offset = builder.CreateString(str(getattr(model, "name", "") or ""))
    joint_name_offsets = [builder.CreateString(str(name or "")) for name in joint_names]
    MotionPacket.StartJointNamesVector(builder, len(joint_name_offsets))
    for joint_name_offset in reversed(joint_name_offsets):
        builder.PrependUOffsetTRelative(joint_name_offset)
    joint_names_offset = builder.EndVector()
    joint_parents_offset = builder.CreateNumpyVector(np.asarray(joint_parents, dtype=np.int32))
    root_positions_offset = builder.CreateNumpyVector(root_positions)
    local_rot_quats_offset = builder.CreateNumpyVector(local_rot_quats)

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
    packet = MotionPacket.End(builder)
    builder.Finish(packet, file_identifier=b"KMB1")
    return bytes(builder.Output())


def _write_json_line(file, payload: dict) -> None:
    file.write((json.dumps(payload) + "\n").encode("utf-8"))
    file.flush()


def _write_flatbuffer_generate_response(file, payload: bytes) -> None:
    header = {
        "status": "done",
        "output_format": "flatbuf_motion_v1",
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


def _generate(req: dict, model, cancel_event: threading.Event | None = None):
    from kimodo.tools import seed_everything

    prompt = str(req.get("prompt", "A person walks forward.")).strip()
    if not prompt.endswith("."):
        prompt += "."

    duration = float(req.get("duration", 5.0))
    seed = req.get("seed", None)
    diffusion_steps = int(req.get("diffusion_steps", 100))
    constraints_path = req.get("constraints_json", "")

    if seed is not None:
        seed_everything(int(seed))

    num_frames = max(1, int(duration * float(model.fps)))
    constraints = _load_constraints(constraints_path, model)
    progress_bar = _make_cancelable_progress_bar(cancel_event or threading.Event())

    _out({"status": "progress", "message": f"Running diffusion ({diffusion_steps} steps)..."})
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
    if cancel_event is not None and cancel_event.is_set():
        raise GenerateCancelledError("Generation canceled.")

    _out(_build_generate_response(model, output, prompt, sample_index=0))


def main():
    parser = argparse.ArgumentParser(description="Kimodo Unity Bridge Server")
    parser.add_argument("--model", default="Kimodo-SOMA-RP-v1")
    parser.add_argument("--device", default=None)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--kimodo-root", default=None)
    parser.add_argument("--force-hf-download", action="store_true")
    args = parser.parse_args()
    _log(
        f"[bridge] bootstrap start pid={os.getpid()} model={args.model} "
        f"kimodo_root={args.kimodo_root or ''} force_hf_download={bool(args.force_hf_download)}"
    )

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((args.host, int(args.port)))
    server.listen(16)

    host, port = server.getsockname()
    kimodo_root = args.kimodo_root or os.environ.get("KIMODO_ROOT_PATH") or os.getcwd()
    port_file = os.path.join(kimodo_root, "serverport")
    _write_text_atomic(port_file, f"{host}:{port}\n")
    os.environ["KIMODO_BRIDGE_LOG"] = _default_bridge_log_path(kimodo_root)
    _log(f"[bridge] listening host={host} port={port} model={args.model}")

    state = {
        "model": None,
        "fps": 30,
        "device": "unknown",
        "loading": True,
        "error": "",
        "loading_message": "Importing Kimodo...",
        "loading_started_at": time.time(),
    }
    state_lock = threading.Lock()
    idle_timeout_seconds = max(0, int(float(os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600"))))
    last_command_ts = time.time()
    last_command_lock = threading.Lock()
    active_command_count = 0
    active_command_lock = threading.Lock()
    active_generation_cancel_event = None
    active_generation_lock = threading.Lock()
    quitting = False
    quitting_lock = threading.Lock()

    def _set_loading_message(message: str):
        with state_lock:
            if state["loading"]:
                state["loading_message"] = str(message)

    def _load_model_worker():
        _set_loading_message("Importing Kimodo...")
        _out({"status": "loading", "message": "Importing Kimodo..."})
        _log("[bridge] load stage: importing torch and kimodo...")
        try:
            import torch
            _log("[bridge] load stage: torch imported.")
            from kimodo import load_model
            _log("[bridge] load stage: kimodo imported.")
        except Exception as exc:
            with state_lock:
                state["error"] = f"Failed to import kimodo: {exc}"
                state["loading"] = False
            _log(f"[bridge] load error {state['error']}")
            return

        runtime_profile = _runtime_self_check(args.device)
        total_vram_gb = runtime_profile.total_vram_gb
        device = runtime_profile.runtime_device
        os.environ["KIMODO_RUNTIME_BACKEND_PROFILE"] = runtime_profile.backend_profile
        os.environ["KIMODO_RUNTIME_DEVICE"] = runtime_profile.runtime_device
        _log(
            "[bridge] runtime self-check: "
            f"profile={runtime_profile.backend_profile} device={runtime_profile.runtime_device} "
            f"kernel_ok={runtime_profile.kernel_ok} "
            f"bnb_present={runtime_profile.bnb_present} bnb_ok={runtime_profile.bnb_ok} "
            f"nf4_available={runtime_profile.nf4_available} vram={total_vram_gb:.2f}GB"
        )
        if args.device and device == "cpu" and str(args.device).strip().lower() != "cpu":
            _out({"status": "loading", "message": f"Requested device {args.device} is unavailable; running on CPU."})

        _set_loading_message("Checking local models...")
        _out({"status": "loading", "message": "Checking local models..."})
        try:
            force_download_site = assets.DownloadSite.HUGGINGFACE if args.force_hf_download else None
            provision_plan = _provision_bridge_assets(
                kimodo_root,
                args.model,
                runtime_profile=runtime_profile,
                force_download_site=force_download_site,
            )
        except Exception as exc:
            with state_lock:
                state["error"] = f"Model prepare failed: {exc}\n{traceback.format_exc()}"
                state["loading"] = False
            _log(f"[bridge] model prepare error {exc}")
            return

        use_int8_encoder = provision_plan.encoder_route == assets.ENCODER_ROUTE_INT8
        _log(
            f"[bridge] route decision: profile={runtime_profile.backend_profile} vram={total_vram_gb:.2f}GB device={device} "
            f"encoder_route={provision_plan.encoder_route} "
            f"encoder_layout={provision_plan.text_encoder_layout.layout_id}"
        )

        if provision_plan.encoder_route == assets.ENCODER_ROUTE_NF4:
            _log("[bridge] NF4 text encoder selected for low-VRAM mode on validated CUDA runtime.")
        elif use_int8_encoder:
            _log("[bridge] INT8 text encoder selected (non-highvram route or CPU fallback).")
        elif provision_plan.text_encoder_layout.layout_id == "legacy_base_peft":
            _log("[bridge] local legacy Meta-Llama-3-8B + LLM2Vec PEFT layout selected (compatibility hit).")
        else:
            _log("[bridge] FP16 text encoder selected.")

        resolved_model_name = provision_plan.resolved_model.local_name
        _set_loading_message(f"Loading {resolved_model_name} on {device}...")
        _out({"status": "loading", "message": f"Loading {resolved_model_name} on {device}..."})
        _log(f"[bridge] load stage: load_model start model={resolved_model_name} device={device}")
        try:
            model = load_model(resolved_model_name, device=device)
        except Exception as exc:
            with state_lock:
                state["error"] = f"Model load failed: {exc}\n{traceback.format_exc()}"
                state["loading"] = False
            _log(f"[bridge] load error {exc}")
            return
        _log("[bridge] load stage: load_model done.")

        with state_lock:
            state["model"] = model
            state["fps"] = int(model.fps)
            state["device"] = device
            state["loading"] = False
            state["error"] = ""

        _log(f"[bridge] ready host={host} port={port} model={resolved_model_name} device={device}")
        _out({
            "status": "ready",
            "model": resolved_model_name,
            "device": device,
            "fps": int(model.fps),
            "host": host,
            "port": int(port)
        })

    threading.Thread(target=_load_model_worker, daemon=True).start()

    def _is_quitting() -> bool:
        with quitting_lock:
            return quitting

    def _set_quitting() -> None:
        nonlocal quitting
        with quitting_lock:
            quitting = True

    def _touch_last_command() -> None:
        nonlocal last_command_ts
        with last_command_lock:
            last_command_ts = time.time()

    def _command_started() -> None:
        nonlocal active_command_count
        with active_command_lock:
            active_command_count += 1

    def _command_finished() -> None:
        nonlocal active_command_count
        with active_command_lock:
            active_command_count = max(0, active_command_count - 1)

    def _try_begin_generate() -> threading.Event | None:
        nonlocal active_generation_cancel_event
        with active_generation_lock:
            if active_generation_cancel_event is not None:
                return None
            active_generation_cancel_event = threading.Event()
            return active_generation_cancel_event

    def _finish_generate(cancel_event: threading.Event) -> None:
        nonlocal active_generation_cancel_event
        with active_generation_lock:
            if active_generation_cancel_event is cancel_event:
                active_generation_cancel_event = None

    def _request_generate_cancel() -> bool:
        with active_generation_lock:
            if active_generation_cancel_event is None:
                return False
            active_generation_cancel_event.set()
            return True

    def _idle_watchdog_worker():
        while not _is_quitting():
            time.sleep(1.0)
            with active_command_lock:
                busy = active_command_count > 0
            if busy:
                continue
            with last_command_lock:
                idle_seconds = time.time() - last_command_ts
            if idle_timeout_seconds > 0 and idle_seconds >= idle_timeout_seconds:
                _log(
                    f"[bridge] idle timeout reached: {int(idle_seconds)}s >= {idle_timeout_seconds}s, shutting down"
                )
                _set_quitting()
                try:
                    server.close()
                except Exception:
                    pass
                return

    threading.Thread(target=_idle_watchdog_worker, daemon=True).start()

    def _client_worker(conn: socket.socket, addr) -> None:
        with conn:
            file = conn.makefile("rwb")
            while not _is_quitting():
                try:
                    line = file.readline()
                except (ConnectionResetError, BrokenPipeError, OSError):
                    break
                if not line:
                    break

                try:
                    req = json.loads(line.decode("utf-8").strip())
                except Exception as exc:
                    resp = {"status": "error", "message": f"Bad JSON: {exc}"}
                    file.write((json.dumps(resp) + "\n").encode("utf-8"))
                    file.flush()
                    continue

                cmd = req.get("cmd", "")
                _touch_last_command()
                _log(f"[bridge] cmd={cmd}")
                stage = f"cmd:{cmd}" if cmd else "cmd:unknown"
                _command_started()
                try:
                    if cmd == "ping":
                        with state_lock:
                            if state["loading"]:
                                resp = {"status": "loading", "message": "Model is loading."}
                            elif state["error"]:
                                resp = {"status": "error", "message": state["error"]}
                            else:
                                resp = {"status": "pong"}
                    elif cmd == "cancel":
                        if _request_generate_cancel():
                            resp = {"status": "cancelling", "message": "Cancel requested."}
                        else:
                            resp = {"status": "idle", "message": "No active generation."}
                    elif cmd == "generate":
                        with state_lock:
                            loading = state["loading"]
                            load_error = state["error"]
                            model = state["model"]
                            fps = state["fps"]

                        if loading:
                            resp = {"status": "loading", "message": "Model is loading."}
                            file.write((json.dumps(resp) + "\n").encode("utf-8"))
                            file.flush()
                            continue
                        if load_error:
                            resp = {"status": "error", "message": load_error}
                            file.write((json.dumps(resp) + "\n").encode("utf-8"))
                            file.flush()
                            continue
                        if model is None:
                            resp = {"status": "error", "message": "Model not available."}
                            file.write((json.dumps(resp) + "\n").encode("utf-8"))
                            file.flush()
                            continue

                        cancel_event = _try_begin_generate()
                        if cancel_event is None:
                            resp = {"status": "busy", "message": "Another generation is already running."}
                            file.write((json.dumps(resp) + "\n").encode("utf-8"))
                            file.flush()
                            continue

                        try:
                            from kimodo.tools import seed_everything

                            prompt = str(req.get("prompt", "A person walks forward.")).strip()
                            if not prompt.endswith("."):
                                prompt += "."
                            duration = float(req.get("duration", 5.0))
                            seed = req.get("seed", None)
                            diffusion_steps = int(req.get("diffusion_steps", 100))
                            constraints_path = req.get("constraints_json", "")

                            if seed is not None:
                                seed_everything(int(seed))

                            num_frames = max(1, int(duration * float(fps)))
                            constraints = _load_constraints(constraints_path, model)
                            progress_bar = _make_cancelable_progress_bar(cancel_event)

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
                                raise GenerateCancelledError("Generation canceled.")

                            requested_output_format = _resolve_requested_output_format(req)
                            if requested_output_format == "flatbuf_motion_v1":
                                payload = _build_generate_flatbuffer_payload(model, output, sample_index=0)
                                _write_flatbuffer_generate_response(file, payload)
                                continue

                            resp = _build_generate_response(model, output, prompt, sample_index=0)
                        except GenerateCancelledError as exc:
                            resp = {"status": "cancelled", "message": str(exc)}
                        finally:
                            _finish_generate(cancel_event)
                    elif cmd == "quit":
                        resp = {"status": "bye"}
                        _set_quitting()
                        try:
                            server.close()
                        except Exception:
                            pass
                    else:
                        resp = {"status": "error", "message": f"Unknown cmd: {cmd!r}"}
                except Exception as exc:
                    resp = {
                        "status": "error",
                        "message": str(exc),
                        "server_message": f"Bridge exception while handling {stage}",
                        "error_type": type(exc).__name__,
                        "stage": stage,
                        "traceback": traceback.format_exc(),
                    }
                    _log(f"[bridge] error {exc}")
                finally:
                    _command_finished()

                try:
                    _write_json_line(file, resp)
                except (ConnectionResetError, BrokenPipeError, OSError):
                    break

    try:
        while not _is_quitting():
            try:
                conn, _addr = server.accept()
            except OSError:
                if _is_quitting():
                    break
                raise
            _log(f"[bridge] accept {_addr}")
            threading.Thread(target=_client_worker, args=(conn, _addr), daemon=True).start()
    finally:
        try:
            server.close()
        except Exception:
            pass
        try:
            if os.path.exists(port_file):
                os.remove(port_file)
        except Exception:
            _log("[bridge] shutdown")
            pass


if __name__ == "__main__":
    main()


