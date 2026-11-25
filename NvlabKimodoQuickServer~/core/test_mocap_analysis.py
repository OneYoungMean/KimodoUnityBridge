import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import mocap_analysis


class MocapAnalysisMemoryTests(unittest.TestCase):
    def test_explicit_simulated_vram_controls_flowhmr_budget(self):
        self.assertEqual(mocap_analysis._resolve_free_vram_gb(6), 6.0)

    def test_quickserver_simulated_vram_environment_is_used(self):
        with patch.dict(os.environ, {"KIMODO_SIMULATE_FREE_VRAM_GB": "4"}, clear=False):
            self.assertEqual(mocap_analysis._resolve_free_vram_gb(), 4.0)

    def test_invalid_explicit_simulated_vram_is_rejected(self):
        with self.assertRaises(ValueError):
            mocap_analysis._resolve_free_vram_gb(float("nan"))

    def test_simulated_vram_does_not_fake_cuda_availability(self):
        with patch("torch.cuda.is_available", return_value=False):
            self.assertEqual(mocap_analysis._resolve_flowhmr_device(32), "cpu")
        self.assertEqual(mocap_analysis._resolve_flowhmr_device(1, "cuda"), "cuda")

    def test_auto_media_type_routes_image_and_video_to_separate_worker_modes(self):
        completed = __import__('subprocess').CompletedProcess(
            [], 0, stdout=json.dumps({
                'status': 'done', 'output_format': 'kmb_attachments_v1',
                'byte_length': 1, '_binary_payload': '00',
            }), stderr='')
        with __import__('tempfile').TemporaryDirectory() as temp:
            root = Path(temp)
            python = root / 'python.exe'
            python.touch()
            image = root / 'pose.png'
            video = root / 'motion.mp4'
            image.touch()
            video.touch()
            with patch.dict(os.environ, {'KIMODO_FLOWHMR_PYTHON': str(python)}), \
                    patch.object(mocap_analysis.sys, 'executable', str(python)), \
                    patch.object(mocap_analysis, 'ensure_flowhmr_assets', return_value=root / 'model.ckpt'), \
                    patch.object(mocap_analysis.subprocess, 'run', return_value=completed) as run:
                for media, expected in ((image, 'picture'), (video, 'video')):
                    mocap_analysis.analyze_media('auto', str(media), simulate_free_vram_gb=0)
                    args = run.call_args.args[0]
                    self.assertEqual(args[args.index('--kind') + 1], expected)

    def test_auto_picture_rejects_camera_track(self):
        with __import__('tempfile').TemporaryDirectory() as temp:
            image = Path(temp) / 'pose.png'
            image.touch()
            with self.assertRaisesRegex(ValueError, 'only for video'):
                mocap_analysis.analyze_media('auto', str(image), enable_camera=True)


if __name__ == "__main__":
    unittest.main()
