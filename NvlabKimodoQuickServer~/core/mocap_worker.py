from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from core.flowhmr_adapter import flowhmr_npz_to_kmb
from core.flowhmr_memory import CONSERVATIVE_FLOWHMR_OPTIONS, is_cuda_oom


def _build_result(kind: str, npz_path: Path, person: str | None) -> tuple[dict[str, Any], bytes]:
    import numpy as np
    with np.load(npz_path, allow_pickle=False) as data:
        poses = np.asarray(data["poses"])
        fps = float(np.asarray(data.get("mocap_framerate", 30.0)).reshape(-1)[0])
        frame_count = int(poses.shape[0])
    frame_indices = [frame_count // 2] if kind == "picture" else None
    payload, kmb_meta = flowhmr_npz_to_kmb(npz_path, frame_indices=frame_indices)
    output_frame_count = 1 if frame_indices else frame_count
    result = {
        "status": "done", "analysis_schema_version": "flowhmr_smplh_kmb_v1",
        "source_type": kind, "skeleton": "SMPL-H", "model_name": "smplh",
        "fps": fps, "source_frame_count": frame_count, "frame_count": output_frame_count,
        "coordinate_spaces": {"global": "FlowHMR world, meters", "rotations": "local axis-angle radians"},
        "output_format": "kmb_attachments_v1", "byte_length": len(payload), "kmb": kmb_meta,
        "kmb_attachments": [{"index": 0, "offset": 0, "byte_length": len(payload),
                              "purpose": "flowhmr_motion", "start_frame": 0,
                              "end_frame_exclusive": output_frame_count}],
    }
    if person is not None:
        result["person"] = person
    return result, payload


def analyze(kind: str, media: Path, flowhmr_root: Path, checkpoint: Path,
            flowhmr_python: Path, person: str | None = None, device: str = "cuda",
            vggt_interval: int = 1, sam_chunk_size: int = 16,
            static_camera: bool = False) -> tuple[dict[str, Any], bytes]:
    with tempfile.TemporaryDirectory(prefix="kimodo-flowhmr-") as temp_text:
        temp = Path(temp_text)
        staged = temp / f"kimodo_{uuid.uuid4().hex}{media.suffix.lower()}"
        if kind == "picture":
            import cv2
            image = cv2.imread(str(media), cv2.IMREAD_COLOR)
            if image is None:
                raise RuntimeError(f"Could not decode image: {media}")
            staged = temp / f"kimodo_{uuid.uuid4().hex}.mp4"
            writer = cv2.VideoWriter(str(staged), cv2.VideoWriter_fourcc(*"mp4v"), 30.0, (image.shape[1], image.shape[0]))
            if not writer.isOpened():
                raise RuntimeError("OpenCV could not create a temporary video from the picture.")
            try:
                for _ in range(30): writer.write(image)
            finally:
                writer.release()
        else:
            shutil.copy2(media, staged)
        command = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(staged),
                   "--ckpt", str(checkpoint), "--device", device,
                   "--vggt_interval", str(vggt_interval),
                   "--sam_chunk_size", str(sam_chunk_size)]
        if static_camera:
            command.append("--static_camera")
        completed = subprocess.run(command, cwd=str(flowhmr_root), capture_output=True, text=True, check=False)
        if completed.returncode != 0:
            diagnostic = (completed.stderr or completed.stdout or "").strip()
            if device.startswith("cuda") and is_cuda_oom(diagnostic):
                retry_staged = temp / f"kimodo_retry_{uuid.uuid4().hex}{staged.suffix}"
                shutil.copy2(staged, retry_staged)
                retry = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(retry_staged),
                         "--ckpt", str(checkpoint), "--device", device,
                         "--vggt_interval", str(CONSERVATIVE_FLOWHMR_OPTIONS.vggt_interval),
                         "--sam_chunk_size", str(CONSERVATIVE_FLOWHMR_OPTIONS.sam_chunk_size),
                         "--static_camera"]
                completed = subprocess.run(retry, cwd=str(flowhmr_root), capture_output=True, text=True, check=False)
                staged = retry_staged
                diagnostic = (completed.stderr or completed.stdout or diagnostic).strip()
                if completed.returncode != 0 and is_cuda_oom(diagnostic):
                    cpu_staged = temp / f"kimodo_cpu_{uuid.uuid4().hex}{staged.suffix}"
                    shutil.copy2(staged, cpu_staged)
                    cpu_retry = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(cpu_staged),
                                 "--ckpt", str(checkpoint), "--device", "cpu",
                                 "--vggt_interval", str(CONSERVATIVE_FLOWHMR_OPTIONS.vggt_interval),
                                 "--sam_chunk_size", str(CONSERVATIVE_FLOWHMR_OPTIONS.sam_chunk_size),
                                 "--static_camera"]
                    completed = subprocess.run(cpu_retry, cwd=str(flowhmr_root), capture_output=True, text=True, check=False)
                    staged = cpu_staged
                    diagnostic = (completed.stderr or completed.stdout or diagnostic).strip()
                    if completed.returncode != 0:
                        diagnostic = "FlowHMR failed on GPU and CPU fallback; " + diagnostic
                elif completed.returncode != 0:
                    diagnostic = "CUDA OOM after conservative retry; " + diagnostic
            if completed.returncode != 0:
                diagnostic = diagnostic or "FlowHMR exited without diagnostics"
                raise RuntimeError(diagnostic[-4000:])
        output_dir = flowhmr_root / "output" / staged.stem
        candidates = sorted(output_dir.glob("*.npz"), key=lambda p: p.stat().st_mtime, reverse=True)
        if not candidates:
            raise RuntimeError(f"FlowHMR completed without writing an NPZ result under {output_dir}.")
        return _build_result(kind, candidates[0], person)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("video", "picture"), required=True)
    parser.add_argument("--media", type=Path, required=True)
    parser.add_argument("--flowhmr-root", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--flowhmr-python", type=Path, required=True)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--vggt-interval", type=int, default=1)
    parser.add_argument("--sam-chunk-size", type=int, default=16)
    parser.add_argument("--static-camera", action="store_true")
    parser.add_argument("--person", choices=("left", "right"))
    args = parser.parse_args()
    result, payload = analyze(args.kind, args.media, args.flowhmr_root, args.checkpoint,
                              args.flowhmr_python, args.person, args.device,
                              args.vggt_interval, args.sam_chunk_size, args.static_camera)
    result["_binary_payload"] = payload.hex()
    sys.stdout.write(json.dumps(result, separators=(",", ":"), allow_nan=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
