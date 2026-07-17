"""Compare Kimodo High Precision FP16 text encoding on GPU versus CPU."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models-root", required=True)
    parser.add_argument("--model", default="Kimodo-SOMA-RP-v1")
    parser.add_argument("--clips", type=int, default=10)
    parser.add_argument("--duration", type=float, default=2.0)
    parser.add_argument("--steps", type=int, default=10)
    parser.add_argument("--prompt", default="A person walks forward and waves hello.")
    parser.add_argument("--encoder-device", choices=("both", "gpu", "cpu"), default="both")
    parser.add_argument("--result-json", help=argparse.SUPPRESS)
    return parser


def _run_child(args: argparse.Namespace) -> dict:
    models_root = Path(args.models_root).resolve()
    fp16_dir = models_root / "KIMODO-Meta3_llm2vec_FP16"
    if not fp16_dir.is_dir():
        raise FileNotFoundError(f"High Precision FP16 text encoder not found: {fp16_dir}")

    encoder_device = "cuda:0" if args.encoder_device == "gpu" else "cpu"
    os.environ.update(
        {
            "KIMODO_MODELS_ROOT": str(models_root),
            "KIMODO_LLM2VEC_DIR": str(fp16_dir),
            "KIMODO_LLM2VEC_PEFT_DIR": "",
            "TEXT_ENCODERS_DIR": "",
            "TEXT_ENCODER": "llm2vec",
            "TEXT_ENCODER_MODE": "local",
            "TEXT_ENCODER_DEVICE": encoder_device,
        }
    )

    import torch
    from kimodo.bridge.bridge_load_model import load_bridge_model
    from kimodo.bridge.quickserver_cli import _execute_generate

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required because the Kimodo motion model stays on GPU in both modes.")

    load_started = time.perf_counter()
    model = load_bridge_model(args.model, models_root=models_root, device="cuda:0")
    load_seconds = time.perf_counter() - load_started

    clip_seconds = []
    for index in range(args.clips):
        request = {
            "prompt": args.prompt,
            "duration": args.duration,
            "seed": 42,
            "diffusion_steps": args.steps,
            "text_weight": 1.0,
            "constraints_json": "",
            "output_format": "flatbuf_motion_v1",
        }
        started = time.perf_counter()
        response, payload = _execute_generate(request, model, threading.Event(), None, None, "")
        elapsed = time.perf_counter() - started
        if response.get("status") != "done" or not payload:
            raise RuntimeError(f"Clip {index + 1} failed: {response}")
        clip_seconds.append(elapsed)
        print(f"[{args.encoder_device}] clip {index + 1}/{args.clips}: {elapsed:.3f}s", flush=True)

    steady = clip_seconds[1:] or clip_seconds
    result = {
        "encoder_device": args.encoder_device,
        "clips": args.clips,
        "duration_seconds": args.duration,
        "diffusion_steps": args.steps,
        "model_load_seconds": load_seconds,
        "generation_total_seconds": sum(clip_seconds),
        "first_clip_seconds": clip_seconds[0],
        "steady_mean_seconds": statistics.mean(steady),
        "steady_median_seconds": statistics.median(steady),
        "clip_seconds": clip_seconds,
    }
    Path(args.result_json).write_text(json.dumps(result, indent=2), encoding="utf-8")
    return result


def _run_both(args: argparse.Namespace) -> None:
    results = []
    with tempfile.TemporaryDirectory() as temp_dir:
        for mode in ("gpu", "cpu"):
            result_path = Path(temp_dir) / f"{mode}.json"
            command = [
                sys.executable,
                str(Path(__file__).resolve()),
                "--models-root", args.models_root,
                "--model", args.model,
                "--clips", str(args.clips),
                "--duration", str(args.duration),
                "--steps", str(args.steps),
                "--prompt", args.prompt,
                "--encoder-device", mode,
                "--result-json", str(result_path),
            ]
            subprocess.run(command, check=True)
            results.append(json.loads(result_path.read_text(encoding="utf-8")))

    print(f"\nmode  load(s)  first(s)  next mean(s)  {args.clips} clips(s)")
    for result in results:
        print(
            f"{result['encoder_device']:<4}  {result['model_load_seconds']:>7.2f}  "
            f"{result['first_clip_seconds']:>8.2f}  {result['steady_mean_seconds']:>12.2f}  "
            f"{result['generation_total_seconds']:>11.2f}"
        )
    gpu, cpu = results
    print(f"CPU/GPU steady-state ratio: {cpu['steady_mean_seconds'] / gpu['steady_mean_seconds']:.2f}x")


def main() -> None:
    args = _parser().parse_args()
    if args.clips < 1 or args.duration <= 0 or args.steps < 1:
        raise ValueError("clips, duration, and steps must be positive.")
    if args.encoder_device == "both":
        _run_both(args)
    elif not args.result_json:
        raise ValueError("Single-device mode is reserved for benchmark child processes.")
    else:
        _run_child(args)


if __name__ == "__main__":
    main()
