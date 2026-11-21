"""FlowHMR SMPL-H output adapter for Kimodo KMB1.

FlowHMR emits local axis-angle rotations in the SMPL-H 52-joint order.  KMB1
stores the same local rotations as WXYZ quaternions plus a root trajectory.
The adapter deliberately contains no Unity or estimator-specific dependencies.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import numpy as np

from core.protocol.kmb_motion import KmbMotion, encode_kmb1


SMPLH_JOINT_NAMES = (
    "pelvis", "left_hip", "right_hip", "spine1", "left_knee", "right_knee",
    "spine2", "left_ankle", "right_ankle", "spine3", "left_foot", "right_foot",
    "neck", "left_collar", "right_collar", "head", "left_shoulder", "right_shoulder",
    "left_elbow", "right_elbow", "left_wrist", "right_wrist",
    "left_index1", "left_index2", "left_index3", "left_middle1", "left_middle2",
    "left_middle3", "left_pinky1", "left_pinky2", "left_pinky3", "left_ring1",
    "left_ring2", "left_ring3", "left_thumb1", "left_thumb2", "left_thumb3",
    "right_index1", "right_index2", "right_index3", "right_middle1", "right_middle2",
    "right_middle3", "right_pinky1", "right_pinky2", "right_pinky3", "right_ring1",
    "right_ring2", "right_ring3", "right_thumb1", "right_thumb2", "right_thumb3",
)

SMPLH_JOINT_PARENTS = (
    -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19,
    20, 22, 23, 20, 25, 26, 20, 28, 29, 20, 31, 32, 20, 34, 35,
    21, 37, 38, 21, 40, 41, 21, 43, 44, 21, 46, 47, 21, 49, 50,
)


def _axis_angle_to_wxyz(axis_angle: np.ndarray) -> np.ndarray:
    """Convert axis-angle radians to normalized WXYZ quaternions."""
    values = np.asarray(axis_angle, dtype=np.float64)
    if values.shape[-1] != 3:
        raise ValueError(f"Expected axis-angle triples, got shape {values.shape}.")
    angles = np.linalg.norm(values, axis=-1, keepdims=True)
    half = angles * 0.5
    safe = np.full_like(angles, 0.5)
    np.divide(np.sin(half), angles, out=safe, where=angles > 1e-8)
    xyz = values * safe
    w = np.cos(half)
    result = np.concatenate((w, xyz), axis=-1)
    norms = np.linalg.norm(result, axis=-1, keepdims=True)
    result /= np.maximum(norms, 1e-12)
    return result.astype(np.float32)


def flowhmr_npz_to_motion(npz_path: str | Path, frame_indices: list[int] | None = None) -> KmbMotion:
    """Read the official FlowHMR NPZ output and build a SMPL-H KMB motion."""
    path = Path(npz_path).expanduser().resolve(strict=True)
    with np.load(path, allow_pickle=False) as data:
        if "poses" not in data or "trans" not in data:
            raise ValueError("FlowHMR output must contain 'poses' and 'trans'.")
        poses = np.asarray(data["poses"], dtype=np.float64)
        roots = np.asarray(data["trans"], dtype=np.float32)
        fps_value = data["mocap_framerate"] if "mocap_framerate" in data else data.get("fps", 30.0)
        fps = float(np.asarray(fps_value).reshape(-1)[0])
        contacts = np.asarray(data["foot_contacts"], dtype=np.float32) if "foot_contacts" in data else None

    if poses.ndim == 2 and poses.shape[1] == 156:
        poses = poses.reshape(poses.shape[0], 52, 3)
    if poses.ndim != 3 or poses.shape[1:] != (52, 3):
        raise ValueError(f"FlowHMR poses must have shape [frames,52,3], got {poses.shape}.")
    if roots.shape != (poses.shape[0], 3):
        raise ValueError(f"FlowHMR trans must have shape [{poses.shape[0]},3], got {roots.shape}.")
    if not np.isfinite(poses).all() or not np.isfinite(roots).all():
        raise ValueError("FlowHMR output contains non-finite pose values.")
    if not np.isfinite(fps) or fps <= 0:
        raise ValueError(f"FlowHMR output has invalid frame rate: {fps}.")
    if contacts is not None:
        if contacts.shape != (poses.shape[0], 4):
            raise ValueError(f"FlowHMR foot_contacts must have shape [{poses.shape[0]},4], got {contacts.shape}.")
        if not np.isfinite(contacts).all():
            raise ValueError("FlowHMR foot_contacts contains non-finite values.")
        contacts = (contacts >= 0.5).astype(np.uint8)
    if frame_indices is not None:
        indices = np.asarray(frame_indices, dtype=np.int64)
        if indices.ndim != 1 or indices.size == 0 or np.any(indices < 0) or np.any(indices >= poses.shape[0]):
            raise ValueError("frame_indices must be non-empty valid FlowHMR frame indices.")
        poses = poses[indices]
        roots = roots[indices]
        if contacts is not None:
            contacts = contacts[indices]

    return KmbMotion(
        payload=b"",
        model_name="smplh",
        fps=fps,
        joint_names=SMPLH_JOINT_NAMES,
        joint_parents=SMPLH_JOINT_PARENTS,
        root_positions=roots,
        local_rot_quats=_axis_angle_to_wxyz(poses),
        foot_contacts=contacts,
    )


def flowhmr_npz_to_kmb(npz_path: str | Path, frame_indices: list[int] | None = None) -> tuple[bytes, dict[str, Any]]:
    motion = flowhmr_npz_to_motion(npz_path, frame_indices=frame_indices)
    payload = encode_kmb1(motion)
    return payload, {
        "format": "kmb_v1",
        "skeleton": "smplh",
        "fps": motion.fps,
        "frame_count": motion.num_frames,
        "joint_count": len(motion.joint_names),
    }
