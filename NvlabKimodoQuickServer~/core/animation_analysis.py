"""Optional, deterministic post-generation motion analysis."""

from __future__ import annotations

from typing import Any

import numpy as np


def build_generation_analysis(request: dict[str, Any], model: Any, output: dict[str, Any]) -> dict[str, Any] | None:
    options = request.get("analysis_option")
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


def build_clip_constraint_analysis(clips: list[dict[str, Any]], options: dict[str, Any]) -> dict[str, Any]:
    """Analyze KMB clip slices without loading a motion model.

    Each item supplies ``root_positions``, ``local_rot_quats`` and ``fps``.  The
    caller owns KMB parsing so this module stays independent from the protocol
    implementation.
    """
    if not clips:
        raise ValueError("analysis_only requires at least one ClipConstraint.")

    results = [_analyze_clip(index, clip, options) for index, clip in enumerate(clips)]
    total_frames = sum(item["frame_count"] for item in results)
    weighted_quality = sum(item["quality_score"] * item["frame_count"] for item in results)
    return {
        "algorithm": "motion-quality-v1",
        "quality_score": round(float(weighted_quality / max(1, total_frames)), 4),
        "keyframes": [
            {"clip_index": index, **keyframe}
            for index, item in enumerate(results)
            for keyframe in item["keyframes"]
        ],
        "issues": [
            {"clip_index": index, **issue}
            for index, item in enumerate(results)
            for issue in item["issues"]
        ],
        "clips": results,
    }


def _analyze_clip(index: int, clip: dict[str, Any], options: dict[str, Any]) -> dict[str, Any]:
    roots = np.asarray(clip["root_positions"], dtype=np.float64)
    quats = np.asarray(clip["local_rot_quats"], dtype=np.float64)
    fps = float(clip["fps"])
    if roots.ndim != 2 or roots.shape[1] != 3 or len(roots) < 1:
        raise ValueError(f"clip {index} root_positions must have shape [frames,3].")
    if quats.ndim != 3 or quats.shape[0] != len(roots) or quats.shape[2] != 4:
        raise ValueError(f"clip {index} local_rot_quats must have shape [frames,joints,4].")
    if not np.isfinite(fps) or fps <= 0.0:
        raise ValueError(f"clip {index} fps must be finite and positive.")

    frames = len(roots)
    valid_root = np.isfinite(roots).all(axis=1)
    valid_quats = np.isfinite(quats).all(axis=(1, 2))
    quat_norm = np.linalg.norm(quats, axis=2)
    valid_quats &= np.all(quat_norm >= 1e-6, axis=1)
    safe_quats = quats / np.maximum(quat_norm[..., None], 1e-6)

    planar_velocity = np.diff(roots[:, (0, 2)], axis=0, prepend=roots[:1, (0, 2)]) * fps
    speed = np.linalg.norm(planar_velocity, axis=1)
    acceleration = np.abs(np.diff(speed, prepend=speed[:1])) * fps
    jerk = np.abs(np.diff(acceleration, prepend=acceleration[:1])) * fps
    dot = np.sum(safe_quats[1:] * safe_quats[:-1], axis=2)
    angular_delta = 2.0 * np.arccos(np.clip(np.abs(dot), 0.0, 1.0))
    angular_speed = np.concatenate((np.zeros(1), np.mean(angular_delta, axis=1) * fps))
    angular_acceleration = np.abs(np.diff(angular_speed, prepend=angular_speed[:1])) * fps

    velocity_score = _normalize(speed)
    acceleration_score = np.maximum(_normalize(acceleration), _normalize(jerk))
    pose_score = np.maximum(_normalize(angular_speed), _normalize(angular_acceleration))
    invalid_score = (~(valid_root & valid_quats)).astype(np.float64)
    continuity_score = np.maximum(acceleration_score, pose_score)
    severity = np.clip(
        0.45 * continuity_score + 0.35 * acceleration_score + 0.20 * pose_score + invalid_score,
        0.0,
        1.0,
    )
    saliency = np.clip(0.45 * acceleration_score + 0.35 * pose_score + 0.20 * velocity_score, 0.0, 1.0)
    keyframes = _select_signal_keyframes(saliency, fps, options)
    issues = _select_issues(severity, continuity_score, acceleration_score, pose_score, invalid_score, fps)
    worst_count = max(1, int(np.ceil(frames * 0.1)))
    quality = 1.0 - float(np.mean(np.sort(severity)[-worst_count:]))
    return {
        "frame_count": int(frames),
        "fps": float(fps),
        "quality_score": round(float(np.clip(quality, 0.0, 1.0)), 4),
        "keyframes": keyframes,
        "issues": issues,
    }


def _select_signal_keyframes(signal: np.ndarray, fps: float, options: dict[str, Any]) -> list[dict[str, Any]]:
    frames = len(signal)
    keyframe_options = options.get("keyframes", {})
    if keyframe_options is True:
        keyframe_options = {}
    if not isinstance(keyframe_options, dict):
        keyframe_options = {}
    maximum = max(1, min(24, int(keyframe_options.get("max_count", 8)), frames))
    min_gap = max(1, int(round(float(keyframe_options.get("min_interval_seconds", 0.35)) * fps)))
    selected = {0, frames - 1}
    while len(selected) < maximum:
        candidates = [frame for frame in range(1, frames - 1) if frame not in selected and min(abs(frame - other) for other in selected) >= min_gap]
        if not candidates:
            break
        selected.add(max(candidates, key=lambda frame: float(signal[frame])))
    return [
        {"frame": int(frame), "time": round(float(frame / fps), 6), "saliency": round(float(signal[frame]), 4)}
        for frame in sorted(selected)
    ]


def _select_issues(
    severity: np.ndarray,
    continuity: np.ndarray,
    acceleration: np.ndarray,
    pose: np.ndarray,
    invalid: np.ndarray,
    fps: float,
) -> list[dict[str, Any]]:
    minimum = 0.55
    min_gap = max(1, int(round(0.15 * fps)))
    candidates = sorted((int(frame) for frame in np.where(severity >= minimum)[0]), key=lambda frame: float(severity[frame]), reverse=True)
    selected: list[int] = []
    for frame in candidates:
        if all(abs(frame - other) >= min_gap for other in selected):
            selected.append(frame)
    result: list[dict[str, Any]] = []
    for frame in sorted(selected):
        reasons = []
        if invalid[frame] > 0.0:
            reasons.append("invalid_pose")
        if continuity[frame] >= 0.55:
            reasons.append("continuity")
        if acceleration[frame] >= 0.55:
            reasons.append("acceleration")
        if pose[frame] >= 0.55:
            reasons.append("pose")
        result.append({"frame": frame, "score": round(float(severity[frame]), 4), "reasons": reasons or ["motion_outlier"]})
    return result


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
