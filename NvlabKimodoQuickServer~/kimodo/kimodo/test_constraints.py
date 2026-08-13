import unittest

import math

import torch

from kimodo.constraints import FullBodyConstraintSet, LeftHandConstraintSet, Root2DConstraintSet, normalize_constraints_to_anchor
from kimodo.motion_rep.conditioning import build_condition_dicts, get_unique_index_and_data
from kimodo.postprocess import _merge_fullbody_constraint, extract_input_motion_from_constraints
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


class ConstraintAnchorNormalizationTests(unittest.TestCase):
    def test_root2d_anchor_moves_anchor_to_zero_and_preserves_ordered_delta(self):
        anchor = Root2DConstraintSet(
            None,
            frame_indices=torch.tensor([0]),
            smooth_root_2d=torch.tensor([[10.0, 5.0]]),
            global_root_heading=torch.tensor([[0.0, 1.0]]),
        )
        later = Root2DConstraintSet(
            None,
            frame_indices=torch.tensor([10]),
            smooth_root_2d=torch.tensor([[10.0, 7.0]]),
            global_root_heading=torch.tensor([[0.0, 1.0]]),
        )

        translation, yaw = normalize_constraints_to_anchor([anchor, later])

        self.assertTrue(torch.allclose(translation, torch.tensor([10.0, 5.0])))
        self.assertAlmostEqual(float(yaw), math.pi / 2.0, places=5)
        self.assertTrue(torch.allclose(anchor.smooth_root_2d, torch.zeros(1, 2), atol=1e-6))
        self.assertTrue(torch.allclose(later.smooth_root_2d, torch.tensor([[-2.0, 0.0]]), atol=1e-6))

    def test_root2d_is_merged_into_fullbody_before_motion_correction(self):
        skeleton = SOMASkeleton77(load=False)
        joints = torch.zeros(1, skeleton.nbjoints, 3)
        joints[0, skeleton.root_idx] = torch.tensor([1.0, 1.25, 2.0])
        right_hip, left_hip = skeleton.hip_joint_idx
        joints[0, right_hip] = joints[0, skeleton.root_idx] + torch.tensor([0.2, 0.0, 0.0])
        joints[0, left_hip] = joints[0, skeleton.root_idx] + torch.tensor([-0.2, 0.0, 0.0])
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        fullbody = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0]),
            global_joints_positions=joints,
            global_joints_rots=rotations,
        )
        root = Root2DConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0]),
            smooth_root_2d=torch.tensor([[5.0, 7.0]]),
            global_root_heading=torch.tensor([[1.0, 0.0]]),
        )

        resolved = _merge_fullbody_constraint(
            [root, fullbody], skeleton, rotations, joints, num_frames=1
        )

        self.assertEqual(len(resolved), 1)
        self.assertIsInstance(resolved[0], FullBodyConstraintSet)
        merged = resolved[0]
        self.assertTrue(
            torch.allclose(
                merged.global_joints_positions[0, skeleton.root_idx],
                torch.tensor([5.0, 1.25, 7.0]),
            )
        )
        self.assertTrue(
            torch.allclose(merged.global_root_heading[0], torch.tensor([1.0, 0.0]), atol=1e-5)
        )

    def test_end_effector_is_final_after_fullbody_and_root2d(self):
        skeleton = SOMASkeleton77(load=False)
        joints = torch.zeros(1, skeleton.nbjoints, 3)
        joints[0, skeleton.root_idx] = torch.tensor([1.0, 1.25, 2.0])
        right_hip, left_hip = skeleton.hip_joint_idx
        joints[0, right_hip] = joints[0, skeleton.root_idx] + torch.tensor([0.2, 0.0, 0.0])
        joints[0, left_hip] = joints[0, skeleton.root_idx] + torch.tensor([-0.2, 0.0, 0.0])
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        fullbody = FullBodyConstraintSet(
            skeleton,
            torch.tensor([0]),
            joints,
            rotations,
            smooth_root_2d=torch.tensor([[1.0, 2.0]]),
        )
        root = Root2DConstraintSet(skeleton, torch.tensor([0]), torch.tensor([[5.0, 7.0]]))
        hand_index = skeleton.bone_index[skeleton.left_hand_joint_names[0]]
        hand_positions = joints.clone()
        hand_target = torch.tensor([9.0, 2.0, -3.0])
        hand_positions[0, hand_index] = hand_target
        hand = LeftHandConstraintSet(
            skeleton,
            torch.tensor([0]),
            hand_positions,
            rotations,
            smooth_root_2d=torch.tensor([[1.0, 2.0]]),
        )

        resolved = _merge_fullbody_constraint(
            [hand, root, fullbody], skeleton, rotations, joints, num_frames=1
        )
        self.assertEqual(len(resolved), 1)
        merged = resolved[0]
        self.assertTrue(
            torch.allclose(
                merged.global_joints_positions[0, skeleton.root_idx],
                torch.tensor([5.0, 1.25, 7.0]),
            )
        )
        self.assertTrue(torch.allclose(merged.global_joints_positions[0, hand_index], hand_target))

        hips, _ = extract_input_motion_from_constraints(
            [hand, root, fullbody], skeleton, num_frames=1, num_joints=skeleton.nbjoints
        )
        self.assertTrue(torch.allclose(hips[0], torch.tensor([5.0, 1.25, 7.0])))

    def test_conditioning_root2d_wins_over_fullbody_and_limb_reference_root(self):
        skeleton = SOMASkeleton77(load=False)
        joints = torch.zeros(1, skeleton.nbjoints, 3)
        joints[0, skeleton.root_idx] = torch.tensor([1.0, 1.25, 2.0])
        right_hip, left_hip = skeleton.hip_joint_idx
        joints[0, right_hip] = joints[0, skeleton.root_idx] + torch.tensor([0.2, 0.0, 0.0])
        joints[0, left_hip] = joints[0, skeleton.root_idx] + torch.tensor([-0.2, 0.0, 0.0])
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        fullbody = FullBodyConstraintSet(
            skeleton,
            torch.tensor([0]),
            joints,
            rotations,
            smooth_root_2d=torch.tensor([[1.0, 2.0]]),
        )
        root = Root2DConstraintSet(skeleton, torch.tensor([0]), torch.tensor([[5.0, 7.0]]))
        hand = LeftHandConstraintSet(
            skeleton,
            torch.tensor([0]),
            joints.clone(),
            rotations,
            smooth_root_2d=torch.tensor([[99.0, 98.0]]),
        )

        index_dict, data_dict = build_condition_dicts([hand, root, fullbody])
        indices = torch.cat(index_dict["smooth_root_2d"])
        values = torch.cat(data_dict["smooth_root_2d"])
        _, values = get_unique_index_and_data(indices, values)
        self.assertTrue(torch.allclose(values, torch.tensor([[5.0, 7.0]])))

    def test_root_only_frame_remains_root2d(self):
        skeleton = SOMASkeleton77(load=False)
        joints = torch.zeros(1, skeleton.nbjoints, 3)
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        fullbody = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0]),
            global_joints_positions=joints,
            global_joints_rots=rotations,
        )
        root = Root2DConstraintSet(
            skeleton,
            frame_indices=torch.tensor([1]),
            smooth_root_2d=torch.tensor([[2.0, 3.0]]),
        )

        resolved = _merge_fullbody_constraint(
            [fullbody, root],
            skeleton,
            rotations.repeat(2, 1, 1, 1),
            joints.repeat(2, 1, 1),
            num_frames=2,
        )

        self.assertEqual(len(resolved), 2)
        self.assertEqual(resolved[0].name, "fullbody")
        self.assertEqual(resolved[1].name, "root2d")
        self.assertEqual(resolved[1].frame_indices.tolist(), [1])

    def test_non_overlapping_hand_constraint_is_preserved(self):
        skeleton = SOMASkeleton77(load=False)
        joints = torch.zeros(1, skeleton.nbjoints, 3)
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        fullbody = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0]),
            global_joints_positions=joints,
            global_joints_rots=rotations,
        )
        hand_positions = joints.clone()
        hand_joint = skeleton.bone_index[skeleton.left_hand_joint_names[0]]
        hand_positions[0, hand_joint, 0] = 2.0
        hand = LeftHandConstraintSet(
            skeleton,
            frame_indices=torch.tensor([1]),
            global_joints_positions=hand_positions,
            global_joints_rots=rotations,
            smooth_root_2d=torch.zeros(1, 2),
        )

        resolved = _merge_fullbody_constraint(
            [fullbody, hand],
            skeleton,
            rotations.repeat(2, 1, 1, 1),
            joints.repeat(2, 1, 1),
            num_frames=2,
        )

        self.assertEqual([item.name for item in resolved], ["fullbody", "left-hand"])
        self.assertEqual(resolved[1].frame_indices.tolist(), [1])

    def test_fullbody_root2d_uses_the_same_rotation_as_global_root_position(self):
        skeleton = SOMASkeleton77()
        positions = torch.zeros(2, skeleton.nbjoints, 3)
        positions[:, skeleton.root_idx] = torch.tensor(
            [[3.0, 1.25, -2.0], [4.0, 1.25, 2.0]]
        )
        right_hip, left_hip = skeleton.hip_joint_idx
        positions[:, right_hip] = positions[:, skeleton.root_idx] + torch.tensor([0.0, 0.0, 0.5])
        positions[:, left_hip] = positions[:, skeleton.root_idx] + torch.tensor([0.0, 0.0, -0.5])
        rotations = torch.eye(3).repeat(2, skeleton.nbjoints, 1, 1)
        constraint = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0, 10]),
            global_joints_positions=positions,
            global_joints_rots=rotations,
            smooth_root_2d=positions[:, skeleton.root_idx, [0, 2]],
        )

        normalize_constraints_to_anchor([constraint])

        self.assertTrue(
            torch.allclose(
                constraint.smooth_root_2d,
                constraint.global_joints_positions[:, skeleton.root_idx][:, [0, 2]],
                atol=1e-6,
            )
        )

    def test_fullbody_anchor_keeps_height_out_of_planar_normalization(self):
        skeleton = SOMASkeleton77()
        positions = torch.zeros(1, skeleton.nbjoints, 3)
        positions[0, skeleton.root_idx] = torch.tensor([3.0, 1.25, -2.0])
        right_hip, left_hip = skeleton.hip_joint_idx
        positions[0, right_hip] = torch.tensor([2.5, 1.25, -2.0])
        positions[0, left_hip] = torch.tensor([3.5, 1.25, -2.0])
        rotations = torch.eye(3).repeat(1, skeleton.nbjoints, 1, 1)
        constraint = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([0]),
            global_joints_positions=positions,
            global_joints_rots=rotations,
            smooth_root_2d=positions[:, skeleton.root_idx, [0, 2]],
        )

        normalize_constraints_to_anchor([constraint])

        self.assertTrue(torch.allclose(constraint.smooth_root_2d, torch.zeros(1, 2), atol=1e-6))
        self.assertAlmostEqual(float(constraint.global_joints_positions[0, skeleton.root_idx, 1]), 1.25, places=6)

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
