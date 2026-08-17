import ast
from pathlib import Path
import unittest
import zipfile

import numpy as np

from core.protocol.kmb_motion import KmbMotion
from core.retargeting import (
    RetargetReferencePose,
    calibrate_target_arms_to_tpose,
    kmb_global_pose,
    retarget_kmb_motion,
    retarget_kmb_motion_with_target_arm_calibration,
)


def _rotation_y(angle: float) -> np.ndarray:
    c, s = np.cos(angle), np.sin(angle)
    return np.asarray(((c, 0.0, s), (0.0, 1.0, 0.0), (-s, 0.0, c)), dtype=np.float64)


def _cocos_xyzw_to_matrix(quaternion) -> np.ndarray:
    """Cocos Node rotations are serialized as (x, y, z, w), unlike KMB."""
    x, y, z, w = np.asarray(quaternion, dtype=np.float64)
    length = np.linalg.norm((w, x, y, z))
    if length < 1e-7:
        raise AssertionError("Cocos test node rotation must not be zero-length.")
    w, x, y, z = np.asarray((w, x, y, z), dtype=np.float64) / length
    return np.asarray(
        (
            (1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y - z * w), 2.0 * (x * z + y * w)),
            (2.0 * (x * y + z * w), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z - x * w)),
            (2.0 * (x * z - y * w), 2.0 * (y * z + x * w), 1.0 - 2.0 * (x * x + y * y)),
        ),
        dtype=np.float64,
    )


def _cocos_style_reference(skeleton_id: str, nodes: list[dict]) -> RetargetReferencePose:
    """Adapt a minimal Cocos-style Node rest hierarchy to the core reference contract."""
    names = tuple(node["name"] for node in nodes)
    parents = tuple(node["parent"] for node in nodes)
    positions = np.empty((len(nodes), 3), dtype=np.float64)
    rotations = np.empty((len(nodes), 3, 3), dtype=np.float64)
    for index, node in enumerate(nodes):
        local_position = np.asarray(node["position"], dtype=np.float64)
        local_rotation = _cocos_xyzw_to_matrix(node.get("rotation", (0.0, 0.0, 0.0, 1.0)))
        parent = parents[index]
        if parent < 0:
            positions[index] = local_position
            rotations[index] = local_rotation
        else:
            positions[index] = positions[parent] + rotations[parent] @ local_position
            rotations[index] = rotations[parent] @ local_rotation
    return RetargetReferencePose(
        skeleton_id=skeleton_id,
        joint_names=names,
        joint_parents=parents,
        global_positions=positions,
        global_rot_mats=rotations,
    )


def _kimodo_root() -> Path:
    return Path(__file__).resolve().parents[1] / "kimodo" / "kimodo"


def _load_layout(class_name: str) -> tuple[tuple[str, ...], tuple[int, ...]]:
    """Read the canonical static layout without importing the torch model package."""
    definitions = ast.parse((_kimodo_root() / "skeleton" / "definitions.py").read_text(encoding="utf-8"))
    for node in definitions.body:
        if not isinstance(node, ast.ClassDef) or node.name != class_name:
            continue
        for statement in node.body:
            if (
                isinstance(statement, ast.Assign)
                and len(statement.targets) == 1
                and isinstance(statement.targets[0], ast.Name)
                and statement.targets[0].id == "bone_order_names_with_parents"
            ):
                pairs = ast.literal_eval(statement.value)
                names = tuple(name for name, _ in pairs)
                by_name = {name: index for index, name in enumerate(names)}
                return names, tuple(-1 if parent is None else by_name[parent] for _, parent in pairs)
    raise AssertionError(f"Could not find {class_name}.bone_order_names_with_parents.")


def _load_neutral_joints(skeleton_name: str, joint_count: int) -> np.ndarray:
    path = _kimodo_root() / "assets" / "skeletons" / skeleton_name / "joints.p"
    with zipfile.ZipFile(path) as archive:
        metadata = archive.read("joints/data.pkl")
        raw = archive.read("joints/data/0")
    dtype = "<f8" if b"DoubleStorage" in metadata else "<f4"
    values = np.frombuffer(raw, dtype=dtype)
    if values.size != joint_count * 3:
        raise AssertionError(f"{path} has {values.size} scalars, expected {joint_count * 3}.")
    return values.reshape((joint_count, 3)).astype(np.float64)


def _reference_from_kimodo_skeleton(class_name: str, skeleton_name: str) -> RetargetReferencePose:
    names, parents = _load_layout(class_name)
    return RetargetReferencePose(
        skeleton_id=skeleton_name,
        joint_names=names,
        joint_parents=parents,
        global_positions=_load_neutral_joints(skeleton_name, len(names)),
        global_rot_mats=np.repeat(np.eye(3, dtype=np.float64)[None], len(names), axis=0),
    )


def _quat_error_degrees(expected: np.ndarray, actual: np.ndarray) -> np.ndarray:
    expected = expected / np.linalg.norm(expected, axis=-1, keepdims=True)
    actual = actual / np.linalg.norm(actual, axis=-1, keepdims=True)
    dot = np.abs(np.sum(expected * actual, axis=-1))
    return np.degrees(2.0 * np.arccos(np.clip(dot, -1.0, 1.0)))


def _rotation_error_degrees(expected: np.ndarray, actual: np.ndarray) -> np.ndarray:
    relative = np.swapaxes(expected, -1, -2) @ actual
    trace = np.trace(relative, axis1=-2, axis2=-1)
    return np.degrees(np.arccos(np.clip((trace - 1.0) * 0.5, -1.0, 1.0)))


class RetargetingTests(unittest.TestCase):
    def _reference(self, skeleton_id: str, positions, rotations=None):
        positions = np.asarray(positions, dtype=np.float64)
        if rotations is None:
            rotations = np.repeat(np.eye(3, dtype=np.float64)[None], len(positions), axis=0)
        return RetargetReferencePose(
            skeleton_id=skeleton_id,
            joint_names=("root", "hand"),
            joint_parents=(-1, 0),
            global_positions=positions,
            global_rot_mats=np.asarray(rotations, dtype=np.float64),
        )

    def test_same_reference_preserves_local_deltas_and_root(self):
        reference = self._reference("source", ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)))
        rotations = np.asarray(
            [
                [[1.0, 0.0, 0.0, 0.0], [1.0, 0.0, 0.0, 0.0]],
                [[0.9238795, 0.0, 0.3826834, 0.0], [0.7071068, 0.0, 0.7071068, 0.0]],
            ],
            dtype=np.float32,
        )
        motion = KmbMotion(
            payload=b"",
            model_name="source",
            fps=30.0,
            joint_names=reference.joint_names,
            joint_parents=reference.joint_parents,
            root_positions=np.asarray(((0.0, 0.0, 0.0), (1.0, 0.0, 2.0)), dtype=np.float32),
            local_rot_quats=rotations,
            foot_contacts=None,
        )
        result = retarget_kmb_motion(motion, reference, reference)
        np.testing.assert_allclose(result.local_rot_quats, motion.local_rot_quats, atol=2e-5)
        np.testing.assert_allclose(result.root_positions, motion.root_positions, atol=2e-5)

    def test_reference_orientation_and_scale_are_applied_to_root(self):
        source = self._reference("source", ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)))
        target = self._reference("target", ((10.0, 0.0, 4.0), (10.0, 2.0, 4.0)), (_rotation_y(np.pi / 2),) * 2)
        identity = np.zeros((2, 2, 4), dtype=np.float32)
        identity[..., 0] = 1.0
        motion = KmbMotion(
            payload=b"",
            model_name="source",
            fps=30.0,
            joint_names=source.joint_names,
            joint_parents=source.joint_parents,
            root_positions=np.asarray(((0.0, 0.0, 0.0), (0.0, 0.0, 1.0)), dtype=np.float32),
            local_rot_quats=identity,
            foot_contacts=None,
        )
        result = retarget_kmb_motion(motion, source, target)
        np.testing.assert_allclose(result.root_positions[0], target.global_positions[0], atol=2e-5)
        # Source +Z displacement becomes target +X displacement after the +90° Y basis.
        np.testing.assert_allclose(result.root_positions[1], (12.0, 0.0, 4.0), atol=2e-5)

    def test_fullbody_reference_requires_global_data(self):
        with self.assertRaisesRegex(ValueError, "global_joints_positions"):
            RetargetReferencePose.from_fullbody(
                {
                    "skeleton_id": "source",
                    "joint_names": ["root"],
                    "joint_parents": [-1],
                    "local_joints_rot": [[[1.0, 0.0, 0.0]]],
                }
            )

    def test_fullbody_reference_accepts_global_wxyz_rotations(self):
        reference = RetargetReferencePose.from_fullbody(
            {
                "skeleton_id": "fullbody",
                "joint_names": ["root", "hand"],
                "joint_parents": [-1, 0],
                "global_joints_positions": [[0.0, 0.0, 0.0], [0.0, 1.0, 0.0]],
                "global_joints_rots": [[1.0, 0.0, 0.0, 0.0], [1.0, 0.0, 0.0, 0.0]],
            }
        )
        np.testing.assert_allclose(reference.global_rot_mats, np.repeat(np.eye(3)[None], 2, axis=0), atol=2e-7)

    def test_explicit_mapping_supports_different_joint_names(self):
        source = self._reference("source", ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)))
        target = RetargetReferencePose(
            skeleton_id="target",
            joint_names=("pelvis", "palm"),
            joint_parents=(-1, 0),
            global_positions=np.asarray(((0.0, 0.0, 0.0), (0.0, 2.0, 0.0)), dtype=np.float64),
            global_rot_mats=np.repeat(np.eye(3, dtype=np.float64)[None], 2, axis=0),
        )
        rotations = np.asarray(
            [[[1.0, 0.0, 0.0, 0.0], [0.7071068, 0.0, 0.7071068, 0.0]]], dtype=np.float32
        )
        motion = KmbMotion(
            payload=b"",
            model_name="source",
            fps=30.0,
            joint_names=source.joint_names,
            joint_parents=source.joint_parents,
            root_positions=np.zeros((1, 3), dtype=np.float32),
            local_rot_quats=rotations,
            foot_contacts=None,
        )
        result = retarget_kmb_motion(motion, source, target, {"root": "pelvis", "hand": "palm"})
        self.assertEqual(("pelvis", "palm"), result.joint_names)
        np.testing.assert_allclose(result.local_rot_quats[0, 1], rotations[0, 1], atol=2e-5)

    def test_unmapped_target_defaults_to_parent_inheritance_and_freeze_is_opt_in(self):
        source = RetargetReferencePose(
            skeleton_id="source",
            joint_names=("root",),
            joint_parents=(-1,),
            global_positions=np.zeros((1, 3), dtype=np.float64),
            global_rot_mats=np.eye(3, dtype=np.float64)[None],
        )
        target = RetargetReferencePose(
            skeleton_id="target",
            joint_names=("root", "unmapped_leaf"),
            joint_parents=(-1, 0),
            global_positions=np.asarray(((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)), dtype=np.float64),
            global_rot_mats=np.repeat(np.eye(3, dtype=np.float64)[None], 2, axis=0),
        )
        root_turn = np.asarray(
            [[[np.cos(np.pi / 4.0), 0.0, np.sin(np.pi / 4.0), 0.0]]], dtype=np.float32
        )
        motion = KmbMotion(
            payload=b"",
            model_name="source",
            fps=30.0,
            joint_names=source.joint_names,
            joint_parents=source.joint_parents,
            root_positions=np.zeros((1, 3), dtype=np.float32),
            local_rot_quats=root_turn,
            foot_contacts=None,
        )

        inherited = retarget_kmb_motion(motion, source, target)
        frozen = retarget_kmb_motion(motion, source, target, unmapped_target="freeze_global")
        _, inherited_global = kmb_global_pose(inherited, target)
        _, frozen_global = kmb_global_pose(frozen, target)

        # A missing target leaf follows its animated parent by default.
        np.testing.assert_allclose(inherited_global[0, 1], inherited_global[0, 0], atol=2e-5)
        # Holding that leaf at its reference global orientation is only opt-in.
        np.testing.assert_allclose(frozen_global[0, 1], target.global_rot_mats[1], atol=2e-5)

    def test_cocos_style_a_pose_target_arm_calibration_avoids_inward_arm_overshoot(self):
        # This represents Cocos Node local transforms: an ordinary T-pose
        # source, and a target whose upper-arm child offsets form an A-pose.
        # The target needs no Unity Avatar or engine API for this correction.
        source = _cocos_style_reference(
            "cocos-source-t",
            [
                {"name": "root", "parent": -1, "position": (0.0, 0.0, 0.0)},
                {"name": "left_upper", "parent": 0, "position": (-0.2, 1.5, 0.0)},
                {"name": "left_lower", "parent": 1, "position": (-0.7, 0.0, 0.0)},
                {"name": "left_hand", "parent": 2, "position": (-0.7, 0.0, 0.0)},
                {"name": "right_upper", "parent": 0, "position": (0.2, 1.5, 0.0)},
                {"name": "right_lower", "parent": 4, "position": (0.7, 0.0, 0.0)},
                {"name": "right_hand", "parent": 5, "position": (0.7, 0.0, 0.0)},
            ],
        )
        target = _cocos_style_reference(
            "cocos-target-a",
            [
                {"name": "root", "parent": -1, "position": (0.0, 0.0, 0.0)},
                {"name": "left_upper", "parent": 0, "position": (-0.2, 1.5, 0.0)},
                {"name": "left_lower", "parent": 1, "position": (-0.5, -0.5, 0.0)},
                {"name": "left_hand", "parent": 2, "position": (-0.5, -0.5, 0.0)},
                {"name": "right_upper", "parent": 0, "position": (0.2, 1.5, 0.0)},
                {"name": "right_lower", "parent": 4, "position": (0.5, -0.5, 0.0)},
                {"name": "right_hand", "parent": 5, "position": (0.5, -0.5, 0.0)},
            ],
        )
        arm_joints = {
            "left_upper_arm": "left_upper",
            "left_lower_arm": "left_lower",
            "right_upper_arm": "right_upper",
            "right_lower_arm": "right_lower",
        }
        virtual_tpose = calibrate_target_arms_to_tpose(target, arm_joints)
        left_upper = target.joint_index["left_upper"]
        left_lower = target.joint_index["left_lower"]
        right_upper = target.joint_index["right_upper"]
        right_lower = target.joint_index["right_lower"]
        self.assertAlmostEqual(
            virtual_tpose.global_positions[left_lower, 1], virtual_tpose.global_positions[left_upper, 1], places=5
        )
        self.assertAlmostEqual(
            virtual_tpose.global_positions[right_lower, 1], virtual_tpose.global_positions[right_upper, 1], places=5
        )

        close_arm_rotation = np.zeros((1, len(source.joint_names), 4), dtype=np.float32)
        close_arm_rotation[..., 0] = 1.0
        half_turn = np.sqrt(0.5)
        close_arm_rotation[0, source.joint_index["left_upper"]] = (half_turn, 0.0, 0.0, half_turn)
        close_arm_rotation[0, source.joint_index["right_upper"]] = (half_turn, 0.0, 0.0, -half_turn)
        source_motion = KmbMotion(
            payload=b"",
            model_name=source.skeleton_id,
            fps=30.0,
            joint_names=source.joint_names,
            joint_parents=source.joint_parents,
            root_positions=np.zeros((1, 3), dtype=np.float32),
            local_rot_quats=close_arm_rotation,
            foot_contacts=None,
        )

        uncalibrated = retarget_kmb_motion(source_motion, source, target)
        calibrated = retarget_kmb_motion_with_target_arm_calibration(
            source_motion, source, target, arm_joints
        )
        uncalibrated_positions, _ = kmb_global_pose(uncalibrated, target)
        calibrated_positions, _ = kmb_global_pose(calibrated, target)

        # Direct T->A transfer rotates the arms through the torso center. The
        # virtual-T retarget plus rebase makes both elbows descend from their
        # own shoulders instead, which is the intended close-arm behavior.
        self.assertGreater(float(uncalibrated_positions[0, left_lower, 0]), 0.0)
        self.assertLess(float(uncalibrated_positions[0, right_lower, 0]), 0.0)
        self.assertAlmostEqual(
            float(calibrated_positions[0, left_lower, 0]), float(target.global_positions[left_upper, 0]), places=5
        )
        self.assertAlmostEqual(
            float(calibrated_positions[0, right_lower, 0]), float(target.global_positions[right_upper, 0]), places=5
        )

    def test_soma30_to_smplx22_round_trip_preserves_mapped_joints_and_distributes_missing_chain(self):
        soma = _reference_from_kimodo_skeleton("SOMASkeleton30", "somaskel30")
        smplx = _reference_from_kimodo_skeleton("SMPLXSkeleton22", "smplx22")
        soma_to_smplx = {
            "Hips": "pelvis",
            "Spine1": "spine1",
            "Spine2": "spine2",
            "Chest": "spine3",
            "Neck1": "neck",
            "Head": "head",
            "LeftShoulder": "left_collar",
            "LeftArm": "left_shoulder",
            "LeftForeArm": "left_elbow",
            "LeftHand": "left_wrist",
            "RightShoulder": "right_collar",
            "RightArm": "right_shoulder",
            "RightForeArm": "right_elbow",
            "RightHand": "right_wrist",
            "LeftLeg": "left_hip",
            "LeftShin": "left_knee",
            "LeftFoot": "left_ankle",
            "LeftToeBase": "left_foot",
            "RightLeg": "right_hip",
            "RightShin": "right_knee",
            "RightFoot": "right_ankle",
            "RightToeBase": "right_foot",
        }
        inverse = {target: source for source, target in soma_to_smplx.items()}
        self.assertEqual(len(soma_to_smplx), len(inverse), "The experiment requires a one-to-one mapping.")

        angle = np.deg2rad(45.0)
        local_rotations = np.zeros((2, len(soma.joint_names), 4), dtype=np.float32)
        local_rotations[..., 0] = np.cos(angle / 2.0)
        local_rotations[..., 2] = np.sin(angle / 2.0)
        source_motion = KmbMotion(
            payload=b"",
            model_name="somaskel30",
            fps=30.0,
            joint_names=soma.joint_names,
            joint_parents=soma.joint_parents,
            root_positions=np.asarray(((0.0, 0.0, 0.0), (0.4, 0.0, 0.2)), dtype=np.float32),
            local_rot_quats=local_rotations,
            foot_contacts=np.ones((2, 4), dtype=np.float32),
        )
        smplx_motion = retarget_kmb_motion(source_motion, soma, smplx, soma_to_smplx)
        round_trip = retarget_kmb_motion(smplx_motion, smplx, soma, inverse)

        per_joint_error = _quat_error_degrees(source_motion.local_rot_quats, round_trip.local_rot_quats)
        mapped = np.asarray([soma.joint_index[name] for name in soma_to_smplx], dtype=np.int32)
        unmapped = np.asarray(
            [index for index in range(len(soma.joint_names)) if index not in set(mapped.tolist())], dtype=np.int32
        )
        mapped_mean_error = float(per_joint_error[:, mapped].mean())
        unmapped_mean_error = float(per_joint_error[:, unmapped].mean())
        all_joint_mean_error = float(per_joint_error.mean())
        root_error = float(np.max(np.linalg.norm(round_trip.root_positions - source_motion.root_positions, axis=-1)))

        self.assertLess(mapped_mean_error, 0.03)
        self.assertGreater(unmapped_mean_error, mapped_mean_error)
        self.assertLess(root_error, 1e-4)
        # The missing SOMA Neck2 is restored by distributing the SMPL-X
        # neck-to-head relative rotation over Neck2 and Head. The seven truly
        # unmapped leaves inherit their parent but cannot retain their own 45°
        # local delta, so their deterministic loss remains visible.
        self.assertLess(float(per_joint_error[:, soma.joint_index["Neck2"]].max()), 0.03)
        self.assertAlmostEqual(39.375, unmapped_mean_error, places=3)
        self.assertAlmostEqual(10.5, all_joint_mean_error, places=3)

        source_positions, source_rotations = kmb_global_pose(source_motion, soma)
        round_trip_positions, round_trip_rotations = kmb_global_pose(round_trip, soma)
        end_effectors = ("LeftHand", "RightHand", "LeftFoot", "RightFoot")
        end_effector_indices = [soma.joint_index[name] for name in end_effectors]
        end_effector_position_error = np.linalg.norm(
            source_positions[:, end_effector_indices] - round_trip_positions[:, end_effector_indices], axis=-1
        )
        end_effector_rotation_error = _rotation_error_degrees(
            source_rotations[:, end_effector_indices], round_trip_rotations[:, end_effector_indices]
        )
        self.assertLess(float(end_effector_position_error.max()), 1e-5)
        self.assertLess(float(end_effector_rotation_error.max()), 0.03)

        inherited_leaves = {
            "Jaw": "Head",
            "LeftEye": "Head",
            "RightEye": "Head",
            "LeftHandThumbEnd": "LeftHand",
            "LeftHandMiddleEnd": "LeftHand",
            "RightHandThumbEnd": "RightHand",
            "RightHandMiddleEnd": "RightHand",
        }
        for leaf_name, parent_name in inherited_leaves.items():
            np.testing.assert_allclose(
                round_trip_rotations[:, soma.joint_index[leaf_name]],
                round_trip_rotations[:, soma.joint_index[parent_name]],
                atol=2e-5,
            )


if __name__ == "__main__":
    unittest.main()
