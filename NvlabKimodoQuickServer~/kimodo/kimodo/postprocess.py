# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Post-processing utilities for motion generation output."""

from types import SimpleNamespace
from typing import Dict, List, Optional, Tuple

import numpy as np
import torch

from .constraints import (
    ClipConstraintSet,
    EndEffectorConstraintSet,
    FullBodyConstraintSet,
    Root2DConstraintSet,
    compute_global_heading,
)
from .geometry import matrix_to_quaternion, quaternion_to_matrix
from .skeleton import (
    G1Skeleton34,
    SkeletonBase,
    SMPLXSkeleton22,
    SOMASkeleton30,
    SOMASkeleton77,
    fk,
)


_CONSTRAINT_PRIORITY = {
    "root2d": 4,
    "end-effector": 3,
    "left-hand": 3,
    "right-hand": 3,
    "left-foot": 3,
    "right-foot": 3,
    "fullbody": 2,
    "clip": 1,
}


def _constraint_priority(constraint) -> int:
    return _CONSTRAINT_PRIORITY.get(getattr(constraint, "name", ""), 0)


def _constraint_rows_without_frames(constraint, covered_frames: torch.Tensor):
    """Drop rows already represented by a merged FullBody constraint."""
    frame_indices = constraint.frame_indices
    if not isinstance(frame_indices, torch.Tensor):
        frame_indices = torch.as_tensor(frame_indices, dtype=torch.long)
    keep = ~torch.isin(frame_indices, covered_frames.to(frame_indices.device))
    if bool(keep.all()):
        return constraint
    if not bool(keep.any()):
        return None

    frame_indices = frame_indices[keep]
    if isinstance(constraint, Root2DConstraintSet):
        heading = constraint.global_root_heading
        if heading is not None:
            heading = heading[keep]
        return Root2DConstraintSet(
            constraint.skeleton,
            frame_indices,
            constraint.smooth_root_2d[keep],
            global_root_heading=heading,
        )
    if isinstance(constraint, EndEffectorConstraintSet):
        args = (
            constraint.skeleton,
            frame_indices,
            constraint.global_joints_positions[keep],
            constraint.global_joints_rots[keep],
            constraint.smooth_root_2d[keep],
        )
        if type(constraint) is EndEffectorConstraintSet:
            return type(constraint)(*args, joint_names=list(constraint.joint_names))
        return type(constraint)(*args)
    if isinstance(constraint, ClipConstraintSet):
        return ClipConstraintSet(
            constraint.skeleton,
            frame_indices,
            constraint.global_joints_positions[keep],
            constraint.global_joints_rots[keep],
            constraint.position_axis_mask,
            constraint.rot_indices,
            root_position_axes=constraint.root_position_axes,
            root_heading=constraint.root_heading,
        )
    return constraint


def _merge_fullbody_constraint(
    constraint_lst: List,
    skeleton: SkeletonBase,
    baseline_global_rots: torch.Tensor,
    baseline_positions: torch.Tensor,
    num_frames: int,
) -> List:
    """Resolve overlapping constraints into one FullBody target per FullBody frame.

    The native MotionCorrection API has one full-body mask and cannot represent
    per-channel priority.  Merge the higher-priority target into the FullBody
    sample before calling it, then remove the original overlapping rows.
    """
    if not constraint_lst:
        return []

    fullbody_frames = sorted(
        {
            int(frame)
            for constraint in constraint_lst
            if isinstance(constraint, FullBodyConstraintSet)
            for frame in constraint.frame_indices.detach().cpu().tolist()
            if 0 <= int(frame) < num_frames
        }
    )
    if not fullbody_frames:
        return list(constraint_lst)

    device = baseline_positions.device
    frames = torch.tensor(fullbody_frames, dtype=torch.long, device=device)
    frame_to_row = {frame: row for row, frame in enumerate(fullbody_frames)}
    positions = baseline_positions[frames].clone()
    global_rots = baseline_global_rots[frames].clone()
    position_priority = torch.zeros(
        positions.shape, dtype=torch.int16, device=device
    )
    rotation_priority = torch.zeros(
        (len(frames), positions.shape[1]), dtype=torch.int16, device=device
    )
    heading_priority = torch.zeros(len(frames), dtype=torch.int16, device=device)
    desired_heading = torch.zeros((len(frames), 2), dtype=positions.dtype, device=device)

    def row_for(frame: int):
        return frame_to_row.get(frame)

    def assign_position(row: int, joint: int, value: torch.Tensor, rank: int, axes=None):
        axes = (0, 1, 2) if axes is None else axes
        for axis in axes:
            if rank > int(position_priority[row, joint, axis]):
                positions[row, joint, axis] = value[axis]
                position_priority[row, joint, axis] = rank

    def assign_rotation(row: int, joint: int, value: torch.Tensor, rank: int):
        if rank > int(rotation_priority[row, joint]):
            global_rots[row, joint] = value
            rotation_priority[row, joint] = rank

    for constraint in constraint_lst:
        rank = _constraint_priority(constraint)
        if rank <= 0:
            continue
        name = getattr(constraint, "name", "")
        rows = constraint.frame_indices.detach().cpu().tolist()
        for source_row, frame in enumerate(rows):
            row = row_for(int(frame))
            if row is None:
                continue
            if isinstance(constraint, FullBodyConstraintSet):
                source_positions = constraint.global_joints_positions[source_row].to(device=device)
                source_rots = constraint.global_joints_rots[source_row].to(device=device)
                for joint in range(positions.shape[1]):
                    assign_position(row, joint, source_positions[joint], rank)
                    assign_rotation(row, joint, source_rots[joint], rank)
                heading_priority[row] = max(int(heading_priority[row]), rank)
            elif isinstance(constraint, EndEffectorConstraintSet):
                source_positions = constraint.global_joints_positions[source_row].to(device=device)
                source_rots = constraint.global_joints_rots[source_row].to(device=device)
                for joint in constraint.pos_indices.detach().cpu().tolist():
                    assign_position(row, int(joint), source_positions[int(joint)], rank)
                for joint in constraint.rot_indices.detach().cpu().tolist():
                    assign_rotation(row, int(joint), source_rots[int(joint)], rank)
                if "Hips" in constraint.joint_names:
                    heading_priority[row] = max(int(heading_priority[row]), rank)
            elif isinstance(constraint, Root2DConstraintSet):
                root = skeleton.root_idx
                source_root = constraint.smooth_root_2d[source_row].to(device=device)
                assign_position(row, root, torch.stack((source_root[0], positions[row, root, 1], source_root[1])), rank, axes=(0, 2))
                if constraint.global_root_heading is not None and rank > int(heading_priority[row]):
                    desired_heading[row] = constraint.global_root_heading[source_row].to(device=device)
                    heading_priority[row] = rank
            elif isinstance(constraint, ClipConstraintSet):
                source_positions = constraint.global_joints_positions[source_row].to(device=device)
                source_rots = constraint.global_joints_rots[source_row].to(device=device)
                root_axes = constraint.root_position_axes.detach().cpu().tolist()
                for axis, enabled in enumerate(root_axes):
                    if enabled:
                        assign_position(row, skeleton.root_idx, source_positions[skeleton.root_idx], rank, axes=(axis,))
                for joint, axis in constraint.position_axis_mask.nonzero(as_tuple=False).detach().cpu().tolist():
                    assign_position(row, int(joint), source_positions[int(joint)], rank, axes=(int(axis),))
                for joint in constraint.rot_indices.detach().cpu().tolist():
                    assign_rotation(row, int(joint), source_rots[int(joint)], rank)
                if constraint.root_heading and rank > int(heading_priority[row]):
                    desired_heading[row] = constraint.global_root_heading[source_row].to(device=device)
                    heading_priority[row] = rank

    # Root heading is a global orientation. Rotate the merged FK target around its root.
    for row in range(len(frames)):
        if int(heading_priority[row]) < _CONSTRAINT_PRIORITY["root2d"]:
            continue
        current_heading = compute_global_heading(positions[row : row + 1], skeleton)[0]
        current_yaw = torch.atan2(current_heading[1], current_heading[0])
        target_yaw = torch.atan2(desired_heading[row, 1], desired_heading[row, 0])
        delta = target_yaw - current_yaw
        cos_delta, sin_delta = torch.cos(delta), torch.sin(delta)
        rotation = torch.stack(
            (
                torch.stack((cos_delta, torch.zeros_like(delta), sin_delta)),
                torch.stack((torch.zeros_like(delta), torch.ones_like(delta), torch.zeros_like(delta))),
                torch.stack((-sin_delta, torch.zeros_like(delta), cos_delta)),
            )
        )
        root = positions[row, skeleton.root_idx].clone()
        positions[row] = (positions[row] - root) @ rotation.T + root
        global_rots[row] = rotation @ global_rots[row]

    merged = FullBodyConstraintSet(skeleton, frames, positions, global_rots)
    output = [merged]
    covered = frames.detach().cpu()
    for constraint in constraint_lst:
        if isinstance(constraint, FullBodyConstraintSet):
            continue
        filtered = _constraint_rows_without_frames(constraint, covered)
        if filtered is not None:
            output.append(filtered)
    return output


def extract_input_motion_from_constraints(
    constraint_lst: List,
    skeleton: SkeletonBase,
    num_frames: int,
    num_joints: int,
) -> Tuple[torch.Tensor, torch.Tensor]:
    """Extract hip translations and local rotations from constraints for postprocessing.

    Args:
        constraint_lst: List of constraints (FullBodyConstraintSet, EndEffectorConstraintSet, etc.)
        skeleton: Skeleton instance
        num_frames: Total number of frames in the motion
        num_joints: Number of joints

    Returns:
        Tuple of (hip_translations_input, rotations_input):
            - hip_translations_input: Hip translations, shape (T, 3)
            - rotations_input: Local joint rotations as quaternions, shape (T, J, 4)
    """
    # Initialize with zeros for all frames
    hip_translations_input = torch.zeros(num_frames, 3)
    rotations_input = torch.zeros(num_frames, num_joints, 4)
    rotations_input[..., 0] = 1.0  # Initialize as identity quaternions (w=1, x=y=z=0)

    def _match_hip_dtype(tensor: torch.Tensor) -> torch.Tensor:
        return tensor.to(device=hip_translations_input.device, dtype=hip_translations_input.dtype)

    def _match_rot_dtype(tensor: torch.Tensor) -> torch.Tensor:
        return tensor.to(device=rotations_input.device, dtype=rotations_input.dtype)

    if not constraint_lst:
        return hip_translations_input, rotations_input

    # Sort constraints to ensure FullBodyConstraintSet is processed last
    #   This ensures it will get the last say on whether hip translations need to be exact root or smoothed root
    sorted_constraints = sorted(constraint_lst, key=lambda c: isinstance(c, FullBodyConstraintSet))
    for constraint in sorted_constraints:
        frame_indices = constraint.frame_indices
        if isinstance(frame_indices, torch.Tensor):
            valid_mask = frame_indices < num_frames
            if valid_mask.sum() == 0:
                continue
            frame_indices = frame_indices[valid_mask]
        else:
            valid_positions = [i for i, idx in enumerate(frame_indices) if idx < num_frames]
            if not valid_positions:
                continue
            frame_indices = [frame_indices[i] for i in valid_positions]

        # Handle Root2DConstraintSet separately - only assign smooth_root_2d at xz dimensions
        if isinstance(constraint, Root2DConstraintSet):
            smooth_root_2d = constraint.smooth_root_2d  # (K, 2) where K = len(frame_indices)
            if isinstance(frame_indices, torch.Tensor):
                smooth_root_2d = smooth_root_2d[valid_mask]
            else:
                smooth_root_2d = smooth_root_2d[valid_positions]
            smooth_root_2d = _match_hip_dtype(smooth_root_2d)
            hip_translations_input[frame_indices, 0] = smooth_root_2d[:, 0]  # x
            hip_translations_input[frame_indices, 2] = smooth_root_2d[:, 1]  # z
            continue
        elif isinstance(constraint, FullBodyConstraintSet) or isinstance(constraint, EndEffectorConstraintSet):
            global_rots = constraint.global_joints_rots  # (K, J, 3, 3) where K = len(frame_indices)
            global_positions = constraint.global_joints_positions  # (K, J, 3)
            if isinstance(frame_indices, torch.Tensor):
                global_rots = global_rots[valid_mask]
                global_positions = global_positions[valid_mask]
                smooth_root_2d = constraint.smooth_root_2d[valid_mask]
            else:
                global_rots = global_rots[valid_positions]
                global_positions = global_positions[valid_positions]
                smooth_root_2d = constraint.smooth_root_2d[valid_positions]

            root_positions = global_positions[:, skeleton.root_idx]  # (K, 3)
            # replace xz with smooth_root_2d values for EE constraints that do not include Hips
            #    since the hips themselves are not actually constrained in the model conditioning
            if isinstance(constraint, EndEffectorConstraintSet) and "Hips" not in constraint.joint_names:
                root_positions[:, 0] = smooth_root_2d[:, 0]  # x
                root_positions[:, 2] = smooth_root_2d[:, 1]  # z

            local_rot_mats = skeleton.global_rots_to_local_rots(global_rots)  # (K, J, 3, 3)
            local_rot_quats = matrix_to_quaternion(local_rot_mats)  # (K, J, 4)

            hip_translations_input[frame_indices] = _match_hip_dtype(root_positions)
            rotations_input[frame_indices] = _match_rot_dtype(local_rot_quats)
        else:
            NotImplementedError(f"Constraint {constraint.name} is not supported")

    return hip_translations_input, rotations_input


def create_working_rig_from_skeleton(
    skeleton: SkeletonBase, above_ground_offset: float = 0.007
) -> List[SimpleNamespace]:
    """Create the working rig as a list of SimpleNamespace objects from skeleton.

    Args:
        skeleton: SkeletonBase instance with bone_order_names, neutral_joints, joint_parents
        above_ground_offset: Additional offset to position the rig slightly above ground
    Returns:
        List of SimpleNamespace objects representing the working rig
    """
    working_rig_joints = []

    joint_names = skeleton.bone_order_names
    neutral_positions = skeleton.neutral_joints.cpu().numpy()
    parent_indices = skeleton.joint_parents.cpu().numpy()

    if isinstance(skeleton, (G1Skeleton34, SMPLXSkeleton22)):
        retarget_map = {
            skeleton.bone_order_names[skeleton.root_idx]: "Hips",
            skeleton.left_hand_joint_names[0]: "LeftHand",
            skeleton.right_hand_joint_names[0]: "RightHand",
            skeleton.left_foot_joint_names[0]: "LeftFoot",
            skeleton.right_foot_joint_names[0]: "RightFoot",
        }
    else:
        # works for SOMA
        retarget_map = {
            "Hips": "Hips",
            "Head": "Head",
            "LeftHand": "LeftHand",
            "RightHand": "RightHand",
            "LeftFoot": "LeftFoot",
            "RightFoot": "RightFoot",
        }

    for i, joint_name in enumerate(joint_names):
        parent_name = None if parent_indices[i] == -1 else joint_names[parent_indices[i]]

        # Calculate local translation relative to parent
        if parent_indices[i] == -1:
            # Move the rig so that the lowest point (toe) is at ground level (y=0),
            # plus a small offset to position the rig slightly above ground
            toe_height = neutral_positions[:, 1].min()  # lowest y-coordinate (toe)
            local_translation = (
                neutral_positions[i] + np.array([0.0, -toe_height + above_ground_offset, 0.0])
            ).tolist()
        else:
            parent_idx = parent_indices[i]
            parent_position = neutral_positions[parent_idx]
            joint_position = neutral_positions[i]
            local_translation = (joint_position - parent_position).tolist()

        # Default rotation (identity quaternion: x=0, y=0, z=0, w=1)
        default_rotation = [0.0, 0.0, 0.0, 1.0]

        joint_info = SimpleNamespace(
            name=joint_name,
            parent=parent_name,
            t_pose_rotation=default_rotation,
            t_pose_translation=local_translation,
            retarget_tag=retarget_map.get(joint_name),
        )

        working_rig_joints.append(joint_info)

    return working_rig_joints


def post_process_motion(
    local_rot_mats: torch.Tensor,
    root_positions: torch.Tensor,
    contacts: torch.Tensor,
    skeleton: SkeletonBase,
    constraint_lst: Optional[List] = None,
    contact_threshold: float = 0.5,
    root_margin: float = 0.04,
) -> Dict[str, torch.Tensor]:
    """Post-process generated motion to reduce foot skating and improve quality.

    Args:
        local_rot_mats: Local joint rotation matrices, shape (B, T, J, 3, 3)
        root_positions: Root joint positions, shape (B, T, 3)
        contacts: Foot contact labels, shape (B, T, num_contacts)
        skeleton: Skeleton instance
        constraint_lst: Optional list of constraints (or list of lists of constraints for batched inference)(FullBodyConstraintSet, etc.)
        contact_threshold: Threshold for foot contact detection
        root_margin: Margin for root position correction

    Returns:
        Dictionary with corrected motion data:
            - local_rot_mats: Corrected local rotation matrices (B, T, J, 3, 3)
            - root_positions: Corrected root positions (B, T, 3)
            - posed_joints: Corrected global joint positions (B, T, J, 3)
            - global_rot_mats: Corrected global rotation matrices (B, T, J, 3, 3)
    """
    # Ensure batch dimension
    assert local_rot_mats.dim() == 5, "local_rot_mats should be 5D, make sure to include the batch dimension"

    batch_size, num_frames, num_joints = local_rot_mats.shape[:3]

    batched_constraints = bool(constraint_lst) and isinstance(constraint_lst[0], list)
    effective_constraint_lst = []
    if constraint_lst:
        # Compute the fallback target on the model's device, then move it to
        # CPU because MotionCorrection receives CPU tensors.
        baseline_global_rots, baseline_positions, _ = fk(
            local_rot_mats.detach(),
            root_positions.detach(),
            skeleton,
        )
        baseline_global_rots = baseline_global_rots.cpu()
        baseline_positions = baseline_positions.cpu()
        if batched_constraints:
            effective_constraint_lst = [
                _merge_fullbody_constraint(
                    constraint_lst[b],
                    skeleton,
                    baseline_global_rots[b],
                    baseline_positions[b],
                    num_frames,
                )
                for b in range(batch_size)
            ]
        else:
            effective_constraint_lst = _merge_fullbody_constraint(
                constraint_lst,
                skeleton,
                baseline_global_rots[0],
                baseline_positions[0],
                num_frames,
            )

    def _build_constraint_masks_dict(constraints: List) -> Dict[str, torch.Tensor]:
        out = {
            key: torch.zeros(num_frames, dtype=torch.float32)
            for key in [
                "FullBody",
                "LeftFoot",
                "RightFoot",
                "LeftHand",
                "RightHand",
                "Root",
            ]
        }
        for constraint in constraints:
            frame_indices = constraint.frame_indices
            if isinstance(frame_indices, torch.Tensor):
                frame_indices = frame_indices[frame_indices < num_frames]
                if frame_indices.numel() == 0:
                    continue
            else:
                frame_indices = [idx for idx in frame_indices if idx < num_frames]
                if not frame_indices:
                    continue
            if constraint.name == "fullbody":
                out["FullBody"][frame_indices] = 1.0
            elif constraint.name == "left-foot":
                out["LeftFoot"][frame_indices] = 1.0
            elif constraint.name == "right-foot":
                out["RightFoot"][frame_indices] = 1.0
            elif constraint.name == "left-hand":
                out["LeftHand"][frame_indices] = 1.0
            elif constraint.name == "right-hand":
                out["RightHand"][frame_indices] = 1.0
            elif constraint.name == "root2d":
                out["Root"][frame_indices] = 1.0
        return out

    # Create constraint masks from the priority-resolved constraints.
    if batched_constraints:
        constraint_masks_dict_lst = [
            _build_constraint_masks_dict(effective_constraint_lst[b])
            for b in range(batch_size)
        ]
    else:
        constraint_masks_dict = (
            _build_constraint_masks_dict(effective_constraint_lst)
            if effective_constraint_lst
            else {
                key: torch.zeros(num_frames, dtype=torch.float32)
                for key in [
                    "FullBody",
                    "LeftFoot",
                    "RightFoot",
                    "LeftHand",
                    "RightHand",
                    "Root",
                ]
            }
        )

    # Create working rig
    above_ground_offset = 0.02 if isinstance(skeleton, (SOMASkeleton30, SOMASkeleton77)) else 0.007
    # larger offset for SOMA since model tends to generate lower to the ground
    working_rig = create_working_rig_from_skeleton(skeleton, above_ground_offset=above_ground_offset)
    has_double_ankle_joints = isinstance(skeleton, G1Skeleton34)

    # Prepare input tensors. The generated motion will be modified in place. Clone first.
    hip_translations_corrected = root_positions.cpu().clone()
    rotations_corrected = matrix_to_quaternion(local_rot_mats).cpu().clone()  # (B, T, J, 4)
    contacts = contacts.cpu()

    # Extract input motion (target keyframes) from constraints for each batch
    # For constrained keyframes, use the original motion from constraints
    # For non-constrained frames, zeros are used
    hip_translations_input = torch.zeros(batch_size, num_frames, 3)
    rotations_input = torch.zeros(batch_size, num_frames, num_joints, 4)
    rotations_input[..., 0] = 1.0  # Initialize as identity quaternions (w=1, x=y=z=0)

    if effective_constraint_lst:
        for b in range(batch_size):
            constraints_lst_el = (
                effective_constraint_lst[b]
                if batched_constraints
                else effective_constraint_lst
            )
            hip_translations_input[b], rotations_input[b] = extract_input_motion_from_constraints(
                constraints_lst_el,
                skeleton,
                num_frames,
                num_joints,
            )

    # Call the motion correction for each batch (optional package)
    try:
        from motion_correction import motion_postprocess
    except ImportError as e:
        print(
            "[WARN] motion_correction package is unavailable; skipping optional motion correction postprocess. "
            f"({type(e).__name__}: {e})"
        )
        local_rot_mats_corrected = quaternion_to_matrix(rotations_corrected)
        device = local_rot_mats.device
        global_rot_mats, posed_joints, _ = fk(
            local_rot_mats_corrected.to(device),
            hip_translations_corrected.to(device),
            skeleton,
        )
        return {
            "local_rot_mats": local_rot_mats_corrected.to(device),
            "global_rot_mats": global_rot_mats,
            "posed_joints": posed_joints,
            "root_positions": hip_translations_corrected.to(device),
        }
    for b in range(batch_size):
        masks_b = constraint_masks_dict_lst[b] if batched_constraints else constraint_masks_dict
        motion_postprocess.correct_motion(
            hip_translations_corrected[b : b + 1],
            rotations_corrected[b : b + 1],
            contacts[b : b + 1],
            hip_translations_input[b : b + 1],
            rotations_input[b : b + 1],
            masks_b,
            contact_threshold,
            root_margin,
            working_rig,
            has_double_ankle_joints,
        )

    local_rot_mats_corrected = quaternion_to_matrix(rotations_corrected)

    # Compute posed joints using FK
    device = local_rot_mats.device
    global_rot_mats, posed_joints, _ = fk(
        local_rot_mats_corrected.to(device),
        hip_translations_corrected.to(device),
        skeleton,
    )

    result = {
        "local_rot_mats": local_rot_mats_corrected.to(device),
        "root_positions": hip_translations_corrected.to(device),
        "posed_joints": posed_joints,
        "global_rot_mats": global_rot_mats,
    }

    return result
