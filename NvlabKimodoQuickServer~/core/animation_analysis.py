"""Optional, deterministic post-generation motion analysis."""

from __future__ import annotations

from typing import Any

import numpy as np


def build_generation_analysis(request: dict[str, Any], model: Any, output: dict[str, Any]) -> dict[str, Any] | None:
    options = request.get("analysis_options")
    if not isinstance(options, dict):
        return None
    keyframe_options = options.get("keyframes")
    if keyframe_options is False or keyframe_options is None:
        return None
    if keyframe_options is True:
        keyframe_options = {}
    if not isinstance(keyframe_options, dict) or not bool(keyframe_options.get("enabled", True)):
        return None

    try:
        fps = float(getattr(model, "fps", 30.0))
        root_index = int(getattr(getattr(model, "skeleton", None), "root_idx", 0))
        return {
            "keyframes": _select_keyframes(output, fps, keyframe_options, root_index),
            "algorithm": "motion-v1",
        }
    except Exception as exc:  # Analysis must never discard an otherwise valid animation.
        return {"keyframes": [], "warnings": [f"keyframe analysis unavailable: {exc}"], "algorithm": "motion-v1"}


def _select_keyframes(
    output: dict[str, Any],
    fps: float,
    options: dict[str, Any],
    root_index: int,
) -> list[dict[str, Any]]:
    joints = np.asarray(output["posed_joints"], dtype=np.float32)
    if joints.ndim == 4:
        joints = joints[0]
    if joints.ndim != 3 or joints.shape[0] < 1 or joints.shape[2] < 3:
        raise ValueError(f"posed_joints must have shape [frames,joints,3], got {joints.shape!r}")
    if not np.isfinite(joints).all() or not np.isfinite(fps) or fps <= 0.0:
        raise ValueError("posed_joints and fps must be finite")
    if root_index < 0 or root_index >= joints.shape[1]:
        root_index = 0

    frames = int(joints.shape[0])
    maximum = int(options.get("max_count", 8))
    maximum = max(1, min(24, maximum, frames))
    min_gap = max(1, int(round(float(options.get("min_interval_seconds", 0.35)) * fps)))

    root = joints[:, root_index, :]
    planar_velocity = np.diff(root[:, (0, 2)], axis=0, prepend=root[:1, (0, 2)]) * fps
    root_speed = np.linalg.norm(planar_velocity, axis=1)
    root_acceleration = np.abs(np.diff(root_speed, prepend=root_speed[:1])) * fps

    heading_turn = np.zeros(frames, dtype=np.float32)
    moving = root_speed[1:] > 1e-4
    if np.any(moving):
        heading = np.unwrap(np.arctan2(planar_velocity[1:, 1], planar_velocity[1:, 0]))
        heading_delta = np.abs(np.diff(heading, prepend=heading[:1])) * fps
        heading_turn[1:] = np.where(moving, heading_delta, 0.0)

    relative = joints - root[:, None, :]
    pose_velocity = np.linalg.norm(np.diff(relative, axis=0, prepend=relative[:1]), axis=2).mean(axis=1) * fps
    pose_acceleration = np.abs(np.diff(pose_velocity, prepend=pose_velocity[:1])) * fps
    contact_change = _foot_contact_changes(output, frames)

    root_score = _normalize(root_acceleration)
    turn_score = _normalize(heading_turn)
    pose_score = _normalize(pose_acceleration)
    contact_score = contact_change.astype(np.float32)
    saliency = np.clip(
        0.30 * root_score + 0.20 * turn_score + 0.40 * pose_score + 0.10 * contact_score,
        0.0,
        1.0,
    )

    selected = {0, frames - 1}
    while len(selected) < maximum:
        best_frame = None
        best_value = -1.0
        for frame in range(1, frames - 1):
            if frame in selected or min(abs(frame - existing) for existing in selected) < min_gap:
                continue
            coverage = min(abs(frame - existing) for existing in selected) / max(1, frames - 1)
            value = float(saliency[frame]) + 0.20 * coverage
            if value > best_value:
                best_frame, best_value = frame, value
        if best_frame is None:
            break
        selected.add(best_frame)

    keyframes: list[dict[str, Any]] = []
    for frame in sorted(selected):
        reasons = []
        if frame == 0:
            reasons.append("start")
        if frame == frames - 1:
            reasons.append("end")
        if root_score[frame] >= 0.55:
            reasons.append("root_acceleration")
        if turn_score[frame] >= 0.55:
            reasons.append("heading_turn")
        if pose_score[frame] >= 0.55:
            reasons.append("pose_transition")
        if contact_change[frame]:
            reasons.append("foot_contact_change")
        if not reasons:
            reasons.append("coverage")
        keyframes.append(
            {
                "frame": int(frame),
                "time": float(frame / fps),
                "saliency": round(float(saliency[frame]), 4),
                "reasons": reasons,
            }
        )
    return keyframes


def _foot_contact_changes(output: dict[str, Any], frames: int) -> np.ndarray:
    contacts = np.asarray(output.get("foot_contacts", []))
    if contacts.ndim == 3:
        contacts = contacts[0]
    if contacts.ndim != 2 or contacts.shape[0] != frames:
        return np.zeros(frames, dtype=bool)
    states = contacts >= 0.5
    return np.any(states != np.vstack((states[:1], states[:-1])), axis=1)


def _normalize(values: np.ndarray) -> np.ndarray:
    finite = np.where(np.isfinite(values), np.maximum(values, 0.0), 0.0)
    scale = float(np.percentile(finite, 95.0)) if finite.size else 0.0
    if scale <= 1e-6:
        return np.zeros_like(finite, dtype=np.float32)
    return np.clip(finite / scale, 0.0, 1.0).astype(np.float32)
