"""FlowHMR SMPL-H output adapter for Kimodo KMB1.

FlowHMR emits local axis-angle rotations in the SMPL-H 52-joint order.  KMB1
stores the same local rotations as WXYZ quaternions plus a root trajectory.
The adapter deliberately contains no Unity or estimator-specific dependencies.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import numpy as np

from core.camera_alignment import (
    CameraWorldAlignment,
    solve_camera_world_alignment,
    transform_camera_track_actor_to_unity,
    transform_camera_track_to_actor_world,
)
from core.protocol.kmb_motion import KmbCameraTrack, KmbMotion, encode_kmb1


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


def _rotation_matrix_to_wxyz(matrix: np.ndarray) -> np.ndarray:
    """Convert proper 3x3 matrices to normalized WXYZ quaternions."""
    values = np.asarray(matrix, dtype=np.float64)
    result = np.empty(values.shape[:-2] + (4,), dtype=np.float64)
    trace = np.trace(values, axis1=-2, axis2=-1)
    positive = trace > 0.0
    s = np.sqrt(np.maximum(trace[positive] + 1.0, 1e-12)) * 2.0
    result[positive, 0] = 0.25 * s
    result[positive, 1] = (values[positive, 2, 1] - values[positive, 1, 2]) / s
    result[positive, 2] = (values[positive, 0, 2] - values[positive, 2, 0]) / s
    result[positive, 3] = (values[positive, 1, 0] - values[positive, 0, 1]) / s
    remaining = ~positive
    for index in np.flatnonzero(remaining):
        row = values[index]
        if row[0, 0] >= row[1, 1] and row[0, 0] >= row[2, 2]:
            s = np.sqrt(max(1.0 + row[0, 0] - row[1, 1] - row[2, 2], 1e-12)) * 2.0
            result[index] = ((row[2, 1] - row[1, 2]) / s, 0.25 * s,
                             (row[0, 1] + row[1, 0]) / s, (row[0, 2] + row[2, 0]) / s)
        elif row[1, 1] >= row[2, 2]:
            s = np.sqrt(max(1.0 + row[1, 1] - row[0, 0] - row[2, 2], 1e-12)) * 2.0
            result[index] = ((row[0, 2] - row[2, 0]) / s, (row[0, 1] + row[1, 0]) / s,
                             0.25 * s, (row[1, 2] + row[2, 1]) / s)
        else:
            s = np.sqrt(max(1.0 + row[2, 2] - row[0, 0] - row[1, 1], 1e-12)) * 2.0
            result[index] = ((row[1, 0] - row[0, 1]) / s, (row[0, 2] + row[2, 0]) / s,
                             (row[1, 2] + row[2, 1]) / s, 0.25 * s)
    result /= np.maximum(np.linalg.norm(result, axis=-1, keepdims=True), 1e-12)
    return result.astype(np.float32)


def _load_camera_track(camera_npz_path: str | Path, frame_count: int, fps: float,
                       alignment: CameraWorldAlignment) -> KmbCameraTrack:
    path = Path(camera_npz_path).expanduser().resolve(strict=True)
    with np.load(path, allow_pickle=False) as data:
        if "camera_RT_yup" in data.files:
            camera_rt = np.asarray(data["camera_RT_yup"], dtype=np.float64)
        elif "RT" in data.files:
            camera_rt = np.asarray(data["RT"], dtype=np.float64)
        else:
            raise ValueError("FlowHMR camera cache has no RT/camera_RT_yup field.")
        intrinsics = np.asarray(data["K"], dtype=np.float64) if "K" in data.files else None
        image_size = np.asarray(data["image_size_hw"], dtype=np.float64) if "image_size_hw" in data.files else None
    if camera_rt.shape != (frame_count, 4, 4):
        raise ValueError(f"FlowHMR camera cache frame count {camera_rt.shape} does not match motion {frame_count}.")
    if alignment is None:
        raise ValueError("A camera-to-SMPL-X pelvis alignment is required for a world camera track.")
    actor_positions, actor_rotations = transform_camera_track_to_actor_world(str(path), alignment)
    # Convert both the actor world basis and the OpenCV camera-local basis.
    # These are distinct reflections: world X mirrors with the imported motion,
    # and camera-local Y changes from image-down to Unity-up.
    positions, rotations = transform_camera_track_actor_to_unity(actor_positions, actor_rotations)
    quaternions = _rotation_matrix_to_wxyz(rotations)
    if not np.isfinite(positions).all() or not np.isfinite(quaternions).all():
        raise ValueError("FlowHMR camera cache contains non-finite values.")
    fov_y = None
    lens_shift = None
    if intrinsics is not None and image_size is not None and intrinsics.shape == (frame_count, 3, 3) and image_size.size == 2:
        height, width = float(image_size[0]), float(image_size[1])
        fy = intrinsics[:, 1, 1]
        cx = intrinsics[:, 0, 2]
        cy = intrinsics[:, 1, 2]
        if height > 0 and width > 0 and np.isfinite(fy).all() and np.all(fy > 0):
            fov_y = (2.0 * np.arctan2(height, 2.0 * fy) * 180.0 / np.pi).astype(np.float32)
            # Unity's normalized lens shift is expressed around the image center.
            lens_shift = np.stack((0.5 - cx / width, 0.5 - cy / height), axis=-1).astype(np.float32)
    return KmbCameraTrack(float(fps), positions.astype(np.float32), quaternions, fov_y, lens_shift)


def flowhmr_npz_to_motion(npz_path: str | Path, frame_indices: list[int] | None = None,
                          camera_npz_path: str | Path | None = None,
                          bbox_npz_path: str | Path | None = None,
                          keypoints_npz_path: str | Path | None = None,
                          camera_alignment: CameraWorldAlignment | None = None,
                          global_time_seconds: list[float] | np.ndarray | None = None,
                          human_detection: list[int] | np.ndarray | None = None) -> KmbMotion:
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
    indices = None
    if frame_indices is not None:
        indices = np.asarray(frame_indices, dtype=np.int64)
        if indices.ndim != 1 or indices.size == 0 or np.any(indices < 0) or np.any(indices >= poses.shape[0]):
            raise ValueError("frame_indices must be non-empty valid FlowHMR frame indices.")
    global_times = None
    if global_time_seconds is not None:
        raw_times = np.asarray(global_time_seconds, dtype=np.float32).reshape(-1)
        if indices is not None and raw_times.size == indices.size:
            global_times = raw_times
        elif raw_times.size == poses.shape[0]:
            global_times = raw_times[indices] if indices is not None else raw_times
        else:
            global_times = raw_times
        if global_times.size != (indices.size if indices is not None else poses.shape[0]) or not np.isfinite(global_times).all() or np.any(global_times < 0) or (
                global_times.size > 1 and np.any(np.diff(global_times) <= 0)):
            raise ValueError("global_time_seconds must contain one finite, strictly increasing time per frame.")
    detections = None
    if human_detection is not None:
        raw_detections = np.asarray(human_detection).reshape(-1)
        if indices is not None and raw_detections.size == indices.size:
            detections = raw_detections
        elif raw_detections.size == poses.shape[0]:
            detections = raw_detections[indices] if indices is not None else raw_detections
        else:
            detections = raw_detections
        if detections.size != (indices.size if indices is not None else poses.shape[0]):
            raise ValueError("human_detection must contain one binary value per frame.")
        try:
            binary = np.isfinite(detections).all() and np.isin(detections, (0, 1)).all()
        except TypeError:
            binary = False
        if not binary:
            raise ValueError("human_detection must contain one binary value per frame.")
        detections = detections.astype(np.uint8)
    if camera_npz_path is not None:
        if camera_alignment is None:
            if bbox_npz_path is None:
                raise ValueError("bbox_npz_path is required to align the camera to the SMPL-X pelvis world.")
            camera_alignment = solve_camera_world_alignment(
                str(path), str(camera_npz_path),
                str(Path(bbox_npz_path).expanduser().resolve(strict=True)),
                str(Path(keypoints_npz_path).expanduser().resolve(strict=True)) if keypoints_npz_path is not None else None)
        camera_track = _load_camera_track(camera_npz_path, poses.shape[0], fps, camera_alignment)
    else:
        camera_track = None
    if frame_indices is not None:
        poses = poses[indices]
        roots = roots[indices]
        if contacts is not None:
            contacts = contacts[indices]
        if camera_track is not None:
            camera_track = KmbCameraTrack(
                camera_track.fps,
                camera_track.positions[indices],
                camera_track.rotations[indices],
                camera_track.fov_y[indices] if camera_track.fov_y is not None else None,
                camera_track.lens_shift[indices] if camera_track.lens_shift is not None else None,
            )

    return KmbMotion(
        payload=b"",
        model_name="smplh",
        fps=fps,
        joint_names=SMPLH_JOINT_NAMES,
        joint_parents=SMPLH_JOINT_PARENTS,
        root_positions=roots,
        local_rot_quats=_axis_angle_to_wxyz(poses),
        foot_contacts=contacts,
        camera_track=camera_track,
        global_time_seconds=global_times,
        human_detection=detections,
    )


def flowhmr_npz_to_kmb(npz_path: str | Path, frame_indices: list[int] | None = None,
                       camera_npz_path: str | Path | None = None,
                       bbox_npz_path: str | Path | None = None,
                       keypoints_npz_path: str | Path | None = None,
                       global_time_seconds: list[float] | np.ndarray | None = None,
                       human_detection: list[int] | np.ndarray | None = None) -> tuple[bytes, dict[str, Any]]:
    alignment = None
    if camera_npz_path is not None:
        if bbox_npz_path is None:
            raise ValueError("bbox_npz_path is required to align the camera to the SMPL-X pelvis world.")
        alignment = solve_camera_world_alignment(
            str(Path(npz_path).expanduser().resolve(strict=True)),
            str(Path(camera_npz_path).expanduser().resolve(strict=True)),
            str(Path(bbox_npz_path).expanduser().resolve(strict=True)),
            str(Path(keypoints_npz_path).expanduser().resolve(strict=True)) if keypoints_npz_path is not None else None,
        )
    motion = flowhmr_npz_to_motion(
        npz_path, frame_indices=frame_indices, camera_npz_path=camera_npz_path,
        bbox_npz_path=bbox_npz_path, keypoints_npz_path=keypoints_npz_path,
        camera_alignment=alignment,
        global_time_seconds=global_time_seconds,
        human_detection=human_detection)
    payload = encode_kmb1(motion)
    metadata = {
        "format": "kmb_v1",
        "skeleton": "smplh",
        "fps": motion.fps,
        "frame_count": motion.num_frames,
        "joint_count": len(motion.joint_names),
        "camera_track": motion.camera_track is not None,
        "global_time_seconds": motion.global_time_seconds is not None,
        "human_detection": motion.human_detection is not None,
    }
    if alignment is not None:
        metadata["camera_space"] = "smplx_pelvis_world"
        metadata["camera_alignment"] = {
            "source_space": "vggt_world",
            "target_space": "smplx_rest_pelvis_world",
            "origin_definition": "SMPL-X/SMPL-H rest-pose pelvis XZ projection at (0,0), Y-up",
            "camera_transform": "camera-to-world in target actor space",
            "scale": alignment.scale,
            "yaw_radians": alignment.yaw_radians,
            "translation": alignment.translation.tolist(),
            "actor_to_vggt_matrix": alignment.actor_to_vggt.reshape(-1).tolist(),
            "vggt_to_actor_matrix": alignment.vggt_to_actor.reshape(-1).tolist(),
            "median_observation_error_px": alignment.median_observation_error_px,
            "frames_used": alignment.frames_used,
            "observation_type": alignment.observation_type,
        }
    return payload, metadata
