from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import types
from unittest.mock import patch
import json
import importlib.util

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mocap_worker
from core.protocol.kmb_motion import parse_kmb1


def _vfr_timestamps():
    intervals = np.full(844, 0.02, dtype=np.float64)
    intervals[:59] = 0.04
    return np.concatenate(([0.0], np.cumsum(intervals)))


class _FakeCapture:
    def __init__(self, _path):
        self.timestamps = _vfr_timestamps()
        self.index = -1

    def isOpened(self):
        return True

    def read(self):
        self.index += 1
        return self.index < len(self.timestamps), None

    def get(self, prop):
        if prop == 5:
            return 46.73221671704488
        if prop == 7:
            return float(len(self.timestamps))
        return self.timestamps[self.index] * 1000.0

    def release(self):
        pass


class MocapWorkerTests(unittest.TestCase):
    def test_interpolated_bbox_does_not_mark_detection_track_valid(self):
        flowhmr_root = Path(__file__).resolve().parents[1] / "vendor" / "FlowHMR"
        if importlib.util.find_spec("supervision") is None:
            self.skipTest("FlowHMR detection dependencies are not installed in this Python environment.")
        if str(flowhmr_root) not in sys.path:
            sys.path.insert(0, str(flowhmr_root))
        try:
            from flowhmr.utils.runtime_video_processing import TrackInfo, TrackingResult, VideoProcessor
        except ImportError as exc:
            self.skipTest(f"FlowHMR detection dependencies are unavailable: {exc}")

        track = TrackInfo(
            track_id=12,
            frames=np.asarray([0, 2], dtype=np.int64),
            bboxes=np.asarray([[10, 10, 30, 40], [20, 10, 40, 40]], dtype=np.float32),
            length=2,
            avg_area=600.0,
        )
        result = TrackingResult(tracks=[track], total_frames=3, width=100, height=100)
        interpolated = VideoProcessor.interpolate_track(track, result.total_frames)
        detections = VideoProcessor.track_human_detection(result, 0)

        self.assertTrue(np.all(interpolated[1] > 0))
        np.testing.assert_array_equal(detections, [1, 0, 1])

    def test_source_timing_preserves_full_video_frame_count_and_duration(self):
        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            CAP_PROP_POS_MSEC=0,
            VideoCapture=_FakeCapture,
        )
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / "source.mp4"
            media.write_bytes(b"video")
            bbox = root / "source_bbox.npz"
            detections = np.ones(542, dtype=np.uint8)
            detections[100] = 0
            np.savez(
                bbox,
                bbox=np.tile(np.asarray([[10, 10, 30, 40]], dtype=np.float32), (542, 1)),
                start_end=np.array([0, 541], dtype=np.int64),
                tracked_start_end=np.array([45, 497], dtype=np.int64),
                human_detection=detections,
                selected_track_id=np.array([12], dtype=np.int64),
            )
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                timing = mocap_worker._resolve_source_timing("video", media, bbox, 542, 30.0)

        self.assertEqual(timing["source_frame_count"], 845)
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 542)
        self.assertEqual(timing["tracking_frame_range"], [45, 498])
        self.assertAlmostEqual(timing["source_duration_seconds"], 18.0817, delta=0.001)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][0], 0.0, delta=1e-7)
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 542)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][1], 0.04, delta=1e-5)
        self.assertTrue(np.all(np.diff(timing["frame_timestamps_seconds"]) > 0))
        self.assertEqual(len(timing["human_detection"]), 542)
        self.assertEqual(timing["human_detection"][100], 0)
        self.assertEqual(timing["human_detection_track_id"], 12)

    def test_source_timing_probes_with_flowhmr_python_when_quickserver_lacks_opencv(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / "source.mp4"
            media.write_bytes(b"video")
            bbox = root / "source_bbox.npz"
            np.savez(
                bbox,
                bbox=np.zeros((542, 4), dtype=np.float32),
                start_end=np.array([0, 541], dtype=np.int64),
                tracked_start_end=np.array([0, 452], dtype=np.int64),
                human_detection=np.ones(542, dtype=np.uint8),
                selected_track_id=np.array([12], dtype=np.int64),
            )
            python = root / "flowhmr-python.exe"
            probe = subprocess.CompletedProcess(
                [str(python)], 0,
                stdout=json.dumps({"fps": 46.73221671704488, "frame_count": 845,
                                   "timestamps": _vfr_timestamps().tolist()}), stderr="")
            with patch.dict(sys.modules, {"cv2": None}), patch.object(
                mocap_worker.subprocess, "run", return_value=probe) as run:
                timing = mocap_worker._resolve_source_timing("video", media, bbox, 542, 30.0, python)

        self.assertEqual(Path(run.call_args.args[0][0]), python.resolve())
        self.assertEqual(timing["source_frame_count"], 845)
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 542)
        self.assertEqual(timing["tracking_frame_range"], [0, 453])
        self.assertAlmostEqual(timing["source_duration_seconds"], 18.0817, delta=0.001)

    def test_result_builder_forwards_flowhmr_python_to_source_timing(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            motion = root / "motion.npz"
            np.savez(motion, poses=np.zeros((2, 1), dtype=np.float32), mocap_framerate=np.array([30.0]))
            media = root / "source.mp4"
            media.write_bytes(b"video")
            python = root / "flowhmr-python.exe"
            timing_result = {
                "source_fps": 30.0,
                "source_frame_count": 2,
                "source_duration_seconds": 2.0 / 30.0,
                "frame_timestamps_seconds": [0.0, 1.0 / 30.0],
                "tracking_frame_range": [0, 2],
                "human_detection": np.asarray([1, 0], dtype=np.uint8),
                "human_detection_track_id": 7,
                "human_detection_frame_range": [0, 1],
            }
            with patch.object(mocap_worker, "_resolve_source_timing", return_value=timing_result) as resolve_timing, patch.object(
                mocap_worker, "flowhmr_npz_to_kmb", return_value=(b"kmb", {"frame_count": 2})
            ) as build_kmb:
                result, payload = mocap_worker._build_result(
                    "video", motion, None, media_path=media, flowhmr_python=python)

        resolve_timing.assert_called_once_with("video", media, None, 2, 30.0, python)
        self.assertEqual(payload, b"kmb")
        self.assertEqual(result["source_frame_count"], 2)
        self.assertEqual(result["frame_timestamps_seconds"], [0.0, 1.0 / 30.0])
        self.assertEqual(build_kmb.call_args.kwargs["global_time_seconds"], [0.0, 1.0 / 30.0])
        np.testing.assert_array_equal(build_kmb.call_args.kwargs["human_detection"], [1, 0])
        self.assertEqual(result["fps"], 30.0)
        self.assertEqual(result["human_detection_count"], 1)
        self.assertEqual(result["human_detection_track_id"], 7)
        self.assertEqual(result["recognition_status"], "verified")
        self.assertEqual(result["flowhmr_status"], "completed")
        self.assertEqual(result["kmb_status"], "completed")

    def test_result_builder_reports_detection_failure_after_kmb_creation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            motion = root / "motion.npz"
            np.savez(motion, poses=np.zeros((2, 1), dtype=np.float32), mocap_framerate=np.array([30.0]))
            timing_result = {
                "source_fps": 30.0,
                "source_frame_count": 2,
                "source_duration_seconds": 2.0 / 30.0,
                "frame_timestamps_seconds": [0.0, 1.0 / 30.0],
                "tracking_frame_range": [0, 2],
                "human_detection": np.zeros(2, dtype=np.uint8),
                "human_detection_track_id": -1,
                "human_detection_frame_range": None,
            }
            with patch.object(mocap_worker, "_resolve_source_timing", return_value=timing_result), patch.object(
                mocap_worker, "flowhmr_npz_to_kmb", return_value=(b"kmb", {"frame_count": 2})
            ):
                result, payload = mocap_worker._build_result("video", motion, None)

        self.assertEqual(payload, b"kmb")
        self.assertEqual(result["flowhmr_status"], "completed")
        self.assertEqual(result["recognition_status"], "failed")
        self.assertEqual(result["kmb_status"], "completed")
        self.assertEqual(result["human_detection_count"], 0)

    def test_source_timing_uses_original_fps_and_preserved_tracking_range(self):
        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            CAP_PROP_POS_MSEC=0,
            VideoCapture=_FakeCapture,
        )
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / "source.mp4"
            media.write_bytes(b"video")
            bbox = root / "source_bbox.npz"
            np.savez(
                bbox,
                bbox=np.zeros((542, 4), dtype=np.float32),
                start_end=np.array([0, 541], dtype=np.int64),
                tracked_start_end=np.array([45, 497], dtype=np.int64),
                human_detection=np.ones(542, dtype=np.uint8),
                selected_track_id=np.array([12], dtype=np.int64),
            )
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                timing = mocap_worker._resolve_source_timing("video", media, bbox, 453, 30.0)

        self.assertAlmostEqual(timing["source_fps"], 46.73221671704488)
        self.assertEqual(timing["source_frame_count"], 845)
        self.assertAlmostEqual(timing["source_duration_seconds"], 845.0 / 46.73221671704488, places=6)
        self.assertEqual(timing["tracking_frame_range"], [45, 498])
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 453)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][0], 1.48, delta=0.041)
        self.assertTrue(np.all(np.diff(timing["frame_timestamps_seconds"]) > 0))
        expected_last_pts = _vfr_timestamps()[np.argmin(np.abs(_vfr_timestamps() - 497 / 30.0))]
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][-1], expected_last_pts, delta=1e-5)

    def test_legacy_bbox_cache_is_rejected_with_regeneration_message(self):
        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            CAP_PROP_POS_MSEC=0,
            VideoCapture=_FakeCapture,
        )
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / "source.mp4"
            media.write_bytes(b"video")
            bbox = root / "source_bbox.npz"
            np.savez(
                bbox,
                bbox=np.zeros((542, 4), dtype=np.float32),
                start_end=np.array([0, 541], dtype=np.int64),
            )
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                with self.assertRaisesRegex(RuntimeError, "pre-track format"):
                    mocap_worker._resolve_source_timing("video", media, bbox, 542, 30.0)

    def test_picture_timing_uses_detector_hit_for_exported_middle_pose(self):
        with tempfile.TemporaryDirectory() as temp:
            bbox = Path(temp) / "picture_bbox.npz"
            detections = np.zeros(30, dtype=np.uint8)
            detections[15] = 1
            np.savez(
                bbox,
                human_detection=detections,
                selected_track_id=np.array([5], dtype=np.int64),
            )
            timing = mocap_worker._resolve_source_timing("picture", None, bbox, 30, 30.0)

        np.testing.assert_array_equal(timing["human_detection"], [1])
        self.assertEqual(timing["human_detection_track_id"], 5)
        self.assertEqual(timing["human_detection_frame_range"], [0, 1])

    def test_vfr_global_time_track_keeps_flowhmr_sample_count_and_nominal_fps(self):
        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            CAP_PROP_POS_MSEC=0,
            VideoCapture=_FakeCapture,
        )
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            media = root / "source.mp4"
            media.write_bytes(b"video")
            motion_npz = root / "motion.npz"
            np.savez(
                motion_npz,
                poses=np.zeros((3, 52, 3), dtype=np.float32),
                trans=np.zeros((3, 3), dtype=np.float32),
                mocap_framerate=np.asarray(30.0),
            )
            bbox = root / "source_bbox.npz"
            detection = np.ones(542, dtype=np.uint8)
            detection[76] = 0
            np.savez(
                bbox,
                bbox=np.tile(np.asarray([[10, 10, 30, 40]], dtype=np.float32), (542, 1)),
                start_end=np.array([0, 541], dtype=np.int64),
                tracked_start_end=np.array([75, 77], dtype=np.int64),
                human_detection=detection,
                selected_track_id=np.array([12], dtype=np.int64),
            )
            with np.load(bbox, allow_pickle=False) as bbox_data:
                self.assertTrue(np.all(bbox_data["bbox"][76] > 0))
                self.assertEqual(int(bbox_data["human_detection"][76]), 0)
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                result, payload = mocap_worker._build_result(
                    "video", motion_npz, None, media_path=media, bbox_npz_path=bbox)

        motion = parse_kmb1(payload)
        self.assertEqual(result["frame_count"], 3)
        self.assertEqual(result["fps"], 30.0)
        self.assertEqual(motion.num_frames, 3)
        self.assertEqual(motion.fps, 30.0)
        self.assertEqual(len(motion.global_time_seconds), 3)
        self.assertEqual(len(motion.human_detection), 3)
        self.assertEqual(motion.human_detection.tolist(), [1, 0, 1])
        self.assertGreater(len(set(np.diff(motion.global_time_seconds).round(6))), 1)
        self.assertEqual(result["human_detection_count"], 2)
        self.assertEqual(result["human_detection_track_id"], 12)

    def test_flowhmr_child_receives_assets_root_without_dinov3_override(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            flowhmr = root / 'vendor' / 'FlowHMR'
            flowhmr.mkdir(parents=True)
            media = root / 'motion.mp4'
            media.write_bytes(b'video')
            completed = subprocess.CompletedProcess([], 1, stdout='', stderr='expected test failure')

            with patch.object(mocap_worker.subprocess, 'run', return_value=completed) as run:
                with self.assertRaisesRegex(RuntimeError, 'expected test failure'):
                    mocap_worker.analyze(
                        'video', media, flowhmr, root / 'model.ckpt', root / 'python.exe', device='cpu',
                    )

            environment = run.call_args.kwargs['env']
            self.assertEqual(environment['FLOWHMR_ASSETS_ROOT'], str(flowhmr.resolve()))
            args = run.call_args.args[0]
            self.assertEqual(args[args.index('--assets-root') + 1], str(flowhmr.resolve()))


if __name__ == '__main__':
    unittest.main()
