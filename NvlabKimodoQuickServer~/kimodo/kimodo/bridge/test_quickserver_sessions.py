import io
import math
from pathlib import Path
import threading
from types import SimpleNamespace
from types import MethodType
import unittest
from unittest.mock import patch

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

    def test_dense_root_waypoint_expands_from_the_preserved_seam(self):
        expanded = ardy_backend._expand_dense_root_constraint(
            {
                "type": "root2d",
                "frame_indices": [4],
                "smooth_root_2d": [[4.0, 8.0]],
                "dense_path": True,
            },
            anchor_frame=0,
            anchor_root_2d=(0.0, 0.0),
        )

        self.assertEqual(len(expanded), 1)
        self.assertEqual(expanded[0]["frame_indices"], [1, 2, 3, 4])
        np.testing.assert_allclose(
            expanded[0]["smooth_root_2d"],
            [[1.0, 2.0], [2.0, 4.0], [3.0, 6.0], [4.0, 8.0]],
        )

    def test_root_target_plans_sparse_speed_limited_waypoints_with_heading(self):
        target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)

        planned = ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (0.0, 0.0), -1, 20.0)

        self.assertEqual(planned["frame_indices"], [9, 19, 29, 39])
        positions = np.asarray(planned["smooth_root_2d"])
        self.assertTrue(np.all(np.diff(positions[:, 0]) > 0.0))
        self.assertLessEqual(float(positions[-1, 0]), 2.5)
        np.testing.assert_allclose(
            planned["global_root_heading"],
            np.full(4, math.pi / 2),
            atol=1e-7,
        )

    def test_root_target_behind_uses_backward_world_heading_not_backward_motion(self):
        target = ardy_backend.Root2DTarget((-10.0, 0.0), 1.25, 1.5, 0.1, True)

        planned = ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (0.0, 0.0), 23, 20.0)

        self.assertEqual(planned["frame_indices"][0], 33)
        self.assertTrue(all(point[0] < 0.0 for point in planned["smooth_root_2d"]))
        np.testing.assert_allclose(planned["global_root_heading"], np.full(4, -math.pi / 2), atol=1e-7)

    def test_root_target_heading_uses_ardy_plus_z_forward_axes(self):
        for target_position, expected_heading in (
            ((0.0, 10.0), 0.0),
            ((10.0, 0.0), math.pi / 2),
            ((0.0, -10.0), math.pi),
            ((-10.0, 0.0), -math.pi / 2),
        ):
            with self.subTest(target_position=target_position):
                target = ardy_backend.Root2DTarget(target_position, 1.25, 1.5, 0.1, True)
                planned = ardy_backend._plan_root_2d_target(
                    target, (0.0, 0.0), (0.0, 0.0), -1, 20.0
                )
                self.assertAlmostEqual(planned["global_root_heading"][0], expected_heading)

    def test_root_target_heading_follows_limited_velocity_during_a_turn(self):
        target = ardy_backend.Root2DTarget((-10.0, 0.0), 1.25, 1.5, 0.1, True)

        planned = ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (1.0, 0.0), -1, 20.0)

        self.assertGreater(planned["smooth_root_2d"][0][0], 0.0)
        self.assertAlmostEqual(planned["global_root_heading"][0], math.pi / 2)
        self.assertAlmostEqual(planned["global_root_heading"][-1], -math.pi / 2)

    def test_root_target_stops_inside_arrival_threshold(self):
        target = ardy_backend.Root2DTarget((0.05, 0.0), 1.25, 1.5, 0.1, True)
        self.assertIsNone(
            ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (0.0, 0.0), -1, 20.0)
        )

    def test_root_target_protocol_is_resolved_replaced_and_cleared_in_python(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 8, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 8, 3), dtype=np.float32)}
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        loaded = []

        def load_constraints(items, _skeleton):
            loaded.append(items)
            return items

        with patch("ardy.constraints.load_constraints_lst", side_effect=load_constraints):
            session._set_constraints(
                [{"type": "root2d_target", "target_root_2d": [5.0, 0.0]}],
                (),
                model,
                apply_from=8,
                initial=False,
            )
            self.assertEqual(session.root_2d_target.position, (5.0, 0.0))
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d"])

            session._set_constraints(
                [{"type": "root2d_target", "target_root_2d": [-3.0, 2.0]}],
                (),
                model,
                apply_from=8,
                initial=False,
            )
            self.assertEqual(session.root_2d_target.position, (-3.0, 2.0))
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d"])

            session._set_constraints([], (), model, apply_from=8, initial=False)
            self.assertIsNone(session.root_2d_target)
            self.assertEqual(session.constraints, [])

    def test_root_target_replan_discards_only_the_unreturned_suffix(self):
        session = self._fake_ardy_session()
        session.root_2d_target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)
        session.returned_until = 24
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 40, 3), dtype=np.float32)}
        replanned_from = []

        def refresh(self, _model, boundary_frame):
            replanned_from.append(boundary_frame)

        session._refresh_root_2d_target_constraints = MethodType(refresh, session)
        metadata, _ = session.generate(
            {"time_as_double": 1.0}, (), SimpleNamespace(), threading.Event()
        )

        self.assertEqual(replanned_from, [24])
        self.assertEqual(metadata["start_frame"], 24)
        self.assertGreaterEqual(session.frame_count, metadata["end_frame_exclusive"])

    def test_far_constraint_releases_history_for_future_context(self):
        profile = SimpleNamespace(
            horizon_frames=40,
            frames_per_token=4,
            max_context_frames=200,
        )
        settings = ardy_backend.ArdySettings(160, 160, 4, 20, 20, True)

        self.assertEqual(ardy_backend._history_limit_for_future(profile, settings, 100, 139), 160)
        self.assertEqual(ardy_backend._history_limit_for_future(profile, settings, 100, 232), 64)
        self.assertEqual(ardy_backend._history_limit_for_future(profile, settings, 100, 259), 40)

    @staticmethod
    def _fake_ardy_session():
        profile = SimpleNamespace(
            source_fps=20.0,
            frames_per_token=4,
            horizon_frames=40,
            max_context_frames=200,
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
        session.constraint_items = []
        session.root_2d_target = None
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
