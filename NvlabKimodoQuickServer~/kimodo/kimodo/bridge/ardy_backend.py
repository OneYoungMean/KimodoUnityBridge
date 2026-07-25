from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import secrets
import threading
from typing import Any

import numpy as np


MAX_KMB_BYTES = 256 * 1024**2


class ArdyBackendError(ValueError):
    code = "ardy_backend_error"


class ArdyStreamGenerator:
    """Per-session eager ARDY state; the GPU worker owns and supplies the model."""

    def __init__(
        self,
        task_request: dict[str, Any],
        model: Any,
        profile: Any,
        spool: Any,
        quickserver_root: str | Path,
    ):
        import torch

        from kimodo.bridge import bridge_server as bridge_runtime_helpers

        self.profile = profile
        self.prompt = str(task_request.get("prompt") or "A person walks forward.").strip()
        if not self.prompt.endswith("."):
            self.prompt += "."
        self.diffusion_steps = int(task_request.get("diffusion_steps", profile.max_diffusion_steps))
        if not 1 <= self.diffusion_steps <= int(profile.max_diffusion_steps):
            raise ArdyBackendError(
                f"diffusion_steps must be in [1, {profile.max_diffusion_steps}]; got {self.diffusion_steps}."
            )
        self.cfg_text_weight = bridge_runtime_helpers._resolve_cfg_text_weight(task_request)
        requested_seed = task_request.get("seed")
        self.resolved_seed = secrets.randbelow(2**31) if requested_seed is None else int(requested_seed)
        (
            self.history,
            self.observed_motion,
            self.motion_mask,
            self.total_frames,
            self.history_len,
            self.postprocess_constraints,
        ) = prepare_generation_inputs(
            model,
            profile,
            task_request.get("constraints_json", ""),
            spool,
            quickserver_root,
            window_future_clips=True,
        )
        self._first_horizon = True
        self._root_body_pass = _has_future_clip_body_constraints(task_request.get("constraints_json", ""))
        self._future_clip = _has_future_clips(task_request.get("constraints_json", ""))
        self._windowed_constraints = self.observed_motion is not None
        self._future_offset = 0
        if self._windowed_constraints:
            self.observed_motion = self.observed_motion[:, self.history_len :].clone()
            self.motion_mask = self.motion_mask[:, self.history_len :].clone()
        self._committed_history = self.history.detach().clone() if self.history is not None else None
        self._pending_history: deque[Any] = deque()
        self._history_lock = threading.RLock()
        self._cpu_rng_state = torch.Generator(device="cpu").manual_seed(self.resolved_seed).get_state()
        self._cuda_rng_state = None
        if str(model.device).startswith("cuda"):
            self._cuda_rng_state = torch.Generator(device=model.device).manual_seed(self.resolved_seed).get_state()
        encode_text = getattr(model, "_encode_text", None)
        if callable(encode_text):
            self.text_feat, self.text_pad_mask = encode_text([self.prompt])
        else:
            self.text_feat = None
            self.text_pad_mask = None

    def generate_horizon(self, model: Any) -> dict[str, Any]:
        import torch

        if not hasattr(self, "_history_lock"):
            self._history_lock = threading.RLock()
            self._committed_history = self.history.detach().clone() if self.history is not None else None
            self._pending_history = deque()

        history_len = int(self.history.shape[1]) if self.history is not None else 0
        horizon = int(self.profile.horizon_frames)
        total_frames = history_len + horizon
        observed_motion = None
        motion_mask = None
        root_body_pass = self._root_body_pass
        future_clip_active = False
        if self._windowed_constraints:
            future_observed = self.observed_motion[:, self._future_offset : self._future_offset + horizon]
            future_mask = self.motion_mask[:, self._future_offset : self._future_offset + horizon]
            if future_observed.shape[1] < horizon:
                pad = horizon - int(future_observed.shape[1])
                shape = (1, pad, int(self.observed_motion.shape[-1]))
                future_observed = torch.cat(
                    (future_observed, torch.zeros(shape, dtype=self.observed_motion.dtype, device=model.device)),
                    dim=1,
                )
                future_mask = torch.cat(
                    (future_mask, torch.zeros(shape, dtype=self.motion_mask.dtype, device=model.device)),
                    dim=1,
                )
            observed_motion = torch.cat(
                (torch.zeros(1, history_len, future_observed.shape[-1], device=model.device), future_observed),
                dim=1,
            )
            motion_mask = torch.cat((torch.zeros_like(observed_motion[:, :history_len]), future_mask), dim=1)
            future_clip_active = bool(future_mask.any())
            root_body_pass = self._root_body_pass and bool(future_mask[..., model.motion_rep.root_slice.stop :].any())
        devices = [torch.device(model.device).index or 0] if str(model.device).startswith("cuda") else []
        with torch.random.fork_rng(devices=devices):
            torch.random.set_rng_state(self._cpu_rng_state)
            if self._cuda_rng_state is not None:
                torch.cuda.set_rng_state(self._cuda_rng_state, device=model.device)
            with torch.no_grad():
                motion = _autoregressive_step(
                    model,
                    num_frames=total_frames,
                    num_denoising_steps=self.diffusion_steps,
                    motion_mask=motion_mask,
                    observed_motion=observed_motion,
                    cfg_weight=(self.cfg_text_weight, self.profile.cfg_constraint_weight),
                    texts=[self.prompt],
                    text_feat=getattr(self, "text_feat", None),
                    text_pad_mask=getattr(self, "text_pad_mask", None),
                    init_history_sequence=self.history,
                    root_body_pass=root_body_pass,
                )
            self._cpu_rng_state = torch.random.get_rng_state()
            if self._cuda_rng_state is not None:
                self._cuda_rng_state = torch.cuda.get_rng_state(device=model.device)

        generated = motion[:, history_len : history_len + horizon]
        # Keep the same bounded context as official ARDY: history + next horizon
        # must fit the profile's trained context window.
        max_history = resolve_history_frame_limit(self.profile)
        self.history = motion[:, -min(max_history, int(motion.shape[1])) :].detach() if max_history > 0 else None
        with self._history_lock:
            self._pending_history.append(generated.detach())
        output = model.motion_rep.inverse(generated, is_normalized=True)
        postprocess_constraints = (
            [
                constraint.crop_move(self._future_offset, self._future_offset + horizon)
                for constraint in self.postprocess_constraints
            ]
            if self._windowed_constraints
            else (self.postprocess_constraints if self._first_horizon else [])
        )
        output = _finalize_output(
            output,
            model,
            postprocess_constraints,
            bool(getattr(self.profile, "postprocess", False)) and not future_clip_active,
        )
        if self._windowed_constraints:
            self._future_offset += horizon
        self._first_horizon = False
        if not self._windowed_constraints:
            self.observed_motion = None
            self.motion_mask = None
        if not self._windowed_constraints:
            self.postprocess_constraints = []
        return output

    def commit_frames(self, frame_count: int) -> None:
        """Advance the immutable client-delivery boundary by ``frame_count`` frames."""
        import torch

        with self._history_lock:
            remaining = int(frame_count)
            committed: list[Any] = []
            while remaining > 0:
                if not self._pending_history:
                    raise ArdyBackendError("ARDY stream delivered beyond its generated history.")
                chunk = self._pending_history[0]
                take = min(remaining, int(chunk.shape[1]))
                committed.append(chunk[:, :take])
                if take == int(chunk.shape[1]):
                    self._pending_history.popleft()
                else:
                    self._pending_history[0] = chunk[:, take:]
                remaining -= take
            if committed:
                addition = committed[0] if len(committed) == 1 else torch.cat(committed, dim=1)
                combined = addition if self._committed_history is None else torch.cat((self._committed_history, addition), dim=1)
                keep = resolve_history_frame_limit(self.profile) + int(self.profile.frames_per_token) - 1
                self._committed_history = combined[:, -min(keep, int(combined.shape[1])) :].detach()

    def update(self, task_request: dict[str, Any], model: Any, spool: Any, quickserver_root: str | Path) -> None:
        """Apply prompt/constraint changes at a Horizon boundary without resetting RNG or Handle."""
        with self._history_lock:
            committed_len = 0 if self._committed_history is None else int(self._committed_history.shape[1])
            patch = int(self.profile.frames_per_token)
            usable = committed_len // patch * patch
            self.history = self._committed_history[:, -usable:].detach() if usable > 0 else None
            self._pending_history.clear()

        self.prompt = str(task_request.get("prompt") or self.prompt).strip()
        if not self.prompt.endswith("."):
            self.prompt += "."
        self.diffusion_steps = int(task_request.get("diffusion_steps", self.diffusion_steps))
        if not 1 <= self.diffusion_steps <= int(self.profile.max_diffusion_steps):
            raise ArdyBackendError(
                f"diffusion_steps must be in [1, {self.profile.max_diffusion_steps}]; got {self.diffusion_steps}."
            )
        from kimodo.bridge import bridge_server as bridge_runtime_helpers

        self.cfg_text_weight = bridge_runtime_helpers._resolve_cfg_text_weight(task_request)
        (
            _request_history,
            self.observed_motion,
            self.motion_mask,
            self.total_frames,
            request_history_len,
            self.postprocess_constraints,
        ) = prepare_generation_inputs(
            model,
            self.profile,
            task_request.get("constraints_json", ""),
            spool,
            quickserver_root,
            window_future_clips=True,
        )
        if request_history_len:
            raise ArdyBackendError("An active ARDY stream update cannot replace its committed History.")
        self.history_len = usable
        self._first_horizon = True
        self._root_body_pass = _has_future_clip_body_constraints(task_request.get("constraints_json", ""))
        self._future_clip = _has_future_clips(task_request.get("constraints_json", ""))
        self._windowed_constraints = self.observed_motion is not None
        self._future_offset = 0
        if self._windowed_constraints:
            self.observed_motion = self.observed_motion.clone()
            self.motion_mask = self.motion_mask.clone()
        encode_text = getattr(model, "_encode_text", None)
        if callable(encode_text):
            self.text_feat, self.text_pad_mask = encode_text([self.prompt])
        else:
            self.text_feat = None
            self.text_pad_mask = None

    def close(self) -> None:
        self.history = None
        self.observed_motion = None
        self.motion_mask = None
        self.text_feat = None
        self.text_pad_mask = None
        self.postprocess_constraints = []
        with self._history_lock:
            self._committed_history = None
            self._pending_history.clear()


def resolve_stream_capacity_frames(task_request: dict[str, Any], profile: Any) -> int:
    duration = float(task_request.get("duration", 0.0))
    if not math.isfinite(duration) or duration <= 0.0:
        raise ArdyBackendError("ARDY stream duration must be a finite value greater than zero.")
    requested_frames = max(1, int(math.ceil(duration * float(profile.source_fps))))
    horizon = int(profile.horizon_frames)
    capacity = int(math.ceil(requested_frames / horizon) * horizon)
    max_frames = int(os.environ.get("KIMODO_ARDY_STREAM_MAX_FRAMES", "36000"))
    if capacity > max_frames:
        raise ArdyBackendError(
            f"ARDY stream capacity {capacity} exceeds KIMODO_ARDY_STREAM_MAX_FRAMES={max_frames}."
        )
    return capacity


def resolve_history_frame_limit(profile: Any, future_frames: int | None = None) -> int:
    """Return the token-aligned history budget that leaves room for a future horizon."""
    patch = int(profile.frames_per_token)
    future = int(profile.horizon_frames) if future_frames is None else int(future_frames)
    limit = ((int(profile.max_context_frames) - future) // patch) * patch
    if limit < 0:
        raise ArdyBackendError("Future constraints exceed the registered ARDY context window.")
    return limit


@dataclass(frozen=True)
class KmbMotion:
    payload: bytes
    model_name: str
    fps: float
    joint_names: tuple[str, ...]
    joint_parents: tuple[int, ...]
    root_positions: np.ndarray
    local_rot_quats: np.ndarray
    foot_contacts: np.ndarray | None

    @property
    def num_frames(self) -> int:
        return int(self.root_positions.shape[0])


def parse_kmb1(payload: bytes) -> KmbMotion:
    from kimodo.bridge.protocol.generated import MotionPacket

    if len(payload or b"") > MAX_KMB_BYTES:
        raise ArdyBackendError(f"KMB1 payload exceeds the {MAX_KMB_BYTES}-byte limit.")
    if not payload or not MotionPacket.MotionPacket.MotionPacketBufferHasIdentifier(payload, 0):
        raise ArdyBackendError("Clip is not a flatbuf_motion_v1 / KMB1 MotionPacket.")
    packet = MotionPacket.MotionPacket.GetRootAs(payload, 0)
    version = int(packet.Version())
    num_frames = int(packet.NumFrames())
    num_joints = int(packet.NumJoints())
    if version != 1 or num_frames <= 0 or num_joints <= 0:
        raise ArdyBackendError(
            f"Invalid KMB1 header: version={version}, frames={num_frames}, joints={num_joints}."
        )

    joint_names = tuple(
        (packet.JointNames(i) or b"").decode("utf-8", errors="strict")
        for i in range(packet.JointNamesLength())
    )
    joint_parents = tuple(int(packet.JointParents(i)) for i in range(packet.JointParentsLength()))
    root_positions = np.asarray(packet.RootPositionsAsNumpy(), dtype=np.float32).copy()
    local_rot_quats = np.asarray(packet.LocalRotQuatsAsNumpy(), dtype=np.float32).copy()
    foot_contacts = None
    if packet.FootContactsLength():
        raw_contacts = np.asarray(packet.FootContactsAsNumpy(), dtype=np.uint8).copy()
        expected_contacts = num_frames * 4
        if raw_contacts.size != expected_contacts:
            raise ArdyBackendError(
                f"KMB1 foot_contacts length mismatch: contacts={raw_contacts.size}/{expected_contacts}."
            )
        foot_contacts = raw_contacts.reshape(num_frames, 4).astype(np.float32)
    expected_roots = num_frames * 3
    expected_quats = num_frames * num_joints * 4
    if len(joint_names) != num_joints or len(joint_parents) != num_joints:
        raise ArdyBackendError("KMB1 joint metadata length does not match num_joints.")
    if root_positions.size != expected_roots or local_rot_quats.size != expected_quats:
        raise ArdyBackendError(
            "KMB1 vector length mismatch: "
            f"roots={root_positions.size}/{expected_roots}, quats={local_rot_quats.size}/{expected_quats}."
        )
    if not np.isfinite(root_positions).all() or not np.isfinite(local_rot_quats).all():
        raise ArdyBackendError("KMB1 contains non-finite motion values.")
    model_name_raw = packet.ModelName() or b""
    return KmbMotion(
        payload=bytes(payload),
        model_name=model_name_raw.decode("utf-8", errors="strict"),
        fps=float(packet.Fps()),
        joint_names=joint_names,
        joint_parents=joint_parents,
        root_positions=root_positions.reshape(num_frames, 3),
        local_rot_quats=local_rot_quats.reshape(num_frames, num_joints, 4),
        foot_contacts=foot_contacts,
    )


def extract_handle_refs(constraints_json: Any) -> tuple[str, ...]:
    try:
        value = json.loads(constraints_json) if isinstance(constraints_json, str) else constraints_json
    except Exception:
        return ()
    if isinstance(value, dict):
        value = [value]
    if not isinstance(value, list):
        return ()
    return tuple(
        str(item.get("handle") or "").strip()
        for item in value
        if isinstance(item, dict)
        and item.get("type") == "clip"
        and item.get("format") in {"ardy_handle_v1", "kmb_handle_v1"}
        and str(item.get("handle") or "").strip()
    )


def _allowed_file_roots(quickserver_root: str | Path) -> tuple[Path, ...]:
    roots = [Path(quickserver_root).resolve() / "cache" / "ardy_files"]
    roots.extend(
        Path(value).expanduser().resolve()
        for value in os.environ.get("KIMODO_ARDY_FILE_ROOTS", "").split(os.pathsep)
        if value.strip()
    )
    if os.environ.get("KIMODO_ARDY_ALLOW_TEST_FILES", "").strip().lower() in {"1", "true", "yes"}:
        roots.extend(
            Path(value).expanduser().resolve()
            for value in os.environ.get("KIMODO_ARDY_TEST_FILE_ROOTS", "").split(os.pathsep)
            if value.strip()
        )
    return tuple(dict.fromkeys(roots))


def _read_managed_file(path_value: Any, quickserver_root: str | Path) -> bytes:
    path = Path(str(path_value or "")).expanduser().resolve(strict=True)
    if not path.is_file() or not any(path.is_relative_to(root) for root in _allowed_file_roots(quickserver_root)):
        raise ArdyBackendError(f"ardy_file_v1 path is outside the configured managed cache roots: {path}.")
    return path.read_bytes()


def _validate_kmb(motion: KmbMotion, model: Any, profile: Any) -> None:
    skeleton = model.motion_rep.skeleton
    expected_names = tuple(str(name) for name in skeleton.bone_order_names)
    expected_parents = tuple(int(value) for value in skeleton.joint_parents.detach().cpu().tolist())
    if motion.model_name != profile.model_name:
        raise ArdyBackendError(
            f"KMB1 model mismatch: expected {profile.model_name!r}, got {motion.model_name!r}."
        )
    if not math.isclose(motion.fps, profile.source_fps, rel_tol=0.0, abs_tol=1e-5):
        raise ArdyBackendError(f"KMB1 FPS mismatch: expected {profile.source_fps}, got {motion.fps}.")
    if motion.joint_names != expected_names or motion.joint_parents != expected_parents:
        raise ArdyBackendError("KMB1 rig does not match the registered ARDY skeleton.")


def _parse_constraints(constraints_json: Any) -> list[dict[str, Any]]:
    if not constraints_json:
        return []
    try:
        value = json.loads(constraints_json) if isinstance(constraints_json, str) else constraints_json
    except Exception as exc:
        raise ArdyBackendError(f"Invalid inline constraints_json payload: {exc}") from exc
    if isinstance(value, dict):
        value = [value]
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise ArdyBackendError("constraints_json must be a JSON array/object.")
    return [dict(item) for item in value]


def _future_frame_indices(constraints: list[dict[str, Any]]) -> list[int]:
    result: list[int] = []
    for item in constraints:
        if item.get("type") == "clip":
            continue
        indices = item.get("frame_indices", [])
        if not isinstance(indices, list):
            raise ArdyBackendError("Constraint frame_indices must be an array.")
        for value in indices:
            if isinstance(value, bool) or int(value) != value or int(value) < 0:
                raise ArdyBackendError(f"Constraint frame index must be a non-negative integer; got {value!r}.")
            result.append(int(value))
    return result


def _clip_is_history(item: dict[str, Any]) -> bool:
    value = item.get("is_history", True)
    if not isinstance(value, bool):
        raise ArdyBackendError("clip is_history must be a boolean.")
    return value


def _clip_slice(item: dict[str, Any], motion: KmbMotion) -> tuple[int, int]:
    start_value = item.get("start_frame", 0)
    end_value = item.get("end_frame_exclusive", motion.num_frames)
    if (
        isinstance(start_value, bool)
        or isinstance(end_value, bool)
        or not isinstance(start_value, int)
        or not isinstance(end_value, int)
    ):
        raise ArdyBackendError("Clip slice bounds must be integers.")
    start = int(start_value)
    end = int(end_value)
    if not 0 <= start < end <= motion.num_frames:
        raise ArdyBackendError(
            f"Invalid half-open clip slice [{start}, {end}) for {motion.num_frames} frames."
        )
    return start, end


def _future_clip_mask(item: dict[str, Any], joint_count: int) -> list[bool]:
    values = item.get("mask")
    expected = 4 + max(0, int(joint_count) - 1) * 3
    if not isinstance(values, list) or len(values) != expected or any(not isinstance(value, bool) for value in values):
        raise ArdyBackendError(
            "Future clip mask must be a boolean array with "
            f"{expected} entries: Root XYZ, heading, then non-Root joint XYZ in KMB joint order."
        )
    return values


def _apply_future_clip(
    observed_motion: Any,
    motion_mask: Any,
    source: Any,
    mask: list[bool],
    history_len: int,
    motion_rep: Any,
) -> None:
    frame_count = int(source.shape[1])
    target = slice(history_len, history_len + frame_count)
    root_slice = motion_rep.slice_dict["root_pos"]
    heading_slice = motion_rep.slice_dict["global_root_heading"]
    joints_slice = motion_rep.slice_dict["local_joints_positions"]

    for axis in range(3):
        channel = root_slice.start + axis
        enabled = mask[axis]
        observed_motion[:, target, channel] = source[:, :, channel] if enabled else 0
        motion_mask[:, target, channel] = float(enabled)

    heading_enabled = mask[3]
    observed_motion[:, target, heading_slice] = source[:, :, heading_slice] if heading_enabled else 0
    motion_mask[:, target, heading_slice] = float(heading_enabled)

    joint_mask = source.new_tensor(mask[4:], dtype=motion_mask.dtype).reshape(1, 1, -1)
    observed_motion[:, target, joints_slice] = source[:, :, joints_slice] * joint_mask
    motion_mask[:, target, joints_slice] = joint_mask


def _has_future_clip_body_constraints(constraints_json: Any) -> bool:
    return any(
        item.get("type") == "clip"
        and item.get("is_history") is False
        and isinstance(item.get("mask"), list)
        and any(value is True for value in item["mask"][4:])
        for item in _parse_constraints(constraints_json)
    )


def _has_future_clips(constraints_json: Any) -> bool:
    return any(
        item.get("type") == "clip" and item.get("is_history") is False
        for item in _parse_constraints(constraints_json)
    )


def _autoregressive_step(
    model: Any,
    *,
    num_frames: int,
    num_denoising_steps: int,
    motion_mask: Any,
    observed_motion: Any,
    cfg_weight: tuple[float, float],
    texts: list[str],
    text_feat: Any = None,
    text_pad_mask: Any = None,
    init_history_sequence: Any,
    root_body_pass: bool,
):
    kwargs = {
        "num_frames": num_frames,
        "num_denoising_steps": num_denoising_steps,
        "cfg_weight": cfg_weight,
        "texts": texts,
        "init_history_sequence": init_history_sequence,
        "cancel_callback": None,
    }
    if text_feat is not None:
        kwargs["text_feat"] = text_feat
        kwargs["text_pad_mask"] = text_pad_mask
    if not root_body_pass:
        return model.autoregressive_step(
            motion_mask=motion_mask,
            observed_motion=observed_motion,
            **kwargs,
        )

    root_motion = model.autoregressive_step(
        motion_mask=None,
        observed_motion=None,
        **kwargs,
    )
    body_observed = observed_motion.clone()
    body_mask = motion_mask.clone()
    history_len = 0 if init_history_sequence is None else int(init_history_sequence.shape[1])
    root_slice = model.motion_rep.root_slice
    root_end = min(int(body_observed.shape[1]), int(root_motion.shape[1]))
    body_observed[:, history_len:, root_slice] = 0
    body_mask[:, history_len:, root_slice] = 0
    body_observed[:, history_len:root_end, root_slice] = root_motion[:, history_len:root_end, root_slice]
    body_mask[:, history_len:root_end, root_slice] = 1
    return model.autoregressive_step(
        motion_mask=body_mask,
        observed_motion=body_observed,
        **kwargs,
    )


def _clip_payload(item: dict[str, Any], spool: Any, profile: Any, quickserver_root: str | Path) -> bytes:
    clip_format = str(item.get("format") or "")
    if clip_format in {"ardy_handle_v1", "kmb_handle_v1"}:
        return spool.read(
            str(item.get("handle") or ""),
            model_name=profile.model_name,
            fingerprint=profile.motion_rep_fingerprint,
            fps=profile.source_fps,
        )
    if clip_format == "ardy_file_v1" and os.environ.get("KIMODO_ARDY_ALLOW_TEST_FILES", "").strip().lower() in {"1", "true", "yes"}:
        return _read_managed_file(item.get("path"), quickserver_root)
    if clip_format == "ardy_file_v1":
        raise ArdyBackendError("ardy_file_v1 is test-only; upload KMB and use kmb_handle_v1.")
    raise ArdyBackendError(f"Unsupported clip format: {clip_format!r}.")


def _normalize_root_heading(item: dict[str, Any]) -> None:
    if item.get("type") != "root2d" or "global_root_heading" not in item:
        return
    headings = item["global_root_heading"]
    if not isinstance(headings, list):
        raise ArdyBackendError("global_root_heading must be an array.")
    converted = []
    for value in headings:
        if isinstance(value, (list, tuple)) and len(value) == 2:
            converted.append(math.atan2(float(value[1]), float(value[0])))
        else:
            converted.append(float(value))
    item["global_root_heading"] = converted


def prepare_generation_inputs(
    model: Any,
    profile: Any,
    constraints_json: Any,
    spool: Any,
    quickserver_root: str | Path,
    window_future_clips: bool = False,
):
    import torch
    from ardy.constraints import load_constraints_lst
    from ardy.geometry import quaternion_to_matrix

    items = _parse_constraints(constraints_json)
    future_items = [item for item in items if item.get("type") != "clip"]
    future_indices = _future_frame_indices(future_items)
    clips: list[tuple[dict[str, Any], KmbMotion, int, int, list[bool] | None]] = []
    future_clip_frames = 0
    for item in items:
        if item.get("type") != "clip":
            continue
        motion = parse_kmb1(_clip_payload(item, spool, profile, quickserver_root))
        _validate_kmb(motion, model, profile)
        start, end = _clip_slice(item, motion)
        is_history = _clip_is_history(item)
        mask = None
        if is_history:
            if "mask" in item:
                raise ArdyBackendError("History clips are complete KMB motion and cannot specify mask.")
        else:
            mask = _future_clip_mask(item, len(motion.joint_names))
            future_clip_frames = max(future_clip_frames, end - start)
        clips.append((item, motion, start, end, mask))

    patch = int(profile.frames_per_token)
    future_frames = max(int(profile.horizon_frames), max(future_indices, default=-1) + 1, future_clip_frames)
    future_frames = int(math.ceil(future_frames / patch) * patch)
    context_future_frames = int(profile.horizon_frames) if window_future_clips else future_frames
    max_history = resolve_history_frame_limit(profile, context_future_frames)

    roots: list[np.ndarray] = []
    quats: list[np.ndarray] = []
    contacts: list[np.ndarray] = []
    has_stored_contacts = True
    for item, motion, start, end, _ in clips:
        if not _clip_is_history(item):
            continue
        roots.append(motion.root_positions[start:end])
        quats.append(motion.local_rot_quats[start:end])
        if motion.foot_contacts is None:
            has_stored_contacts = False
        else:
            contacts.append(motion.foot_contacts[start:end])

    init_history = None
    history_len = 0
    if roots:
        all_roots = np.concatenate(roots, axis=0)
        all_quats = np.concatenate(quats, axis=0)
        history_len = min(len(all_roots), max_history)
        history_len -= history_len % patch
        if history_len <= 0:
            raise ArdyBackendError(f"Clip history must contain at least {patch} usable frames.")
        device = model.device
        local_quats = torch.as_tensor(all_quats, dtype=torch.float32, device=device)
        norms = torch.linalg.vector_norm(local_quats, dim=-1, keepdim=True)
        if (norms < 1e-6).any():
            raise ArdyBackendError("KMB1 contains a zero-length local rotation quaternion.")
        local_rot_mats = quaternion_to_matrix(local_quats / norms)
        root_positions = torch.as_tensor(all_roots, dtype=torch.float32, device=device)
        rebuilt = model.motion_rep(
            local_joint_rots=local_rot_mats,
            root_positions=root_positions,
            to_normalize=not has_stored_contacts,
        )
        if has_stored_contacts:
            if "foot_contacts" not in model.motion_rep.slice_dict:
                raise ArdyBackendError("ARDY motion representation has no foot_contacts feature slice.")
            rebuilt[..., model.motion_rep.slice_dict["foot_contacts"]] = torch.as_tensor(
                np.concatenate(contacts, axis=0), dtype=torch.float32, device=device
            )
            rebuilt = model.motion_rep.normalize(rebuilt)
        if rebuilt.ndim == 2:
            rebuilt = rebuilt.unsqueeze(0)
        init_history = rebuilt[:, -history_len:]
        print(
            f"[ARDY] History applied: clips={len(roots)} source_frames={len(all_roots)} "
            f"context_frames={history_len}",
            flush=True,
        )

    ordered: list[dict[str, Any]] = []
    for item in future_items:
        copied = dict(item)
        _normalize_root_heading(copied)
        ordered.append(copied)

    # Stable sorting plus ARDY's last-write behavior gives the requested channel priority.
    priority = {
        "end-effector": 0,
        "left-hand": 0,
        "right-hand": 0,
        "left-foot": 0,
        "right-foot": 0,
        "root2d": 1,
        "fullbody": 2,
    }
    try:
        ordered.sort(key=lambda item: priority[str(item.get("type"))])
    except KeyError as exc:
        raise ArdyBackendError(f"Unsupported ARDY constraint type: {exc.args[0]!r}.") from exc

    postprocess_constraints = load_constraints_lst(ordered, model.motion_rep.skeleton) if ordered else []
    rebased: list[dict[str, Any]] = []
    for item in ordered:
        copied = dict(item)
        copied["frame_indices"] = [int(value) + history_len for value in copied.get("frame_indices", [])]
        rebased.append(copied)

    total_frames = history_len + future_frames
    observed_motion = motion_mask = None
    if rebased:
        constraints = load_constraints_lst(rebased, model.motion_rep.skeleton)
        lengths = torch.tensor([total_frames], dtype=torch.long, device=model.device)
        observed_motion, motion_mask = model.motion_rep.create_conditions_from_constraints_batched(
            constraints,
            lengths,
            to_normalize=True,
            device=model.device,
        )
        if history_len:
            observed_motion[:, :history_len] = 0
            motion_mask[:, :history_len] = 0

    future_clips = [entry for entry in clips if not _clip_is_history(entry[0])]
    if future_clips:
        if observed_motion is None:
            observed_motion = torch.zeros(
                1, total_frames, model.motion_rep.motion_rep_dim, dtype=torch.float32, device=model.device
            )
            motion_mask = torch.zeros_like(observed_motion)
        for _, motion, start, end, mask in future_clips:
            local_quats = torch.as_tensor(motion.local_rot_quats[start:end], dtype=torch.float32, device=model.device)
            norms = torch.linalg.vector_norm(local_quats, dim=-1, keepdim=True)
            if (norms < 1e-6).any():
                raise ArdyBackendError("KMB1 contains a zero-length local rotation quaternion.")
            source = model.motion_rep(
                local_joint_rots=quaternion_to_matrix(local_quats / norms),
                root_positions=torch.as_tensor(motion.root_positions[start:end], dtype=torch.float32, device=model.device),
                to_normalize=True,
            )
            if source.ndim == 2:
                source = source.unsqueeze(0)
            _apply_future_clip(observed_motion, motion_mask, source, mask or [], history_len, model.motion_rep)
    return init_history, observed_motion, motion_mask, total_frames, history_len, postprocess_constraints


def _finalize_output(output: dict[str, Any], model: Any, constraints: list[Any], postprocess: bool) -> dict[str, Any]:
    from ardy.tools import to_numpy

    if postprocess:
        from ardy.postprocess import post_process_motion

        output.update(
            post_process_motion(
                output["local_rot_mats"],
                output["root_positions"],
                output["foot_contacts"],
                model.skeleton,
                constraint_lst=constraints or None,
            )
        )
    return to_numpy(output)


def execute_generate(
    task_request: dict[str, Any],
    model: Any,
    profile: Any,
    cancel_event: threading.Event,
    spool: Any,
    quickserver_root: str | Path,
) -> tuple[dict[str, Any], bytes | None]:
    import torch

    from kimodo.bridge import bridge_server as bridge_runtime_helpers
    from kimodo.tools import seed_everything

    output_format = bridge_runtime_helpers._resolve_requested_output_format(task_request)
    if output_format not in {"flatbuf_motion_v1", "kmb_handle_v1"}:
        raise ArdyBackendError("ARDY generate requires a KMB output format.")
    diffusion_steps = int(task_request.get("diffusion_steps", profile.max_diffusion_steps))
    if not 1 <= diffusion_steps <= int(profile.max_diffusion_steps):
        raise ArdyBackendError(
            f"diffusion_steps must be in [1, {profile.max_diffusion_steps}]; got {diffusion_steps}."
        )
    cfg_text_weight = bridge_runtime_helpers._resolve_cfg_text_weight(task_request)
    requested_seed = task_request.get("seed")
    resolved_seed = secrets.randbelow(2**31) if requested_seed is None else int(requested_seed)
    seed_everything(resolved_seed)

    prompt = str(task_request.get("prompt") or "A person walks forward.").strip()
    if not prompt.endswith("."):
        prompt += "."
    init_history, observed_motion, motion_mask, total_frames, history_len, postprocess_constraints = prepare_generation_inputs(
        model,
        profile,
        task_request.get("constraints_json", ""),
        spool,
        quickserver_root,
    )

    def check_cancel() -> None:
        if cancel_event.is_set():
            raise bridge_runtime_helpers.GenerateCancelledError("Generation canceled.")

    check_cancel()
    with torch.no_grad():
        motion = _autoregressive_step(
            model,
            num_frames=total_frames,
            num_denoising_steps=diffusion_steps,
            motion_mask=motion_mask,
            observed_motion=observed_motion,
            cfg_weight=(cfg_text_weight, profile.cfg_constraint_weight),
            texts=[prompt],
            init_history_sequence=init_history,
            root_body_pass=_has_future_clip_body_constraints(task_request.get("constraints_json", "")),
        )
        generated = motion[:, history_len : history_len + int(profile.horizon_frames)]
        output = model.motion_rep.inverse(generated, is_normalized=True)
        output = _finalize_output(
            output,
            model,
            postprocess_constraints,
            bool(getattr(profile, "postprocess", False)) and not _has_future_clips(task_request.get("constraints_json", "")),
        )
    check_cancel()
    payload = bridge_runtime_helpers._build_generate_flatbuffer_payload(model, output, sample_index=0)
    check_cancel()
    handle_info = spool.publish(
        payload,
        description=prompt,
        motion_rep_fingerprint=profile.motion_rep_fingerprint,
    )
    handle = handle_info["handle"] if isinstance(handle_info, dict) else handle_info
    return {
        "status": "done",
        "output_format": output_format,
        "byte_length": len(payload) if output_format == "flatbuf_motion_v1" else 0,
        "clip_handle": handle,
        "handle_info": handle_info if isinstance(handle_info, dict) else None,
        "motion_rep_fingerprint": profile.motion_rep_fingerprint,
        "resolved_seed": resolved_seed,
    }, payload if output_format == "flatbuf_motion_v1" else None


def load_runtime(
    profile: Any,
    config: dict[str, Any],
    quickserver_root: str | Path,
    device: str,
    *,
    text_encoder: Any = None,
):
    from ardy.model import load_model
    from kimodo.model.load_model import _select_text_encoder_conf
    from kimodo.model.loading import DEFAULT_TEXT_ENCODER_URL, get_env_var, instantiate_from_dict

    models_root = Path(config.get("models_root") or (Path(quickserver_root).resolve() / "models")).resolve()
    checkpoint_dir = models_root / profile.model_name
    required_checkpoint_files = (
        "config.yaml",
        "tokenizer.safetensors",
        "denoiser.safetensors",
        "stats/motion/mean.npy",
        "stats/motion/std.npy",
    )
    if not all((checkpoint_dir / relative).is_file() for relative in required_checkpoint_files):
        models_root.mkdir(parents=True, exist_ok=True)
        try:
            from modelscope import snapshot_download as modelscope_snapshot_download

            modelscope_snapshot_download(model_id=profile.modelscope_repo, local_dir=str(checkpoint_dir))
        except Exception as modelscope_error:
            try:
                from huggingface_hub import snapshot_download as huggingface_snapshot_download

                huggingface_snapshot_download(
                    repo_id=f"nvidia/{profile.model_name}",
                    local_dir=str(checkpoint_dir),
                )
            except Exception as huggingface_error:
                raise RuntimeError(
                    "ARDY checkpoint download failed via both ModelScope and Hugging Face: "
                    f"modelscope={modelscope_error}; huggingface={huggingface_error}"
                ) from huggingface_error

    if text_encoder is None:
        text_encoder = instantiate_from_dict(
            _select_text_encoder_conf(
                get_env_var("TEXT_ENCODER_URL", DEFAULT_TEXT_ENCODER_URL),
                device,
            )
        )
    model = load_model(
        profile.model_name,
        device=device,
        text_encoder=text_encoder,
        checkpoints_dir=str(models_root),
    )
    actual = (
        float(model.motion_rep.fps),
        int(model.gen_horizon_len),
        int(model.num_frames_per_token),
        int(model.diffusion.num_base_steps),
    )
    expected = (
        float(profile.source_fps),
        int(profile.horizon_frames),
        int(profile.frames_per_token),
        int(profile.max_diffusion_steps),
    )
    if actual != expected:
        raise ArdyBackendError(f"ARDY checkpoint/profile mismatch: expected {expected}, got {actual}.")
    model.fps = float(profile.source_fps)
    model.skeleton = model.motion_rep.skeleton
    model.name = profile.model_name
    return model
