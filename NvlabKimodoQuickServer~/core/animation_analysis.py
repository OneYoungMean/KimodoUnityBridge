"""Sparse keyframe analysis for dense KMB motion."""

from __future__ import annotations

from typing import Any

import numpy as np


_DEFAULT_KEYFRAME_COUNT = 8
_MIN_CONTACT_STATE_FRAMES = 4


def build_generation_analysis(request: dict[str, Any], model: Any, output: dict[str, Any]) -> dict[str, Any] | None:
    """Return sparse frame markers; dense foot contacts stay in the KMB payload."""
    options = _analysis_options(request)
    if options is None:
        return None
    try:
        joints = np.asarray(output.get("posed_joints"), dtype=np.float64)
        if joints.ndim == 4:
            joints = joints[0]
        if joints.ndim != 3 or joints.shape[0] < 1 or joints.shape[2] < 3:
            raise ValueError("posed_joints must have shape [frames,joints,3].")
        root_index = int(getattr(getattr(model, "skeleton", None), "root_idx", 0))
        fps = float(getattr(model, "fps", 30.0))
        if not np.isfinite(fps) or fps <= 0.0:
            raise ValueError("model fps must be finite and positive.")
        contacts = np.asarray(output.get("foot_contacts", []))
        if contacts.ndim == 3:
            contacts = contacts[0]
        return {
            "keyframes": [
                {**keyframe, "time": round(int(keyframe["frame"]) / fps, 6)}
                for keyframe in _select_position_keyframes(joints, root_index, _keyframe_count(options, len(joints)))
            ],
            "foot_contact_changes": _foot_contact_changes(contacts),
        }
    except Exception as exc:
        return {"keyframes": [], "warnings": [f"keyframe analysis unavailable: {exc}"]}


def build_clip_constraint_analysis(clips: list[dict[str, Any]], options: dict[str, Any]) -> dict[str, Any]:
    """Return sparse markers into the dense KMB attachments returned beside this JSON."""
    if not clips:
        raise ValueError("analysis_only requires at least one ClipConstraint.")
    markers: list[dict[str, int | float]] = []
    foot_contact_changes: list[dict[str, int | bool | str]] = []
    for clip_index, clip in enumerate(clips):
        roots = np.asarray(clip["root_positions"], dtype=np.float64)
        quats = np.asarray(clip["local_rot_quats"], dtype=np.float64)
        if roots.ndim != 2 or roots.shape[1] != 3 or len(roots) < 1:
            raise ValueError(f"clip {clip_index} root_positions must have shape [frames,3].")
        if quats.ndim != 3 or quats.shape[0] != len(roots) or quats.shape[2] != 4:
            raise ValueError(f"clip {clip_index} local_rot_quats must have shape [frames,joints,4].")
        count = _keyframe_count(options, len(roots))
        markers.extend(
            {"clip_index": clip_index, **keyframe}
            for keyframe in _select_kmb_keyframes(roots, quats, count)
        )
        foot_contact_changes.extend(
            {"clip_index": clip_index, **change}
            for change in _foot_contact_changes(clip.get("foot_contacts"))
        )
    markers.sort(key=lambda item: (-float(item["saliency"]), int(item["clip_index"]), int(item["frame"])))
    foot_contact_changes.sort(
        key=lambda item: (int(item["duration_frames"]), int(item["clip_index"]), str(item["foot"]), int(item["frame"]))
    )
    return {"keyframes": markers, "foot_contact_changes": foot_contact_changes}


def _analysis_options(request: dict[str, Any]) -> dict[str, Any] | None:
    options = request.get("analysis_option")
    if not isinstance(options, dict):
        return None
    keyframes = options.get("keyframes", {})
    if keyframes is False or (isinstance(keyframes, dict) and keyframes.get("enabled") is False):
        return None
    return options


def _keyframe_count(options: dict[str, Any], frames: int) -> int:
    keyframes = options.get("keyframes") if isinstance(options.get("keyframes"), dict) else {}
    value = options.get("keyframe_count", keyframes.get("count", keyframes.get("max_count", _DEFAULT_KEYFRAME_COUNT)))
    try:
        requested = int(value)
    except (TypeError, ValueError):
        requested = _DEFAULT_KEYFRAME_COUNT
    return max(1, min(frames, requested))


def _select_position_keyframes(joints: np.ndarray, root_index: int, count: int) -> list[dict[str, int | float]]:
    root_index = root_index if 0 <= root_index < joints.shape[1] else 0
    root = joints[:, root_index, :3]
    relative = joints[:, :, :3] - root[:, None, :]
    # Root X/Z are global placement, not pose semantics. Keep height because
    # it can represent a meaningful state change (jump, crouch, etc.).
    root_features = root[:, 1:2]
    return _select_curve_keyframes(
        _normalise_channels(np.concatenate((root_features, relative.reshape(len(joints), -1)), axis=1)), count
    )


def _select_kmb_keyframes(roots: np.ndarray, quats: np.ndarray, count: int) -> list[dict[str, int | float]]:
    norms = np.linalg.norm(quats, axis=2, keepdims=True)
    if not np.isfinite(roots).all() or not np.isfinite(quats).all() or np.any(norms < 1e-6):
        raise ValueError("KMB motion contains invalid root positions or local rotations.")
    rotations = quats / norms
    for frame in range(1, len(rotations)):
        flip = np.sum(rotations[frame] * rotations[frame - 1], axis=1) < 0.0
        rotations[frame, flip] *= -1.0
    # Root yaw is a separate planar heading signal. Remove it first, then use
    # the remaining root orientation (tilt around X/Z) as the root feature.
    root_rotation_features = _remove_root_yaw(rotations[:, 0, :])
    # Ignore global Root X/Z placement so identical poses at different planar
    # locations do not become salient solely because of their world position.
    root_features = roots[:, 1:2]
    joint_rotation_features = rotations[:, 1:, :].reshape(len(roots), -1)
    values = np.concatenate((root_features, root_rotation_features, joint_rotation_features), axis=1)
    return _select_curve_keyframes(_normalise_channels(values), count)


def _remove_root_yaw(quats: np.ndarray) -> np.ndarray:
    """Return root quaternions with their planar Y rotation removed."""
    forward_x = 2.0 * (quats[:, 1] * quats[:, 3] + quats[:, 0] * quats[:, 2])
    forward_z = 1.0 - 2.0 * (quats[:, 1] ** 2 + quats[:, 2] ** 2)
    half_yaw = 0.5 * np.arctan2(forward_x, forward_z)
    yaw_inverse = np.stack(
        (np.cos(half_yaw), np.zeros_like(half_yaw), -np.sin(half_yaw), np.zeros_like(half_yaw)),
        axis=-1,
    )
    aw, ax, ay, az = np.moveaxis(yaw_inverse, -1, 0)
    bw, bx, by, bz = np.moveaxis(quats, -1, 0)
    stripped = np.stack(
        (aw * bw - ax * bx - ay * by - az * bz,
         aw * bx + ax * bw + ay * bz - az * by,
         aw * by - ax * bz + ay * bw + az * bx,
         aw * bz + ax * by - ay * bx + az * bw),
        axis=-1,
    )
    stripped[stripped[:, 0] < 0.0] *= -1.0
    stripped[np.abs(stripped) < 1e-6] = 0.0
    return stripped


def _normalise_channels(values: np.ndarray) -> np.ndarray:
    scale = np.ptp(values, axis=0)
    return values / np.maximum(scale, 1e-4)


def _select_curve_keyframes(values: np.ndarray, count: int) -> list[dict[str, int | float]]:
    frames = len(values)
    if count <= 1 or frames <= 1:
        return [{"frame": 0, "saliency": 0.0}]
    selected = {0, frames - 1}
    saliency = {0: 0.0, frames - 1: 0.0}
    while len(selected) < count:
        best_frame = -1
        best_error = -1.0
        ordered = sorted(selected)
        for frame in range(1, frames - 1):
            if frame in selected:
                continue
            left = max(item for item in ordered if item < frame)
            right = min(item for item in ordered if item > frame)
            t = (frame - left) / float(right - left)
            estimated = values[left] + (values[right] - values[left]) * t
            error = float(np.linalg.norm(values[frame] - estimated))
            if error > best_error:
                best_error = error
                best_frame = frame
        if best_frame < 0:
            break
        if best_error <= 1e-8:
            candidates = [frame for frame in range(1, frames - 1) if frame not in selected]
            if not candidates:
                break
            best_frame = max(candidates, key=lambda frame: min(abs(frame - item) for item in selected))
        saliency[best_frame] = best_error
        selected.add(best_frame)
    return [
        {"frame": frame, "saliency": round(float(saliency[frame]), 6)}
        for frame in sorted(selected, key=lambda frame: (-saliency[frame], frame))
    ]


def _foot_contact_changes(contacts: Any) -> list[dict[str, int | bool | str]]:
    """Return debounced left/right contact state changes."""
    values = np.asarray(contacts) if contacts is not None else np.empty((0, 0))
    if values.ndim != 2 or values.shape[0] < 2:
        return []
    if values.shape[1] == 4:
        feet = (("left", values[:, :2]), ("right", values[:, 2:]))
    elif values.shape[1] == 6:
        feet = (("left", values[:, :3]), ("right", values[:, 3:]))
    else:
        return []

    changes: list[dict[str, int | bool | str]] = []
    for foot, channels in feet:
        states = _debounce_contact_states(np.any(channels >= 0.5, axis=1))
        switch_frames = np.flatnonzero(states[1:] != states[:-1]) + 1
        boundaries = np.append(switch_frames[1:], len(states))
        for index, frame in enumerate(switch_frames):
            contact = bool(states[frame])
            changes.append(
                {
                    "foot": foot,
                    "frame": int(frame),
                    "contact": contact,
                    "transition": "contact_start" if contact else "contact_end",
                    "duration_frames": int(boundaries[index] - frame),
                }
            )
    return sorted(changes, key=lambda item: (int(item["duration_frames"]), str(item["foot"]), int(item["frame"])))


def _debounce_contact_states(states: np.ndarray) -> np.ndarray:
    """Confirm a reversed contact state only after it persists for four frames."""
    filtered = np.asarray(states, dtype=bool).copy()
    if len(filtered) < 2:
        return filtered

    stable = bool(filtered[0])
    candidate_start = -1
    filtered[:] = stable
    for frame in range(1, len(states)):
        value = bool(states[frame])
        if value == stable:
            candidate_start = -1
        elif candidate_start < 0:
            candidate_start = frame
        elif frame - candidate_start + 1 >= _MIN_CONTACT_STATE_FRAMES:
            stable = value
            filtered[candidate_start : frame + 1] = stable
            candidate_start = -1
        filtered[frame] = stable
    return filtered
