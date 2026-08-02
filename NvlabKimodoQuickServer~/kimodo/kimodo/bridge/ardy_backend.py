from __future__ import annotations

from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import secrets
import sys
import threading
import time
from typing import Any, Callable

import numpy as np

from kimodo.bridge.frame_time import seconds_to_frame_count


MAX_KMB_BYTES = 256 * 1024**2
TARGET_VELOCITY_PREDICTION_SECONDS = 2.0
TARGET_VELOCITY_GOAL_FRAME_INTERVAL = 10


def _install_bundled_ardy_path() -> Path:
    bundled_root = Path(__file__).resolve().parents[3] / "ardy"
    if not (bundled_root / "ardy" / "__init__.py").is_file():
        raise RuntimeError(f"Bundled ARDY source is missing: {bundled_root}")
    root_text = str(bundled_root)
    loaded = sys.modules.get("ardy")
    loaded_file = getattr(loaded, "__file__", None) if loaded is not None else None
    if loaded_file and not Path(loaded_file).resolve().is_relative_to(bundled_root):
        raise RuntimeError(f"External ARDY was imported before the bundled runtime: {loaded_file}")
    sys.path[:] = [entry for entry in sys.path if str(entry) != root_text]
    sys.path.insert(0, root_text)
    return bundled_root


BUNDLED_ARDY_ROOT = _install_bundled_ardy_path()


class ArdyBackendError(ValueError):
    code = "ardy_backend_error"


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


@dataclass(frozen=True)
class Root2DTarget:
    position: tuple[float, float]
    max_speed: float
    max_acceleration: float
    arrival_threshold: float
    include_heading: bool


@dataclass(frozen=True)
class ArdyTimelineSegment:
    prompt: str
    start_frame: int
    end_frame_exclusive: int


@dataclass(frozen=True)
class ArdySettings:
    history_crop_frames: int
    future_crop_frames: int
    playback_reserve_frames: int
    adaptive_playback_reserve: bool

    @classmethod
    def from_request(cls, request: dict[str, Any], profile: Any) -> "ArdySettings":
        fps = float(profile.source_fps)
        patch = int(profile.frames_per_token)
        crop_max = int(profile.max_context_frames) - int(profile.horizon_frames)

        def seconds_to_frames(name: str, default_seconds: float, *, minimum: int = 0) -> int:
            value = float(request.get(name, default_seconds))
            if not math.isfinite(value) or value < 0.0:
                raise ArdyBackendError(f"{name} must be a finite non-negative number of seconds.")
            return max(minimum, seconds_to_frame_count(value, fps))

        history = seconds_to_frames("ardy_history_crop_seconds", crop_max / fps, minimum=patch)
        history = min(crop_max, history // patch * patch)
        future = seconds_to_frames("ardy_future_crop_seconds", crop_max / fps)
        future = min(crop_max, future // patch * patch)
        playback_reserve = seconds_to_frames("ardy_playback_reserve_seconds", 1.0)
        if playback_reserve > 0:
            minimum_reserve = max(1, seconds_to_frame_count(0.2, fps))
            playback_reserve = max(minimum_reserve, playback_reserve)
            playback_reserve = int(math.ceil(playback_reserve / patch) * patch)
        return cls(
            history_crop_frames=history,
            future_crop_frames=future,
            playback_reserve_frames=playback_reserve,
            adaptive_playback_reserve=bool(request.get("ardy_adaptive_playback_reserve", True)),
        )

    def request_fields(self, fps: float) -> dict[str, Any]:
        return {
            "ardy_history_crop_seconds": self.history_crop_frames / fps,
            "ardy_future_crop_seconds": self.future_crop_frames / fps,
            "ardy_playback_reserve_seconds": self.playback_reserve_frames / fps,
            "ardy_adaptive_playback_reserve": self.adaptive_playback_reserve,
        }


def parse_kmb1(payload: bytes) -> KmbMotion:
    from kimodo.bridge.protocol.generated import MotionPacket

    if len(payload or b"") > MAX_KMB_BYTES:
        raise ArdyBackendError(f"KMB1 payload exceeds the {MAX_KMB_BYTES}-byte limit.")
    if not payload or not MotionPacket.MotionPacket.MotionPacketBufferHasIdentifier(payload, 0):
        raise ArdyBackendError("Attachment is not a KMB1 MotionPacket.")
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
    roots = np.asarray(packet.RootPositionsAsNumpy(), dtype=np.float32).copy()
    quats = np.asarray(packet.LocalRotQuatsAsNumpy(), dtype=np.float32).copy()
    contacts = None
    if packet.FootContactsLength():
        raw_contacts = np.asarray(packet.FootContactsAsNumpy(), dtype=np.uint8).copy()
        if raw_contacts.size != num_frames * 4:
            raise ArdyBackendError("KMB1 foot-contact length does not match its frame count.")
        contacts = raw_contacts.reshape(num_frames, 4).astype(np.float32)
    if len(joint_names) != num_joints or len(joint_parents) != num_joints:
        raise ArdyBackendError("KMB1 joint metadata does not match num_joints.")
    if roots.size != num_frames * 3 or quats.size != num_frames * num_joints * 4:
        raise ArdyBackendError("KMB1 motion vector lengths do not match its header.")
    if not np.isfinite(roots).all() or not np.isfinite(quats).all():
        raise ArdyBackendError("KMB1 contains non-finite motion values.")
    model_name = (packet.ModelName() or b"").decode("utf-8", errors="strict")
    return KmbMotion(
        payload=bytes(payload),
        model_name=model_name,
        fps=float(packet.Fps()),
        joint_names=joint_names,
        joint_parents=joint_parents,
        root_positions=roots.reshape(num_frames, 3),
        local_rot_quats=quats.reshape(num_frames, num_joints, 4),
        foot_contacts=contacts,
    )


def _parse_constraints(value: Any) -> list[dict[str, Any]]:
    if value is None or value == "":
        return []
    try:
        parsed = json.loads(value) if isinstance(value, str) else value
    except Exception as exc:
        raise ArdyBackendError(f"Invalid constraints_json: {exc}") from exc
    if isinstance(parsed, dict):
        parsed = [parsed]
    if not isinstance(parsed, list) or any(not isinstance(item, dict) for item in parsed):
        raise ArdyBackendError("constraints_json must be a JSON array or object.")
    return [dict(item) for item in parsed]


def _allowed_file_roots(quickserver_root: str | Path) -> tuple[Path, ...]:
    roots = [Path(quickserver_root).resolve() / "cache" / "ardy_files"]
    roots.extend(
        Path(value).expanduser().resolve()
        for value in os.environ.get("KIMODO_ARDY_TEST_FILE_ROOTS", "").split(os.pathsep)
        if value.strip()
    )
    return tuple(dict.fromkeys(roots))


def _read_debug_file(path_value: Any, quickserver_root: str | Path) -> bytes:
    if os.environ.get("KIMODO_ARDY_ALLOW_TEST_FILES", "").strip().lower() not in {"1", "true", "yes"}:
        raise ArdyBackendError("ardy_file_v1 is available only when KIMODO_ARDY_ALLOW_TEST_FILES is enabled.")
    path = Path(str(path_value or "")).expanduser().resolve(strict=True)
    if not path.is_file() or not any(path.is_relative_to(root) for root in _allowed_file_roots(quickserver_root)):
        raise ArdyBackendError(f"ardy_file_v1 path is outside the configured debug roots: {path}.")
    return path.read_bytes()


def _clip_payload(
    item: dict[str, Any],
    attachments: tuple[bytes, ...],
    quickserver_root: str | Path,
) -> bytes:
    clip_format = str(item.get("format") or "")
    if clip_format == "kmb_attachment_v1":
        index = item.get("attachment")
        if isinstance(index, bool) or not isinstance(index, int) or not 0 <= index < len(attachments):
            raise ArdyBackendError(f"Invalid KMB attachment index: {index!r}.")
        return attachments[index]
    if clip_format == "ardy_file_v1":
        return _read_debug_file(item.get("path"), quickserver_root)
    raise ArdyBackendError(f"Unsupported clip format: {clip_format!r}.")


def _clip_slice(item: dict[str, Any], motion: KmbMotion) -> tuple[int, int]:
    start = item.get("start_frame", 0)
    end = item.get("end_frame_exclusive", motion.num_frames)
    if isinstance(start, bool) or isinstance(end, bool) or not isinstance(start, int) or not isinstance(end, int):
        raise ArdyBackendError("Clip slice bounds must be integers.")
    if not 0 <= start < end <= motion.num_frames:
        raise ArdyBackendError(f"Invalid KMB clip slice [{start}, {end}) for {motion.num_frames} frames.")
    return start, end


def _validate_kmb(motion: KmbMotion, model: Any, profile: Any) -> None:
    skeleton = model.motion_rep.skeleton
    expected_names = tuple(str(name) for name in skeleton.bone_order_names)
    parents = skeleton.joint_parents
    expected_parents = tuple(int(value) for value in parents.detach().cpu().tolist())
    if motion.model_name != profile.model_name:
        raise ArdyBackendError(
            f"KMB1 model mismatch: expected {profile.model_name!r}, got {motion.model_name!r}."
        )
    if not math.isclose(motion.fps, float(profile.source_fps), rel_tol=0.0, abs_tol=1e-5):
        raise ArdyBackendError(f"KMB1 FPS mismatch: expected {profile.source_fps}, got {motion.fps}.")
    if motion.joint_names != expected_names or motion.joint_parents != expected_parents:
        raise ArdyBackendError("KMB1 rig does not match the selected ARDY model.")


def _motion_to_tensor(motion: KmbMotion, model: Any, start: int, end: int):
    import torch
    from ardy.geometry import quaternion_to_matrix

    quats = torch.as_tensor(motion.local_rot_quats[start:end], dtype=torch.float32, device=model.device)
    norms = torch.linalg.vector_norm(quats, dim=-1, keepdim=True)
    if (norms < 1e-6).any():
        raise ArdyBackendError("KMB1 contains a zero-length local rotation quaternion.")
    roots = torch.as_tensor(motion.root_positions[start:end], dtype=torch.float32, device=model.device)
    tensor = model.motion_rep(
        local_joint_rots=quaternion_to_matrix(quats / norms),
        root_positions=roots,
        to_normalize=motion.foot_contacts is None,
    )
    if motion.foot_contacts is not None:
        contact_slice = model.motion_rep.slice_dict.get("foot_contacts")
        if contact_slice is None:
            raise ArdyBackendError("Selected ARDY motion representation has no foot-contact channels.")
        tensor[..., contact_slice] = torch.as_tensor(
            motion.foot_contacts[start:end], dtype=torch.float32, device=model.device
        )
        tensor = model.motion_rep.normalize(tensor)
    return tensor.unsqueeze(0) if tensor.ndim == 2 else tensor


def _normalize_root_heading(item: dict[str, Any]) -> None:
    if item.get("type") != "root2d" or "global_root_heading" not in item:
        return
    headings = item["global_root_heading"]
    if not isinstance(headings, list):
        raise ArdyBackendError("global_root_heading must be an array.")
    item["global_root_heading"] = [
        math.atan2(float(value[1]), float(value[0]))
        if isinstance(value, (list, tuple)) and len(value) == 2
        else float(value)
        for value in headings
    ]


def _parse_root_2d_target(item: dict[str, Any]) -> Root2DTarget:
    position = item.get("target_root_2d")
    if not isinstance(position, (list, tuple)) or len(position) != 2:
        raise ArdyBackendError("root2d_target target_root_2d must contain exactly two coordinates.")

    values = {
        "max_speed": float(item.get("max_speed", 1.25)),
        "max_acceleration": float(item.get("max_acceleration", 1.5)),
        "arrival_threshold": float(item.get("arrival_threshold", 0.1)),
    }
    point = (float(position[0]), float(position[1]))
    if not all(math.isfinite(value) for value in (*point, *values.values())):
        raise ArdyBackendError("root2d_target values must be finite.")
    if values["max_speed"] <= 0.0 or values["max_acceleration"] <= 0.0:
        raise ArdyBackendError("root2d_target speed and acceleration must be positive.")
    if values["arrival_threshold"] < 0.0:
        raise ArdyBackendError("root2d_target arrival_threshold must be non-negative.")
    include_heading = item.get("include_heading", True)
    if not isinstance(include_heading, bool):
        raise ArdyBackendError("root2d_target include_heading must be a boolean.")
    return Root2DTarget(
        position=point,
        max_speed=values["max_speed"],
        max_acceleration=values["max_acceleration"],
        arrival_threshold=values["arrival_threshold"],
        include_heading=include_heading,
    )


def _plan_root_2d_target(
    target: Root2DTarget,
    anchor_root_2d: tuple[float, float],
    current_velocity_2d: tuple[float, float],
    anchor_frame: int,
    fps: float,
) -> dict[str, Any] | None:
    position = np.asarray(anchor_root_2d, dtype=np.float64)
    goal = np.asarray(target.position, dtype=np.float64)
    delta = goal - position
    distance = float(np.linalg.norm(delta))
    if distance <= target.arrival_threshold:
        return None

    direction = delta / distance
    velocity = np.asarray(current_velocity_2d, dtype=np.float64)
    speed = float(np.linalg.norm(velocity))
    if speed > target.max_speed:
        velocity *= target.max_speed / speed

    prediction_frames = max(1, seconds_to_frame_count(TARGET_VELOCITY_PREDICTION_SECONDS, fps))
    dt = 1.0 / fps
    frame_indices: list[int] = []
    positions: list[list[float]] = []
    headings: list[float] = []
    for step in range(1, prediction_frames + 1):
        remaining_delta = goal - position
        remaining_distance = float(np.linalg.norm(remaining_delta))
        if remaining_distance <= target.arrival_threshold:
            position = goal.copy()
            remaining_distance = 0.0
        else:
            direction = remaining_delta / remaining_distance
            stopping_speed = math.sqrt(max(0.0, 2.0 * target.max_acceleration * remaining_distance))
            desired_velocity = direction * min(target.max_speed, stopping_speed)
            velocity_delta = desired_velocity - velocity
            max_velocity_delta = target.max_acceleration * dt
            velocity_delta_length = float(np.linalg.norm(velocity_delta))
            if velocity_delta_length > max_velocity_delta:
                velocity_delta *= max_velocity_delta / velocity_delta_length
            velocity += velocity_delta
            displacement = velocity * dt
            if float(np.dot(displacement, direction)) >= remaining_distance:
                position = goal.copy()
                velocity[:] = 0.0
            else:
                position += displacement

        if step % TARGET_VELOCITY_GOAL_FRAME_INTERVAL == 0 or step == prediction_frames:
            frame_indices.append(anchor_frame + step)
            positions.append([float(position[0]), float(position[1])])
            if target.include_heading:
                velocity_length = float(np.linalg.norm(velocity))
                if velocity_length > 1e-8:
                    heading_direction = velocity / velocity_length
                else:
                    heading_direction = direction
                headings.append(math.atan2(float(heading_direction[0]), float(heading_direction[1])))

    result: dict[str, Any] = {
        "type": "root2d",
        "frame_indices": frame_indices,
        "smooth_root_2d": positions,
    }
    if target.include_heading:
        result["global_root_heading"] = headings
    return result


def _expand_dense_root_constraint(
    item: dict[str, Any],
    anchor_frame: int,
    anchor_root_2d: tuple[float, float] | None,
) -> list[dict[str, Any]]:
    dense_path = item.pop("dense_path", None)
    if dense_path is None or dense_path is False:
        return [item]
    if dense_path is not True:
        raise ArdyBackendError("root2d dense_path must be a boolean.")
    if anchor_root_2d is None:
        return [item]

    root_key = "root_2d" if "root_2d" in item else "smooth_root_2d"
    indices = item.get("frame_indices")
    roots = item.get(root_key)
    if not isinstance(indices, list) or not isinstance(roots, list) or len(indices) != len(roots):
        raise ArdyBackendError("Dense root2d frame_indices and positions must have equal lengths.")

    targets: dict[int, tuple[float, float]] = {}
    for frame, root in zip(indices, roots):
        if isinstance(frame, bool) or not isinstance(frame, int):
            raise ArdyBackendError("Dense root2d frame indices must be integers.")
        if not isinstance(root, (list, tuple)) or len(root) != 2:
            raise ArdyBackendError("Dense root2d positions must contain two coordinates.")
        point = (float(root[0]), float(root[1]))
        if not all(math.isfinite(value) for value in point):
            raise ArdyBackendError("Dense root2d positions must be finite.")
        if frame > anchor_frame:
            targets[frame] = point
    if not targets:
        return [item]

    dense_indices: list[int] = []
    dense_roots: list[list[float]] = []
    previous_frame = anchor_frame
    previous_root = (float(anchor_root_2d[0]), float(anchor_root_2d[1]))
    for target_frame, target_root in sorted(targets.items()):
        span = target_frame - previous_frame
        for frame in range(previous_frame + 1, target_frame + 1):
            alpha = (frame - previous_frame) / span
            dense_indices.append(frame)
            dense_roots.append(
                [
                    previous_root[0] + (target_root[0] - previous_root[0]) * alpha,
                    previous_root[1] + (target_root[1] - previous_root[1]) * alpha,
                ]
            )
        previous_frame = target_frame
        previous_root = target_root

    dense = {"type": "root2d", "frame_indices": dense_indices, root_key: dense_roots}
    if "global_root_heading" not in item:
        return [dense]
    return [dense, item]


def _history_limit_for_future(profile: Any, settings: ArdySettings, frame_count: int, furthest: int) -> int:
    history_limit = int(settings.history_crop_frames)
    horizon = int(profile.horizon_frames)
    future_needed = furthest - (frame_count + horizon) + 1
    if future_needed <= 0:
        return history_limit
    future_needed = min(int(settings.future_crop_frames), future_needed)
    patch = int(profile.frames_per_token)
    available = int(profile.max_context_frames) - horizon - future_needed
    available = max(patch, available // patch * patch)
    return min(history_limit, available)


def _future_clip_mask(item: dict[str, Any], joint_count: int) -> list[bool]:
    values = item.get("mask")
    expected = 4 + max(0, joint_count - 1) * 3
    if not isinstance(values, list) or len(values) != expected or any(not isinstance(value, bool) for value in values):
        raise ArdyBackendError(f"Future KMB clip mask must contain exactly {expected} booleans.")
    return values


def _append_outputs(left: dict[str, np.ndarray] | None, right: dict[str, Any]) -> dict[str, np.ndarray]:
    converted = {key: np.asarray(value) for key, value in right.items() if isinstance(value, np.ndarray)}
    if left is None:
        return {key: value.copy() for key, value in converted.items()}
    result: dict[str, np.ndarray] = {}
    for key in left.keys() & converted.keys():
        if left[key].ndim >= 2 and converted[key].ndim >= 2:
            result[key] = np.concatenate((left[key], converted[key]), axis=1)
    return result


def _slice_outputs(outputs: dict[str, np.ndarray], start: int, end: int) -> dict[str, np.ndarray]:
    return {key: value[:, start:end].copy() for key, value in outputs.items() if value.ndim >= 2}


def _parse_timeline_segments(
    value: Any,
    profile: Any,
    total_frames: int,
) -> tuple[ArdyTimelineSegment, ...]:
    if value is None:
        return ()
    if total_frames <= 0:
        raise ArdyBackendError("ardy_timeline_segments requires a fixed positive duration.")
    if not isinstance(value, list) or not value:
        raise ArdyBackendError("ardy_timeline_segments must be a non-empty array.")

    segments: list[ArdyTimelineSegment] = []
    cursor = 0
    patch = int(profile.frames_per_token)
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise ArdyBackendError(f"ardy_timeline_segments[{index}] must be an object.")
        prompt = str(item.get("prompt") or "").strip() or "idle"
        try:
            duration_seconds = float(item.get("duration"))
        except (TypeError, ValueError) as exc:
            raise ArdyBackendError(
                f"ardy_timeline_segments[{index}].duration must be a finite positive number."
            ) from exc
        if not math.isfinite(duration_seconds) or duration_seconds <= 0.0:
            raise ArdyBackendError(
                f"ardy_timeline_segments[{index}].duration must be a finite positive number."
            )
        frame_count = seconds_to_frame_count(duration_seconds, profile.source_fps)
        if frame_count <= 0:
            raise ArdyBackendError(f"ardy_timeline_segments[{index}] resolves to zero frames.")
        if cursor and cursor % patch:
            raise ArdyBackendError(
                f"ardy_timeline_segments boundary before segment {index + 1} must align to the {patch}-frame motion token."
            )
        segments.append(ArdyTimelineSegment(prompt, cursor, cursor + frame_count))
        cursor += frame_count

    if cursor != total_frames:
        raise ArdyBackendError(
            f"ardy_timeline_segments resolves to {cursor} frames, but duration resolves to {total_frames}."
        )
    return tuple(segments)


class ArdySession:
    """Official ARDY autoregression with per-Session prompt, RNG, history, and CPU seek cache."""

    def __init__(
        self,
        request: dict[str, Any],
        attachments: tuple[bytes, ...],
        model: Any,
        profile: Any,
        quickserver_root: str | Path,
        progress: Callable[[str], None] | None = None,
        cancel_event: threading.Event | None = None,
    ):
        import torch

        self.profile = profile
        self.quickserver_root = Path(quickserver_root).resolve()
        self.settings = ArdySettings.from_request(request, profile)
        self.prompt = self._normalize_prompt(request.get("prompt") if "prompt" in request else "idle")
        self.diffusion_steps = self._resolve_steps(
            request.get("diffusion_steps", profile.max_diffusion_steps)
        )
        self.cfg_text_weight = self._resolve_cfg(request)
        requested_seed = request.get("seed")
        self.resolved_seed = secrets.randbelow(2**31) if requested_seed is None else int(requested_seed)
        self.returned_until = 0
        self.last_played_frame = 0
        self.effective_playback_reserve_frames = self.settings.playback_reserve_frames
        self._response_seconds_ema: float | None = None
        duration_seconds = float(request.get("duration", 0.0) or 0.0)
        if not math.isfinite(duration_seconds) or duration_seconds < 0.0:
            raise ArdyBackendError("duration must be a finite non-negative number of seconds.")
        self._initial_duration_frames = 0
        if duration_seconds > 0.0:
            self._initial_duration_frames = seconds_to_frame_count(
                duration_seconds,
                profile.source_fps,
            )
        self.timeline_segments = _parse_timeline_segments(
            request.get("ardy_timeline_segments"),
            profile,
            self._initial_duration_frames,
        )
        self.motion_cpu = None
        self.outputs: dict[str, np.ndarray] | None = None
        self.initial_history_cpu = None
        self.history_cpu = None
        self.constraints: list[Any] = []
        self.constraint_items: list[dict[str, Any]] = []
        self.root_2d_target: Root2DTarget | None = None
        self.future_clips: list[tuple[int, Any, list[bool]]] = []
        self._cpu_rng_state = torch.Generator(device="cpu").manual_seed(self.resolved_seed).get_state()
        self._cuda_rng_state = None
        if str(model.device).startswith("cuda"):
            self._cuda_rng_state = torch.Generator(device=model.device).manual_seed(self.resolved_seed).get_state()
        self._encoded_prompts: dict[str, tuple[Any, Any]] = {}
        self._activate_prompt(
            model,
            self.timeline_segments[0].prompt if self.timeline_segments else self.prompt,
            progress,
            cancel_event,
        )
        self._set_constraints(request.get("constraints_json", []), attachments, model, apply_from=0, initial=True)

    @staticmethod
    def _normalize_prompt(value: Any) -> str:
        prompt = str(value or "").strip()
        return prompt or "idle"

    @staticmethod
    def _resolve_cfg(request: dict[str, Any]) -> float:
        from kimodo.bridge import bridge_server

        return bridge_server._resolve_cfg_text_weight(request)

    def _resolve_steps(self, value: Any) -> int:
        if isinstance(value, bool):
            raise ArdyBackendError("diffusion_steps must be an integer.")
        try:
            steps = int(value)
        except (TypeError, ValueError) as exc:
            raise ArdyBackendError("diffusion_steps must be an integer.") from exc
        if not 1 <= steps <= int(self.profile.max_diffusion_steps):
            raise ArdyBackendError(
                f"diffusion_steps must be in [1, {self.profile.max_diffusion_steps}]."
            )
        return steps

    def _generation_parameters_changed(self, request: dict[str, Any]) -> bool:
        if "diffusion_steps" in request and self._resolve_steps(request.get("diffusion_steps")) != self.diffusion_steps:
            return True
        if ("text_weight" in request or "cfg_weight" in request) and not math.isclose(
            self._resolve_cfg(request), self.cfg_text_weight, rel_tol=0.0, abs_tol=1e-9
        ):
            return True
        return False

    @property
    def frame_count(self) -> int:
        return 0 if self.motion_cpu is None else int(self.motion_cpu.shape[1])

    def _encode_prompt(
        self,
        model: Any,
        progress: Callable[[str], None] | None = None,
        cancel_event: threading.Event | None = None,
    ) -> None:
        from kimodo.bridge import bridge_server

        if cancel_event is not None and cancel_event.is_set():
            raise bridge_server.GenerateCancelledError("Generation canceled.")
        encode_text = getattr(model, "_encode_text", None)
        if callable(encode_text):
            encoder = getattr(model, "text_encoder", None)
            cold_start = encoder is not None and getattr(encoder, "model", object()) is None
            if progress is not None:
                progress(
                    "Loading TextEncoder weights and moving them to the accelerator..."
                    if cold_start
                    else "Encoding prompt..."
                )
            self.text_feat, self.text_pad_mask = encode_text([self.prompt])
            if cancel_event is not None and cancel_event.is_set():
                raise bridge_server.GenerateCancelledError("Generation canceled.")
            if progress is not None:
                progress("TextEncoder ready. Generating ARDY motion...")
        else:
            self.text_feat = self.text_pad_mask = None

    def _activate_prompt(
        self,
        model: Any,
        prompt: str,
        progress: Callable[[str], None] | None = None,
        cancel_event: threading.Event | None = None,
    ) -> None:
        self.prompt = self._normalize_prompt(prompt)
        cached = self._encoded_prompts.get(self.prompt)
        if cached is None:
            self._encode_prompt(model, progress, cancel_event)
            self._encoded_prompts[self.prompt] = (self.text_feat, self.text_pad_mask)
            return
        self.text_feat, self.text_pad_mask = cached

    def _activate_timeline_prompt(self, model: Any, frame: int, cancel_event: threading.Event) -> int | None:
        for segment in self.timeline_segments:
            if frame < segment.end_frame_exclusive:
                self._activate_prompt(model, segment.prompt, cancel_event=cancel_event)
                return segment.end_frame_exclusive
        return None

    def _set_constraints(
        self,
        value: Any,
        attachments: tuple[bytes, ...],
        model: Any,
        *,
        apply_from: int,
        initial: bool,
    ) -> None:
        import torch

        plain: list[dict[str, Any]] = []
        root_2d_target = None
        history_tensors: list[Any] = []
        future_clips: list[tuple[int, Any, list[bool]]] = []
        anchor_root_2d = None
        if apply_from > 0 and self.outputs is not None and "root_positions" in self.outputs:
            anchor = self.outputs["root_positions"][0, apply_from - 1]
            anchor_root_2d = (float(anchor[0]), float(anchor[2]))
        for item in _parse_constraints(value):
            if item.get("type") == "root2d_target":
                root_2d_target = _parse_root_2d_target(item)
                continue
            if item.get("type") != "clip":
                copied = dict(item)
                _normalize_root_heading(copied)
                indices = copied.get("frame_indices", [])
                if not isinstance(indices, list):
                    raise ArdyBackendError("Constraint frame_indices must be an array.")
                copied["frame_indices"] = [int(index) + apply_from for index in indices]
                plain.extend(_expand_dense_root_constraint(copied, apply_from - 1, anchor_root_2d))
                continue

            motion = parse_kmb1(_clip_payload(item, attachments, self.quickserver_root))
            _validate_kmb(motion, model, self.profile)
            start, end = _clip_slice(item, motion)
            is_history = item.get("is_history", True)
            if not isinstance(is_history, bool):
                raise ArdyBackendError("clip is_history must be a boolean.")
            tensor = _motion_to_tensor(motion, model, start, end)
            if is_history:
                if not initial:
                    raise ArdyBackendError("Only the first Generate may provide explicit History KMB attachments.")
                if "mask" in item:
                    raise ArdyBackendError("History KMB attachments cannot specify a mask.")
                history_tensors.append(tensor)
            else:
                future_clips.append(
                    (apply_from, tensor.detach().cpu(), _future_clip_mask(item, len(motion.joint_names)))
                )

        self.constraint_items = plain
        self.root_2d_target = root_2d_target
        self._refresh_root_2d_target_constraints(model, apply_from)
        self.future_clips = future_clips
        if history_tensors:
            combined = torch.cat(history_tensors, dim=1)
            keep = min(self.settings.history_crop_frames, int(combined.shape[1]))
            keep -= keep % int(self.profile.frames_per_token)
            if keep <= 0:
                raise ArdyBackendError("Explicit History must contain at least one complete motion token.")
            self.initial_history_cpu = combined[:, -keep:].detach().cpu()

    def _root_state_at_boundary(self, boundary_frame: int) -> tuple[tuple[float, float], tuple[float, float]]:
        fps = float(self.profile.source_fps)
        if self.outputs is None or "root_positions" not in self.outputs or boundary_frame <= 0:
            return (0.0, 0.0), (0.0, 0.0)
        roots = self.outputs["root_positions"][0]
        current_index = min(boundary_frame, int(roots.shape[0])) - 1
        current = roots[current_index]
        velocity = np.zeros(2, dtype=np.float64)
        if current_index > 0:
            previous = roots[current_index - 1]
            velocity = (current[[0, 2]] - previous[[0, 2]]) * fps
        return (float(current[0]), float(current[2])), (float(velocity[0]), float(velocity[1]))

    def _refresh_root_2d_target_constraints(self, model: Any, boundary_frame: int) -> None:
        from ardy.constraints import load_constraints_lst

        plain = list(self.constraint_items)
        if self.root_2d_target is not None:
            root_2d, velocity_2d = self._root_state_at_boundary(boundary_frame)
            target_constraint = _plan_root_2d_target(
                self.root_2d_target,
                root_2d,
                velocity_2d,
                boundary_frame - 1,
                float(self.profile.source_fps),
            )
            if target_constraint is None:
                self.root_2d_target = None
            else:
                plain.append(target_constraint)
        self.constraints = load_constraints_lst(plain, model.motion_rep.skeleton) if plain else []

    def _apply_settings(self, request: dict[str, Any]) -> bool:
        settings_keys = {
            "ardy_history_crop_seconds",
            "ardy_future_crop_seconds",
            "ardy_playback_reserve_seconds",
            "ardy_adaptive_playback_reserve",
        }
        if not any(key in request for key in settings_keys):
            return False
        merged = dict(request)
        for key, value in self.settings.request_fields(float(self.profile.source_fps)).items():
            merged.setdefault(key, value)
        self.settings = ArdySettings.from_request(merged, self.profile)
        self.effective_playback_reserve_frames = self.settings.playback_reserve_frames
        self._response_seconds_ema = None
        return True

    def _apply_patch(
        self,
        request: dict[str, Any],
        attachments: tuple[bytes, ...],
        model: Any,
        apply_from: int,
        cancel_event: threading.Event,
    ) -> bool:
        if "ardy_timeline_segments" in request:
            raise ArdyBackendError("ardy_timeline_segments is only supported by fixed-duration generation.")
        changed = "prompt" in request or "constraints_json" in request or self._generation_parameters_changed(request)
        if not changed:
            return False
        if "prompt" in request:
            self._activate_prompt(model, request.get("prompt"), cancel_event=cancel_event)
        if "diffusion_steps" in request:
            self.diffusion_steps = self._resolve_steps(request.get("diffusion_steps"))
        if "text_weight" in request or "cfg_weight" in request:
            self.cfg_text_weight = self._resolve_cfg(request)
        if "constraints_json" in request:
            self._set_constraints(
                request.get("constraints_json"), attachments, model, apply_from=apply_from, initial=False
            )
        return True

    def _truncate(self, frame: int) -> None:
        import torch

        frame = max(0, min(frame, self.frame_count))
        if self.motion_cpu is not None:
            self.motion_cpu = self.motion_cpu[:, :frame].clone()
        if self.outputs is not None:
            self.outputs = _slice_outputs(self.outputs, 0, frame)
        pieces = [item for item in (self.initial_history_cpu, self.motion_cpu) if item is not None]
        if not pieces:
            self.history_cpu = None
            return
        combined = pieces[0] if len(pieces) == 1 else torch.cat(pieces, dim=1)
        keep = min(self.settings.history_crop_frames, int(combined.shape[1]))
        keep -= keep % int(self.profile.frames_per_token)
        self.history_cpu = combined[:, -keep:].clone() if keep > 0 else None

    def _history(self, model: Any, frame_limit: int | None = None):
        import torch

        pieces = [self.history_cpu] if self.history_cpu is not None else [
            item for item in (self.initial_history_cpu, self.motion_cpu) if item is not None
        ]
        if not pieces:
            return None, 0, 0
        combined = pieces[0] if len(pieces) == 1 else torch.cat(pieces, dim=1)
        keep = min(self.settings.history_crop_frames, int(combined.shape[1]))
        if frame_limit is not None:
            keep = min(keep, frame_limit)
        keep -= keep % int(self.profile.frames_per_token)
        if keep <= 0:
            return None, 0, self.frame_count
        history = combined[:, -keep:].to(device=model.device)
        return history, keep, self.frame_count - keep

    def _condition_window(self, model: Any, history_len: int, window_start: int, num_frames: int):
        import torch

        window_end = window_start + num_frames
        cropped = [
            constraint.crop_move(window_start, window_end)
            for constraint in self.constraints
            if bool(((constraint.frame_indices >= window_start) & (constraint.frame_indices < window_end)).any())
        ]
        observed = mask = None
        if cropped:
            lengths = torch.tensor([num_frames], dtype=torch.long, device=model.device)
            observed, mask = model.motion_rep.create_conditions_from_constraints_batched(
                cropped, lengths, to_normalize=True, device=model.device
            )
            if history_len:
                observed[:, :history_len] = 0
                mask[:, :history_len] = 0

        for target_start, source_cpu, clip_mask in self.future_clips:
            target_end = target_start + int(source_cpu.shape[1])
            overlap_start = max(target_start, window_start + history_len)
            overlap_end = min(target_end, window_end)
            if overlap_start >= overlap_end:
                continue
            if observed is None:
                observed = torch.zeros(
                    1, num_frames, model.motion_rep.motion_rep_dim, dtype=torch.float32, device=model.device
                )
                mask = torch.zeros_like(observed)
            source = source_cpu[:, overlap_start - target_start : overlap_end - target_start].to(model.device)
            destination = slice(overlap_start - window_start, overlap_end - window_start)
            root_slice = model.motion_rep.slice_dict["root_pos"]
            heading_slice = model.motion_rep.slice_dict["global_root_heading"]
            joint_slice = model.motion_rep.slice_dict["local_joints_positions"]
            for axis in range(3):
                channel = root_slice.start + axis
                if clip_mask[axis]:
                    observed[:, destination, channel] = source[:, :, channel]
                    mask[:, destination, channel] = 1
            if clip_mask[3]:
                observed[:, destination, heading_slice] = source[:, :, heading_slice]
                mask[:, destination, heading_slice] = 1
            joint_mask = source.new_tensor(clip_mask[4:]).reshape(1, 1, -1)
            observed[:, destination, joint_slice] = source[:, :, joint_slice] * joint_mask
            mask[:, destination, joint_slice] = joint_mask
        return observed, mask

    def _generate_horizon(self, model: Any, cancel_event: threading.Event | None = None) -> None:
        import torch
        from ardy.postprocess import post_process_motion
        from ardy.tools import to_numpy

        horizon_start = self.frame_count
        segment_end = self._activate_timeline_prompt(
            model,
            horizon_start,
            cancel_event or threading.Event(),
        )
        horizon = int(self.profile.horizon_frames)
        if segment_end is not None:
            horizon = min(horizon, segment_end - horizon_start)
        if horizon <= 0:
            return
        max_constraint = max(
            (int(constraint.frame_indices.max()) for constraint in self.constraints if len(constraint.frame_indices)),
            default=-1,
        )
        max_clip = max((start + int(source.shape[1]) - 1 for start, source, _ in self.future_clips), default=-1)
        furthest = max(max_constraint, max_clip)
        history_limit = _history_limit_for_future(self.profile, self.settings, self.frame_count, furthest)
        history, history_len, window_start = self._history(model, history_limit)
        num_frames = history_len + horizon
        if furthest >= self.frame_count:
            num_frames = max(num_frames, furthest - window_start + 1)
            num_frames = min(num_frames, history_len + horizon + self.settings.future_crop_frames)
            patch = int(self.profile.frames_per_token)
            num_frames = int(math.ceil(num_frames / patch) * patch)
        num_frames = min(int(self.profile.max_context_frames), max(history_len + horizon, num_frames))
        observed, motion_mask = self._condition_window(model, history_len, window_start, num_frames)

        devices = [torch.device(model.device).index or 0] if str(model.device).startswith("cuda") else []
        with torch.random.fork_rng(devices=devices):
            torch.random.set_rng_state(self._cpu_rng_state)
            if self._cuda_rng_state is not None:
                torch.cuda.set_rng_state(self._cuda_rng_state, device=model.device)
            with torch.no_grad():
                kwargs = {
                    "num_frames": num_frames,
                    "num_denoising_steps": self.diffusion_steps,
                    "motion_mask": motion_mask,
                    "observed_motion": observed,
                    "cfg_weight": (self.cfg_text_weight, float(self.profile.cfg_constraint_weight)),
                    "texts": [self.prompt],
                    "init_history_sequence": history,
                }
                if self.text_feat is not None:
                    kwargs["text_feat"] = self.text_feat
                    kwargs["text_pad_mask"] = self.text_pad_mask
                motion = model.autoregressive_step(**kwargs)
            self._cpu_rng_state = torch.random.get_rng_state()
            if self._cuda_rng_state is not None:
                self._cuda_rng_state = torch.cuda.get_rng_state(device=model.device)

        generated = motion[:, history_len : history_len + horizon]
        output = model.motion_rep.inverse(generated, is_normalized=True)
        post_constraints = [
            constraint.crop_move(horizon_start, horizon_start + horizon)
            for constraint in self.constraints
            if bool(((constraint.frame_indices >= horizon_start) & (constraint.frame_indices < horizon_start + horizon)).any())
        ]
        future_clip_active = any(
            max(start, horizon_start) < min(start + int(source.shape[1]), horizon_start + horizon)
            for start, source, _ in self.future_clips
        )
        if bool(getattr(self.profile, "postprocess", False)) and not future_clip_active:
            output.update(
                post_process_motion(
                    output["local_rot_mats"],
                    output["root_positions"],
                    output["foot_contacts"],
                    model.motion_rep.skeleton,
                    constraint_lst=post_constraints or None,
                )
            )

        generated_cpu = generated.detach().cpu()
        self.motion_cpu = generated_cpu if self.motion_cpu is None else torch.cat((self.motion_cpu, generated_cpu), dim=1)
        keep = min(self.settings.history_crop_frames, int(motion.shape[1]))
        keep -= keep % int(self.profile.frames_per_token)
        self.history_cpu = motion[:, -keep:].detach().cpu() if keep > 0 else None
        self.outputs = _append_outputs(self.outputs, to_numpy(output))

    def _ensure_generated(self, frame_exclusive: int, model: Any, cancel_event: threading.Event) -> None:
        from kimodo.bridge import bridge_server

        while self.frame_count < frame_exclusive:
            if cancel_event.is_set():
                raise bridge_server.GenerateCancelledError("Generation canceled.")
            if self.root_2d_target is not None:
                self._refresh_root_2d_target_constraints(model, self.frame_count)
            self._generate_horizon(model, cancel_event)
        if cancel_event.is_set():
            raise bridge_server.GenerateCancelledError("Generation canceled.")

    def record_response_duration(self, elapsed_seconds: float, delivered_frames: int) -> None:
        if not self.settings.adaptive_playback_reserve or delivered_frames <= 0:
            return
        fps = float(self.profile.source_fps)
        patch = int(self.profile.frames_per_token)
        elapsed = max(0.0, float(elapsed_seconds))
        self._response_seconds_ema = (
            elapsed
            if self._response_seconds_ema is None
            else 0.75 * self._response_seconds_ema + 0.25 * elapsed
        )
        minimum = int(math.ceil(seconds_to_frame_count(0.2, fps) / patch) * patch)
        estimate = int(math.ceil((1.5 * self._response_seconds_ema * fps + patch) / patch) * patch)
        hard_max = max(minimum, self.settings.history_crop_frames + self.settings.future_crop_frames)
        estimate = max(minimum, min(estimate, hard_max))
        current = max(minimum, self.effective_playback_reserve_frames)
        if estimate < current:
            estimate = max(estimate, current - patch)
        self.effective_playback_reserve_frames = max(minimum, min(estimate, hard_max))

    def generate(
        self,
        request: dict[str, Any],
        attachments: tuple[bytes, ...],
        model: Any,
        cancel_event: threading.Event,
    ) -> tuple[dict[str, Any], dict[str, np.ndarray] | None]:
        fps = float(self.profile.source_fps)
        patch = int(self.profile.frames_per_token)
        time_seconds = float(request.get("time_as_double", 0.0))
        if not math.isfinite(time_seconds) or time_seconds < 0.0:
            raise ArdyBackendError("time_as_double must be a finite non-negative number.")
        played_exact = seconds_to_frame_count(time_seconds, fps)
        played = played_exact // patch * patch
        seek = played_exact < self.last_played_frame
        patch_requested = (
            "prompt" in request
            or "constraints_json" in request
            or self._generation_parameters_changed(request)
        )
        self._apply_settings(request)
        reserve = self.effective_playback_reserve_frames

        if played > self.frame_count:
            self._ensure_generated(played, model, cancel_event)
        apply_from = 0
        if self.frame_count > 0:
            reserve_end = played_exact + reserve
            apply_from = int(math.ceil(reserve_end / patch) * patch)
            self._ensure_generated(apply_from, model, cancel_event)
        if patch_requested or seek:
            self._truncate(apply_from)

        if patch_requested:
            self._apply_patch(request, attachments, model, apply_from, cancel_event)
        if patch_requested or seek:
            return_start = min(self.returned_until, apply_from)
            generation_start = apply_from
        else:
            return_start = max(self.returned_until, played_exact)
            generation_start = return_start
        minimum_delivery = reserve + 1
        if self.returned_until == 0 and self._initial_duration_frames > 0:
            minimum_delivery = max(minimum_delivery, self._initial_duration_frames)
        target = generation_start + max(1, minimum_delivery)
        self._ensure_generated(target, model, cancel_event)
        return_end = (
            min(self.frame_count, self._initial_duration_frames)
            if self.returned_until == 0 and self._initial_duration_frames > 0
            else self.frame_count
        )
        result = None if return_end <= return_start else _slice_outputs(self.outputs or {}, return_start, return_end)
        self.returned_until = return_end
        self._initial_duration_frames = 0
        self.last_played_frame = played_exact
        return {
            "start_frame": return_start,
            "end_frame_exclusive": return_end,
        }, result

    def close(self) -> None:
        self.motion_cpu = None
        self.outputs = None
        self.initial_history_cpu = None
        self.history_cpu = None
        self.constraints = []
        self.constraint_items = []
        self.root_2d_target = None
        self.future_clips = []
        self.timeline_segments = ()
        self._encoded_prompts = {}
        self.text_feat = self.text_pad_mask = None


def execute_stream_generate(
    session: ArdySession | None,
    request: dict[str, Any],
    attachments: tuple[bytes, ...],
    model: Any,
    profile: Any,
    cancel_event: threading.Event,
    quickserver_root: str | Path,
    progress: Callable[[str], None] | None = None,
) -> tuple[ArdySession | None, dict[str, Any], bytes | None]:
    from kimodo.bridge import bridge_server

    fixed_length = "duration" in request
    if fixed_length:
        try:
            duration_seconds = float(request.get("duration"))
        except (TypeError, ValueError) as exc:
            raise ArdyBackendError("duration must be a finite positive number of seconds.") from exc
        if not math.isfinite(duration_seconds) or duration_seconds <= 0.0:
            raise ArdyBackendError("duration must be a finite positive number of seconds.")
        if session is not None:
            session.close()
        session = None
    if session is None:
        session = ArdySession(
            request,
            attachments,
            model,
            profile,
            quickserver_root,
            progress,
            cancel_event,
        )
        request = {"time_as_double": request.get("time_as_double", 0.0)}
    try:
        started = time.perf_counter()
        metadata, output = session.generate(request, attachments, model, cancel_event)
        payload = b""
        if output:
            payload = bridge_server._build_generate_flatbuffer_payload(model, output, sample_index=0)
        elapsed = time.perf_counter() - started
        session.record_response_duration(
            elapsed,
            int(metadata["end_frame_exclusive"]) - int(metadata["start_frame"]),
        )
        return (None if fixed_length else session), {
            "status": "done",
            "output_format": "kmb_v1",
            "byte_length": len(payload),
            "motion_rep_fingerprint": profile.motion_rep_fingerprint,
            "resolved_seed": session.resolved_seed,
            "ardy_playback_reserve_seconds": session.effective_playback_reserve_frames / float(profile.source_fps),
            "ardy_adaptive_playback_reserve": session.settings.adaptive_playback_reserve,
            "ardy_server_response_seconds": elapsed,
            **metadata,
        }, payload or None
    finally:
        if fixed_length:
            session.close()


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
    required = (
        "config.yaml",
        "tokenizer.safetensors",
        "denoiser.safetensors",
        "stats/motion/mean.npy",
        "stats/motion/std.npy",
    )
    if not all((checkpoint_dir / relative).is_file() for relative in required):
        models_root.mkdir(parents=True, exist_ok=True)
        try:
            from modelscope import snapshot_download

            snapshot_download(model_id=profile.modelscope_repo, local_dir=str(checkpoint_dir))
        except Exception as modelscope_error:
            try:
                from huggingface_hub import snapshot_download

                snapshot_download(repo_id=f"nvidia/{profile.model_name}", local_dir=str(checkpoint_dir))
            except Exception as huggingface_error:
                raise RuntimeError(
                    "ARDY checkpoint download failed via both ModelScope and Hugging Face: "
                    f"modelscope={modelscope_error}; huggingface={huggingface_error}"
                ) from huggingface_error

    if text_encoder is None:
        text_encoder = instantiate_from_dict(
            _select_text_encoder_conf(get_env_var("TEXT_ENCODER_URL", DEFAULT_TEXT_ENCODER_URL), device)
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
