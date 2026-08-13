# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Constraint sets for conditioning motion generation (root 2D, full body, end-effectors)."""

from typing import Optional, Union

import torch
from torch import Tensor

from kimodo.motion_rep.feature_utils import RotateFeatures, compute_heading_angle
from kimodo.skeleton import SkeletonBase, SOMASkeleton30, SOMASkeleton77
from kimodo.tools import ensure_batched, load_json, save_json

from .geometry import axis_angle_to_matrix, matrix_to_axis_angle


def _convert_constraint_local_rots_to_skeleton(local_rot_mats: Tensor, skeleton: SkeletonBase) -> Tensor:
    """Convert loaded local rotation matrices to match the skeleton's joint count.

    Handles SOMA 30↔77: constraint files may have been saved with 30 or 77 joints while the session
    skeleton (e.g. from the SOMA30 model) uses SOMASkeleton77.
    """
    n_joints = local_rot_mats.shape[-3]
    skeleton_joints = skeleton.nbjoints
    if n_joints == skeleton_joints:
        return local_rot_mats
    if n_joints == 77 and skeleton_joints == 30 and isinstance(skeleton, SOMASkeleton30):
        return skeleton.from_SOMASkeleton77(local_rot_mats)
    if n_joints == 30 and skeleton_joints == 77 and isinstance(skeleton, SOMASkeleton77):
        skel30 = SOMASkeleton30()
        return skel30.to_SOMASkeleton77(local_rot_mats)
    raise ValueError(
        f"Constraint joint count ({n_joints}) does not match skeleton joint count "
        f"({skeleton_joints}). Only SOMA 30↔77 conversion is supported."
    )


def create_pairs(tensor_A: Tensor, tensor_B: Tensor) -> Tensor:
    """Form all (a, b) pairs from two 1D tensors; output shape (len(A)*len(B), 2)."""
    pairs = torch.stack(
        (
            tensor_A[:, None].expand(-1, len(tensor_B)),
            tensor_B.expand(len(tensor_A), -1),
        ),
        dim=-1,
    ).reshape(-1, 2)
    return pairs


def compute_global_heading(global_joints_positions: Tensor, skeleton: SkeletonBase) -> Tensor:
    """Compute global root heading (cos, sin) from global joint positions using skeleton."""
    root_heading_angle = compute_heading_angle(global_joints_positions, skeleton)
    global_root_heading = torch.stack([torch.cos(root_heading_angle), torch.sin(root_heading_angle)], dim=-1)
    return global_root_heading


def _root_2d_attribute(constraint) -> str:
    if hasattr(constraint, "smooth_root_2d"):
        return "smooth_root_2d"
    if hasattr(constraint, "root_2d"):
        return "root_2d"
    raise AttributeError(f"{type(constraint).__name__} has no planar root constraint.")


def transform_constraints_to_origin(constraints_lst: list, transform) -> None:
    if transform is None:
        return

    translation, yaw = transform
    for constraint in constraints_lst:
        if hasattr(constraint, "transform_to_origin"):
            constraint.transform_to_origin(transform)
            continue
        root_attribute = _root_2d_attribute(constraint)
        root_2d = getattr(constraint, root_attribute)
        device = root_2d.device
        dtype = root_2d.dtype
        local_translation = translation.to(device=device, dtype=dtype)
        local_yaw = yaw.to(device=device, dtype=dtype)
        local_rotation = RotateFeatures((-local_yaw).reshape(1))
        heading_rotation_2d_t = local_rotation.corrective_mat_2d_T[0]
        rotation_3d = local_rotation.corrective_mat_Y[0]
        rotation_3d_t = local_rotation.corrective_mat_Y_T[0]
        # Root positions are stored as (x, z), while heading vectors are stored
        # as (cos(yaw), sin(yaw)).  Those two pairs use opposite matrix layouts;
        # using the heading matrix for root positions mirrors the trajectory at
        # non-zero anchor yaw.  Take the x/z block from the 3D position rotation
        # so root_2d stays in the same space as global_joints_positions.
        root_rotation_2d_t = rotation_3d_t[[0, 2]][:, [0, 2]]
        setattr(constraint, root_attribute, (root_2d - local_translation) @ root_rotation_2d_t)

        heading = getattr(constraint, "global_root_heading", None)
        if heading is not None:
            constraint.global_root_heading = (
                heading - local_yaw
                if heading.ndim == 1
                else heading @ heading_rotation_2d_t
            )
        if hasattr(constraint, "global_joints_positions"):
            offset = torch.zeros(3, device=device, dtype=dtype)
            offset[[0, 2]] = local_translation
            constraint.global_joints_positions = (constraint.global_joints_positions - offset) @ rotation_3d_t
            constraint.global_joints_rots = rotation_3d @ constraint.global_joints_rots
            if root_attribute == "smooth_root_2d":
                constraint.global_root_heading = compute_global_heading(
                    constraint.global_joints_positions, constraint.skeleton
                )


def normalize_constraints_to_anchor(constraints_lst: list):
    """Move Kimodo or ARDY constraints into one planar anchor space.

    The earliest constrained frame wins. At that frame the composition order is
    FullBody, Root2D, then end-effectors; input order breaks ties. Y is preserved.
    Returns the planar ``(x, z)`` translation and yaw needed to restore output.
    """
    candidates = []
    priority = {
        "clip": 1,
        "fullbody": 2,
        "root2d": 3,
        "end-effector": 4,
        "left-hand": 4,
        "right-hand": 4,
        "left-foot": 4,
        "right-foot": 4,
    }
    for order, constraint in enumerate(constraints_lst or []):
        if len(constraint.frame_indices) == 0:
            continue
        rank = priority.get(getattr(constraint, "name", ""), 0)
        if getattr(constraint, "name", "") == "clip" and not getattr(constraint, "root_position", False):
            rank = 0
        if rank:
            candidates.append((int(constraint.frame_indices.min().item()), -rank, order, constraint))
    if not candidates:
        return None

    anchor_frame, _, _, anchor = min(candidates, key=lambda item: item[:3])
    matches = (anchor.frame_indices == anchor_frame).nonzero(as_tuple=False).flatten()
    anchor_row = int(matches[0].item())
    root_attribute = _root_2d_attribute(anchor)
    translation = getattr(anchor, root_attribute)[anchor_row].detach().clone()
    heading = getattr(anchor, "global_root_heading", None)
    yaw = (
        (
            heading[anchor_row]
            if heading.ndim == 1
            else torch.atan2(heading[anchor_row, 1], heading[anchor_row, 0])
        )
        .detach()
        .clone()
        if heading is not None
        else torch.zeros((), device=translation.device, dtype=translation.dtype)
    )
    transform = translation, yaw
    transform_constraints_to_origin(constraints_lst, transform)
    return transform


def _tensor_to(
    t: Tensor,
    device: Optional[Union[str, torch.device]] = None,
    dtype: Optional[torch.dtype] = None,
) -> Tensor:
    """Move tensor to device and/or dtype.

    Returns same tensor if no args.
    """
    if device is not None and dtype is not None:
        return t.to(device=device, dtype=dtype)
    if device is not None:
        return t.to(device=device)
    if dtype is not None:
        return t.to(dtype=dtype)
    return t


class Root2DConstraintSet:
    """Constraint set fixing root (x, z) trajectory and optionally global heading on given
    frames."""

    name = "root2d"

    def __init__(
        self,
        skeleton: SkeletonBase,
        frame_indices: Tensor,
        smooth_root_2d: Tensor,
        to_crop: bool = False,
        global_root_heading: Optional[Tensor] = None,
    ) -> None:
        self.skeleton = skeleton

        # if we pass the full smooth root 3D as input
        if smooth_root_2d.shape[-1] == 3:
            smooth_root_2d = smooth_root_2d[..., [0, 1]]

        if to_crop:
            smooth_root_2d = smooth_root_2d[frame_indices]
            if global_root_heading is not None:
                global_root_heading = global_root_heading[frame_indices]
        else:
            assert len(smooth_root_2d) == len(
                frame_indices
            ), "The number of smooth root 2d should be match the number of frames"
            if global_root_heading is not None:
                assert len(global_root_heading) == len(
                    frame_indices
                ), "The number of global root heading should be match the number of frames"

        self.smooth_root_2d = smooth_root_2d
        self.global_root_heading = global_root_heading
        self.frame_indices = frame_indices

    def update_constraints(self, data_dict: dict, index_dict: dict) -> None:
        """Append this constraint's smooth_root_2d (and optional global_root_heading) to data/index
        dicts."""
        data_dict["smooth_root_2d"].append(self.smooth_root_2d)
        index_dict["smooth_root_2d"].append(self.frame_indices)

        if self.global_root_heading is not None:
            # constraint the global heading
            data_dict["global_root_heading"].append(self.global_root_heading)
            index_dict["global_root_heading"].append(self.frame_indices)

    def crop_move(self, start: int, end: int) -> "Root2DConstraintSet":
        """Return a new constraint set for the cropped frame range [start, end)."""
        mask = (self.frame_indices >= start) & (self.frame_indices < end)

        if self.global_root_heading is not None:
            masked_global_root_heading = self.global_root_heading[mask]
        else:
            masked_global_root_heading = None

        return Root2DConstraintSet(
            self.skeleton,
            self.frame_indices[mask] - start,
            self.smooth_root_2d[mask],
            global_root_heading=masked_global_root_heading,
        )

    def get_save_info(self) -> dict:
        """Return a dict suitable for JSON serialization (frame_indices, smooth_root_2d, optional
        global_root_heading)."""
        out = {
            "type": self.name,
            "frame_indices": self.frame_indices,
            "smooth_root_2d": self.smooth_root_2d,
        }
        if self.global_root_heading is not None:
            out["global_root_heading"] = self.global_root_heading
        return out

    def to(
        self,
        device: Optional[Union[str, torch.device]] = None,
        dtype: Optional[torch.dtype] = None,
    ) -> "Root2DConstraintSet":
        self.smooth_root_2d = _tensor_to(self.smooth_root_2d, device, dtype)
        self.frame_indices = _tensor_to(self.frame_indices, device, dtype)
        if self.global_root_heading is not None:
            self.global_root_heading = _tensor_to(self.global_root_heading, device, dtype)
        if device is not None and hasattr(self.skeleton, "to"):
            self.skeleton = self.skeleton.to(device)
        return self

    @classmethod
    def from_dict(cls, skeleton: SkeletonBase, dico: dict) -> "Root2DConstraintSet":
        """Build a Root2DConstraintSet from a dict (e.g. loaded from JSON)."""
        device = skeleton.device if hasattr(skeleton, "device") else "cpu"

        if "global_root_heading" in dico:
            global_root_heading = torch.tensor(dico["global_root_heading"], device=device)
        else:
            global_root_heading = None

        return cls(
            skeleton,
            frame_indices=torch.tensor(dico["frame_indices"], device=device, dtype=torch.long),
            smooth_root_2d=torch.tensor(dico["smooth_root_2d"], device=device),
            global_root_heading=global_root_heading,
        )


class FullBodyConstraintSet:
    """Constraint set fixing full-body global positions and rotations on given keyframes."""

    name = "fullbody"

    def __init__(
        self,
        skeleton: SkeletonBase,
        frame_indices: Tensor,
        global_joints_positions: Tensor,
        global_joints_rots: Tensor,
        smooth_root_2d: Optional[Tensor] = None,
        to_crop: bool = False,
    ):
        self.skeleton = skeleton
        self.frame_indices = frame_indices

        # if we pass the full smooth root 3D as input
        if smooth_root_2d is not None and smooth_root_2d.shape[-1] == 3:
            smooth_root_2d = smooth_root_2d[..., [0, 1]]

        if to_crop:
            global_joints_positions = global_joints_positions[frame_indices]
            global_joints_rots = global_joints_rots[frame_indices]
            if smooth_root_2d is not None:
                smooth_root_2d = smooth_root_2d[frame_indices]
        else:
            assert len(global_joints_positions) == len(
                frame_indices
            ), "The number of global positions should be match the number of frames"
            assert len(global_joints_rots) == len(
                frame_indices
            ), "The number of global joint rotations should be match the number of frames"

            if smooth_root_2d is not None:
                assert len(smooth_root_2d) == len(
                    frame_indices
                ), "The number of smooth root 2d (if specified) should be match the number of frames"

        if smooth_root_2d is None:
            # substitute the smooth root 2d with the real root
            smooth_root_2d = global_joints_positions[:, skeleton.root_idx, [0, 2]]

        # root y: from smooth or pelvis is the same
        self.root_y_pos = global_joints_positions[:, skeleton.root_idx, 1]

        self.global_joints_positions = global_joints_positions
        self.global_joints_rots = global_joints_rots
        self.global_root_heading = compute_global_heading(global_joints_positions, skeleton)
        self.smooth_root_2d = smooth_root_2d

    def update_constraints(self, data_dict: dict, index_dict: dict) -> None:
        """Append global positions, smooth root 2D, root y, and global heading to data/index
        dicts."""
        nbjoints = self.skeleton.nbjoints
        indices_lst = create_pairs(
            self.frame_indices,
            torch.arange(nbjoints, device=self.frame_indices.device),
        )
        data_dict["global_joints_positions"].append(
            self.global_joints_positions.reshape(-1, 3)
        )  # flatten the global positions
        index_dict["global_joints_positions"].append(indices_lst)

        # global rotations are not used here

        # FullBody establishes the base root. Root2D is sorted later and wins
        # duplicate root samples in the sparse conditioning tensor.
        data_dict["smooth_root_2d"].append(self.smooth_root_2d)
        index_dict["smooth_root_2d"].append(self.frame_indices)

        data_dict["root_y_pos"].append(self.root_y_pos)
        index_dict["root_y_pos"].append(self.frame_indices)

        data_dict["global_root_heading"].append(self.global_root_heading)
        index_dict["global_root_heading"].append(self.frame_indices)

    def crop_move(self, start: int, end: int) -> "FullBodyConstraintSet":
        """Return a new FullBodyConstraintSet for the cropped frame range [start, end)."""
        mask = (self.frame_indices >= start) & (self.frame_indices < end)
        return FullBodyConstraintSet(
            self.skeleton,
            self.frame_indices[mask] - start,
            self.global_joints_positions[mask],
            self.global_joints_rots[mask],
            self.smooth_root_2d[mask],
        )

    def get_save_info(self) -> dict:
        """Return a dict for JSON save: type, frame_indices, local_joints_rot, root_positions, smooth_root_2d."""
        local_joints_rot = self.skeleton.global_rots_to_local_rots(self.global_joints_rots)
        if isinstance(self.skeleton, SOMASkeleton30):
            local_joints_rot = self.skeleton.to_SOMASkeleton77(local_joints_rot)
        local_joints_rot = matrix_to_axis_angle(local_joints_rot)

        root_positions = self.global_joints_positions[:, self.skeleton.root_idx]
        return {
            "type": self.name,
            "frame_indices": self.frame_indices,
            "local_joints_rot": local_joints_rot,
            "root_positions": root_positions,
            "smooth_root_2d": self.smooth_root_2d,
        }

    def to(
        self,
        device: Optional[Union[str, torch.device]] = None,
        dtype: Optional[torch.dtype] = None,
    ) -> "FullBodyConstraintSet":
        self.frame_indices = _tensor_to(self.frame_indices, device, dtype)
        self.global_joints_positions = _tensor_to(self.global_joints_positions, device, dtype)
        self.global_joints_rots = _tensor_to(self.global_joints_rots, device, dtype)
        self.root_y_pos = _tensor_to(self.root_y_pos, device, dtype)
        self.global_root_heading = _tensor_to(self.global_root_heading, device, dtype)
        self.smooth_root_2d = _tensor_to(self.smooth_root_2d, device, dtype)
        if device is not None and hasattr(self.skeleton, "to"):
            self.skeleton = self.skeleton.to(device)
        return self

    @classmethod
    def from_dict(cls, skeleton: SkeletonBase, dico: dict) -> "FullBodyConstraintSet":
        """Build a FullBodyConstraintSet from a dict (e.g. loaded from JSON)."""
        device = skeleton.device if hasattr(skeleton, "device") else "cpu"
        frame_indices = torch.tensor(dico["frame_indices"], device=device, dtype=torch.long)
        local_rot = torch.tensor(dico["local_joints_rot"], device=device)
        local_rot_mats = axis_angle_to_matrix(local_rot)
        local_rot_mats = _convert_constraint_local_rots_to_skeleton(local_rot_mats, skeleton)
        global_joints_rots, global_joints_positions, _ = skeleton.fk(
            local_rot_mats,
            torch.tensor(dico["root_positions"], device=device),
        )
        smooth_root_2d = None
        if "smooth_root_2d" in dico:
            smooth_root_2d = torch.tensor(dico["smooth_root_2d"], device=device)

        return cls(
            skeleton,
            frame_indices=frame_indices,
            global_joints_positions=global_joints_positions,
            global_joints_rots=global_joints_rots,
            smooth_root_2d=smooth_root_2d,
        )


class ClipConstraintSet:
    """Sparse per-frame pose conditioning decoded from a generic ClipConstraint.

    Position channels require a constrained smooth root in Kimodo's motion representation. When
    the root is free, retain selected rotations and let the model generate a compatible root/body.
    """

    name = "clip"

    def __init__(
        self,
        skeleton: SkeletonBase,
        frame_indices: Tensor,
        global_joints_positions: Tensor,
        global_joints_rots: Tensor,
        position_axis_mask: Tensor,
        rot_indices: Tensor,
        *,
        root_position_axes: Tensor,
        root_heading: bool,
    ) -> None:
        if len(frame_indices) != len(global_joints_positions) or len(frame_indices) != len(global_joints_rots):
            raise ValueError("ClipConstraint frame data must match frame_indices length.")
        self.skeleton = skeleton
        self.frame_indices = frame_indices.long()
        self.global_joints_positions = global_joints_positions
        self.global_joints_rots = global_joints_rots
        if position_axis_mask.shape != (skeleton.nbjoints, 3):
            raise ValueError("ClipConstraint joint position mask must have shape [joint_count, 3].")
        if root_position_axes.shape != (3,):
            raise ValueError("ClipConstraint root position mask must contain three axes.")
        self.position_axis_mask = position_axis_mask.to(device=frame_indices.device, dtype=torch.bool)
        self.rot_indices = rot_indices.to(device=frame_indices.device, dtype=torch.long)
        self.root_position_axes = root_position_axes.to(device=frame_indices.device, dtype=torch.bool)
        self.root_position = bool(self.root_position_axes.any())
        self.root_heading = bool(root_heading)
        self.smooth_root_2d = global_joints_positions[:, skeleton.root_idx, [0, 2]]
        self.root_y_pos = global_joints_positions[:, skeleton.root_idx, 1]
        self.root_positions = global_joints_positions[:, skeleton.root_idx]
        local_reference = self.root_positions.clone()
        local_reference[..., 1] = 0.0
        self.local_joints_positions = global_joints_positions - local_reference[:, None, :]
        self.global_root_heading = compute_global_heading(global_joints_positions, skeleton)

    def update_constraints(self, data_dict: dict, index_dict: dict) -> None:
        crop_frames = torch.arange(len(self.frame_indices), device=self.frame_indices.device)
        root_axes = self.root_position_axes.nonzero(as_tuple=False).flatten()
        if len(root_axes):
            real = create_pairs(self.frame_indices, root_axes)
            crop = create_pairs(crop_frames, root_axes)
            data_dict["clip_root_positions"].append(self.root_positions[tuple(crop.T)])
            index_dict["clip_root_positions"].append(real)

            selected_joint_axes = self.position_axis_mask.nonzero(as_tuple=False)
            if len(selected_joint_axes):
                frame_rows = crop_frames.repeat_interleave(len(selected_joint_axes))
                joint_axes = selected_joint_axes.repeat(len(crop_frames), 1)
                indices = torch.cat(
                    [self.frame_indices.repeat_interleave(len(selected_joint_axes))[:, None], joint_axes],
                    dim=1,
                )
                values = self.local_joints_positions[
                    frame_rows,
                    joint_axes[:, 0],
                    joint_axes[:, 1],
                ]
                data_dict["clip_local_joints_positions"].append(values)
                index_dict["clip_local_joints_positions"].append(indices)
        if self.root_heading:
            data_dict["global_root_heading"].append(self.global_root_heading)
            index_dict["global_root_heading"].append(self.frame_indices)
        if len(self.rot_indices):
            real = create_pairs(self.frame_indices, self.rot_indices)
            crop = create_pairs(crop_frames, self.rot_indices)
            data_dict["global_joints_rots"].append(self.global_joints_rots[tuple(crop.T)])
            index_dict["global_joints_rots"].append(real)

    def crop_move(self, start: int, end: int) -> "ClipConstraintSet":
        mask = (self.frame_indices >= start) & (self.frame_indices < end)
        return ClipConstraintSet(
            self.skeleton,
            self.frame_indices[mask] - start,
            self.global_joints_positions[mask],
            self.global_joints_rots[mask],
            self.position_axis_mask,
            self.rot_indices,
            root_position_axes=self.root_position_axes,
            root_heading=self.root_heading,
        )

    def transform_to_origin(self, transform) -> None:
        translation, yaw = transform
        device = self.global_joints_positions.device
        dtype = self.global_joints_positions.dtype
        local_translation = translation.to(device=device, dtype=dtype)
        local_yaw = yaw.to(device=device, dtype=dtype)
        rotation = RotateFeatures((-local_yaw).reshape(1))
        rotation_3d = rotation.corrective_mat_Y[0]
        rotation_3d_t = rotation.corrective_mat_Y_T[0]
        offset = torch.zeros(3, device=device, dtype=dtype)
        offset[[0, 2]] = local_translation
        self.global_joints_positions = (self.global_joints_positions - offset) @ rotation_3d_t
        self.global_joints_rots = rotation_3d @ self.global_joints_rots
        self.root_positions = self.global_joints_positions[:, self.skeleton.root_idx]
        self.smooth_root_2d = self.root_positions[:, [0, 2]]
        self.root_y_pos = self.root_positions[:, 1]
        local_reference = self.root_positions.clone()
        local_reference[..., 1] = 0.0
        self.local_joints_positions = self.global_joints_positions - local_reference[:, None, :]
        self.global_root_heading = compute_global_heading(self.global_joints_positions, self.skeleton)

    def to(
        self,
        device: Optional[Union[str, torch.device]] = None,
        dtype: Optional[torch.dtype] = None,
    ) -> "ClipConstraintSet":
        self.frame_indices = _tensor_to(self.frame_indices, device, dtype)
        self.global_joints_positions = _tensor_to(self.global_joints_positions, device, dtype)
        self.global_joints_rots = _tensor_to(self.global_joints_rots, device, dtype)
        self.position_axis_mask = _tensor_to(self.position_axis_mask, device, None)
        self.rot_indices = _tensor_to(self.rot_indices, device, None)
        self.root_position_axes = _tensor_to(self.root_position_axes, device, None)
        self.root_positions = _tensor_to(self.root_positions, device, dtype)
        self.local_joints_positions = _tensor_to(self.local_joints_positions, device, dtype)
        self.smooth_root_2d = _tensor_to(self.smooth_root_2d, device, dtype)
        self.root_y_pos = _tensor_to(self.root_y_pos, device, dtype)
        self.global_root_heading = _tensor_to(self.global_root_heading, device, dtype)
        if device is not None and hasattr(self.skeleton, "to"):
            self.skeleton = self.skeleton.to(device)
        return self


class EndEffectorConstraintSet:
    """Constraint set fixing selected end-effector positions and rotations on given frames."""

    name = "end-effector"

    def __init__(
        self,
        skeleton: SkeletonBase,
        frame_indices: Tensor,
        global_joints_positions: Tensor,
        global_joints_rots: Tensor,
        smooth_root_2d: Optional[Tensor],
        *,
        joint_names: list[str],
        to_crop: bool = False,
    ) -> None:
        self.skeleton = skeleton
        # Keep every indexing tensor on the same device as the motion/model.
        # JSON deserialization creates frame_indices on CPU by default, while
        # inference commonly runs on CUDA.  Mixing them in create_pairs (which
        # internally calls torch.stack/cat) raises a CPU/CUDA device error.
        device = getattr(skeleton, "device", global_joints_positions.device)
        self.frame_indices = frame_indices.to(device=device, dtype=torch.long)
        global_joints_positions = global_joints_positions.to(device=device)
        global_joints_rots = global_joints_rots.to(device=device)
        if smooth_root_2d is not None:
            smooth_root_2d = smooth_root_2d.to(device=device)
        self.joint_names = joint_names

        # joint_names are constant for all the frames
        rot_joint_names, pos_joint_names = self.skeleton.expand_joint_names(self.joint_names)
        # indexing works for motion_rep with smooth root only (contains pelvis index)
        self.pos_indices = torch.tensor(
            [self.skeleton.bone_index[jname] for jname in pos_joint_names],
            device=device,
            dtype=torch.long,
        )
        self.rot_indices = torch.tensor(
            [self.skeleton.bone_index[jname] for jname in rot_joint_names],
            device=device,
            dtype=torch.long,
        )

        # if we pass the full smooth root 3D as input
        if smooth_root_2d is not None and smooth_root_2d.shape[-1] == 3:
            smooth_root_2d = smooth_root_2d[..., [0, 1]]

        if to_crop:
            global_joints_positions = global_joints_positions[frame_indices]
            global_joints_rots = global_joints_rots[frame_indices]
            if smooth_root_2d is not None:
                smooth_root_2d = smooth_root_2d[frame_indices]
        else:
            assert len(global_joints_positions) == len(
                frame_indices
            ), "The number of global positions should be match the number of frames"
            assert len(global_joints_rots) == len(
                frame_indices
            ), "The number of global joint rotations should be match the number of frames"
            if smooth_root_2d is not None:
                assert len(smooth_root_2d) == len(
                    frame_indices
                ), "The number of smooth root 2d (if specified) should be match the number of frames"

        if smooth_root_2d is None:
            # substitute the smooth root 2d with the real root
            smooth_root_2d = global_joints_positions[:, skeleton.root_idx, [0, 2]]

        # root y: from smooth or pelvis is the same
        self.root_y_pos = global_joints_positions[:, skeleton.root_idx, 1]

        self.global_joints_positions = global_joints_positions
        self.global_root_heading = compute_global_heading(global_joints_positions, skeleton)
        self.global_joints_rots = global_joints_rots
        self.smooth_root_2d = smooth_root_2d

    def update_constraints(self, data_dict: dict, index_dict: dict) -> None:
        """Append constrained joint positions/rots, smooth root 2D, root y, and heading to
        data/index dicts."""
        crop_frames_indexing = torch.arange(len(self.frame_indices), device=self.frame_indices.device)

        # constraint positions
        pos_indices_real = create_pairs(
            self.frame_indices,
            self.pos_indices,
        )
        pos_indices_crop = create_pairs(
            crop_frames_indexing,
            self.pos_indices,
        )
        data_dict["global_joints_positions"].append(self.global_joints_positions[tuple(pos_indices_crop.T)])
        index_dict["global_joints_positions"].append(pos_indices_real)

        # constraint rotations
        rot_indices_real = create_pairs(
            self.frame_indices,
            self.rot_indices,
        )
        rot_indices_crop = create_pairs(
            crop_frames_indexing,
            self.rot_indices,
        )
        data_dict["global_joints_rots"].append(self.global_joints_rots[tuple(rot_indices_crop.T)])
        index_dict["global_joints_rots"].append(rot_indices_real)

        # Limb constraints are world-space targets. Their embedded root pose is
        # reference data only; conditioning adds it as a fallback after
        # FullBody/Root2D channels have been emitted.

    def crop_move(self, start: int, end: int) -> "EndEffectorConstraintSet":
        """Return a new EndEffectorConstraintSet for the cropped frame range [start, end)."""
        mask = (self.frame_indices >= start) & (self.frame_indices < end)

        cls = type(self)
        kwargs = {}
        if not hasattr(cls, "joint_names"):
            kwargs["joint_names"] = self.joint_names

        return cls(
            self.skeleton,
            self.frame_indices[mask] - start,
            self.global_joints_positions[mask],
            self.global_joints_rots[mask],
            self.smooth_root_2d[mask],
            **kwargs,
        )

    def get_save_info(self) -> dict:
        """Return a dict for JSON save: type, frame_indices, local_joints_rot, root_positions, smooth_root_2d, joint_names."""
        local_joints_rot = self.skeleton.global_rots_to_local_rots(self.global_joints_rots)
        if isinstance(self.skeleton, SOMASkeleton30):
            local_joints_rot = self.skeleton.to_SOMASkeleton77(local_joints_rot)
        local_joints_rot = matrix_to_axis_angle(local_joints_rot)

        root_positions = self.global_joints_positions[:, self.skeleton.root_idx]
        output = {
            "type": self.name,
            "frame_indices": self.frame_indices,
            "local_joints_rot": local_joints_rot,
            "root_positions": root_positions,
            "smooth_root_2d": self.smooth_root_2d,
        }
        if not hasattr(self.__class__, "joint_names"):
            # save the joint_names for this base class
            # but not for children
            output["joint_names"] = self.joint_names
        return output

    def to(
        self,
        device: Optional[Union[str, torch.device]] = None,
        dtype: Optional[torch.dtype] = None,
    ) -> "EndEffectorConstraintSet":
        self.frame_indices = _tensor_to(self.frame_indices, device, dtype)
        self.pos_indices = _tensor_to(self.pos_indices, device, dtype)
        self.rot_indices = _tensor_to(self.rot_indices, device, dtype)
        self.root_y_pos = _tensor_to(self.root_y_pos, device, dtype)
        self.global_joints_positions = _tensor_to(self.global_joints_positions, device, dtype)
        self.global_root_heading = _tensor_to(self.global_root_heading, device, dtype)
        self.global_joints_rots = _tensor_to(self.global_joints_rots, device, dtype)
        self.smooth_root_2d = _tensor_to(self.smooth_root_2d, device, dtype)
        if device is not None and hasattr(self.skeleton, "to"):
            self.skeleton = self.skeleton.to(device)
        return self

    @classmethod
    def from_dict(cls, skeleton: SkeletonBase, dico: dict) -> "EndEffectorConstraintSet":
        """Build an EndEffectorConstraintSet from a dict (e.g. loaded from JSON)."""
        device = skeleton.device if hasattr(skeleton, "device") else "cpu"
        frame_indices = torch.tensor(dico["frame_indices"], device=device, dtype=torch.long)
        local_rot = torch.tensor(dico["local_joints_rot"], device=device)
        local_rot_mats = axis_angle_to_matrix(local_rot)
        local_rot_mats = _convert_constraint_local_rots_to_skeleton(local_rot_mats, skeleton)
        global_joints_rots, global_joints_positions, _ = skeleton.fk(
            local_rot_mats,
            torch.tensor(dico["root_positions"], device=device),
        )

        kwargs = {}
        joint_names = getattr(cls, "joint_names", None)
        if joint_names is None:
            joint_names = dico["joint_names"]
            kwargs["joint_names"] = joint_names

        target_positions = dico.get("target_positions")
        if target_positions is not None:
            if len(target_positions) != len(frame_indices):
                raise ValueError("target_positions must match frame_indices length")
            _, position_joint_names = skeleton.expand_joint_names(joint_names)
            if not position_joint_names:
                raise ValueError("end-effector target has no position joint")
            target_joint_index = skeleton.bone_index[position_joint_names[0]]
            global_joints_positions = global_joints_positions.clone()
            for frame, target_position in enumerate(target_positions):
                if target_position is None:
                    continue
                target = torch.as_tensor(
                    target_position,
                    device=global_joints_positions.device,
                    dtype=global_joints_positions.dtype,
                )
                if target.shape != (3,) or not torch.isfinite(target).all():
                    raise ValueError("target_positions entries must be finite xyz vectors")
                global_joints_positions[frame, target_joint_index] = target

        smooth_root_2d = None
        if "smooth_root_2d" in dico:
            smooth_root_2d = torch.tensor(dico["smooth_root_2d"], device=device)

        return cls(
            skeleton,
            frame_indices=frame_indices,
            global_joints_positions=global_joints_positions,
            global_joints_rots=global_joints_rots,
            smooth_root_2d=smooth_root_2d,
            **kwargs,
        )


class LeftHandConstraintSet(EndEffectorConstraintSet):
    """End-effector constraint for the left hand only."""

    name = "left-hand"
    joint_names: list[str] = ["LeftHand"]

    def __init__(self, *args, **kwargs: dict):
        super().__init__(*args, joint_names=self.joint_names, **kwargs)


class RightHandConstraintSet(EndEffectorConstraintSet):
    """End-effector constraint for the right hand only."""

    name = "right-hand"
    joint_names: list[str] = ["RightHand"]

    def __init__(self, *args, **kwargs: dict):
        super().__init__(*args, joint_names=self.joint_names, **kwargs)


class LeftFootConstraintSet(EndEffectorConstraintSet):
    """End-effector constraint for the left foot only."""

    name = "left-foot"
    joint_names: list[str] = ["LeftFoot"]

    def __init__(self, *args, **kwargs: dict):
        super().__init__(*args, joint_names=self.joint_names, **kwargs)


class RightFootConstraintSet(EndEffectorConstraintSet):
    """End-effector constraint for the right foot only."""

    name = "right-foot"
    joint_names: list[str] = ["RightFoot"]

    def __init__(self, *args, **kwargs: dict):
        super().__init__(*args, joint_names=self.joint_names, **kwargs)


TYPE_TO_CLASS = {
    "root2d": Root2DConstraintSet,
    "fullbody": FullBodyConstraintSet,
    "left-hand": LeftHandConstraintSet,
    "right-hand": RightHandConstraintSet,
    "left-foot": LeftFootConstraintSet,
    "right-foot": RightFootConstraintSet,
    "end-effector": EndEffectorConstraintSet,
}


def load_constraints_lst(
    path_or_data: str | list,
    skeleton: SkeletonBase,
    device: Optional[Union[str, torch.device]] = None,
    dtype: Optional[torch.dtype] = None,
):
    """Load a list of constraints from JSON path or list of dicts.

    Args:
        path_or_data: Path to constraints.json or list of constraint dicts.
        skeleton: Skeleton instance (used for from_dict).
        device: If set, move all constraint tensors and skeleton to this device.
        dtype: If set, cast constraint tensors to this dtype.
    """
    if isinstance(path_or_data, str):
        saved = load_json(path_or_data)
    else:
        saved = path_or_data

    constraints_lst = []
    for el in saved:
        cls = TYPE_TO_CLASS[el["type"]]
        c = cls.from_dict(skeleton, el)
        if device is not None or dtype is not None:
            c.to(device=device, dtype=dtype)
        constraints_lst.append(c)
    return constraints_lst


def save_constraints_lst(path: str, constraints_lst: list) -> list | None:
    """Save a list of constraint sets to a JSON file.

    Returns None if list is empty.
    """
    if not constraints_lst:
        print("The constraints lst is empty. Skip saving")
        return

    to_save = []

    def tensor_to_list(obj):
        """Recursively convert tensors to lists for JSON serialization."""
        if isinstance(obj, Tensor):
            return obj.cpu().tolist()
        elif isinstance(obj, dict):
            return {k: tensor_to_list(v) for k, v in obj.items()}
        elif isinstance(obj, list):
            return [tensor_to_list(v) for v in obj]
        else:
            return obj

    for constraint in constraints_lst:
        constraint_info = constraint.get_save_info()
        # Convert all tensors to lists for JSON serialization
        constraint_info = tensor_to_list(constraint_info)
        to_save.append(constraint_info)

    save_json(path, to_save)
    print(f"Saved constraints to {path}")
    return to_save
