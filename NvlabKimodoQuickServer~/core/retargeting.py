from __future__ import annotations

"""Engine-independent humanoid retargeting over KMB motion data.

This module is an experimental implementation of the reference-pose part of
the retargeting algorithm used by ``Mwni/animation-retargeting``.  It does not
import Blender, Unity, Cocos, or Godot.  Adapters should convert their native
``Skeleton``/``fullbody`` data into :class:`RetargetReferencePose` and keep
engine-specific asset handling outside this module.

The KMB input contract for this first API is deliberately explicit: each
``local_rot_quats`` sample is a local rotation delta from the source reference
pose.  The result contains local rotation deltas from the target reference
pose.  This avoids treating a source A-pose, T-pose, or arbitrary bind pose as
if it were a universal T-pose.
"""

from dataclasses import dataclass
from typing import Any, Mapping

import numpy as np

from core.protocol.kmb_motion import KmbMotion


_EPS = 1e-7


def _as_float_array(value: Any, shape: tuple[int, ...], label: str) -> np.ndarray:
    array = np.asarray(value, dtype=np.float64)
    if array.shape != shape:
        raise ValueError(f"{label} must have shape {shape}, got {array.shape}.")
    if not np.isfinite(array).all():
        raise ValueError(f"{label} contains non-finite values.")
    return array


def _normalize_quaternions(quats: np.ndarray) -> np.ndarray:
    norms = np.linalg.norm(quats, axis=-1, keepdims=True)
    if np.any(norms < _EPS):
        raise ValueError("Quaternion contains a zero-length value.")
    return quats / norms


def _quat_wxyz_to_matrix(quats: np.ndarray) -> np.ndarray:
    q = _normalize_quaternions(np.asarray(quats, dtype=np.float64))
    w, x, y, z = np.moveaxis(q, -1, 0)
    return np.stack(
        (
            1.0 - 2.0 * (y * y + z * z),
            2.0 * (x * y - z * w),
            2.0 * (x * z + y * w),
            2.0 * (x * y + z * w),
            1.0 - 2.0 * (x * x + z * z),
            2.0 * (y * z - x * w),
            2.0 * (x * z - y * w),
            2.0 * (y * z + x * w),
            1.0 - 2.0 * (x * x + y * y),
        ),
        axis=-1,
    ).reshape(q.shape[:-1] + (3, 3))


def _matrix_to_quat_wxyz(matrices: np.ndarray) -> np.ndarray:
    """Convert proper 3x3 rotation matrices to normalized (w, x, y, z)."""
    matrices = np.asarray(matrices, dtype=np.float64)
    if matrices.shape[-2:] != (3, 3):
        raise ValueError(f"Rotation matrices must end in (3, 3), got {matrices.shape}.")

    flat = matrices.reshape((-1, 3, 3))
    result = np.empty((len(flat), 4), dtype=np.float64)
    for index, matrix in enumerate(flat):
        trace = float(np.trace(matrix))
        if trace > 0.0:
            scale = 2.0 * np.sqrt(max(trace + 1.0, 0.0))
            result[index] = (
                0.25 * scale,
                (matrix[2, 1] - matrix[1, 2]) / max(scale, _EPS),
                (matrix[0, 2] - matrix[2, 0]) / max(scale, _EPS),
                (matrix[1, 0] - matrix[0, 1]) / max(scale, _EPS),
            )
            continue

        diagonal = np.diag(matrix)
        axis = int(np.argmax(diagonal))
        if axis == 0:
            scale = 2.0 * np.sqrt(max(1.0 + matrix[0, 0] - matrix[1, 1] - matrix[2, 2], 0.0))
            result[index] = (
                (matrix[2, 1] - matrix[1, 2]) / max(scale, _EPS),
                0.25 * scale,
                (matrix[0, 1] + matrix[1, 0]) / max(scale, _EPS),
                (matrix[0, 2] + matrix[2, 0]) / max(scale, _EPS),
            )
        elif axis == 1:
            scale = 2.0 * np.sqrt(max(1.0 - matrix[0, 0] + matrix[1, 1] - matrix[2, 2], 0.0))
            result[index] = (
                (matrix[0, 2] - matrix[2, 0]) / max(scale, _EPS),
                (matrix[0, 1] + matrix[1, 0]) / max(scale, _EPS),
                0.25 * scale,
                (matrix[1, 2] + matrix[2, 1]) / max(scale, _EPS),
            )
        else:
            scale = 2.0 * np.sqrt(max(1.0 - matrix[0, 0] - matrix[1, 1] + matrix[2, 2], 0.0))
            result[index] = (
                (matrix[1, 0] - matrix[0, 1]) / max(scale, _EPS),
                (matrix[0, 2] + matrix[2, 0]) / max(scale, _EPS),
                (matrix[1, 2] + matrix[2, 1]) / max(scale, _EPS),
                0.25 * scale,
            )
    result = _normalize_quaternions(result).reshape(matrices.shape[:-2] + (4,))
    # A deterministic sign prevents frame-to-frame sign flips in JSON/KMB.
    result *= np.where(result[..., :1] < 0.0, -1.0, 1.0)
    return result


def _validate_parent_order(parents: tuple[int, ...], label: str) -> None:
    roots = 0
    for index, parent in enumerate(parents):
        if parent == -1:
            roots += 1
            continue
        if parent < 0 or parent >= len(parents) or parent == index:
            raise ValueError(f"{label} joint {index} has an invalid parent index {parent}.")
        if parent > index:
            raise ValueError(
                f"{label} joints must be parent-before-child; joint {index} references later parent {parent}."
            )
    if roots != 1:
        raise ValueError(f"{label} must contain exactly one root, got {roots}.")


@dataclass(frozen=True)
class RetargetReferencePose:
    """Engine-neutral reference pose for one skeleton.

    ``global_positions`` and ``global_rot_mats`` are expressed in the same
    coordinate system as the KMB motion.  They may describe a bind pose,
    neutral pose, A-pose, or generated canonical pose; they do not have to be
    a T-pose.
    """

    skeleton_id: str
    joint_names: tuple[str, ...]
    joint_parents: tuple[int, ...]
    global_positions: np.ndarray
    global_rot_mats: np.ndarray
    root_index: int = 0
    semantic_roles: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        names = tuple(str(name) for name in self.joint_names)
        parents = tuple(int(parent) for parent in self.joint_parents)
        if not self.skeleton_id:
            raise ValueError("skeleton_id is required.")
        if not names or len(names) != len(parents):
            raise ValueError("joint_names and joint_parents must be non-empty and have equal length.")
        if len(set(names)) != len(names):
            raise ValueError("joint_names must be unique.")
        _validate_parent_order(parents, self.skeleton_id)
        if self.root_index < 0 or self.root_index >= len(names) or parents[self.root_index] != -1:
            raise ValueError("root_index must identify the single root joint.")
        positions = _as_float_array(self.global_positions, (len(names), 3), "global_positions")
        rotations = _as_float_array(self.global_rot_mats, (len(names), 3, 3), "global_rot_mats")
        for index, rotation in enumerate(rotations):
            if not np.allclose(rotation.T @ rotation, np.eye(3), atol=2e-4) or np.linalg.det(rotation) <= 0.0:
                raise ValueError(f"global_rot_mats[{index}] is not a proper rotation matrix.")
        roles = tuple(str(role) for role in self.semantic_roles)
        if roles and len(roles) != len(names):
            raise ValueError("semantic_roles must be empty or match joint_names length.")
        object.__setattr__(self, "joint_names", names)
        object.__setattr__(self, "joint_parents", parents)
        object.__setattr__(self, "global_positions", positions)
        object.__setattr__(self, "global_rot_mats", rotations)
        object.__setattr__(self, "semantic_roles", roles)

    @property
    def joint_index(self) -> dict[str, int]:
        return {name: index for index, name in enumerate(self.joint_names)}

    @property
    def local_rot_mats(self) -> np.ndarray:
        local = np.empty_like(self.global_rot_mats)
        for index, parent in enumerate(self.joint_parents):
            local[index] = (
                self.global_rot_mats[index]
                if parent == -1
                else self.global_rot_mats[parent].T @ self.global_rot_mats[index]
            )
        return local

    @classmethod
    def from_fullbody(cls, value: Mapping[str, Any], *, skeleton_id: str | None = None) -> "RetargetReferencePose":
        """Build a reference from a portable fullbody payload.

        The payload intentionally requires global joint positions and global
        joint rotations.  The regular generation ``fullbody`` constraint may
        only contain local rotations and a root position; that is not enough to
        reconstruct bone lengths and must not silently be treated as a
        Retarget reference.
        """
        if not isinstance(value, Mapping):
            raise ValueError("fullbody reference must be an object.")
        names = value.get("joint_names")
        parents = value.get("joint_parents")
        positions = value.get("global_joints_positions")
        rotations = value.get("global_joints_rots", value.get("global_joint_rots"))
        if names is None or parents is None or positions is None or rotations is None:
            raise ValueError(
                "Retarget fullbody reference requires joint_names, joint_parents, "
                "global_joints_positions, and global_joints_rots."
            )
        rotations_array = np.asarray(rotations, dtype=np.float64)
        if rotations_array.ndim == 2 and rotations_array.shape[-1] == 4:
            rotations_array = _quat_wxyz_to_matrix(rotations_array)
        return cls(
            skeleton_id=str(skeleton_id or value.get("skeleton_id") or ""),
            joint_names=tuple(names),
            joint_parents=tuple(parents),
            global_positions=np.asarray(positions, dtype=np.float64),
            global_rot_mats=rotations_array,
            root_index=int(value.get("root_index", 0)),
            semantic_roles=tuple(value.get("semantic_roles") or ()),
        )


def _resolve_mapping(
    source: RetargetReferencePose,
    target: RetargetReferencePose,
    mapping: Mapping[str, str] | None,
) -> dict[int, int]:
    source_indices = source.joint_index
    target_indices = target.joint_index
    if mapping is None:
        mapping = {name: name for name in source.joint_names if name in target_indices}
    resolved: dict[int, int] = {}
    for source_name, target_name in mapping.items():
        if source_name not in source_indices:
            raise ValueError(f"Retarget mapping references unknown source joint '{source_name}'.")
        if target_name not in target_indices:
            raise ValueError(f"Retarget mapping references unknown target joint '{target_name}'.")
        resolved[source_indices[source_name]] = target_indices[target_name]
    if source.root_index not in resolved:
        raise ValueError("Retarget mapping must include the source root joint.")
    if resolved[source.root_index] != target.root_index:
        raise ValueError("Retarget mapping must map source root to target root.")
    return resolved


def _motion_scale(source: RetargetReferencePose, target: RetargetReferencePose, mapping: dict[int, int]) -> float:
    ratios = []
    for source_index, target_index in mapping.items():
        source_parent = source.joint_parents[source_index]
        target_parent = target.joint_parents[target_index]
        if source_parent < 0 or target_parent < 0:
            continue
        source_length = np.linalg.norm(source.global_positions[source_index] - source.global_positions[source_parent])
        target_length = np.linalg.norm(target.global_positions[target_index] - target.global_positions[target_parent])
        if source_length > _EPS and target_length > _EPS:
            ratios.append(target_length / source_length)
    return float(np.median(ratios)) if ratios else 1.0


def _normalize_vector(value: Any, label: str) -> np.ndarray:
    vector = np.asarray(value, dtype=np.float64)
    if vector.shape != (3,) or not np.isfinite(vector).all():
        raise ValueError(f"{label} must be a finite three-component vector.")
    length = float(np.linalg.norm(vector))
    if length < _EPS:
        raise ValueError(f"{label} must not be zero-length.")
    return vector / length


def _rotation_from_to(source_direction: np.ndarray, target_direction: np.ndarray) -> np.ndarray:
    """Return the shortest proper rotation taking one unit direction to another."""
    source = _normalize_vector(source_direction, "source_direction")
    target = _normalize_vector(target_direction, "target_direction")
    cosine = float(np.clip(np.dot(source, target), -1.0, 1.0))
    if cosine >= 1.0 - _EPS:
        return np.eye(3, dtype=np.float64)

    axis = np.cross(source, target)
    axis_length = float(np.linalg.norm(axis))
    if axis_length < _EPS:
        # The only remaining case is a 180 degree turn. Pick a deterministic
        # perpendicular axis so repeated calibration produces the same pose.
        basis = np.asarray((1.0, 0.0, 0.0), dtype=np.float64)
        if abs(float(np.dot(source, basis))) > 0.9:
            basis = np.asarray((0.0, 1.0, 0.0), dtype=np.float64)
        axis = _normalize_vector(np.cross(source, basis), "calibration_axis")
        return 2.0 * np.outer(axis, axis) - np.eye(3, dtype=np.float64)

    axis /= axis_length
    skew = np.asarray(
        ((0.0, -axis[2], axis[1]), (axis[2], 0.0, -axis[0]), (-axis[1], axis[0], 0.0)),
        dtype=np.float64,
    )
    return np.eye(3, dtype=np.float64) + skew * axis_length + (skew @ skew) * (1.0 - cosine)


def _descendants_including(reference: RetargetReferencePose, ancestor: int) -> tuple[int, ...]:
    descendants: list[int] = []
    for joint in range(ancestor, len(reference.joint_names)):
        current = joint
        while current >= 0 and current != ancestor:
            current = reference.joint_parents[current]
        if current == ancestor:
            descendants.append(joint)
    return tuple(descendants)


def _require_arm_joint_indices(
    reference: RetargetReferencePose,
    arm_joints: Mapping[str, str],
) -> tuple[tuple[int, int], tuple[int, int]]:
    if not isinstance(arm_joints, Mapping):
        raise ValueError("arm_joints must map canonical arm roles to target joint names.")
    canonical = {str(role).strip().lower(): str(name) for role, name in arm_joints.items()}
    required_roles = (
        "left_upper_arm",
        "left_lower_arm",
        "right_upper_arm",
        "right_lower_arm",
    )
    missing = [role for role in required_roles if not canonical.get(role)]
    if missing:
        raise ValueError(f"arm_joints is missing required roles: {', '.join(missing)}.")

    indices: dict[str, int] = {}
    joint_index = reference.joint_index
    for role in required_roles:
        name = canonical[role]
        if name not in joint_index:
            raise ValueError(f"arm_joints[{role!r}] references unknown target joint '{name}'.")
        indices[role] = joint_index[name]

    pairs = (
        (indices["left_upper_arm"], indices["left_lower_arm"]),
        (indices["right_upper_arm"], indices["right_lower_arm"]),
    )
    used = set()
    for upper, lower in pairs:
        if upper == lower:
            raise ValueError("Each arm upper and lower joint must be different.")
        current = lower
        while current >= 0 and current != upper:
            current = reference.joint_parents[current]
        if current != upper:
            raise ValueError("Each arm lower joint must be a descendant of its upper joint.")
        if upper in used or lower in used:
            raise ValueError("Left and right arm calibration joints must be distinct.")
        used.update((upper, lower))
    return pairs


def calibrate_target_arms_to_tpose(
    reference: RetargetReferencePose,
    arm_joints: Mapping[str, str],
    *,
    up_axis: Any = (0.0, 1.0, 0.0),
) -> RetargetReferencePose:
    """Build a virtual T-pose reference by calibrating only the two arm subtrees.

    ``reference`` remains unchanged.  Each upper-arm subtree is rotated around
    its upper-arm joint until the upper-to-lower-arm direction is horizontal,
    preserving its existing horizontal azimuth and all local offsets.  The
    resulting virtual reference is intended for retargeting only and must be
    rebased to the original target reference before ordinary engine playback.

    ``arm_joints`` explicitly identifies the four target joints with the
    canonical role keys ``left_upper_arm``, ``left_lower_arm``,
    ``right_upper_arm``, and ``right_lower_arm``.  This deliberately avoids
    engine-specific name guessing.
    """
    if reference is None:
        raise ValueError("reference is required.")
    arms = _require_arm_joint_indices(reference, arm_joints)
    up = _normalize_vector(up_axis, "up_axis")
    positions = reference.global_positions.copy()
    rotations = reference.global_rot_mats.copy()

    # Compute both rotations from the original rest pose. The two validated
    # subtrees are disjoint, so applying them cannot influence one another.
    plans: list[tuple[int, np.ndarray]] = []
    for upper, lower in arms:
        arm_direction = reference.global_positions[lower] - reference.global_positions[upper]
        horizontal = arm_direction - up * float(np.dot(arm_direction, up))
        if np.linalg.norm(horizontal) < _EPS:
            raise ValueError(
                f"Cannot calibrate '{reference.joint_names[upper]}' to T-pose: "
                "its upper-to-lower-arm direction has no horizontal component."
            )
        plans.append((upper, _rotation_from_to(arm_direction, horizontal)))

    for upper, rotation in plans:
        pivot = reference.global_positions[upper]
        for joint in _descendants_including(reference, upper):
            positions[joint] = pivot + rotation @ (reference.global_positions[joint] - pivot)
            rotations[joint] = rotation @ reference.global_rot_mats[joint]

    return RetargetReferencePose(
        skeleton_id=reference.skeleton_id,
        joint_names=reference.joint_names,
        joint_parents=reference.joint_parents,
        global_positions=positions,
        global_rot_mats=rotations,
        root_index=reference.root_index,
        semantic_roles=reference.semantic_roles,
    )


def _rotation_fraction(rotation: np.ndarray, fraction: float) -> np.ndarray:
    """Return the fraction-th power of a proper rotation matrix."""
    if fraction <= 0.0:
        return np.eye(3, dtype=np.float64)
    if fraction >= 1.0:
        return rotation
    quaternion = _matrix_to_quat_wxyz(rotation)
    w = float(np.clip(quaternion[0], -1.0, 1.0))
    xyz = quaternion[1:]
    sin_half = float(np.linalg.norm(xyz))
    if sin_half < _EPS:
        return np.eye(3, dtype=np.float64)
    half_angle = float(np.arctan2(sin_half, w))
    axis = xyz / sin_half
    partial = np.concatenate(
        (
            np.asarray((np.cos(half_angle * fraction),), dtype=np.float64),
            axis * np.sin(half_angle * fraction),
        )
    )
    return _quat_wxyz_to_matrix(partial)


def _distribute_unmapped_target_chain_rotation(
    target_current_global: np.ndarray,
    target_reference: RetargetReferencePose,
    target_index_to_source: Mapping[int, int],
) -> None:
    """Spread one mapped descendant rotation over missing target intermediates.

    A source chain such as ``Neck1 -> Neck2 -> Head`` may map onto a target
    chain with only ``neck -> head``.  During the reverse direction the target
    may again contain the extra middle joint.  Keeping all of the accumulated
    rotation on ``Head`` makes its local error large and moves its descendants.
    This distributes the reference-relative rotation over the missing middle
    joints and the mapped descendant.  It is exact when the missing links use
    compatible axes and a useful continuity fallback otherwise.
    """
    references = target_reference.global_rot_mats
    parents = target_reference.joint_parents
    for target_joint in sorted(target_index_to_source):
        path: list[int] = []
        parent = parents[target_joint]
        while parent >= 0 and parent not in target_index_to_source:
            path.append(parent)
            parent = parents[parent]
        if parent < 0 or not path:
            continue

        nearest_mapped_parent = parent
        intermediates = list(reversed(path))
        desired_child_global = target_current_global[target_joint].copy()
        parent_current = target_current_global[nearest_mapped_parent]
        reference_child_relative = references[nearest_mapped_parent].T @ references[target_joint]
        current_child_relative = parent_current.T @ desired_child_global
        accumulated_delta = reference_child_relative.T @ current_child_relative
        chain = intermediates + [target_joint]
        for index, joint in enumerate(chain):
            fraction = float(index + 1) / float(len(chain))
            reference_relative = references[nearest_mapped_parent].T @ references[joint]
            target_current_global[joint] = (
                parent_current @ reference_relative @ _rotation_fraction(accumulated_delta, fraction)
            )


def kmb_global_pose(
    motion: KmbMotion,
    reference: RetargetReferencePose,
) -> tuple[np.ndarray, np.ndarray]:
    """Return global joint positions and rotations for a KMB delta-motion.

    The returned arrays are shaped ``(frames, joints, 3)`` and
    ``(frames, joints, 3, 3)``.  This is also the canonical way for an adapter
    or analysis test to measure end-effector error after retargeting.
    """
    if motion is None:
        raise ValueError("motion is required.")
    if tuple(motion.joint_names) != reference.joint_names:
        raise ValueError("KMB joint_names must match reference joint_names in order.")
    if tuple(motion.joint_parents) != reference.joint_parents:
        raise ValueError("KMB joint_parents must match reference joint_parents.")
    deltas = _quat_wxyz_to_matrix(np.asarray(motion.local_rot_quats, dtype=np.float64))
    expected_shape = (motion.num_frames, len(reference.joint_names), 3, 3)
    if deltas.shape != expected_shape:
        raise ValueError("KMB local_rot_quats shape does not match reference.")

    local_reference = reference.local_rot_mats
    global_positions = np.empty((motion.num_frames, len(reference.joint_names), 3), dtype=np.float64)
    global_rotations = np.empty_like(deltas)
    local_offsets = np.zeros((len(reference.joint_names), 3), dtype=np.float64)
    for joint, parent in enumerate(reference.joint_parents):
        if parent >= 0:
            local_offsets[joint] = reference.global_rot_mats[parent].T @ (
                reference.global_positions[joint] - reference.global_positions[parent]
            )

    roots = np.asarray(motion.root_positions, dtype=np.float64)
    for frame in range(motion.num_frames):
        for joint, parent in enumerate(reference.joint_parents):
            local_rotation = local_reference[joint] @ deltas[frame, joint]
            if parent < 0:
                global_rotations[frame, joint] = local_rotation
                global_positions[frame, joint] = roots[frame]
            else:
                global_rotations[frame, joint] = global_rotations[frame, parent] @ local_rotation
                global_positions[frame, joint] = (
                    global_positions[frame, parent] + global_rotations[frame, parent] @ local_offsets[joint]
                )
    return global_positions, global_rotations


def retarget_kmb_motion(
    motion: KmbMotion,
    source_reference: RetargetReferencePose,
    target_reference: RetargetReferencePose,
    mapping: Mapping[str, str] | None = None,
    *,
    root_motion: str = "scale",
    unmapped_target: str = "inherit",
) -> KmbMotion:
    """Retarget a KMB motion from one reference pose to another.

    ``motion.local_rot_quats`` are interpreted as local rotation deltas from
    ``source_reference``.  ``root_positions`` are world-space positions in the
    source reference coordinate system.  The returned KMB contains target-local
    rotation deltas and target world-space root positions.
    """
    if motion is None:
        raise ValueError("motion is required.")
    if tuple(motion.joint_names) != source_reference.joint_names:
        raise ValueError("KMB joint_names must match source_reference joint_names in order.")
    if tuple(motion.joint_parents) != source_reference.joint_parents:
        raise ValueError("KMB joint_parents must match source_reference joint_parents.")
    if motion.num_frames <= 0:
        raise ValueError("KMB motion must contain at least one frame.")
    if root_motion not in {"scale", "preserve", "in_place"}:
        raise ValueError("root_motion must be 'scale', 'preserve', or 'in_place'.")
    if unmapped_target not in {"inherit", "freeze_global"}:
        raise ValueError("unmapped_target must be 'inherit' or 'freeze_global'.")

    resolved = _resolve_mapping(source_reference, target_reference, mapping)
    target_local_ref = target_reference.local_rot_mats
    source_ref_global = source_reference.global_rot_mats
    target_ref_global = target_reference.global_rot_mats
    _, source_current_global = kmb_global_pose(motion, source_reference)

    target_deltas = np.zeros((motion.num_frames, len(target_reference.joint_names), 4), dtype=np.float64)
    target_deltas[..., 0] = 1.0
    target_current_global = np.empty((motion.num_frames, len(target_reference.joint_names), 3, 3), dtype=np.float64)
    target_index_to_source = {target_index: source_index for source_index, target_index in resolved.items()}
    for frame in range(motion.num_frames):
        for target_joint, target_parent in enumerate(target_reference.joint_parents):
            source_joint = target_index_to_source.get(target_joint)
            if source_joint is None:
                target_current_global[frame, target_joint] = (
                    target_ref_global[target_joint]
                    if target_parent < 0 or unmapped_target == "freeze_global"
                    else target_current_global[frame, target_parent] @ target_local_ref[target_joint]
                )
                continue
            source_delta_global = source_ref_global[source_joint].T @ source_current_global[frame, source_joint]
            target_current_global[frame, target_joint] = target_ref_global[target_joint] @ source_delta_global

        if unmapped_target == "inherit":
            _distribute_unmapped_target_chain_rotation(
                target_current_global[frame], target_reference, target_index_to_source
            )

        # Convert desired target global rotations to target local rotations.
        for target_joint, target_parent in enumerate(target_reference.joint_parents):
            desired_global = target_current_global[frame, target_joint]
            parent_global = (
                target_ref_global[target_parent]
                if target_parent < 0
                else target_current_global[frame, target_parent]
            )
            desired_local = desired_global if target_parent < 0 else parent_global.T @ desired_global
            target_deltas[frame, target_joint] = _matrix_to_quat_wxyz(
                target_local_ref[target_joint].T @ desired_local
            )

    scale = _motion_scale(source_reference, target_reference, resolved) if root_motion == "scale" else 1.0
    source_root_ref = source_reference.global_positions[source_reference.root_index]
    target_root_ref = target_reference.global_positions[target_reference.root_index]
    source_root_basis = source_ref_global[source_reference.root_index]
    target_root_basis = target_ref_global[target_reference.root_index]
    root_positions = np.empty_like(motion.root_positions, dtype=np.float64)
    for frame, source_root in enumerate(np.asarray(motion.root_positions, dtype=np.float64)):
        if root_motion == "in_place":
            root_positions[frame] = target_root_ref
            continue
        delta = source_root_basis.T @ (source_root - source_root_ref)
        root_positions[frame] = target_root_ref + target_root_basis @ (delta * scale)

    contacts = None if motion.foot_contacts is None else np.asarray(motion.foot_contacts, dtype=np.float32).copy()
    return KmbMotion(
        payload=b"",
        model_name=target_reference.skeleton_id,
        fps=float(motion.fps),
        joint_names=target_reference.joint_names,
        joint_parents=target_reference.joint_parents,
        root_positions=root_positions.astype(np.float32),
        local_rot_quats=target_deltas.astype(np.float32),
        foot_contacts=contacts,
    )


def rebase_kmb_motion(
    motion: KmbMotion,
    source_reference: RetargetReferencePose,
    target_reference: RetargetReferencePose,
) -> KmbMotion:
    """Express a KMB delta-motion against another reference of the same skeleton.

    The skeleton layout must be identical.  Absolute local rotations are
    preserved, while their deltas are changed from ``source_reference`` to
    ``target_reference``.  This is the final step after virtual arm-T-pose
    retargeting: it makes the KMB playable against the character's real
    A-pose/bind reference again.
    """
    if motion is None:
        raise ValueError("motion is required.")
    if tuple(motion.joint_names) != source_reference.joint_names:
        raise ValueError("KMB joint_names must match source_reference joint_names in order.")
    if tuple(motion.joint_parents) != source_reference.joint_parents:
        raise ValueError("KMB joint_parents must match source_reference joint_parents.")
    if source_reference.joint_names != target_reference.joint_names:
        raise ValueError("Rebase source and target references must have identical joint_names.")
    if source_reference.joint_parents != target_reference.joint_parents:
        raise ValueError("Rebase source and target references must have identical joint_parents.")

    source_deltas = _quat_wxyz_to_matrix(np.asarray(motion.local_rot_quats, dtype=np.float64))
    expected_shape = (motion.num_frames, len(source_reference.joint_names), 3, 3)
    if source_deltas.shape != expected_shape:
        raise ValueError("KMB local_rot_quats shape does not match source_reference.")
    source_local = source_reference.local_rot_mats
    target_local = target_reference.local_rot_mats
    absolute_local = source_local[None] @ source_deltas
    target_deltas = _matrix_to_quat_wxyz(np.swapaxes(target_local, -1, -2)[None] @ absolute_local)

    source_root = source_reference.root_index
    target_root = target_reference.root_index
    source_root_ref = source_reference.global_positions[source_root]
    target_root_ref = target_reference.global_positions[target_root]
    source_root_basis = source_reference.global_rot_mats[source_root]
    target_root_basis = target_reference.global_rot_mats[target_root]
    root_positions = np.empty_like(motion.root_positions, dtype=np.float64)
    for frame, root_position in enumerate(np.asarray(motion.root_positions, dtype=np.float64)):
        relative_position = source_root_basis.T @ (root_position - source_root_ref)
        root_positions[frame] = target_root_ref + target_root_basis @ relative_position

    contacts = None if motion.foot_contacts is None else np.asarray(motion.foot_contacts, dtype=np.float32).copy()
    return KmbMotion(
        payload=b"",
        model_name=target_reference.skeleton_id,
        fps=float(motion.fps),
        joint_names=target_reference.joint_names,
        joint_parents=target_reference.joint_parents,
        root_positions=root_positions.astype(np.float32),
        local_rot_quats=target_deltas.astype(np.float32),
        foot_contacts=contacts,
    )


def retarget_kmb_motion_with_target_arm_calibration(
    motion: KmbMotion,
    source_reference: RetargetReferencePose,
    target_reference: RetargetReferencePose,
    arm_joints: Mapping[str, str],
    mapping: Mapping[str, str] | None = None,
    *,
    root_motion: str = "scale",
    unmapped_target: str = "inherit",
    up_axis: Any = (0.0, 1.0, 0.0),
) -> KmbMotion:
    """Retarget through a virtual target T-pose, then rebase to its real pose.

    Only the target arm subtrees named by ``arm_joints`` are calibrated.  The
    returned motion is relative to the original ``target_reference`` and is
    therefore suitable for normal Unity, Cocos, or Godot playback adapters.
    """
    calibrated_target = calibrate_target_arms_to_tpose(target_reference, arm_joints, up_axis=up_axis)
    calibrated_motion = retarget_kmb_motion(
        motion,
        source_reference,
        calibrated_target,
        mapping,
        root_motion=root_motion,
        unmapped_target=unmapped_target,
    )
    return rebase_kmb_motion(calibrated_motion, calibrated_target, target_reference)
