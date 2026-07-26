# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

"""
Step (1) of evaluation pipeline.

This script builds the benchmark test suites from BVH motions in the Bones-SEED dataset using 
the benchmark metadata. Currently it is only set up for the SOMA skeleton.
"""

import argparse
from functools import partial
from multiprocessing import Pool
from pathlib import Path

import numpy as np
import torch
from tqdm import tqdm

from kimodo.geometry import matrix_to_axis_angle
from kimodo.motion_rep import KimodoMotionRep
from kimodo.skeleton import SOMASkeleton77
from kimodo.skeleton.bvh import parse_bvh_motion
from kimodo.tools import load_json, save_json, to_numpy, to_torch

FPS = 30
BENCHMARK_REPO_ID = "nvidia/Kimodo-Motion-Gen-Benchmark"


def download_benchmark(dest: Path) -> Path:
    """Download the benchmark testsuite from HuggingFace to *dest*."""
    from huggingface_hub import snapshot_download

    print(f"Downloading benchmark testsuite from {BENCHMARK_REPO_ID} to {dest} ...")
    snapshot_dir = snapshot_download(
        repo_id=BENCHMARK_REPO_ID,
        repo_type="dataset",
        local_dir=str(dest),
    )
    return Path(snapshot_dir)


def discover_seed_motion_folders(root: Path) -> list[Path]:
    """Find all directories under root that contain seed_motion.json; return sorted list of those
    dirs."""
    root = root.resolve()
    if not root.is_dir():
        raise FileNotFoundError(f"Folder does not exist: {root}")
    out: list[Path] = []
    for meta_path in root.rglob("seed_motion.json"):
        src_dir = meta_path.parent
        out.append(src_dir)
    return sorted(out)


def constraints_and_motion_from_seed(folder: str, dataset_folder: str, fps=FPS):
    """Load seed_motion.json and BVH from folder; subsample to fps, convert to SOMA gt_motion.npz
    and constraints."""
    folder = Path(folder)
    dataset_folder = Path(dataset_folder)
    out_path = folder / "gt_motion.npz"

    seed_motion = load_json(folder / "seed_motion.json")

    start = seed_motion["crop_start_frame_index"]
    end = seed_motion["crop_end_frame_index"]

    bvh_path = dataset_folder / seed_motion["bvh_path"].replace("BVH/", "bvh/")

    local_rot_mats, root_trans, bvh_fps = parse_bvh_motion(bvh_path)
    step = round(bvh_fps / fps)

    # Subsample fps
    root_trans = root_trans[::step]
    local_rot_mats = local_rot_mats[::step]

    skeleton = SOMASkeleton77()
    # Changing t_pose: essential step
    local_rot_mats, global_rot_mats = skeleton.to_standard_tpose(local_rot_mats)

    # Use the motion rep to canonicalize the motion (start z+ at 0,0)
    # and get other components (smooth root, foot contacts etc)
    motion_rep = KimodoMotionRep(skeleton, fps)
    feats = motion_rep(local_rot_mats, root_trans, to_normalize=False)

    # Crop the features and canonicalizing them
    feats = feats[start:end]
    can_feats = motion_rep.canonicalize(feats)
    # Get back the motion
    motion = motion_rep.inverse(can_feats, is_normalized=False)
    motion = to_numpy(to_torch(motion, dtype=torch.float32))

    np.savez(out_path, **motion)

    seed_constraints_path = folder / "seed_constraints.json"
    if seed_constraints_path.exists():
        seed_constraints_lst = load_json(seed_constraints_path)

        constraints_lst = []
        for seed_cons in seed_constraints_lst:
            cons = seed_cons.copy()
            frame_indices = cons["frame_indices"]

            cons["smooth_root_2d"] = motion["smooth_root_pos"][frame_indices][..., [0, 2]].tolist()

            if cons["type"] == "root2d":
                if cons.get("use_global_orient", False):
                    cons["global_root_heading"] = motion["global_root_heading"][  # noqa
                        frame_indices
                    ].tolist()
            elif cons["type"] in ["fullbody"] or cons["type"] in [
                "left-hand",
                "right-hand",
                "left-foot",
                "right-foot",
                "end-effector",
            ]:
                cons["local_joints_rot"] = matrix_to_axis_angle(
                    to_torch(motion["local_rot_mats"][frame_indices])
                ).tolist()
                cons["root_positions"] = motion["root_positions"][frame_indices].tolist()
            else:
                raise TypeError(f"This constraint type is not recognized: {cons['type']}")

            constraints_lst.append(cons)

        # check that it is close to old_constraints_lst
        save_json(folder / "constraints.json", constraints_lst)


def main():
    parser = argparse.ArgumentParser(
        description="Recursively find test case to fill with motions and constraints.",
    )
    parser.add_argument(
        "benchmark",
        type=Path,
        help="Root folder to search recursively or seed_motion.json for to download the benchmark testsuite from HuggingFace to.",
    )
    parser.add_argument(
        "--dataset",
        type=Path,
        default="datasets/bones-seed/soma_uniform",
        help="SEED dataset folder",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Redo the process even if gt_motion.npz already exists",
    )
    parser.add_argument(
        "--workers",
        type=int,
        default=1,
        help="Number of parallel worker processes (default: 1, sequential)",
    )
    args = parser.parse_args()

    folder = args.benchmark.resolve()
    if not folder.is_dir():
        print(f"Benchmark folder not found at {folder}, downloading from HuggingFace...")
        download_benchmark(folder)

    dirs = discover_seed_motion_folders(folder)
    if not dirs:
        raise SystemExit(f"No directories with seed_motion.json found under {folder}")
    print(f"Discovered {len(dirs)} motion to populate.")

    skipped = 0
    to_process = []
    for d in dirs:
        if not args.overwrite and (d / "gt_motion.npz").is_file():
            skipped += 1
        else:
            to_process.append(d)

    fn = partial(constraints_and_motion_from_seed, dataset_folder=args.dataset)
    with Pool(args.workers) as pool:
        list(tqdm(pool.imap_unordered(fn, to_process), total=len(to_process), desc="Extracting GT motions"))

    if skipped:
        print(f"Processed {len(dirs) - skipped} folders, skipped {skipped} (already present).")
    else:
        print("Saved gt_motion.npz and constraints.json from the seed files.")


if __name__ == "__main__":
    main()
