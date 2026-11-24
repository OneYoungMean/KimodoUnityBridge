"""Align VGGT camera poses to the FlowHMR SMPL-H world.

The alignment is deliberately small and explicit: both spaces are Y-up, so
we solve a similarity transform with one yaw angle, one scale, and a
translation.  The observed person boxes provide the image-space constraints.
The resulting actor space uses the SMPL-H/SMPL-X rest-pose pelvis XZ origin.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np


@dataclass(frozen=True)
class CameraWorldAlignment:
    """Similarity transform mapping actor coordinates into VGGT world."""

    scale: float
    yaw_radians: float
    translation: np.ndarray
    median_observation_error_px: float
    frames_used: int
    observation_type: str = "bbox"

    @property
    def actor_to_vggt(self) -> np.ndarray:
        c = float(np.cos(self.yaw_radians))
        s = float(np.sin(self.yaw_radians))
        rotation = np.asarray(((c, 0.0, -s), (0.0, 1.0, 0.0), (s, 0.0, c)), dtype=np.float64)
        result = np.eye(4, dtype=np.float64)
        result[:3, :3] = float(self.scale) * rotation
        result[:3, 3] = np.asarray(self.translation, dtype=np.float64)
        return result

    @property
    def vggt_to_actor(self) -> np.ndarray:
        """Return the inverse similarity transform."""
        return np.linalg.inv(self.actor_to_vggt)


def _reconstruct_smplh_joints(data: Any) -> np.ndarray:
    if "local_joints_positions" not in data:
        raise ValueError("FlowHMR output has no local_joints_positions for camera alignment.")
    joints = np.asarray(data["local_joints_positions"], dtype=np.float64)
    if joints.ndim != 3 or joints.shape[2] != 3:
        raise ValueError(f"FlowHMR local_joints_positions must be [frames,joints,3], got {joints.shape}.")
    if "smooth_root_pos" not in data:
        raise ValueError("FlowHMR output has no smooth_root_pos for camera alignment.")
    smooth = np.asarray(data["smooth_root_pos"], dtype=np.float64)
    if smooth.shape != (joints.shape[0], 3):
        raise ValueError("FlowHMR smooth_root_pos does not match local_joints_positions.")
    joints = joints.copy()
    joints[:, :, 0] += smooth[:, None, 0]
    joints[:, :, 2] += smooth[:, None, 2]
    # FlowHMR's exported SMPL-H parameters use the same Y-up basis after the
    # mandatory 180-degree root conversion in save_fbx.
    joints = joints @ np.diag((-1.0, 1.0, -1.0))
    # FlowHMR stores trans as pelvis_world - pelvis_rest. In the saved NPZ,
    # trans has already been rotated 180 degrees around Y by save_fbx, whereas
    # pelvis_world remains in the pre-export basis. Undo that conversion to
    # recover the rest-pose pelvis reference instead of treating the first
    # video frame as the origin. The requested origin is XZ-only; preserve Y.
    if "pelvis_world" not in data or "trans" not in data:
        raise ValueError("FlowHMR output needs pelvis_world and trans to recover the SMPL-H rest-pose pelvis origin.")
    pelvis_world = np.asarray(data["pelvis_world"], dtype=np.float64)
    saved_trans = np.asarray(data["trans"], dtype=np.float64)
    if pelvis_world.shape != (joints.shape[0], 3) or saved_trans.shape != (joints.shape[0], 3):
        raise ValueError("FlowHMR pelvis_world/trans do not match local_joints_positions.")
    export_y180 = np.diag((-1.0, 1.0, -1.0))
    recovered = pelvis_world - saved_trans @ export_y180
    if not np.isfinite(recovered).all():
        raise ValueError("FlowHMR rest-pose pelvis recovery contains non-finite values.")
    # Convert the recovered rest pelvis into the same post-export basis as the
    # joints, then zero only its X/Z projection. Keep model rest height in Y.
    rest_pelvis = np.median(recovered, axis=0) @ export_y180
    joints[:, :, 0] -= rest_pelvis[0]
    joints[:, :, 2] -= rest_pelvis[2]
    if not np.isfinite(joints).all():
        raise ValueError("FlowHMR joint positions contain non-finite values.")
    return joints


def _project(points_actor: np.ndarray, camera_rt: np.ndarray, intrinsics: np.ndarray,
             scale: float, yaw: float, translation: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    c = float(np.cos(yaw))
    s = float(np.sin(yaw))
    rotation = np.asarray(((c, 0.0, -s), (0.0, 1.0, 0.0), (s, 0.0, c)), dtype=np.float64)
    world = scale * (points_actor @ rotation.T) + translation[None, None, :]
    camera = np.einsum("tij,tkj->tki", camera_rt[:, :3, :3], world) + camera_rt[:, None, :3, 3]
    depth = camera[:, :, 2]
    safe_depth = np.maximum(depth, 1e-5)
    u = intrinsics[:, None, 0, 0] * camera[:, :, 0] / safe_depth + intrinsics[:, None, 0, 2]
    v = intrinsics[:, None, 1, 1] * camera[:, :, 1] / safe_depth + intrinsics[:, None, 1, 2]
    return np.stack((u, v), axis=-1), depth


_OPENCV_TO_YUP = np.diag((1.0, -1.0, -1.0, 1.0))
_ACTOR_TO_UNITY_WORLD_BASIS = np.diag((-1.0, 1.0, 1.0))
_UNITY_TO_OPENCV_CAMERA_BASIS = np.diag((1.0, -1.0, 1.0))


def transform_camera_track_actor_to_unity(
        actor_positions: np.ndarray, actor_rotations: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Convert an actor-space camera pose to Unity world and local-camera bases.

    Actor positions are mirrored on X to match the Unity motion import. Camera
    rotations use separate world and camera-local basis changes: actor world to
    Unity world is an X reflection, while OpenCV camera Y-down to Unity camera
    Y-up is a Y reflection. Applying both reflections around the rotation
    matrix preserves a proper rotation that can be represented by a quaternion.
    """
    positions = np.asarray(actor_positions, dtype=np.float64)
    rotations = np.asarray(actor_rotations, dtype=np.float64)
    if positions.ndim != 2 or positions.shape[1] != 3:
        raise ValueError(f"Camera positions must have shape [frames,3], got {positions.shape}.")
    if rotations.shape != (positions.shape[0], 3, 3):
        raise ValueError(
            f"Camera rotations must have shape [{positions.shape[0]},3,3], got {rotations.shape}.")

    unity_positions = positions @ _ACTOR_TO_UNITY_WORLD_BASIS.T
    unity_rotations = (
        _ACTOR_TO_UNITY_WORLD_BASIS[None]
        @ rotations
        @ _UNITY_TO_OPENCV_CAMERA_BASIS[None]
    )
    if not np.isfinite(unity_positions).all() or not np.isfinite(unity_rotations).all():
        raise ValueError("Camera track contains non-finite values after Unity basis conversion.")
    return unity_positions.astype(np.float32), unity_rotations


def _load_yup_camera_rt(camera_data: Any) -> np.ndarray:
    """Load camera-from-Y-up-world RT from a FlowHMR camera cache.

    ``camera_RT_yup`` is already in the actor/SMPL Y-up basis.  Older or
    lower-level caches expose raw VGGT OpenCV camera-from-world RT instead.
    The latter uses camera Y-down/Z-forward axes, so the same basis change as
    ``runtime_vggt_camera._vggt_opencv_to_yup_rt`` is required before it can
    project the Y-up FlowHMR joints.
    """
    if "camera_RT_yup" in camera_data.files:
        camera_rt = np.asarray(camera_data["camera_RT_yup"], dtype=np.float64)
    elif "RT_vggt" in camera_data.files:
        camera_rt = np.asarray(camera_data["RT_vggt"], dtype=np.float64) @ _OPENCV_TO_YUP
    elif "RT" in camera_data.files or "camera_RT" in camera_data.files:
        key = "RT" if "RT" in camera_data.files else "camera_RT"
        camera_rt = np.asarray(camera_data[key], dtype=np.float64)
        camera_world = str(camera_data["camera_world"]) if "camera_world" in camera_data.files else ""
        if camera_world and "opencv" in camera_world.lower():
            camera_rt = camera_rt @ _OPENCV_TO_YUP
    else:
        raise ValueError("Camera cache has no camera_RT_yup or raw VGGT RT field.")
    if camera_rt.ndim != 3 or camera_rt.shape[1:] != (4, 4) or not np.isfinite(camera_rt).all():
        raise ValueError(f"Camera cache has invalid RT shape or values: {camera_rt.shape}.")
    return camera_rt


def _select_frames(bbox: np.ndarray, image_size_hw: np.ndarray) -> np.ndarray:
    height, width = (float(image_size_hw[0]), float(image_size_hw[1]))
    if bbox.ndim != 2 or bbox.shape[1] != 4:
        raise ValueError(f"FlowHMR bbox must be [frames,4], got {bbox.shape}.")
    widths = bbox[:, 2] - bbox[:, 0]
    heights = bbox[:, 3] - bbox[:, 1]
    finite = np.isfinite(bbox).all(axis=1)
    # A full-frame fallback is not a person observation and cannot calibrate
    # camera distance.  Fail clearly instead of returning a guessed transform.
    valid = (finite & (widths > 2.0) & (heights > 2.0) &
             (widths < width * 0.98) & (heights < height * 0.98) &
             (bbox[:, 0] >= -2.0) & (bbox[:, 1] >= -2.0) &
             (bbox[:, 2] <= width + 2.0) & (bbox[:, 3] <= height + 2.0))
    indices = np.flatnonzero(valid)
    if indices.size < 4:
        raise ValueError("At least four non-full-frame person boxes are required for camera alignment.")
    if indices.size > 64:
        indices = indices[np.linspace(0, indices.size - 1, 64).round().astype(np.int64)]
    return indices


_MHR_TO_SMPLH = (
    (5, 16), (6, 17),       # shoulders
    (7, 18), (8, 19),       # elbows
    (62, 20), (41, 21),     # wrists
    (9, 1), (10, 2),        # hips
    (11, 4), (12, 5),       # knees
    (13, 7), (14, 8),       # ankles
    (15, 10), (18, 11),     # feet/toes
    (69, 12),               # neck
)


def _select_keypoint_observations(keypoints: np.ndarray, actor_joints: np.ndarray,
                                  image_size_hw: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    if keypoints.ndim != 3 or keypoints.shape[2] != 2 or keypoints.shape[1] < 70:
        raise ValueError(f"Camera keypoints must be [frames,70,2], got {keypoints.shape}.")
    obs = np.stack([keypoints[:, mhr] for mhr, _ in _MHR_TO_SMPLH], axis=1)
    points = np.stack([actor_joints[:, smplh] for _, smplh in _MHR_TO_SMPLH], axis=1)
    height, width = (float(image_size_hw[0]), float(image_size_hw[1]))
    valid = (np.isfinite(obs).all(axis=(1, 2)) &
             (obs[:, :, 0].min(axis=1) >= -2.0) &
             (obs[:, :, 0].max(axis=1) <= width + 2.0) &
             (obs[:, :, 1].min(axis=1) >= -2.0) &
             (obs[:, :, 1].max(axis=1) <= height + 2.0))
    indices = np.flatnonzero(valid)
    if indices.size < 4:
        raise ValueError("At least four valid keypoint frames are required for camera alignment.")
    if indices.size > 64:
        indices = indices[np.linspace(0, indices.size - 1, 64).round().astype(np.int64)]
    return points[indices], obs[indices], indices


def solve_camera_world_alignment(motion_npz_path: str, camera_npz_path: str,
                                 bbox_npz_path: str | None = None,
                                 keypoints_npz_path: str | None = None) -> CameraWorldAlignment:
    """Solve actor-pelvis to VGGT-world alignment from the generated artifacts."""
    try:
        from scipy.optimize import least_squares
    except ImportError as exc:  # pragma: no cover - only hit in an incomplete FlowHMR env
        raise RuntimeError("Camera alignment requires scipy in the FlowHMR environment.") from exc

    with np.load(motion_npz_path, allow_pickle=False) as motion_data:
        actor_joints = _reconstruct_smplh_joints(motion_data)
    with np.load(camera_npz_path, allow_pickle=False) as camera_data:
        camera_rt = _load_yup_camera_rt(camera_data)
        intrinsics = np.asarray(camera_data["K"], dtype=np.float64)
        image_size_hw = np.asarray(camera_data["image_size_hw"], dtype=np.float64).reshape(-1)
    frames = actor_joints.shape[0]
    if camera_rt.shape != (frames, 4, 4) or intrinsics.shape != (frames, 3, 3):
        raise ValueError("Motion and camera artifacts do not have matching frame counts.")
    if image_size_hw.size != 2 or np.any(image_size_hw <= 0):
        raise ValueError("Camera cache has invalid image_size_hw.")
    observation_type = "keypoints"
    if keypoints_npz_path is not None and Path(keypoints_npz_path).is_file():
        with np.load(keypoints_npz_path, allow_pickle=False) as keypoint_data:
            keypoints = np.asarray(keypoint_data["keypoints_2d"], dtype=np.float64)
        if keypoints.shape[0] != frames:
            raise ValueError("Camera keypoint and motion frame counts do not match.")
        points, observations, indices = _select_keypoint_observations(keypoints, actor_joints, image_size_hw)
    else:
        if bbox_npz_path is None:
            raise ValueError("Camera alignment requires a keypoint or bbox cache.")
        with np.load(bbox_npz_path, allow_pickle=False) as bbox_data:
            bbox = np.asarray(bbox_data["bbox"], dtype=np.float64)
        if bbox.shape[0] != frames:
            raise ValueError("Motion, camera, and bbox artifacts do not have matching frame counts.")
        indices = _select_frames(bbox, image_size_hw)
        points = actor_joints[indices]
        observations = bbox[indices]
        observation_type = "bbox"
    rt = camera_rt[indices]
    k = intrinsics[indices]
    if observation_type == "keypoints":
        observed_height = float(np.median(observations[:, :, 1].max(axis=1) - observations[:, :, 1].min(axis=1)))
    else:
        observed_height = float(np.median(observations[:, 3] - observations[:, 1]))
    body_height = float(np.median(np.max(points[:, :, 1], axis=1) - np.min(points[:, :, 1], axis=1)))
    if not np.isfinite(body_height) or body_height <= 1e-4:
        raise ValueError("FlowHMR body height is invalid for camera alignment.")
    fy = float(np.median(k[:, 1, 1]))
    initial_depth = max(0.2, fy * body_height / max(observed_height, 1.0))
    if observation_type == "keypoints":
        first_center = observations.mean(axis=1)
    else:
        first_center = (observations[:, :2] + observations[:, 2:]) * 0.5
    image_center = np.stack((k[:, 0, 2], k[:, 1, 2]), axis=-1)
    ray_offsets = (first_center - image_center) / np.stack((k[:, 0, 0], k[:, 1, 1]), axis=-1)
    actor_center = np.median(points.mean(axis=1), axis=0)

    # The ray is measured in camera coordinates.  Convert it through the
    # selected frame's camera-to-world pose before using it as the initial
    # actor-to-world translation.  With the Y-up cache this is essential: the
    # camera's forward axis is -world Z, so putting ``initial_depth`` directly
    # on world Z places the person behind the camera.
    camera_targets = np.column_stack((ray_offsets * initial_depth,
                                      np.full(len(ray_offsets), initial_depth),
                                      np.ones(len(ray_offsets))))
    camera_to_world = np.linalg.inv(rt)
    world_targets = np.einsum("tij,tj->ti", camera_to_world, camera_targets)
    target_center_world = np.median(world_targets[:, :3], axis=0)

    def residual(parameters: np.ndarray) -> np.ndarray:
        yaw = float(parameters[0])
        translation = parameters[1:4]
        # FlowHMR's SMPL-H joint positions are in model meters.  Keeping their
        # scale fixed anchors the monocular camera distance to a real unit.
        projected, depth = _project(points, rt, k, 1.0, yaw, translation)
        if observation_type == "keypoints":
            center_residual = ((projected - observations) /
                               np.asarray((image_size_hw[1], image_size_hw[0])))
            behind = np.minimum(depth, 0.0)
            depth_residual = (np.abs(behind).mean(axis=1) / max(initial_depth, 1e-3))[:, None]
            return np.concatenate((center_residual.reshape(-1), np.repeat(depth_residual, projected.shape[1], axis=1).reshape(-1)))
        predicted = np.stack((projected[:, :, 0].min(axis=1), projected[:, :, 1].min(axis=1),
                              projected[:, :, 0].max(axis=1), projected[:, :, 1].max(axis=1)), axis=1)
        obs_center = (observations[:, :2] + observations[:, 2:]) * 0.5
        pred_center = (predicted[:, :2] + predicted[:, 2:]) * 0.5
        obs_size = np.maximum(observations[:, 2:] - observations[:, :2], 2.0)
        pred_size = np.maximum(predicted[:, 2:] - predicted[:, :2], 2.0)
        center_residual = (pred_center - obs_center) / np.asarray((image_size_hw[1], image_size_hw[0]))
        size_residual = np.log(pred_size / obs_size)
        behind = np.minimum(depth, 0.0)
        depth_residual = (np.abs(behind).mean(axis=1) / max(initial_depth, 1e-3))[:, None]
        return np.concatenate((center_residual, size_residual, depth_residual), axis=1).reshape(-1)

    candidates = []
    bounds = (np.asarray((-np.pi, -100.0, -100.0, -100.0)),
              np.asarray((np.pi, 100.0, 100.0, 100.0)))
    for yaw0 in (0.0, np.pi, -np.pi / 2, np.pi / 2):
        c0 = float(np.cos(yaw0))
        s0 = float(np.sin(yaw0))
        rotation0 = np.asarray(((c0, 0.0, -s0), (0.0, 1.0, 0.0), (s0, 0.0, c0)))
        translation0 = target_center_world - rotation0 @ actor_center
        x0 = np.asarray((yaw0, *translation0), dtype=np.float64)
        # Keep the geometric initialization as a candidate because the
        # monocular scale is fixed by the metric SMPL-H body dimensions.
        candidates.append(x0)
        fitted = least_squares(
            residual, x0, bounds=bounds, loss="soft_l1", f_scale=0.02, max_nfev=2000,
        )
        if np.isfinite(fitted.x).all():
            candidates.append(fitted.x)

    def candidate_error(parameters: np.ndarray) -> tuple[float, np.ndarray, np.ndarray]:
        projected, depth = _project(points, rt, k, 1.0, float(parameters[0]), parameters[1:4])
        if observation_type == "keypoints":
            error_values = np.linalg.norm(projected - observations, axis=2).reshape(-1)
        else:
            predicted = np.stack((projected[:, :, 0].min(axis=1), projected[:, :, 1].min(axis=1),
                                  projected[:, :, 0].max(axis=1), projected[:, :, 1].max(axis=1)), axis=1)
            error_values = np.linalg.norm(((predicted[:, :2] + predicted[:, 2:]) -
                                           (observations[:, :2] + observations[:, 2:])) * 0.5, axis=1)
        score = float(np.median(error_values)) if np.isfinite(error_values).all() else float("inf")
        if np.any(depth <= 0.0):
            score = float("inf")
        return score, projected, depth

    result_x = min(candidates, key=lambda candidate: candidate_error(candidate)[0])
    best_score, projected, depth = candidate_error(result_x)
    if not np.isfinite(result_x).all() or not np.isfinite(best_score):
        raise ValueError("Camera alignment did not converge to a finite positive-depth solution.")
    yaw = float(result_x[0])
    scale = 1.0
    translation = result_x[1:4].astype(np.float64)
    if best_score > max(image_size_hw) * 0.15:
        raise ValueError(f"Camera alignment residual is too large: median_observation_error_px={best_score:.2f}")
    return CameraWorldAlignment(scale, yaw, translation, best_score, int(indices.size), observation_type)


def transform_camera_track_to_actor_world(camera_npz_path: str, alignment: CameraWorldAlignment) -> tuple[np.ndarray, np.ndarray]:
    """Return actor-world camera positions and camera-to-world rotations."""
    with np.load(camera_npz_path, allow_pickle=False) as data:
        camera_rt = _load_yup_camera_rt(data)
    camera_world = np.linalg.inv(camera_rt)
    inverse_alignment = alignment.vggt_to_actor
    camera_actor = inverse_alignment[None] @ camera_world
    # The inverse similarity's linear block is R^T / scale. Camera rotations
    # need only R^T; retaining or dividing scale here yields a non-orthogonal
    # matrix and changes the resulting orientation.
    rotation_vggt_to_actor = inverse_alignment[:3, :3] * float(alignment.scale)
    rotations = rotation_vggt_to_actor[None] @ camera_world[:, :3, :3]
    if not np.isfinite(camera_actor).all() or not np.isfinite(rotations).all():
        raise ValueError("Camera transform contains non-finite values after alignment.")
    return camera_actor[:, :3, 3].astype(np.float32), rotations.astype(np.float64)
