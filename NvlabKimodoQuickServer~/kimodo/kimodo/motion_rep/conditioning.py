# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Constraint conditioning: build index and data dicts from constraint sets for the denoiser."""

from collections import defaultdict

import torch


def build_condition_dicts(constraints_lst: list):
    index_dict = defaultdict(list)
    data_dict = defaultdict(list)
    for constraint in constraints_lst:
        constraint.update_constraints(data_dict, index_dict)
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
