import unittest

import torch

from kimodo.constraints import LeftHandConstraintSet
from kimodo.skeleton import SOMASkeleton77


class EndEffectorTargetPositionTests(unittest.TestCase):
    def test_direct_target_position_overrides_fk_hand_position(self):
        skeleton = SOMASkeleton77()
        target = [1.25, 2.5, -0.75]
        constraint = LeftHandConstraintSet.from_dict(
            skeleton,
            {
                "frame_indices": [3],
                "local_joints_rot": torch.zeros(1, skeleton.nbjoints, 3).tolist(),
                "root_positions": [[0.0, 0.0, 0.0]],
                "smooth_root_2d": [[0.0, 0.0]],
                "target_positions": [target],
            },
        )

        _, position_joint_names = skeleton.expand_joint_names(["LeftHand"])
        hand_index = skeleton.bone_index[position_joint_names[0]]
        self.assertTrue(
            torch.allclose(
                constraint.global_joints_positions[0, hand_index],
                torch.tensor(target, dtype=constraint.global_joints_positions.dtype),
            )
        )

    def test_null_target_position_keeps_legacy_fk_position(self):
        skeleton = SOMASkeleton77()
        payload = {
            "frame_indices": [3],
            "local_joints_rot": torch.zeros(1, skeleton.nbjoints, 3).tolist(),
            "root_positions": [[0.0, 0.0, 0.0]],
            "smooth_root_2d": [[0.0, 0.0]],
        }
        legacy = LeftHandConstraintSet.from_dict(skeleton, payload)
        with_optional_target = LeftHandConstraintSet.from_dict(
            skeleton,
            {**payload, "target_positions": [None]},
        )

        self.assertTrue(
            torch.allclose(
                with_optional_target.global_joints_positions,
                legacy.global_joints_positions,
            )
        )


if __name__ == "__main__":
    unittest.main()
