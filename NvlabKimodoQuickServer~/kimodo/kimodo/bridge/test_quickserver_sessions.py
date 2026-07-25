import io
from pathlib import Path
import threading
from types import SimpleNamespace
from types import MethodType
import unittest

import numpy as np
import torch

from kimodo.bridge import ardy_backend
from kimodo.bridge import bridge_server
from kimodo.bridge import quickserver_cli


class QuickServerProtocolV2Tests(unittest.TestCase):
    def test_ardy_imports_from_the_bundled_runtime(self):
        import ardy

        self.assertTrue(Path(ardy.__file__).resolve().is_relative_to(ardy_backend.BUNDLED_ARDY_ROOT))

    def test_direct_kmb_is_the_only_binary_motion_format(self):
        self.assertEqual(
            bridge_server._resolve_requested_output_format({"output_format": "kmb_v1"}),
            "kmb_v1",
        )
        self.assertNotEqual(
            bridge_server._resolve_requested_output_format({"output_format": "removed_format"}),
            "removed_format",
        )

    def test_attachment_manifest_splits_concatenated_kmb_blobs(self):
        request = {
            "attachment_byte_length": 5,
            "kmb_attachments": [
                {"index": 0, "offset": 0, "byte_length": 2},
                {"index": 1, "offset": 2, "byte_length": 3},
            ],
        }
        self.assertEqual(
            quickserver_cli._read_kmb_attachments(io.BytesIO(b"abcde"), request),
            (b"ab", b"cde"),
        )

    def test_profile_defaults_reserve_one_horizon(self):
        for fps, horizon, window, expected_history in (
            (20.0, 40, 200, 160),
            (20.0, 8, 200, 192),
            (25.0, 52, 248, 196),
            (25.0, 8, 248, 240),
        ):
            profile = SimpleNamespace(
                source_fps=fps,
                horizon_frames=horizon,
                frames_per_token=4,
                max_context_frames=window,
            )
            settings = ardy_backend.ArdySettings.from_request({}, profile)
            self.assertEqual(settings.history_crop_frames, expected_history)

    def test_cursor_patch_pause_and_seek_use_one_cached_timeline(self):
        session = self._fake_ardy_session()
        model = SimpleNamespace()
        cancel = threading.Event()

        first, output = session.generate({"time_as_double": 0.0}, (), model, cancel)
        self.assertEqual((first["start_frame"], first["end_frame_exclusive"]), (0, 24))
        self.assertEqual(output["root_positions"].shape[1], 24)

        paused, output = session.generate({"time_as_double": 0.0}, (), model, cancel)
        self.assertEqual((paused["start_frame"], paused["end_frame_exclusive"]), (24, 24))
        self.assertIsNone(output)

        patched, output = session.generate(
            {"time_as_double": 0.0, "prompt": "walk", "diffusion_steps": 12, "text_weight": 2.0},
            (),
            model,
            cancel,
        )
        self.assertTrue(patched["updated"])
        self.assertEqual(patched["apply_from_frame"], 24)
        self.assertEqual((patched["start_frame"], patched["end_frame_exclusive"]), (24, 44))
        self.assertEqual(session.diffusion_steps, 12)
        self.assertEqual(session.cfg_text_weight, 4.0)

        session.generate({"time_as_double": 1.0}, (), model, cancel)
        seek, output = session.generate({"time_as_double": 0.2}, (), model, cancel)
        self.assertEqual((seek["start_frame"], seek["end_frame_exclusive"]), (4, 28))
        self.assertEqual(output["root_positions"].shape[1], 24)

        session.generate({"time_as_double": 1.0}, (), model, cancel)
        seek_patch, output = session.generate(
            {"time_as_double": 0.2, "prompt": "idle"}, (), model, cancel
        )
        self.assertEqual(seek_patch["apply_from_frame"], 4)
        self.assertEqual((seek_patch["start_frame"], seek_patch["end_frame_exclusive"]), (4, 28))
        self.assertEqual(session.frame_count, 28)

    def test_generation_uses_the_internal_autoregressive_history(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.history_cpu = torch.ones((1, 40, 1), dtype=torch.float32)

        history, history_len, window_start = session._history(SimpleNamespace(device="cpu"))

        self.assertEqual((history_len, window_start), (40, 0))
        self.assertTrue(torch.equal(history, torch.ones_like(history)))

    @staticmethod
    def _fake_ardy_session():
        profile = SimpleNamespace(
            source_fps=20.0,
            frames_per_token=4,
            max_diffusion_steps=100,
        )
        session = ardy_backend.ArdySession.__new__(ardy_backend.ArdySession)
        session.profile = profile
        session.settings = ardy_backend.ArdySettings(160, 160, 4, 20, 20, True)
        session.prompt = "idle"
        session.diffusion_steps = 10
        session.cfg_text_weight = 1.0
        session.update_revision = 0
        session.returned_until = 0
        session.last_played_frame = 0
        session.motion_cpu = torch.zeros((1, 0, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 0, 3), dtype=np.float32)}
        session.initial_history_cpu = None
        session.history_cpu = None
        session.constraints = []
        session.future_clips = []
        session.text_feat = session.text_pad_mask = None

        def ensure_generated(self, frame_exclusive, _model, _cancel_event):
            frame_count = max(self.frame_count, int(frame_exclusive))
            self.motion_cpu = torch.zeros((1, frame_count, 1), dtype=torch.float32)
            self.outputs = {
                "root_positions": np.arange(frame_count * 3, dtype=np.float32).reshape(1, frame_count, 3)
            }

        session._ensure_generated = MethodType(ensure_generated, session)
        return session


if __name__ == "__main__":
    unittest.main()
