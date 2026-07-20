from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import threading


ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT / "kimodo"), str(ROOT / "ardy")]

import numpy as np
import torch

from ardy.geometry import quaternion_to_matrix
from ardy.model import load_model
from ardy.skeleton.kinematics import fk
from kimodo.bridge import animation_handles, ardy_backend, bridge_server, quickserver_assets


class _ZeroTextEncoder:
    def __call__(self, texts: list[str]):
        return torch.zeros(len(texts), 1, 4096), [1] * len(texts)


def _upper_body_mask(names: list[str], parents: list[int]) -> list[bool]:
    upper_root = next(
        (index for index, name in enumerate(names) if "spine" in name.lower() or "waist" in name.lower()),
        -1,
    )
    if upper_root < 0:
        raise RuntimeError("ARDY rig has no upper-body root joint.")

    result = [False, False, False, False]
    for joint in range(1, len(names)):
        current = joint
        enabled = False
        while current >= 0:
            if current == upper_root:
                enabled = True
                break
            current = parents[current]
        result.extend([enabled, enabled, enabled])
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="Real ARDY upper-body clip-mask smoke test.")
    parser.add_argument("--models-root", default=r"C:\nvlab\models~")
    parser.add_argument("--model", default="ardy-core")
    parser.add_argument("--device", default="cuda:0" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--steps", type=int, default=10)
    parser.add_argument("--max-rmse", type=float, default=0.20)
    args = parser.parse_args()

    profile = quickserver_assets.resolve_motion_model_profile(args.model)
    if profile is None or profile.backend != "ardy":
        raise RuntimeError(f"Not a registered ARDY model: {args.model!r}.")
    if not 1 <= args.steps <= profile.max_diffusion_steps:
        raise RuntimeError(f"--steps must be in [1, {profile.max_diffusion_steps}].")

    model = load_model(
        profile.model_name,
        device=args.device,
        text_encoder=_ZeroTextEncoder(),
        checkpoints_dir=str(Path(args.models_root).resolve()),
    )
    model.fps = profile.source_fps
    model.name = profile.model_name
    model.skeleton = model.motion_rep.skeleton

    frames = profile.horizon_frames
    joints = len(model.skeleton.bone_order_names)
    local_mats = torch.eye(3, device=args.device).reshape(1, 1, 1, 3, 3).repeat(1, frames, joints, 1, 1)
    roots = torch.zeros(1, frames, 3, device=args.device)
    _, posed, _ = fk(local_mats, roots, model.skeleton)
    source_payload = bridge_server._build_generate_flatbuffer_payload(
        model,
        {"posed_joints": posed.cpu().numpy(), "local_rot_mats": local_mats.cpu().numpy()},
    )

    store = animation_handles.AnimationHandleStore(byte_quota=64 * 1024 * 1024)
    handle = store.publish(
        source_payload,
        description="static T pose upper-body constraint",
        motion_rep_fingerprint=profile.motion_rep_fingerprint,
    )["handle"]
    names = list(model.skeleton.bone_order_names)
    parents = [int(value) for value in model.skeleton.joint_parents.detach().cpu().tolist()]
    flat_mask = _upper_body_mask(names, parents)
    constraints = json.dumps(
        [
            {
                "type": "clip",
                "format": "kmb_handle_v1",
                "handle": handle,
                "start_frame": 0,
                "end_frame_exclusive": frames,
                "is_history": False,
                "mask": flat_mask,
            }
        ]
    )
    def generate(constraints_json: str) -> ardy_backend.KmbMotion:
        _, payload = ardy_backend.execute_generate(
            {
            "prompt": "A person holds a T pose.",
            "diffusion_steps": args.steps,
            "text_weight": 1.0,
            "constraints_json": constraints_json,
            "output_format": "flatbuf_motion_v1",
            "seed": 1,
            },
            model,
            profile,
            threading.Event(),
            store,
            ROOT,
        )
        return ardy_backend.parse_kmb1(payload or b"")

    source_features = model.motion_rep(local_joint_rots=local_mats, root_positions=roots, to_normalize=False)
    position_slice = model.motion_rep.slice_dict["local_joints_positions"]
    source_positions = source_features[..., position_slice].reshape(frames, joints - 1, 3)
    selected = torch.as_tensor(flat_mask[4:], device=args.device).reshape(joints - 1, 3).any(dim=-1)

    def measure(output: ardy_backend.KmbMotion) -> tuple[float, float]:
        output_quats = torch.as_tensor(output.local_rot_quats, dtype=torch.float32, device=args.device)
        output_quats /= torch.linalg.vector_norm(output_quats, dim=-1, keepdim=True)
        output_features = model.motion_rep(
            local_joint_rots=quaternion_to_matrix(output_quats),
            root_positions=torch.as_tensor(output.root_positions, dtype=torch.float32, device=args.device),
            to_normalize=False,
        )
        output_positions = output_features[..., position_slice].reshape(frames, joints - 1, 3)
        error = output_positions[:, selected] - source_positions[:, selected]
        return (
            float(torch.sqrt(torch.mean(error.square())).cpu()),
            float(torch.linalg.vector_norm(error, dim=-1).max().cpu()),
        )

    free_rmse, _ = measure(generate(""))
    constrained_rmse, max_error = measure(generate(constraints))
    passed = constrained_rmse <= args.max_rmse and constrained_rmse < free_rmse
    print(
        json.dumps(
            {
                "model": profile.model_name,
                "steps": args.steps,
                "frames": frames,
                "masked_upper_body_joints": int(selected.sum()),
                "free_root_relative_position_rmse_m": free_rmse,
                "constrained_root_relative_position_rmse_m": constrained_rmse,
                "rmse_reduction_percent": (free_rmse - constrained_rmse) / free_rmse * 100.0,
                "max_joint_error_m": max_error,
                "passed": passed,
            },
            indent=2,
        )
    )
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
