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


if __name__ == "__main__":
    unittest.main()
