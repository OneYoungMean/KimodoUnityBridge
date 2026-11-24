from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

import numpy as np

from core.flowhmr_adapter import flowhmr_npz_to_kmb
from core.protocol.kmb_motion import parse_kmb1


class FlowHmrAdapterTests(unittest.TestCase):
    def test_synthetic_flowhmr_output_round_trips_as_smplh_kmb(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "motion.npz"
            poses = np.zeros((3, 52, 3), dtype=np.float32)
            poses[1, 4, 1] = np.pi / 2.0
            trans = np.asarray([[0, 0, 0], [0.1, 0, 0], [0.2, 0, 0]], dtype=np.float32)
            np.savez(path, poses=poses, trans=trans, mocap_framerate=np.asarray(30.0))
            payload, meta = flowhmr_npz_to_kmb(path)
        motion = parse_kmb1(payload)
        self.assertEqual(meta["skeleton"], "smplh")
        self.assertEqual(motion.model_name, "smplh")
        self.assertEqual(motion.joint_names[0], "pelvis")
        self.assertEqual(motion.joint_parents[4], 1)
        self.assertEqual(motion.num_frames, 3)
        self.assertAlmostEqual(float(motion.root_positions[2, 0]), 0.2, places=5)
        self.assertAlmostEqual(float(motion.local_rot_quats[1, 4, 0]), np.cos(np.pi / 4), places=5)

    def test_optional_tracks_round_trip_with_lengths_and_values(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "motion.npz"
            poses = np.zeros((3, 52, 3), dtype=np.float32)
            trans = np.zeros((3, 3), dtype=np.float32)
            np.savez(path, poses=poses, trans=trans, mocap_framerate=np.asarray(30.0))
            payload, meta = flowhmr_npz_to_kmb(
                path,
                global_time_seconds=[0.0, 0.04, 0.0733333],
                human_detection=np.asarray([1, 0, 1], dtype=np.uint8),
            )

        motion = parse_kmb1(payload)
        self.assertTrue(meta["global_time_seconds"])
        self.assertTrue(meta["human_detection"])
        np.testing.assert_allclose(motion.global_time_seconds, [0.0, 0.04, 0.0733333], rtol=0, atol=1e-6)
        np.testing.assert_array_equal(motion.human_detection, [1, 0, 1])

    def test_optional_tracks_validate_frame_count_monotonicity_and_binary_values(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "motion.npz"
            np.savez(
                path,
                poses=np.zeros((3, 52, 3), dtype=np.float32),
                trans=np.zeros((3, 3), dtype=np.float32),
                mocap_framerate=np.asarray(30.0),
            )
            for kwargs, message in (
                ({"global_time_seconds": [0.0, 0.04]}, "global_time_seconds"),
                ({"global_time_seconds": [0.0, 0.04, 0.04]}, "global_time_seconds"),
                ({"human_detection": [1, 0, 2]}, "human_detection"),
                ({"human_detection": [1.0, 0.5, 0.0]}, "human_detection"),
            ):
                with self.subTest(message=message, kwargs=kwargs):
                    with self.assertRaisesRegex(ValueError, message):
                        flowhmr_npz_to_kmb(path, **kwargs)

    def test_old_kmb_without_optional_tracks_remains_compatible(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "motion.npz"
            np.savez(
                path,
                poses=np.zeros((2, 52, 3), dtype=np.float32),
                trans=np.zeros((2, 3), dtype=np.float32),
                mocap_framerate=np.asarray(30.0),
            )
            payload, _ = flowhmr_npz_to_kmb(path)

        motion = parse_kmb1(payload)
        self.assertIsNone(motion.global_time_seconds)
        self.assertIsNone(motion.human_detection)


if __name__ == "__main__":
    unittest.main()
