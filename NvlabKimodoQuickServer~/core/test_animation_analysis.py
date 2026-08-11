import unittest

import numpy as np

from core.animation_analysis import build_clip_constraint_analysis, build_generation_analysis


class AnimationAnalysisTests(unittest.TestCase):
    def test_keyframes_include_endpoints_and_are_time_ordered(self):
        joints = np.zeros((1, 20, 2, 3), dtype=np.float32)
        joints[0, :, 1, 0] = np.linspace(0.0, 5.0, 20)
        joints[0, 10:, 1, 2] = np.linspace(0.0, 3.0, 10)
        output = {"posed_joints": joints, "foot_contacts": np.zeros((1, 20, 4), dtype=np.float32)}
        result = build_generation_analysis(
            {"analysis_option": {"keyframes": {"enabled": True, "max_count": 6}}},
            type("Model", (), {"fps": 10.0})(),
            output,
        )
        keyframes = result["keyframes"]
        self.assertEqual(0, keyframes[0]["frame"])
        self.assertEqual(19, keyframes[-1]["frame"])
        self.assertEqual(sorted(item["time"] for item in keyframes), [item["time"] for item in keyframes])
        self.assertLessEqual(len(keyframes), 6)
        self.assertTrue(all(0.0 <= item["saliency"] <= 1.0 for item in keyframes))
        self.assertTrue(all("score" not in item for item in keyframes))

    def test_analysis_is_omitted_when_not_requested(self):
        output = {"posed_joints": np.zeros((1, 1, 1, 3), dtype=np.float32)}
        self.assertIsNone(build_generation_analysis({}, type("Model", (), {"fps": 30.0})(), output))

    def test_clip_analysis_returns_keyframes_issues_and_quality(self):
        roots = np.zeros((20, 3), dtype=np.float32)
        roots[:, 0] = np.linspace(0.0, 1.0, 20)
        roots[10:, 0] += 4.0
        quats = np.zeros((20, 2, 4), dtype=np.float32)
        quats[..., 3] = 1.0
        quats[10:, 1, 1] = 1.0
        quats[10:, 1, 3] = 0.0
        result = build_clip_constraint_analysis(
            [{"root_positions": roots, "local_rot_quats": quats, "fps": 20.0}],
            {"analysis_only": True, "keyframes": {"max_count": 5}},
        )
        self.assertEqual("motion-quality-v1", result["algorithm"])
        self.assertEqual(1, len(result["clips"]))
        self.assertEqual(0, result["keyframes"][0]["frame"])
        self.assertEqual(19, result["keyframes"][-1]["frame"])
        self.assertTrue(result["issues"])
        self.assertTrue(all(0.0 <= item["score"] <= 1.0 for item in result["issues"]))
        self.assertTrue(0.0 <= result["quality_score"] <= 1.0)
