from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import types
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mocap_worker


class MocapWorkerTests(unittest.TestCase):
    def test_source_timing_preserves_full_video_frame_count_and_duration(self):
        class FakeCapture:
            def __init__(self, _path):
                pass

            def isOpened(self):
                return True

            def get(self, prop):
                return 46.73221671704488 if prop == 5 else 845.0

            def release(self):
                pass

        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            VideoCapture=FakeCapture,
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
            )
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                timing = mocap_worker._resolve_source_timing("video", media, bbox, 542, 30.0)

        self.assertEqual(timing["source_frame_count"], 845)
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 542)
        self.assertEqual(timing["tracking_frame_range"], [45, 498])
        self.assertAlmostEqual(timing["source_duration_seconds"], 18.0817, delta=0.001)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][0], 0.0, delta=1e-7)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][-1], 541 / 30.0, delta=1e-5)

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
            )
            python = root / "flowhmr-python.exe"
            probe = subprocess.CompletedProcess(
                [str(python)], 0, stdout='{"fps":46.73221671704488,"frame_count":845}', stderr="")
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
            }
            with patch.object(mocap_worker, "_resolve_source_timing", return_value=timing_result) as resolve_timing, patch.object(
                mocap_worker, "flowhmr_npz_to_kmb", return_value=(b"kmb", {"frame_count": 2})
            ):
                result, payload = mocap_worker._build_result(
                    "video", motion, None, media_path=media, flowhmr_python=python)

        resolve_timing.assert_called_once_with("video", media, None, 2, 30.0, python)
        self.assertEqual(payload, b"kmb")
        self.assertEqual(result["source_frame_count"], 2)
        self.assertEqual(result["frame_timestamps_seconds"], [0.0, 1.0 / 30.0])

    def test_source_timing_uses_original_fps_and_preserved_tracking_range(self):
        class FakeCapture:
            def __init__(self, _path):
                self.values = {7: 30.0, 7: 30.0, 7: 30.0}

            def isOpened(self):
                return True

            def get(self, prop):
                return 46.73221671704488 if prop == 5 else 845.0

            def release(self):
                pass

        fake_cv2 = types.SimpleNamespace(
            CAP_PROP_FPS=5,
            CAP_PROP_FRAME_COUNT=7,
            VideoCapture=FakeCapture,
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
            )
            with patch.dict(sys.modules, {"cv2": fake_cv2}):
                timing = mocap_worker._resolve_source_timing("video", media, bbox, 453, 30.0)

        self.assertAlmostEqual(timing["source_fps"], 46.73221671704488)
        self.assertEqual(timing["source_frame_count"], 845)
        self.assertAlmostEqual(timing["source_duration_seconds"], 845.0 / 46.73221671704488, places=6)
        self.assertEqual(timing["tracking_frame_range"], [45, 498])
        self.assertEqual(len(timing["frame_timestamps_seconds"]), 453)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][0], 45.0 / 30.0, delta=1e-5)
        self.assertAlmostEqual(timing["frame_timestamps_seconds"][-1], 497.0 / 30.0, delta=1e-5)

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
