import tempfile
import unittest
from pathlib import Path

import numpy as np

from camera_alignment import (
    CameraWorldAlignment,
    _MHR_TO_SMPLH,
    _load_yup_camera_rt,
    _reconstruct_smplh_joints,
    solve_camera_world_alignment,
    transform_camera_track_actor_to_unity,
    transform_camera_track_to_actor_world,
)


class CameraAlignmentTests(unittest.TestCase):
    def test_keypoint_alignment_recovers_actor_world(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            frames = 12
            smplh_indices = [index for _, index in _MHR_TO_SMPLH]
            base = np.zeros((len(smplh_indices), 3), dtype=np.float32)
            base[:, 0] = np.linspace(-0.45, 0.45, len(smplh_indices))
            base[:, 1] = np.linspace(0.1, 1.7, len(smplh_indices))
            joints = np.zeros((frames, 52, 3), dtype=np.float32)
            for frame in range(frames):
                joints[frame, smplh_indices] = base + np.asarray((0.0, 0.0, frame * 0.02))
            joints[:, :, 0] *= -1.0
            joints[:, :, 2] *= -1.0
            np.savez(root / "motion.npz", local_joints_positions=joints,
                     smooth_root_pos=np.zeros((frames, 3), dtype=np.float32),
                     pelvis_world=np.zeros((frames, 3), dtype=np.float32),
                     trans=np.zeros((frames, 3), dtype=np.float32))

            intrinsics = np.repeat(np.asarray([[[600.0, 0.0, 320.0],
                                                [0.0, 600.0, 360.0],
                                                [0.0, 0.0, 1.0]]]), frames, axis=0)
            camera_rt = np.repeat(np.eye(4, dtype=np.float64)[None], frames, axis=0)
            actor_to_vggt = 1.2 * joints + np.asarray((0.1, -0.1, 4.0))
            projected = np.stack((intrinsics[:, None, 0, 0] * actor_to_vggt[:, :, 0] / actor_to_vggt[:, :, 2] + intrinsics[:, None, 0, 2],
                                  intrinsics[:, None, 1, 1] * actor_to_vggt[:, :, 1] / actor_to_vggt[:, :, 2] + intrinsics[:, None, 1, 2]), axis=-1)
            keypoints = np.full((frames, 70, 2), np.nan, dtype=np.float32)
            for mhr_index, smplh_index in _MHR_TO_SMPLH:
                keypoints[:, mhr_index] = projected[:, smplh_index]
            np.savez(root / "camera.npz", camera_RT_yup=camera_rt, K=intrinsics,
                     image_size_hw=np.asarray((720, 640)))
            np.savez(root / "bbox.npz", bbox=np.tile(np.asarray((0, 0, 1, 1)), (frames, 1)))
            np.savez(root / "keypoints.npz", keypoints_2d=keypoints)

            alignment = solve_camera_world_alignment(
                str(root / "motion.npz"), str(root / "camera.npz"),
                str(root / "bbox.npz"), str(root / "keypoints.npz"))

            self.assertAlmostEqual(alignment.scale, 1.0, places=2)
            self.assertLess(alignment.median_observation_error_px, 1.0)
            self.assertEqual(alignment.observation_type, "keypoints")

    def test_rest_pose_pelvis_sets_xz_origin_without_zeroing_height(self):
        frames = 3
        joints = np.zeros((frames, 52, 3), dtype=np.float32)
        joints[:, 0] = np.asarray((2.0, 1.1, -3.0))
        joints[:, 1] = np.asarray((2.2, 1.5, -2.8))
        smooth = np.zeros((frames, 3), dtype=np.float32)
        # save_fbx rotates trans by 180 degrees around Y. With a rest pelvis at
        # (2, .9, -3), each animated pelvis is recovered from world - trans.
        pelvis_world = np.tile(np.asarray((2.0, 1.1, -3.0)), (frames, 1))
        trans = np.tile(np.asarray((0.0, 0.2, 0.0)), (frames, 1))
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "motion.npz"
            np.savez(path, local_joints_positions=joints, smooth_root_pos=smooth,
                     pelvis_world=pelvis_world, trans=trans)
            with np.load(path, allow_pickle=False) as data:
                restored = _reconstruct_smplh_joints(data)
        self.assertAlmostEqual(float(restored[0, 0, 0]), 0.0, places=6)
        self.assertAlmostEqual(float(restored[0, 0, 2]), 0.0, places=6)
        self.assertAlmostEqual(float(restored[0, 0, 1]), 1.1, places=6)

    def test_camera_rotation_remains_orthonormal_and_rt_fallback_works(self):
        yaw = 0.35
        alignment = CameraWorldAlignment(2.0, yaw, np.asarray((0.3, -0.2, 4.0)), 0.0, 4)
        camera_world = np.eye(4, dtype=np.float64)
        camera_world[:3, 3] = (1.0, 1.5, -2.0)
        camera_rt = np.linalg.inv(camera_world)[None]
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "camera.npz"
            np.savez(path, RT=camera_rt)
            positions, rotations = transform_camera_track_to_actor_world(str(path), alignment)
        np.testing.assert_allclose(rotations[0].T @ rotations[0], np.eye(3), atol=1e-8)
        self.assertAlmostEqual(float(np.linalg.det(rotations[0])), 1.0, places=8)
        np.testing.assert_allclose(positions[0], alignment.vggt_to_actor[:3, :3] @ camera_world[:3, 3] + alignment.vggt_to_actor[:3, 3], atol=1e-6)

    def test_raw_opencv_rt_is_converted_to_yup_before_projection(self):
        raw = np.repeat(np.eye(4, dtype=np.float64)[None], 2, axis=0)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "camera.npz"
            np.savez(path, RT=raw, camera_world="vggt_opencv_raw")
            with np.load(path, allow_pickle=False) as data:
                converted = _load_yup_camera_rt(data)
        expected = np.repeat(np.diag((1.0, -1.0, -1.0))[None], 2, axis=0)
        np.testing.assert_allclose(converted[:, :3, :3], expected, atol=1e-12)

    def test_actor_to_unity_camera_conversion_uses_distinct_world_and_camera_bases(self):
        actor_positions = np.asarray(((1.0, 2.0, 3.0), (-4.0, 5.0, 6.0)))
        # OpenCV camera local axes: right, down, forward. This camera looks
        # toward -Z in actor world, with its image-down axis toward -Y.
        actor_rotations = np.repeat(np.diag((1.0, -1.0, -1.0))[None], 2, axis=0)

        positions, rotations = transform_camera_track_actor_to_unity(actor_positions, actor_rotations)

        np.testing.assert_allclose(positions, ((-1.0, 2.0, 3.0), (4.0, 5.0, 6.0)), atol=1e-7)
        expected_rotation = np.diag((-1.0, 1.0, -1.0))
        np.testing.assert_allclose(rotations, np.repeat(expected_rotation[None], 2, axis=0), atol=1e-12)
        np.testing.assert_allclose(rotations[:, :, 1], np.tile((0.0, 1.0, 0.0), (2, 1)), atol=1e-12)
        np.testing.assert_allclose(rotations[:, :, 2], np.tile((0.0, 0.0, -1.0), (2, 1)), atol=1e-12)
        np.testing.assert_allclose(np.linalg.det(rotations), np.ones(2), atol=1e-12)


if __name__ == "__main__":
    unittest.main()
