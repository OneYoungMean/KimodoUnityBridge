import unittest

import numpy as np

from core.animation_analysis import build_clip_constraint_analysis, build_generation_analysis


class AnimationAnalysisTests(unittest.TestCase):
    def test_generation_analysis_returns_the_requested_sparse_marker_count(self):
        joints = np.zeros((1, 9, 2, 3), dtype=np.float32)
        joints[0, :, 0, 0] = np.arange(9, dtype=np.float32)
        joints[0, 3:6, 1, 1] = 1.0
        result = build_generation_analysis(
            {"analysis_option": {"keyframe_count": 4}},
            type("Model", (), {"fps": 30.0})(),
            {"posed_joints": joints, "foot_contacts": np.ones((1, 9, 4), dtype=np.float32)},
        )
        self.assertEqual(4, len(result["keyframes"]))
        self.assertEqual({"frame", "saliency", "time"}, set(result["keyframes"][0]))
        self.assertEqual(
            sorted(result["keyframes"], key=lambda item: (-item["saliency"], item["frame"])),
            result["keyframes"],
        )
        self.assertNotIn("contacts", result)
        self.assertNotIn("trajectory", result)

    def test_analysis_is_omitted_when_not_requested(self):
        output = {"posed_joints": np.zeros((1, 1, 1, 3), dtype=np.float32)}
        self.assertIsNone(build_generation_analysis({}, type("Model", (), {"fps": 30.0})(), output))

    def test_clip_analysis_returns_only_sparse_kmb_markers(self):
        roots = np.zeros((10, 3), dtype=np.float32)
        roots[:, 0] = np.linspace(0.0, 2.0, 10)
        roots[5:, 2] = np.linspace(0.0, 2.0, 5)
        quats = np.zeros((10, 2, 4), dtype=np.float32)
        quats[..., 3] = 1.0
        result = build_clip_constraint_analysis(
            [{"root_positions": roots, "local_rot_quats": quats, "fps": 20.0}],
            {"analysis_only": True, "keyframe_count": 5},
        )
        self.assertEqual(5, len(result["keyframes"]))
        self.assertEqual({"keyframes", "foot_contact_changes"}, set(result))
        self.assertTrue(all(item["clip_index"] == 0 for item in result["keyframes"]))
        self.assertEqual(
            sorted(result["keyframes"], key=lambda item: (-item["saliency"], item["frame"])),
            result["keyframes"],
        )

    def test_foot_contact_changes_debounce_short_reversals(self):
        roots = np.zeros((18, 3), dtype=np.float32)
        quats = np.zeros((18, 1, 4), dtype=np.float32)
        quats[..., 3] = 1.0
        contacts = np.zeros((18, 4), dtype=np.float32)
        contacts[2:14, :2] = 1.0
        contacts[8:10, :2] = 0.0
        contacts[6:8, 2:] = 1.0
        result = build_clip_constraint_analysis(
            [{"root_positions": roots, "local_rot_quats": quats, "foot_contacts": contacts, "fps": 20.0}],
            {"analysis_only": True, "keyframe_count": 2},
        )
        changes = result["foot_contact_changes"]
        self.assertEqual(
            [
                {"clip_index": 0, "foot": "left", "frame": 14, "contact": False, "transition": "contact_end", "duration_frames": 4},
                {"clip_index": 0, "foot": "left", "frame": 2, "contact": True, "transition": "contact_start", "duration_frames": 12},
            ],
            changes,
        )
        expanded_contacts = np.concatenate((contacts[:, :2], contacts[:, 1:2], contacts[:, 2:], contacts[:, 3:4]), axis=1)
        expanded = build_clip_constraint_analysis(
            [{"root_positions": roots, "local_rot_quats": quats, "foot_contacts": expanded_contacts, "fps": 20.0}],
            {"analysis_only": True, "keyframe_count": 2},
        )
        self.assertEqual(changes, expanded["foot_contact_changes"])


if __name__ == "__main__":
    unittest.main()
