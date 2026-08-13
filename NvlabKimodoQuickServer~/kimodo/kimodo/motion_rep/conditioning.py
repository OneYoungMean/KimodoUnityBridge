# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Constraint conditioning: build index and data dicts from constraint sets for the denoiser."""

from collections import defaultdict

import torch


def build_condition_dicts(constraints_lst: list):
    index_dict = defaultdict(list)
    data_dict = defaultdict(list)
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
    # get_unique_index_and_data keeps the first duplicate.  Emit stronger
    # root channels first so Root2D wins over FullBody and limb references.
    for constraint in sorted(
        constraints_lst,
        key=lambda item: -priority.get(getattr(item, "name", ""), 0),
    ):
        constraint.update_constraints(data_dict, index_dict)

    # Limb constraints still need a fallback root when no stronger constraint
    # covers that frame, but their embedded root must not override Root2D or
    # FullBody channels.
    for constraint in constraints_lst:
        if getattr(constraint, "name", "") not in {
            "end-effector", "left-hand", "right-hand", "left-foot", "right-foot"
        }:
            continue
        data_dict["smooth_root_2d"].append(constraint.smooth_root_2d)
        index_dict["smooth_root_2d"].append(constraint.frame_indices)
        data_dict["root_y_pos"].append(constraint.root_y_pos)
        index_dict["root_y_pos"].append(constraint.frame_indices)
        data_dict["global_root_heading"].append(constraint.global_root_heading)
        index_dict["global_root_heading"].append(constraint.frame_indices)
    return index_dict, data_dict


def get_unique_index_and_data(indices_lst, data):
    # unique + sort them by t
    indices_unique, inverse = torch.unique(indices_lst, dim=0, return_inverse=True)
    # pick first value for each unique (t, j)
    positions = torch.arange(len(inverse), device=inverse.device)
    first_idx = torch.full(
        (indices_unique.size(0),),
        len(inverse),
        dtype=torch.long,
        device=inverse.device,
    )
    first_idx.scatter_reduce_(0, inverse, positions, reduce="amin", include_self=True)
    assert (indices_lst[first_idx] == indices_unique).all()
    # get the data
    indices_lst = indices_lst[first_idx]
    data = data[first_idx]
    return indices_lst, data
