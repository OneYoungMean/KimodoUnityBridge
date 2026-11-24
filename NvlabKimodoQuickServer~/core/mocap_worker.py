from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid
from typing import Any

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from core.flowhmr_adapter import flowhmr_npz_to_kmb
from core.flowhmr_memory import CONSERVATIVE_FLOWHMR_OPTIONS, is_cuda_oom


def _build_result(kind: str, npz_path: Path, person: str | None,
                  camera_npz_path: Path | None = None,
                  media_path: Path | None = None,
                  bbox_npz_path: Path | None = None,
                  flowhmr_python: Path | None = None) -> tuple[dict[str, Any], bytes]:
    with np.load(npz_path, allow_pickle=False) as data:
        poses = np.asarray(data["poses"])
        fps = float(np.asarray(data.get("mocap_framerate", 30.0)).reshape(-1)[0])
        frame_count = int(poses.shape[0])
    frame_indices = [frame_count // 2] if kind == "picture" else None
    keypoints_npz_path = None
    if camera_npz_path is not None:
        camera_bbox_path = camera_npz_path.with_name(camera_npz_path.name.replace("_vggt_camera.npz", "_bbox.npz"))
        if not camera_bbox_path.is_file():
            raise RuntimeError(f"FlowHMR camera alignment requires its bbox cache: {camera_bbox_path}")
        bbox_npz_path = camera_bbox_path
        candidate_keypoints = camera_npz_path.with_name(camera_npz_path.name.replace("_vggt_camera.npz", "_sam3d_keypoints2d.npz"))
        if candidate_keypoints.is_file():
            keypoints_npz_path = candidate_keypoints
    timing = _resolve_source_timing(
        kind, media_path, bbox_npz_path, frame_count, fps, flowhmr_python)
    global_time_seconds = timing["frame_timestamps_seconds"]
    human_detection_01 = timing.get("human_detection_01")
    if frame_indices:
        # Picture inference exports the middle FlowHMR sample, so both tracks
        # from the repeated-image detection pass are already reduced to one row.
        global_time_seconds = [global_time_seconds[0]]
    payload, kmb_meta = flowhmr_npz_to_kmb(
        npz_path, frame_indices=frame_indices, camera_npz_path=camera_npz_path,
        bbox_npz_path=bbox_npz_path, keypoints_npz_path=keypoints_npz_path,
        global_time_seconds=global_time_seconds,
        human_detection_01=human_detection_01)
    output_frame_count = 1 if frame_indices else frame_count
    kmb_meta["source_duration_seconds"] = timing["source_duration_seconds"]
    kmb_meta["source_fps"] = timing["source_fps"]
    kmb_meta["frame_timestamps_seconds"] = global_time_seconds
    if human_detection_01 is not None:
        kmb_meta["human_detection_01_count"] = int(np.count_nonzero(human_detection_01))
        kmb_meta["human_detection_01_length"] = int(human_detection_01.size)
        kmb_meta["human_detection_track_id"] = timing.get("human_detection_track_id")
        kmb_meta["human_detection_frame_range"] = timing.get("human_detection_frame_range")
    result = {
        "status": "done", "analysis_schema_version": "flowhmr_smplh_kmb_v1",
        "source_type": kind, "skeleton": "SMPL-H", "model_name": "smplh",
        "fps": fps, "source_fps": timing["source_fps"],
        "source_frame_count": timing["source_frame_count"], "frame_count": output_frame_count,
        "source_duration_seconds": timing["source_duration_seconds"],
        "frame_timestamps_seconds": global_time_seconds,
        "tracking_frame_range": timing["tracking_frame_range"],
        "human_detection_01_count": int(np.count_nonzero(human_detection_01)) if human_detection_01 is not None else None,
        "human_detection_01_length": int(human_detection_01.size) if human_detection_01 is not None else 0,
        "global_time_seconds_length": int(len(global_time_seconds)),
        "human_detection_track_id": timing.get("human_detection_track_id"),
        "human_detection_frame_range": timing.get("human_detection_frame_range"),
        "coordinate_spaces": {"global": "SMPL-X rest-pose pelvis world, meters", "rotations": "local axis-angle radians"},
        "output_format": "kmb_attachments_v1", "byte_length": len(payload), "kmb": kmb_meta,
        "kmb_attachments": [{"index": 0, "offset": 0, "byte_length": len(payload),
                              "purpose": "flowhmr_motion", "start_frame": 0,
                              "end_frame_exclusive": output_frame_count}],
    }
    if camera_npz_path is not None:
        result["camera_enabled"] = True
        result["camera_cache_path"] = str(camera_npz_path)
    if person is not None:
        result["person"] = person
    return result, payload


def _map_sample_times_to_source_pts(
        sample_times: np.ndarray, source_timestamps: np.ndarray) -> np.ndarray | None:
    """Map CFR sample times to distinct, nearest source PTS values.

    Return None when the source timeline does not provide enough source frames
    around this sample span (for example, a 25 FPS source upsampled to 30 FPS).
    In that case, the CFR presentation times are the only accurate timing for
    repeated samples.
    """
    samples = np.asarray(sample_times, dtype=np.float64).reshape(-1)
    timestamps = np.asarray(source_timestamps, dtype=np.float64).reshape(-1)
    if samples.size == 0:
        return np.empty(0, dtype=np.float64)
    if timestamps.size < samples.size:
        return None

    median_interval = (
        float(np.median(np.diff(timestamps))) if timestamps.size > 1 else 0.0
    )
    first_source = int(np.searchsorted(timestamps, samples[0] - median_interval, side="left"))
    end_source = int(np.searchsorted(timestamps, samples[-1] + median_interval, side="right"))
    if end_source - first_source < samples.size:
        return None

    selected = np.empty(samples.size, dtype=np.int64)
    previous = -1
    for index, sample_time in enumerate(samples):
        minimum = previous + 1
        maximum = timestamps.size - (samples.size - index)
        insertion = int(np.searchsorted(timestamps, sample_time, side="left"))
        left = min(max(insertion - 1, minimum), maximum)
        right = min(max(insertion, minimum), maximum)
        # Prefer the later source frame at an exact midpoint so a 30 FPS
        # sample does not drift backwards because of floating-point noise.
        selected[index] = left if abs(timestamps[left] - sample_time) <= abs(timestamps[right] - sample_time) else right
        previous = int(selected[index])
    return timestamps[selected]


def _resolve_source_timing(kind: str, media_path: Path | None, bbox_npz_path: Path | None,
                           frame_count: int, motion_fps: float,
                           probe_python: Path | None = None) -> dict[str, Any]:
    """Map each FlowHMR sample back to the original media timeline."""
    safe_fps = float(motion_fps) if np.isfinite(motion_fps) and motion_fps > 0 else 30.0
    if kind == "picture":
        if bbox_npz_path is None or not bbox_npz_path.is_file():
            raise RuntimeError("FlowHMR picture timing requires its bbox detection cache.")
        with np.load(bbox_npz_path, allow_pickle=False) as bbox_data:
            if "human_detection_01" not in bbox_data.files or "selected_track_id" not in bbox_data.files:
                raise RuntimeError(
                    f"FlowHMR bbox cache is from the pre-track format; rerun inference to regenerate it: {bbox_npz_path}")
            detections = np.asarray(bbox_data["human_detection_01"], dtype=np.uint8).reshape(-1)
            if detections.size != frame_count or np.any(detections > 1):
                raise RuntimeError(f"FlowHMR human_detection_01 does not match the picture inference cache: {bbox_npz_path}")
            selected_track_id = int(np.asarray(bbox_data["selected_track_id"]).reshape(-1)[0])
        selected_frame = frame_count // 2
        is_detected = bool(detections[selected_frame])
        return {
            "source_fps": safe_fps,
            "source_frame_count": 1,
            "source_duration_seconds": 1.0 / safe_fps,
            "frame_timestamps_seconds": [0.0],
            "tracking_frame_range": [0, 1],
            "sample_range": [0, 0],
            "human_detection_01": np.asarray([int(is_detected)], dtype=np.uint8),
            "human_detection_track_id": selected_track_id,
            "human_detection_frame_range": [0, 1] if is_detected else None,
        }
    if media_path is None or not media_path.is_file():
        raise RuntimeError("FlowHMR video timing requires the original media file.")
    if bbox_npz_path is None or not bbox_npz_path.is_file():
        raise RuntimeError("FlowHMR video timing requires its bbox cache.")

    try:
        import cv2
    except ImportError:
        cv2 = None
    source_timestamps = None
    if cv2 is not None:
        capture = cv2.VideoCapture(str(media_path))
        if not capture.isOpened():
            capture.release()
            raise RuntimeError(f"Could not read source video timing: {media_path}")
        try:
            source_fps = float(capture.get(cv2.CAP_PROP_FPS))
            source_frame_count = int(capture.get(cv2.CAP_PROP_FRAME_COUNT))
            if hasattr(capture, "read") and hasattr(cv2, "CAP_PROP_POS_MSEC"):
                pts = []
                while True:
                    ok, _ = capture.read()
                    if not ok:
                        break
                    pts.append(float(capture.get(cv2.CAP_PROP_POS_MSEC)) / 1000.0)
                if pts:
                    source_timestamps = np.asarray(pts, dtype=np.float64)
                    source_frame_count = len(pts)
        finally:
            capture.release()
    else:
        python = Path(probe_python).expanduser().resolve() if probe_python is not None else Path(sys.executable)
        probe_code = (
            "import cv2,json,sys\n"
            "capture=cv2.VideoCapture(sys.argv[1])\n"
            "if not capture.isOpened(): sys.exit(2)\n"
            "fps=float(capture.get(cv2.CAP_PROP_FPS))\n"
            "frames=int(capture.get(cv2.CAP_PROP_FRAME_COUNT))\n"
            "pts=[]\n"
            "while True:\n"
            " ok,_=capture.read()\n"
            " if not ok: break\n"
            " pts.append(float(capture.get(cv2.CAP_PROP_POS_MSEC))/1000.0)\n"
            "capture.release()\n"
            "print(json.dumps({'fps':fps,'frame_count':len(pts) or frames,'timestamps':pts}))\n"
        )
        probe = subprocess.run(
            [str(python), "-c", probe_code, str(media_path)],
            capture_output=True, text=True, check=False,
        )
        if probe.returncode != 0:
            detail = (probe.stderr or probe.stdout or "video metadata probe failed").strip()
            raise RuntimeError(f"Could not read source video timing with {python}: {detail[-1000:]}")
        try:
            video_meta = json.loads(probe.stdout)
            source_fps = float(video_meta["fps"])
            source_frame_count = int(video_meta["frame_count"])
            if video_meta.get("timestamps"):
                source_timestamps = np.asarray(video_meta["timestamps"], dtype=np.float64)
        except (TypeError, ValueError, KeyError, json.JSONDecodeError) as exc:
            raise RuntimeError(f"Video metadata probe returned invalid output for {media_path}.") from exc
    if not np.isfinite(source_fps) or source_fps <= 0 or source_frame_count <= 0:
        raise RuntimeError("Source video has invalid frame-rate or frame-count metadata.")

    with np.load(bbox_npz_path, allow_pickle=False) as bbox_data:
        if "start_end" not in bbox_data.files:
            raise RuntimeError(f"FlowHMR bbox cache has no sample frame range: {bbox_npz_path}")
        if "human_detection_01" not in bbox_data.files or "selected_track_id" not in bbox_data.files:
            raise RuntimeError(
                f"FlowHMR bbox cache is from the pre-track format; rerun inference to regenerate it: {bbox_npz_path}")
        sample_range = np.asarray(bbox_data["start_end"]).reshape(-1)
        tracking_range = np.asarray(bbox_data["tracked_start_end"]).reshape(-1)
        detection_mask = np.asarray(bbox_data["human_detection_01"], dtype=np.uint8).reshape(-1)
        selected_track_id = int(np.asarray(bbox_data["selected_track_id"]).reshape(-1)[0])
    if sample_range.size != 2 or tracking_range.size != 2:
        raise RuntimeError(f"FlowHMR bbox cache has an invalid frame range: {bbox_npz_path}")
    tracked_start, tracked_end = (int(value) for value in tracking_range)
    raw_sample_start, raw_sample_end = (int(value) for value in sample_range)
    if tracked_start >= 0 and tracked_end >= tracked_start and tracked_end - tracked_start + 1 == frame_count:
        # An extended bbox cache covers the full source video, but the motion
        # NPZ still contains only the original tracked span.
        sample_start, sample_end = tracked_start, tracked_end
    else:
        sample_start, sample_end = raw_sample_start, raw_sample_end
    if sample_start < 0 or sample_end < sample_start or sample_end - sample_start + 1 != frame_count:
        raise RuntimeError(
            f"FlowHMR sample frame range [{sample_start},{sample_end}] does not match {frame_count} motion samples.")
    if (tracked_start < raw_sample_start or tracked_end < tracked_start or
            tracked_end > raw_sample_end):
        raise RuntimeError(
            f"FlowHMR tracking frame range [{tracked_start},{tracked_end}] is outside its sampled video range.")

    # FlowHMR indexes the CFR transcode (normally 30 Hz), while source_fps is
    # the native media rate used only to recover the original duration.
    sample_times = (sample_start + np.arange(frame_count, dtype=np.float64)) / safe_fps
    source_duration = source_frame_count / source_fps
    if source_timestamps is not None and source_timestamps.size == source_frame_count:
        source_timestamps = source_timestamps - source_timestamps[0]
        if np.isfinite(source_timestamps).all() and (source_timestamps.size < 2 or np.all(np.diff(source_timestamps) > 0)):
            last_interval = float(np.median(np.diff(source_timestamps))) if source_timestamps.size > 1 else 1.0 / source_fps
            source_duration = max(source_duration, float(source_timestamps[-1]) + last_interval)
            source_fps = source_frame_count / source_duration
        else:
            source_timestamps = None
    has_source_pts = source_timestamps is not None
    if source_timestamps is None:
        source_timestamps = np.arange(source_frame_count, dtype=np.float64) / source_fps
    if sample_times.size and sample_times[-1] >= source_duration + (0.5 / safe_fps):
        raise RuntimeError("FlowHMR sample timestamps extend beyond the source video duration.")
    if has_source_pts:
        global_times = _map_sample_times_to_source_pts(sample_times, source_timestamps)
        if global_times is None:
            global_times = sample_times
    else:
        global_times = sample_times
    global_times = np.asarray(global_times, dtype=np.float32)
    if global_times.size > 1 and np.any(np.diff(global_times) <= 0):
        # Extremely close source PTS may collapse at KMB's float32 precision.
        # CFR times remain strictly increasing and preserve sample cadence.
        global_times = sample_times.astype(np.float32)
    if global_times.size and float(global_times[-1]) > source_duration + (0.5 / safe_fps):
        raise RuntimeError("FlowHMR sample timestamps extend beyond the source video duration.")
    sample_detection = None
    detection_range = None
    if np.any(detection_mask > 1):
        raise RuntimeError(f"FlowHMR human_detection_01 must be binary: {bbox_npz_path}")
    if sample_end < detection_mask.size and sample_end - sample_start + 1 == frame_count:
        sample_detection = detection_mask[sample_start:sample_end + 1]
    elif detection_mask.size == source_frame_count:
        sample_detection = detection_mask[sample_start:sample_end + 1]
    elif detection_mask.size == raw_sample_end - raw_sample_start + 1:
        offset = sample_start - raw_sample_start
        sample_detection = detection_mask[offset:offset + frame_count]
    elif detection_mask.size == frame_count:
        sample_detection = detection_mask
    else:
        raise RuntimeError(f"FlowHMR human_detection_01 does not match its bbox/video range: {bbox_npz_path}")
    hits = np.flatnonzero(sample_detection)
    if hits.size:
        detection_range = [sample_start + int(hits[0]), sample_start + int(hits[-1]) + 1]
    return {
        "source_fps": source_fps,
        "source_frame_count": source_frame_count,
        "source_duration_seconds": source_duration,
        "frame_timestamps_seconds": global_times.tolist(),
        "tracking_frame_range": [tracked_start, tracked_end + 1],
        "sample_range": [sample_start, sample_end],
        "human_detection_01": sample_detection,
        "human_detection_track_id": selected_track_id,
        "human_detection_frame_range": detection_range,
    }


def analyze(kind: str, media: Path, flowhmr_root: Path, checkpoint: Path,
            flowhmr_python: Path, person: str | None = None, device: str = "cuda",
            vggt_interval: int = 1, sam_chunk_size: int = 16,
            static_camera: bool = False, enable_camera: bool = False,
            assets_root: Path | None = None) -> tuple[dict[str, Any], bytes]:
    assets_root = (assets_root or flowhmr_root).resolve()
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
        environment = dict(os.environ)
        environment["FLOWHMR_ASSETS_ROOT"] = str(assets_root)
        command = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(staged),
                   "--ckpt", str(checkpoint), "--device", device,
                   "--assets-root", str(assets_root),
                   "--vggt_interval", str(vggt_interval),
                   "--sam_chunk_size", str(sam_chunk_size), "--skeleton-only"]
        if static_camera:
            command.append("--static_camera")
        completed = subprocess.run(command, cwd=str(flowhmr_root), env=environment, capture_output=True, text=True, check=False)
        if completed.returncode != 0:
            diagnostic = (completed.stderr or completed.stdout or "").strip()
            if device.startswith("cuda") and is_cuda_oom(diagnostic):
                retry_staged = temp / f"kimodo_retry_{uuid.uuid4().hex}{staged.suffix}"
                shutil.copy2(staged, retry_staged)
                retry = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(retry_staged),
                         "--ckpt", str(checkpoint), "--device", device,
                         "--assets-root", str(assets_root),
                         "--vggt_interval", str(CONSERVATIVE_FLOWHMR_OPTIONS.vggt_interval),
                         "--sam_chunk_size", str(CONSERVATIVE_FLOWHMR_OPTIONS.sam_chunk_size),
                         "--skeleton-only"]
                if not enable_camera:
                    retry.append("--static_camera")
                completed = subprocess.run(retry, cwd=str(flowhmr_root), env=environment, capture_output=True, text=True, check=False)
                staged = retry_staged
                diagnostic = (completed.stderr or completed.stdout or diagnostic).strip()
                if completed.returncode != 0 and is_cuda_oom(diagnostic):
                    cpu_staged = temp / f"kimodo_cpu_{uuid.uuid4().hex}{staged.suffix}"
                    shutil.copy2(staged, cpu_staged)
                    cpu_retry = [str(flowhmr_python), str(flowhmr_root / "run_v2m_demo_generation.py"), str(cpu_staged),
                                 "--ckpt", str(checkpoint), "--device", "cpu",
                                 "--assets-root", str(assets_root),
                                 "--vggt_interval", str(CONSERVATIVE_FLOWHMR_OPTIONS.vggt_interval),
                                 "--sam_chunk_size", str(CONSERVATIVE_FLOWHMR_OPTIONS.sam_chunk_size),
                                 "--skeleton-only"]
                    if not enable_camera:
                        cpu_retry.append("--static_camera")
                    completed = subprocess.run(cpu_retry, cwd=str(flowhmr_root), env=environment, capture_output=True, text=True, check=False)
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
        # A dynamic-camera run writes several NPZ sidecars. Only the motion
        # artifact contains poses/trans; selecting the newest NPZ can pick the
        # VGGT camera cache and fail after inference has already succeeded.
        candidates = []
        for candidate in sorted(output_dir.glob("*.npz"), key=lambda p: p.stat().st_mtime, reverse=True):
            try:
                with np.load(candidate, allow_pickle=False) as probe:
                    if "poses" in probe.files and "trans" in probe.files:
                        candidates.append(candidate)
            except Exception:
                continue
        if not candidates:
            raise RuntimeError(f"FlowHMR completed without writing an NPZ result under {output_dir}.")
        camera_path = None
        if enable_camera:
            camera_path = output_dir / f"{staged.stem}_vggt_camera.npz"
            if not camera_path.is_file():
                raise RuntimeError(f"FlowHMR did not produce its dynamic camera cache: {camera_path}")
        bbox_path = output_dir / f"{staged.stem}_bbox.npz"
        return _build_result(kind, candidates[0], person, camera_path, media, bbox_path, flowhmr_python)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("video", "picture"), required=True)
    parser.add_argument("--media", type=Path, required=True)
    parser.add_argument("--flowhmr-root", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--assets-root", type=Path, required=True)
    parser.add_argument("--flowhmr-python", type=Path, required=True)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--vggt-interval", type=int, default=1)
    parser.add_argument("--sam-chunk-size", type=int, default=16)
    parser.add_argument("--static-camera", action="store_true")
    parser.add_argument("--enable-camera", action="store_true")
    parser.add_argument("--person", choices=("left", "right"))
    args = parser.parse_args()
    result, payload = analyze(args.kind, args.media, args.flowhmr_root, args.checkpoint,
                              args.flowhmr_python, args.person, args.device,
                              args.vggt_interval, args.sam_chunk_size, args.static_camera, args.enable_camera,
                              args.assets_root)
    result["_binary_payload"] = payload.hex()
    sys.stdout.write(json.dumps(result, separators=(",", ":"), allow_nan=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
