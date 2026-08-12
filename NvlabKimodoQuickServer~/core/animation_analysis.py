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
        "algorithm": "motion-quality-v2",
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
        "hints": _build_hints(results),
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
    keyframes = _select_representative_keyframes(quats, fps, options)
    issues = _select_issues(severity, continuity_score, acceleration_score, pose_score, invalid_score, fps, options)
    keyframe_options = options.get("keyframes", {})
    if keyframe_options is True:
        keyframe_options = {}
    if not isinstance(keyframe_options, dict):
        keyframe_options = {}
    keyframe_budget = max(1, min(24, int(keyframe_options.get("max_count", 8)), frames))
    worst_count = max(1, int(np.ceil(frames * 0.1)))
    quality = 1.0 - float(np.mean(np.sort(severity)[-worst_count:]))
    return {
        "frame_count": int(frames),
        "fps": float(fps),
        "quality_score": round(float(np.clip(quality, 0.0, 1.0)), 4),
        "keyframes": keyframes,
        "issues": issues,
        "keyframe_budget_reached": len(keyframes) >= keyframe_budget and keyframe_budget < frames,
    }


def _build_hints(results: list[dict[str, Any]]) -> list[str]:
    hints: list[str] = []
    issues = [issue for result in results for issue in result["issues"]]
    reasons = {reason for issue in issues for reason in issue["reasons"]}
    if "continuity" in reasons or "pose" in reasons:
        hints.append("motion discontinuity may exist; inspect the reported issue frames and nearby playback.")
    if "acceleration" in reasons:
        hints.append("abrupt root-motion acceleration may exist; inspect the reported issue frames and nearby playback.")
    if "invalid_pose" in reasons:
        hints.append("invalid pose data was detected; regenerate or repair the affected frames.")
    if any(result["keyframe_budget_reached"] and result["frame_count"] > len(result["keyframes"]) for result in results):
        hints.append("keyframe budget was reached; increase keyframes.max_count if the selected poses do not cover the motion.")
    return hints


def _select_representative_keyframes(
    values: np.ndarray,
    fps: float,
    options: dict[str, Any],
    root_index: int = 0,
    quaternions: bool = True,
) -> list[dict[str, Any]]:
    """Select real frames greedily by their residual outside the selected pose span.

    Root translation and the root joint rotation are deliberately excluded.  The
    span therefore measures only non-root pose reconstruction, not locomotion.
    """
    frames = len(values)
    keyframe_options = options.get("keyframes", {})
    if keyframe_options is True:
        keyframe_options = {}
    if not isinstance(keyframe_options, dict):
        keyframe_options = {}
    maximum = max(1, min(24, int(keyframe_options.get("max_count", 4)), frames))
    # q and -q represent the same rotation. Root translation is removed by
    # callers; exclude the root joint so heading never affects the basis.
    pose_values = np.delete(np.array(values, dtype=np.float64, copy=True), root_index, axis=1)
    if pose_values.shape[1] == 0:
        pose_values = np.array(values, dtype=np.float64, copy=True)
    if quaternions:
        pose_values *= np.where(pose_values[..., 3:4] < 0.0, -1.0, 1.0)
    flattened = pose_values.reshape(frames, -1)
    centered = flattened - np.mean(flattened, axis=0, keepdims=True)
    residual = np.array(centered, copy=True)
    selected: list[int] = []
    for _ in range(maximum):
        norms = np.einsum("ij,ij->i", residual, residual)
        frame = int(np.argmax(norms))
        if norms[frame] <= 1e-12:
            break
        selected.append(frame)
        vector = residual[frame]
        vector /= np.linalg.norm(vector)
        residual -= np.outer(residual @ vector, vector)
    if not selected:
        selected = [0]
    total_energy = float(np.einsum("ij,ij->", centered, centered))
    residual_energy = float(np.einsum("ij,ij->", residual, residual))
    explained = 1.0 if total_energy <= 1e-12 else np.clip(1.0 - residual_energy / total_energy, 0.0, 1.0)
    return [
        {
            "frame": int(frame),
            "time": round(float(frame / fps), 6),
            "role": "representative_basis",
            "non_root_subspace_explained_variance": round(float(explained), 4),
        }
        for frame in selected
    ]


def _select_issues(
    severity: np.ndarray,
    continuity: np.ndarray,
    acceleration: np.ndarray,
    pose: np.ndarray,
    invalid: np.ndarray,
    fps: float,
    options: dict[str, Any],
) -> list[dict[str, Any]]:
    minimum = 0.55
    min_gap = max(1, int(round(0.15 * fps)))
    issue_options = options.get("issues", {})
    if issue_options is True:
        issue_options = {}
    if not isinstance(issue_options, dict):
        issue_options = {}
    maximum = max(1, min(64, int(issue_options.get("max_count", 8))))
    candidates = sorted((int(frame) for frame in np.where(severity >= minimum)[0]), key=lambda frame: float(severity[frame]), reverse=True)
    selected: list[int] = []
    for frame in candidates:
        if all(abs(frame - other) >= min_gap for other in selected):
            selected.append(frame)
        if len(selected) >= maximum:
            break
    result: list[dict[str, Any]] = []
    for rank, frame in enumerate(selected, start=1):
        reasons = []
        if invalid[frame] > 0.0:
            reasons.append("invalid_pose")
        if continuity[frame] >= 0.55:
            reasons.append("continuity")
        if acceleration[frame] >= 0.55:
            reasons.append("acceleration")
        if pose[frame] >= 0.55:
            reasons.append("pose")
        result.append({
            "rank": rank,
            "from_frame": max(0, frame - 1),
            "frame": frame,
            "score": round(float(severity[frame]), 4),
            "reasons": reasons or ["motion_outlier"],
        })
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

    relative_joints = joints - joints[:, root_index:root_index + 1, :]
    return _select_representative_keyframes(relative_joints, fps, options, root_index, quaternions=False)


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
