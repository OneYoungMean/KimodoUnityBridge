from __future__ import annotations

import argparse
from dataclasses import replace
import hashlib
import json
import math
import os
from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[1]
ARDY_ROOT = Path(os.environ.get("ARDY_COMPARE_ROOT", str(ROOT / "ardy"))).resolve()
sys.path[:0] = [str(ARDY_ROOT), str(ROOT / "kimodo"), str(ROOT / "ardy")]

import numpy as np
import torch

from ardy.motion_rep.tools import length_to_mask
from ardy.postprocess import post_process_motion
from ardy.tools import to_numpy
from kimodo.bridge import animation_handles, ardy_backend, bridge_server, quickserver_assets
from kimodo.tools import seed_everything


def _normalize_prompt(prompt: str) -> str:
    value = str(prompt or "A person walks forward.").strip()
    return value if value.endswith(".") else value + "."


def _configure_encoder(models_root: Path, device: str) -> None:
    encoder_device = device if device.startswith("cuda") else "cpu"
    route = quickserver_assets.ENCODER_ROUTE_FP16
    layout = quickserver_assets.select_text_encoder_layout_for_route(route, models_root, encoder_device)
    os.environ.update(
        quickserver_assets.build_runtime_env(
            root_dir=ROOT,
            source_root=ROOT / "kimodo",
            models_root=models_root,
            text_encoder_mode=quickserver_assets.TEXT_ENCODER_MODE_HIGH_PRECISION,
            encoder_device=encoder_device,
            encoder_route=route,
            encoder_layout_id=layout.layout_id,
        )
    )


def _as_numpy(output: dict) -> dict[str, np.ndarray]:
    converted = to_numpy(output)
    return {key: np.asarray(value) for key, value in converted.items() if hasattr(value, "shape")}


def _postprocess(output: dict, model) -> dict[str, np.ndarray]:
    corrected = post_process_motion(
        output["local_rot_mats"],
        output["root_positions"],
        output["foot_contacts"],
        model.skeleton,
        constraint_lst=None,
    )
    merged = dict(output)
    merged.update(corrected)
    return _as_numpy(merged)


def _generate_official_batch(
    model,
    prompt: str,
    text_feat,
    text_pad_mask,
    frames: int,
    steps: int,
    cfg_weight: tuple[float, float],
    seed: int,
    history_frames: int,
) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray]]:
    seed_everything(seed)
    lengths = torch.tensor([frames], dtype=torch.long, device=model.device)
    with torch.no_grad():
        motion = model(
            [prompt],
            frames,
            num_denoising_steps=steps,
            pad_mask=length_to_mask(lengths, max_len=frames),
            first_heading_angle=torch.zeros(1, device=model.device),
            motion_mask=None,
            observed_motion=None,
            cfg_weight=cfg_weight,
            text_feat=text_feat,
            text_pad_mask=text_pad_mask,
            crop_history_length=history_frames,
        )
        raw = model.motion_rep.inverse(motion, is_normalized=True)
    return _as_numpy(raw), _postprocess(raw, model)


def _concat_horizons(outputs: list[dict[str, np.ndarray]], frames: int) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    for key in outputs[0]:
        values = [np.asarray(output[key]) for output in outputs if key in output]
        if values and values[0].ndim >= 2 and values[0].shape[0] == 1:
            result[key] = np.concatenate(values, axis=1)[:, :frames]
        elif values:
            result[key] = values[-1]
    return result


def _generate_stream(
    model,
    profile,
    prompt: str,
    text_feat,
    text_pad_mask,
    frames: int,
    steps: int,
    text_weight: float,
    seed: int,
    postprocess: bool,
    history_frames: int | None = None,
) -> dict[str, np.ndarray]:
    stream_profile = replace(profile, postprocess=postprocess)
    store = animation_handles.AnimationHandleStore(byte_quota=64 * 1024 * 1024)
    generator = ardy_backend.ArdyStreamGenerator(
        {
            "prompt": prompt,
            "duration": frames / float(profile.source_fps),
            "diffusion_steps": steps,
            "text_weight": text_weight,
            "constraints_json": "",
            "seed": seed,
        },
        model,
        stream_profile,
        store,
        ROOT,
    )
    # Both paths consume the exact same embedding; this isolates motion generation.
    generator.text_feat = text_feat
    generator.text_pad_mask = text_pad_mask
    outputs: list[dict[str, np.ndarray]] = []
    for _ in range(math.ceil(frames / int(profile.horizon_frames))):
        outputs.append(generator.generate_horizon(model))
        if history_frames is not None and generator.history is not None:
            generator.history = generator.history[:, -history_frames:].detach()
    generator.close()
    return _concat_horizons(outputs, frames)


def _generate_official_interactive(
    model,
    prompt: str,
    text_feat,
    text_pad_mask,
    frames: int,
    steps: int,
    cfg_weight: tuple[float, float],
    seed: int,
    history_frames: int,
) -> dict[str, np.ndarray]:
    """Mirror scripts/interactive_demo one horizon at a time, without QuickServer code."""
    seed_everything(seed)
    history = None
    outputs: list[dict[str, np.ndarray]] = []
    horizon = int(model.gen_horizon_len)
    for _ in range(math.ceil(frames / horizon)):
        history_len = 0 if history is None else int(history.shape[1])
        with torch.no_grad():
            motion = model.autoregressive_step(
                num_frames=history_len + horizon,
                num_denoising_steps=steps,
                motion_mask=None,
                observed_motion=None,
                cfg_weight=cfg_weight,
                texts=[prompt],
                text_feat=text_feat,
                text_pad_mask=text_pad_mask,
                init_history_sequence=history,
            )
            generated = motion[:, history_len : history_len + horizon]
            outputs.append(_as_numpy(model.motion_rep.inverse(generated, is_normalized=True)))
        history = motion[:, -min(history_frames, int(motion.shape[1])) :].detach()
    return _concat_horizons(outputs, frames)


def _find_joint(names: list[str], name: str) -> int | None:
    lowered = name.lower()
    return next((index for index, value in enumerate(names) if value.lower() == lowered), None)


def _metrics(output: dict[str, np.ndarray], names: list[str], fps: float, horizon: int) -> dict:
    root = np.asarray(output["root_positions"])[0]
    posed = np.asarray(output["posed_joints"])[0]
    global_rot = np.asarray(output["global_rot_mats"])[0]
    planar_speed = np.linalg.norm(np.diff(root[:, [0, 2]], axis=0), axis=-1) * fps
    windows = []
    for start in range(0, len(root), horizon):
        end = min(len(root), start + horizon)
        speed_slice = planar_speed[start : max(start, end - 1)]
        windows.append(
            {
                "start_frame": start,
                "end_frame_exclusive": end,
                "planar_displacement_m": float(np.linalg.norm(root[end - 1, [0, 2]] - root[start, [0, 2]])),
                "mean_planar_speed_mps": float(speed_slice.mean()) if speed_slice.size else 0.0,
                "active_speed_ratio": float(np.mean(speed_slice > 0.15)) if speed_slice.size else 0.0,
            }
        )

    result = {
        "frames": int(len(root)),
        "root_planar_distance_m": float(np.linalg.norm(np.diff(root[:, [0, 2]], axis=0), axis=-1).sum()),
        "root_planar_displacement_m": float(np.linalg.norm(root[-1, [0, 2]] - root[0, [0, 2]])),
        "mean_planar_speed_mps": float(planar_speed.mean()),
        "active_speed_ratio": float(np.mean(planar_speed > 0.15)),
        "horizons": windows,
    }

    head = _find_joint(names, "Head")
    if head is not None:
        forward = global_rot[:, head] @ np.asarray([0.0, 0.0, 1.0])
        downward = np.degrees(np.arctan2(-forward[:, 1], np.linalg.norm(forward[:, [0, 2]], axis=-1)))
        result["head_down_degrees_mean"] = float(downward.mean())
        result["head_down_degrees_p90"] = float(np.percentile(downward, 90))

    for hand_name in ("LeftHand", "RightHand"):
        hand = _find_joint(names, hand_name)
        if hand is None:
            continue
        relative = posed[:, hand] - root
        result[hand_name] = {
            "relative_vertical_range_m": float(np.ptp(relative[:, 1])),
            "relative_horizontal_range_m": float(
                np.linalg.norm(relative[:, [0, 2]].max(axis=0) - relative[:, [0, 2]].min(axis=0))
            ),
            "relative_travel_m": float(np.linalg.norm(np.diff(relative, axis=0), axis=-1).sum()),
        }
    return result


def _comparison(left: dict[str, np.ndarray], right: dict[str, np.ndarray], frames: int | None = None) -> dict:
    count = min(left["root_positions"].shape[1], right["root_positions"].shape[1])
    if frames is not None:
        count = min(count, frames)
    left_root = left["root_positions"][:, :count]
    right_root = right["root_positions"][:, :count]
    left_posed = left["posed_joints"][:, :count]
    right_posed = right["posed_joints"][:, :count]
    left_relative = left_posed - left_root[:, :, None]
    right_relative = right_posed - right_root[:, :, None]
    left_rot = left["local_rot_mats"][:, :count]
    right_rot = right["local_rot_mats"][:, :count]
    relative_rot = np.swapaxes(left_rot, -1, -2) @ right_rot
    cosine = np.clip((np.trace(relative_rot, axis1=-2, axis2=-1) - 1.0) * 0.5, -1.0, 1.0)
    return {
        "frames": int(count),
        "root_rmse_m": float(np.sqrt(np.mean(np.square(left_root - right_root)))),
        "posed_joint_rmse_m": float(np.sqrt(np.mean(np.square(left_posed - right_posed)))),
        "root_relative_joint_rmse_m": float(np.sqrt(np.mean(np.square(left_relative - right_relative)))),
        "local_rotation_mean_angle_deg": float(np.degrees(np.arccos(cosine)).mean()),
    }


def _save_output(path: Path, output: dict[str, np.ndarray], model) -> dict:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = bridge_server._build_generate_flatbuffer_payload(model, output, sample_index=0)
    path.with_suffix(".kmb").write_bytes(payload)
    np.savez_compressed(path.with_suffix(".npz"), **output)
    return {
        "kmb": str(path.with_suffix(".kmb")),
        "npz": str(path.with_suffix(".npz")),
        "kmb_sha256": hashlib.sha256(payload).hexdigest(),
        "kmb_bytes": len(payload),
    }


def _source_snapshot(ardy_root: Path) -> dict:
    files = (
        "ardy/model/ardy_model.py",
        "ardy/model/diffusion.py",
        "ardy/motion_rep/conditioning.py",
    )
    return {
        "ardy_root": str(ardy_root),
        "files": {
            relative: hashlib.sha256(
                (ardy_root / relative).read_text(encoding="utf-8").replace("\r\n", "\n").encode("utf-8")
            ).hexdigest()
            for relative in files
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare official ARDY generation with QuickServer streaming.")
    parser.add_argument("--models-root", default=r"C:\nvlab\unityTest\kimodotest\NvlabKimodoQuickServer~\models")
    parser.add_argument("--model", default="ardy-core")
    parser.add_argument("--prompt", default="Walk And Say Hello")
    parser.add_argument("--duration", type=float, default=20.0)
    parser.add_argument("--steps", type=int, default=10)
    parser.add_argument("--text-weight", type=float, default=1.0)
    parser.add_argument("--seed", type=int, default=2058176595)
    parser.add_argument("--device", default="cuda:0" if torch.cuda.is_available() else "cpu")
    parser.add_argument("--output-dir", default=str(ROOT / "log" / "ardy_ab_compare"))
    parser.add_argument("--ardy-root", default=str(ARDY_ROOT))
    args = parser.parse_args()

    requested_ardy_root = Path(args.ardy_root).resolve()
    if requested_ardy_root != ARDY_ROOT:
        raise RuntimeError(
            "--ardy-root must be supplied through ARDY_COMPARE_ROOT so the ARDY module is selected before import: "
            f"set ARDY_COMPARE_ROOT={requested_ardy_root} and rerun."
        )

    models_root = Path(args.models_root).resolve()
    profile = quickserver_assets.resolve_motion_model_profile(args.model)
    if profile is None or profile.backend != "ardy":
        raise RuntimeError(f"Not a registered ARDY profile: {args.model!r}")
    if not 1 <= args.steps <= int(profile.max_diffusion_steps):
        raise RuntimeError(f"--steps must be in [1, {profile.max_diffusion_steps}].")
    frames = int(round(args.duration * float(profile.source_fps)))
    patch = int(profile.frames_per_token)
    frames = max(patch, int(math.ceil(frames / patch) * patch))
    history_frames = (
        (int(profile.max_context_frames) - int(profile.horizon_frames)) // patch * patch
    )
    prompt = _normalize_prompt(args.prompt)
    cfg_weight = (2.0**float(args.text_weight), float(profile.cfg_constraint_weight))

    _configure_encoder(models_root, args.device)
    model = ardy_backend.load_runtime(
        profile,
        {"models_root": str(models_root)},
        ROOT,
        args.device,
    )
    text_feat, text_pad_mask = model._encode_text([prompt])

    official_raw, official_post = _generate_official_batch(
        model,
        prompt,
        text_feat,
        text_pad_mask,
        frames,
        args.steps,
        cfg_weight,
        args.seed,
        history_frames,
    )
    stream_raw = _generate_stream(
        model,
        profile,
        prompt,
        text_feat,
        text_pad_mask,
        frames,
        args.steps,
        args.text_weight,
        args.seed,
        postprocess=False,
    )
    stream_post = _generate_stream(
        model,
        profile,
        prompt,
        text_feat,
        text_pad_mask,
        frames,
        args.steps,
        args.text_weight,
        args.seed,
        postprocess=True,
    )
    official_interactive160_raw = _generate_official_interactive(
        model,
        prompt,
        text_feat,
        text_pad_mask,
        frames,
        args.steps,
        cfg_weight,
        args.seed,
        history_frames,
    )
    interactive4_raw = _generate_stream(
        model,
        profile,
        prompt,
        text_feat,
        text_pad_mask,
        frames,
        args.steps,
        args.text_weight,
        args.seed,
        postprocess=False,
        history_frames=patch,
    )

    outputs = {
        "official_batch_raw": official_raw,
        "official_batch_postprocess": official_post,
        "quickserver_stream_raw": stream_raw,
        "quickserver_stream_postprocess": stream_post,
        "official_interactive160_raw": official_interactive160_raw,
        "official_interactive4_raw": interactive4_raw,
    }
    output_dir = Path(args.output_dir).resolve()
    names = list(model.skeleton.bone_order_names)
    report = {
        "settings": {
            "model": profile.model_name,
            "prompt_requested": args.prompt,
            "prompt_normalized": prompt,
            "duration_seconds": args.duration,
            "frames": frames,
            "fps": profile.source_fps,
            "horizon_frames": profile.horizon_frames,
            "official_batch_history_frames": history_frames,
            "official_interactive_history_frames": patch,
            "diffusion_steps": args.steps,
            "text_weight_request": args.text_weight,
            "cfg_weight": cfg_weight,
            "seed": args.seed,
            "device": args.device,
            "models_root": str(models_root),
            "source": _source_snapshot(ARDY_ROOT),
        },
        "artifacts": {},
        "metrics": {},
        "comparisons": {},
    }
    for name, output in outputs.items():
        report["artifacts"][name] = _save_output(output_dir / name, output, model)
        report["metrics"][name] = _metrics(
            output,
            names,
            float(profile.source_fps),
            int(profile.horizon_frames),
        )

    comparisons = {
        "official_vs_quickserver_raw": (official_raw, stream_raw, None),
        "official_vs_quickserver_postprocess": (official_post, stream_post, None),
        "quickserver_postprocess_effect": (stream_raw, stream_post, None),
        "history160_vs_interactive4": (stream_raw, interactive4_raw, None),
        "official_interactive160_vs_quickserver_raw": (official_interactive160_raw, stream_raw, None),
        "first_horizon_official_vs_quickserver_raw": (official_raw, stream_raw, int(profile.horizon_frames)),
    }
    for name, (left, right, limit) in comparisons.items():
        report["comparisons"][name] = _comparison(left, right, limit)

    output_dir.mkdir(parents=True, exist_ok=True)
    report_path = output_dir / "report.json"
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False))
    print(f"Report: {report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
