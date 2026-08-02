import io
import math
from pathlib import Path
import threading
from types import SimpleNamespace
from types import MethodType
import unittest
from unittest.mock import ANY, patch

import numpy as np
import torch

from kimodo.bridge import ardy_backend
from kimodo.bridge import bridge_server
from kimodo.bridge import quickserver_cli
from kimodo.bridge.frame_time import seconds_to_frame_count


class QuickServerProtocolV2Tests(unittest.TestCase):
    def test_seconds_to_frame_count_uses_tolerance_protected_ceiling(self):
        self.assertEqual(seconds_to_frame_count(4.5666666, 30.0), 137)
        self.assertEqual(seconds_to_frame_count(5.0, 30.0), 150)
        self.assertEqual(seconds_to_frame_count(1.00001, 30.0), 31)
        self.assertEqual(seconds_to_frame_count(0.0, 30.0), 0)
        with self.assertRaises(ValueError):
            seconds_to_frame_count(float("nan"), 30.0)

    def test_kimodo_long_generation_segments_are_equal_and_never_exceed_ten_seconds(self):
        self.assertEqual(bridge_server._generation_segment_frames(300, 30.0), [300])
        self.assertEqual(bridge_server._generation_segment_frames(360, 30.0), [180, 180])
        self.assertEqual(bridge_server._generation_segment_frames(630, 30.0), [210, 210, 210])
        self.assertEqual(bridge_server._generation_segment_frames(301, 30.0), [151, 150])

    def test_kimodo_long_generation_uses_transition_connected_segments(self):
        class RecordingModel:
            fps = 30.0

            def __call__(self, *args, **kwargs):
                self.args = args
                self.kwargs = kwargs
                return {"generated": True}

        model = RecordingModel()
        with patch.object(bridge_server, "_load_constraints", return_value=[]), patch.object(bridge_server, "_out"):
            output, prompt = bridge_server._run_generate(
                {"duration": 11.0, "prompt": "walk"},
                model,
                emit_progress=False,
            )

        self.assertEqual(output, {"generated": True})
        self.assertEqual(prompt, "walk.")
        self.assertEqual(model.args[:2], (["walk.", "walk."], [165, 165]))
        self.assertTrue(model.kwargs["multi_prompt"])
        self.assertEqual(model.kwargs["num_transition_frames"], 5)

    def test_quickserver_generate_uses_shared_segmented_kimodo_runner(self):
        model = SimpleNamespace()
        expected_output = {"posed_joints": np.zeros((1, 1, 1, 3), dtype=np.float32)}
        expected_response = {"status": "done"}
        with patch.object(bridge_server, "_run_generate", return_value=(expected_output, "walk.")) as run_generate, patch.object(
            bridge_server, "_resolve_requested_output_format", return_value="json_compact"
        ), patch.object(bridge_server, "_build_generate_response", return_value=expected_response):
            response, payload = quickserver_cli._execute_generate({"duration": 11.0}, model, threading.Event())

        self.assertEqual(response, expected_response)
        self.assertIsNone(payload)
        run_generate.assert_called_once_with({"duration": 11.0}, model, ANY, emit_progress=False)

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

    def test_kimodo_rejects_automatic_root_target_with_explicit_semantics(self):
        with self.assertRaisesRegex(ValueError, "automatic ARDY-only navigation constraint"):
            bridge_server._load_constraints(
                '[{"type":"root2d_target","target_root_2d":[1.0,2.0]}]',
                SimpleNamespace(skeleton=object()),
            )

    def test_runtime_loading_progress_uses_stage_details_without_task_ids(self):
        self.assertEqual(
            quickserver_cli._build_streaming_status_message(
                "loading_runtime", -1, "private-task-id", "Task 'private-task-id' waiting in queue."
            ),
            ("loading", "Preparing motion runtime..."),
        )
        self.assertEqual(
            quickserver_cli._build_streaming_status_message(
                "loading_runtime", -1, "private-task-id", "[INFO] Preparing runtime: model=ARDY-Core"
            ),
            ("loading", "[INFO] Preparing runtime: model=ARDY-Core"),
        )
        self.assertEqual(
            quickserver_cli._build_streaming_status_message(
                "generating", -1, "private-task-id", "Loading TextEncoder weights..."
            ),
            ("progress", "Loading TextEncoder weights..."),
        )

    def test_cold_text_encoder_reports_loading_and_generation_stages(self):
        session = object.__new__(ardy_backend.ArdySession)
        session.prompt = "walk forward"
        messages = []
        model = SimpleNamespace(
            text_encoder=SimpleNamespace(model=None),
            _encode_text=lambda _prompts: ("text", "mask"),
        )

        session._encode_prompt(model, messages.append)

        self.assertEqual(session.text_feat, "text")
        self.assertEqual(session.text_pad_mask, "mask")
        self.assertEqual(
            messages,
            [
                "Loading TextEncoder weights and moving them to the accelerator...",
                "TextEncoder ready. Generating ARDY motion...",
            ],
        )

    def test_text_encoder_completion_observes_cancellation_before_generation(self):
        session = object.__new__(ardy_backend.ArdySession)
        session.prompt = "walk forward"
        cancel = threading.Event()

        def encode(_prompts):
            cancel.set()
            return "text", "mask"

        model = SimpleNamespace(
            text_encoder=SimpleNamespace(model=None),
            _encode_text=encode,
        )
        with self.assertRaises(bridge_server.GenerateCancelledError):
            session._encode_prompt(model, cancel_event=cancel)

    def test_active_cancel_publishes_terminal_generate_response_immediately(self):
        task = {
            "task_id": "loading-text-encoder",
            "request_id": "generate-request",
            "event": threading.Event(),
            "response": None,
            "binary": b"stale",
        }

        quickserver_cli._publish_cancelled_task_to_client(task, "Cancellation requested.")

        self.assertTrue(task["event"].is_set())
        self.assertIsNone(task["binary"])
        self.assertEqual(task["response"]["status"], "cancelled")
        self.assertEqual(task["response"]["task_id"], "loading-text-encoder")
        self.assertEqual(task["response"]["request_id"], "generate-request")

    def test_cancelled_kimodo_task_does_not_enter_model(self):
        cancel = threading.Event()
        cancel.set()
        model = SimpleNamespace(fps=30.0)
        with self.assertRaises(bridge_server.GenerateCancelledError):
            quickserver_cli._execute_generate({}, model, cancel)

    def test_shared_text_encoder_signature_uses_mode_not_models_directory_or_placement(self):
        base = {
            "text_encoder_mode": "high_precision",
            "models_root": "C:/runtime/models",
            "simulate_free_vram_gb": None,
        }
        editor = {
            **base,
            "models_root": "D:/editor/models",
            "simulate_free_vram_gb": 0.0,
            "_force_text_encoder_cpu": True,
        }
        high_performance = {**base, "text_encoder_mode": "high_performance"}

        self.assertEqual(
            quickserver_cli._build_text_encoder_signature(base),
            quickserver_cli._build_text_encoder_signature(editor),
        )
        self.assertNotEqual(
            quickserver_cli._build_text_encoder_signature(base),
            quickserver_cli._build_text_encoder_signature(high_performance),
        )

    def test_clearing_shared_text_encoder_detaches_every_runtime_reference(self):
        encoder = object()
        active_model = SimpleNamespace(text_encoder=encoder)
        session_model = SimpleNamespace(text_encoder=encoder)
        retired_model = SimpleNamespace(text_encoder=encoder)
        state = {
            "active_runtime": {"model": active_model},
            "sessions": {"runtime": {"ardy_runtime": {"model": session_model}}},
            "retired_runtimes": [{"model": retired_model}],
            "shared_text_encoder": encoder,
            "shared_text_encoder_signature": "text_encoder_mode=high_precision",
            "shared_text_encoder_decision": object(),
            "shared_text_encoder_models_root": "C:/runtime/models",
            "active_text_encoder_signature": "text_encoder_mode=high_precision",
        }

        self.assertIs(quickserver_cli._clear_shared_text_encoder_state(state), encoder)
        self.assertIsNone(active_model.text_encoder)
        self.assertIsNone(session_model.text_encoder)
        self.assertIsNone(retired_model.text_encoder)
        self.assertIsNone(state["shared_text_encoder"])
        self.assertEqual(state["shared_text_encoder_signature"], "")

    def test_missing_encoder_is_rebuilt_without_reloading_the_motion_runtime(self):
        config = {
            "model": "ARDY-Core-RP-20FPS-Horizon40",
            "text_encoder_mode": "high_precision",
            "models_root": "D:/editor/models",
            "force_hf_download": False,
            "simulate_free_vram_gb": None,
        }
        model = SimpleNamespace(text_encoder=None)
        runtime = {
            "model": model,
            "runtime_signature": quickserver_cli._build_signature(config),
            "resolved_model_name": config["model"],
            "runtime_device": "cpu",
            "fps": 20,
        }
        profile = SimpleNamespace(
            runtime_device="cpu",
            free_vram_gb=64.0,
            nf4_available=False,
            int8_accelerator_available=False,
            fp16_accelerator_available=False,
            backend_profile="cpu",
        )
        decision = quickserver_cli.assets.resolve_text_encoder_runtime(
            config["text_encoder_mode"],
            "cpu",
            62.0,
            nf4_available=False,
            int8_accelerator_available=False,
            fp16_accelerator_available=False,
        )

        def attach_encoder(target, *_args):
            target.text_encoder = object()

        with (
            patch.object(quickserver_cli.bridge_runtime_helpers, "_runtime_self_check", return_value=profile),
            patch.object(quickserver_cli.assets, "motion_model_min_free_vram_gb", return_value=2.0),
            patch.object(quickserver_cli.assets, "resolve_text_encoder_runtime", return_value=decision),
            patch.object(quickserver_cli, "_replace_text_encoder", side_effect=attach_encoder) as replace_encoder,
            patch.object(quickserver_cli, "_unload_runtime_model") as unload_runtime,
        ):
            result = quickserver_cli._ensure_runtime(
                runtime,
                config,
                "C:/quickserver",
                SimpleNamespace(log=lambda _message: None),
            )

        self.assertTrue(result["reused"])
        self.assertIsNotNone(model.text_encoder)
        replace_encoder.assert_called_once()
        unload_runtime.assert_not_called()

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
            expected_reserve = int(math.ceil(fps / 4) * 4)
            self.assertEqual(settings.playback_reserve_frames, expected_reserve)
            self.assertTrue(settings.adaptive_playback_reserve)

    def test_cursor_patch_pause_and_seek_use_one_cached_timeline(self):
        session = self._fake_ardy_session()
        model = SimpleNamespace()
        cancel = threading.Event()

        first, output = session.generate({"time_as_double": 0.0}, (), model, cancel)
        self.assertEqual((first["start_frame"], first["end_frame_exclusive"]), (0, 40))
        self.assertEqual(set(first), {"start_frame", "end_frame_exclusive"})
        self.assertEqual(output["root_positions"].shape[1], 40)

        paused, output = session.generate({"time_as_double": 0.0}, (), model, cancel)
        self.assertEqual((paused["start_frame"], paused["end_frame_exclusive"]), (40, 80))
        self.assertEqual(output["root_positions"].shape[1], 40)

        patched, output = session.generate(
            {"time_as_double": 0.0, "prompt": "walk", "diffusion_steps": 12, "text_weight": 2.0},
            (),
            model,
            cancel,
        )
        self.assertEqual((patched["start_frame"], patched["end_frame_exclusive"]), (20, 60))
        self.assertEqual(session.diffusion_steps, 12)
        self.assertEqual(session.cfg_text_weight, 4.0)

        session.generate({"time_as_double": 1.0}, (), model, cancel)
        seek, output = session.generate({"time_as_double": 0.2}, (), model, cancel)
        self.assertEqual((seek["start_frame"], seek["end_frame_exclusive"]), (24, 64))
        self.assertEqual(output["root_positions"].shape[1], 40)

        session.generate({"time_as_double": 1.0}, (), model, cancel)
        seek_patch, output = session.generate(
            {"time_as_double": 0.2, "prompt": "idle"}, (), model, cancel
        )
        self.assertEqual((seek_patch["start_frame"], seek_patch["end_frame_exclusive"]), (24, 64))
        self.assertEqual(session.frame_count, 64)

    def test_generation_uses_the_internal_autoregressive_history(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.history_cpu = torch.ones((1, 40, 1), dtype=torch.float32)

        history, history_len, window_start = session._history(SimpleNamespace(device="cpu"))

        self.assertEqual((history_len, window_start), (40, 0))
        self.assertTrue(torch.equal(history, torch.ones_like(history)))

    def test_explicit_history_is_token_aligned_and_capped(self):
        session = self._fake_ardy_session()
        session.quickserver_root = Path.cwd()
        motion = ardy_backend.KmbMotion(
            payload=b"kmb",
            model_name="test",
            fps=20.0,
            joint_names=("root",),
            joint_parents=(-1,),
            root_positions=np.zeros((166, 3), dtype=np.float32),
            local_rot_quats=np.zeros((166, 1, 4), dtype=np.float32),
            foot_contacts=None,
        )
        encoded = torch.arange(166, dtype=torch.float32).reshape(1, 166, 1)
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        item = {
            "type": "clip",
            "format": "kmb_attachment_v1",
            "attachment": 0,
            "is_history": True,
        }

        with (
            patch.object(ardy_backend, "parse_kmb1", return_value=motion),
            patch.object(ardy_backend, "_validate_kmb"),
            patch.object(ardy_backend, "_motion_to_tensor", return_value=encoded),
            patch("ardy.constraints.load_constraints_lst", return_value=[]),
        ):
            session._set_constraints([item], (b"kmb",), model, apply_from=0, initial=True)

        self.assertEqual(tuple(session.initial_history_cpu.shape), (1, 160, 1))
        self.assertTrue(torch.equal(session.initial_history_cpu, encoded[:, 6:]))

    def test_truncate_rebuilds_history_from_initial_history_and_generated_prefix(self):
        session = self._fake_ardy_session()
        session.initial_history_cpu = torch.arange(100, dtype=torch.float32).reshape(1, 100, 1)
        session.motion_cpu = torch.arange(100, 180, dtype=torch.float32).reshape(1, 80, 1)
        session.outputs = {"root_positions": np.zeros((1, 80, 3), dtype=np.float32)}
        session.history_cpu = torch.full((1, 160, 1), -1.0, dtype=torch.float32)

        session._truncate(40)

        expected = torch.arange(140, dtype=torch.float32).reshape(1, 140, 1)
        self.assertEqual(tuple(session.motion_cpu.shape), (1, 40, 1))
        self.assertTrue(torch.equal(session.history_cpu, expected))
        history, history_len, window_start = session._history(SimpleNamespace(device="cpu"))
        self.assertEqual((history_len, window_start), (140, -100))
        self.assertTrue(torch.equal(history, expected))

    def test_generated_horizon_rolls_to_the_latest_160_history_frames(self):
        session = self._fake_ardy_session()
        session.initial_history_cpu = torch.arange(160, dtype=torch.float32).reshape(1, 160, 1)
        session.motion_cpu = None
        session.outputs = None
        session._cpu_rng_state = torch.random.get_rng_state()
        session._cuda_rng_state = None
        session.profile.cfg_constraint_weight = 1.0
        session.profile.postprocess = False

        class MotionRep:
            def inverse(self, generated, is_normalized):
                self.generated = generated.detach().clone()
                return {
                    "root_positions": torch.zeros((1, 40, 3), dtype=torch.float32),
                    "local_rot_mats": torch.eye(3).reshape(1, 1, 1, 3, 3).repeat(1, 40, 1, 1, 1),
                    "foot_contacts": torch.zeros((1, 40, 4), dtype=torch.float32),
                }

        motion_rep = MotionRep()
        captured = {}

        def autoregressive_step(**kwargs):
            captured.update(kwargs)
            return torch.arange(200, dtype=torch.float32).reshape(1, 200, 1)

        model = SimpleNamespace(
            device="cpu",
            motion_rep=motion_rep,
            autoregressive_step=autoregressive_step,
        )

        session._generate_horizon(model)

        self.assertTrue(torch.equal(captured["init_history_sequence"], session.initial_history_cpu))
        self.assertTrue(torch.equal(session.motion_cpu, torch.arange(160, 200).reshape(1, 40, 1)))
        self.assertTrue(torch.equal(session.history_cpu, torch.arange(40, 200).reshape(1, 160, 1)))

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

    def test_root_target_cursor_sync_preserves_cached_future(self):
        session = self._fake_ardy_session()
        session.root_2d_target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)
        session.motion_cpu = torch.zeros((1, 80, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 80, 3), dtype=np.float32)}
        session.returned_until = 80
        replanned_from = []
        truncated_at = []

        def refresh(self, _model, boundary_frame):
            replanned_from.append(boundary_frame)

        def truncate(self, frame):
            truncated_at.append(frame)

        session._refresh_root_2d_target_constraints = MethodType(refresh, session)
        session._truncate = MethodType(truncate, session)
        metadata, _ = session.generate(
            {"time_as_double": 1.0}, (), SimpleNamespace(), threading.Event()
        )

        self.assertEqual(replanned_from, [])
        self.assertEqual(truncated_at, [])
        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (80, 120))

    def test_root_target_refreshes_when_extending_a_horizon(self):
        session = self._fake_ardy_session()
        session.root_2d_target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 40, 3), dtype=np.float32)}
        replanned_from = []

        def refresh(self, _model, boundary_frame):
            replanned_from.append(boundary_frame)

        def generate_horizon(self, _model):
            frame_count = self.frame_count + self.profile.horizon_frames
            self.motion_cpu = torch.zeros((1, frame_count, 1), dtype=torch.float32)
            self.outputs = {"root_positions": np.zeros((1, frame_count, 3), dtype=np.float32)}

        session._refresh_root_2d_target_constraints = MethodType(refresh, session)
        session._generate_horizon = MethodType(generate_horizon, session)
        ardy_backend.ArdySession._ensure_generated(
            session, 44, SimpleNamespace(), threading.Event()
        )

        self.assertEqual(replanned_from, [40])
        self.assertEqual(session.frame_count, 80)

    def test_generate_returns_the_complete_computed_horizon(self):
        session = self._fake_ardy_session()

        def ensure_horizon(self, frame_exclusive, _model, _cancel_event):
            frame_count = self.frame_count
            while frame_count < frame_exclusive:
                frame_count += self.profile.horizon_frames
            self.motion_cpu = torch.zeros((1, frame_count, 1), dtype=torch.float32)
            self.outputs = {"root_positions": np.zeros((1, frame_count, 3), dtype=np.float32)}

        session._ensure_generated = MethodType(ensure_horizon, session)
        metadata, output = session.generate(
            {"time_as_double": 0.0}, (), SimpleNamespace(), threading.Event()
        )

        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (0, 40))
        self.assertEqual(output["root_positions"].shape[1], 40)

    def test_horizontal8_generates_until_the_response_exceeds_the_reserve(self):
        session = self._fake_ardy_session()
        session.profile.horizon_frames = 8

        metadata, output = session.generate(
            {"time_as_double": 0.0}, (), SimpleNamespace(), threading.Event()
        )

        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (0, 24))
        self.assertGreater(output["root_positions"].shape[1], session.effective_playback_reserve_frames)

    def test_editor_one_shot_duration_is_independent_from_stream_reserve(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(160, 160, 20, False)
        session.effective_playback_reserve_frames = 20
        session._initial_duration_frames = 60

        metadata, output = session.generate(
            {"time_as_double": 0.0}, (), SimpleNamespace(), threading.Event()
        )

        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (0, 60))
        self.assertEqual(output["root_positions"].shape[1], 60)

    def test_timeline_segments_resolve_prompt_boundaries_from_fixed_duration(self):
        profile = SimpleNamespace(source_fps=20.0, frames_per_token=4)

        segments = ardy_backend._parse_timeline_segments(
            [
                {"prompt": "walk", "duration": 1.0},
                {"prompt": "turn left", "duration": 2.0},
            ],
            profile,
            60,
        )

        self.assertEqual(
            segments,
            (
                ardy_backend.ArdyTimelineSegment("walk", 0, 20),
                ardy_backend.ArdyTimelineSegment("turn left", 20, 60),
            ),
        )

    def test_timeline_segments_allow_unaligned_boundary_and_reject_mismatched_duration(self):
        profile = SimpleNamespace(source_fps=20.0, frames_per_token=4)

        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "resolves to 20 frames"):
            ardy_backend._parse_timeline_segments(
                [{"prompt": "walk", "duration": 1.0}], profile, 40
            )
        segments = ardy_backend._parse_timeline_segments(
            [
                {"prompt": "walk", "duration": 0.1},
                {"prompt": "turn", "duration": 1.9},
            ],
            profile,
            40,
        )
        self.assertEqual(
            segments,
            (
                ardy_backend.ArdyTimelineSegment("walk", 0, 2),
                ardy_backend.ArdyTimelineSegment("turn", 2, 40),
            ),
        )

    def test_fixed_duration_replaces_stream_state_and_closes_after_result(self):
        events = []

        class FakeSession:
            resolved_seed = 7
            effective_playback_reserve_frames = 0
            settings = SimpleNamespace(adaptive_playback_reserve=False)

            def generate(self, request, _attachments, _model, _cancel_event):
                events.append(("generate", dict(request)))
                return {"start_frame": 0, "end_frame_exclusive": 20}, None

            def record_response_duration(self, _elapsed, delivered_frames):
                events.append(("record", delivered_frames))

            def close(self):
                events.append(("close", self))

        previous = FakeSession()
        fixed = FakeSession()
        profile = SimpleNamespace(source_fps=20.0, motion_rep_fingerprint="test")
        with patch.object(ardy_backend, "ArdySession", return_value=fixed):
            returned, response, payload = ardy_backend.execute_stream_generate(
                previous,
                {"duration": 1.0, "prompt": "walk"},
                (),
                SimpleNamespace(),
                profile,
                threading.Event(),
                ".",
            )

        self.assertIsNone(returned)
        self.assertEqual((response["start_frame"], response["end_frame_exclusive"]), (0, 20))
        self.assertIsNone(payload)
        self.assertIn(("close", previous), events)
        self.assertIn(("close", fixed), events)

    def test_fixed_duration_rejects_zero_without_closing_the_stream(self):
        stream = SimpleNamespace(close=lambda: self.fail("invalid duration closed the stream"))
        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "finite positive"):
            ardy_backend.execute_stream_generate(
                stream,
                {"duration": 0.0},
                (),
                SimpleNamespace(),
                SimpleNamespace(),
                threading.Event(),
                ".",
            )

    def test_adaptive_playback_reserve_uses_measured_response_time(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(160, 160, 20, True)

        session.record_response_duration(1.0, delivered_frames=40)

        self.assertEqual(session.effective_playback_reserve_frames, 36)
        self.assertAlmostEqual(session.effective_playback_reserve_frames / session.profile.source_fps, 1.8)

    def test_adaptive_playback_reserve_can_grow_beyond_the_previous_delivery(self):
        session = self._fake_ardy_session()
        session.profile.horizon_frames = 8
        session.settings = ardy_backend.ArdySettings(192, 192, 20, True)

        session.record_response_duration(1.0, delivered_frames=24)

        self.assertEqual(session.effective_playback_reserve_frames, 36)

    def test_adaptive_playback_reserve_decreases_one_token_at_a_time(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(160, 160, 20, True)
        session.effective_playback_reserve_frames = 36
        session._response_seconds_ema = 0.0

        observed = []
        for _ in range(4):
            session.record_response_duration(0.0, delivered_frames=40)
            observed.append(session.effective_playback_reserve_frames)

        self.assertEqual(observed, [32, 28, 24, 20])

    def test_constraint_returns_old_bridge_when_delivered_tail_is_inside_the_reserve(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 8, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 8, 3), dtype=np.float32)}
        session.returned_until = 8

        metadata, output = session.generate(
            {"time_as_double": 0.0, "prompt": "walk"},
            (),
            SimpleNamespace(),
            threading.Event(),
        )

        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (8, 60))
        self.assertEqual(output["root_positions"].shape[1], 52)

    def test_settings_only_update_does_not_truncate_cached_future(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 80, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 80, 3), dtype=np.float32)}
        session.returned_until = 40
        truncated_at = []

        def truncate(self, frame):
            truncated_at.append(frame)

        session._truncate = MethodType(truncate, session)
        metadata, _ = session.generate(
            {
                "time_as_double": 1.0,
                "ardy_playback_reserve_seconds": 1.0,
                "ardy_adaptive_playback_reserve": True,
            },
            (),
            SimpleNamespace(),
            threading.Event(),
        )

        self.assertEqual(truncated_at, [])
        self.assertEqual((metadata["start_frame"], metadata["end_frame_exclusive"]), (40, 80))

    def test_far_constraint_releases_history_for_future_context(self):
        profile = SimpleNamespace(
            horizon_frames=40,
            frames_per_token=4,
            max_context_frames=200,
        )
        settings = ardy_backend.ArdySettings(160, 160, 20, False)

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
        session.settings = ardy_backend.ArdySettings(160, 160, 20, False)
        session.prompt = "idle"
        session.diffusion_steps = 10
        session.cfg_text_weight = 1.0
        session.returned_until = 0
        session.last_played_frame = 0
        session.effective_playback_reserve_frames = 20
        session._response_seconds_ema = None
        session._initial_duration_frames = 0
        session.motion_cpu = torch.zeros((1, 0, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 0, 3), dtype=np.float32)}
        session.initial_history_cpu = None
        session.history_cpu = None
        session.constraints = []
        session.constraint_items = []
        session.root_2d_target = None
        session.future_clips = []
        session.timeline_segments = ()
        session._encoded_prompts = {}
        session.text_feat = session.text_pad_mask = None

        def ensure_generated(self, frame_exclusive, _model, _cancel_event):
            frame_count = self.frame_count
            while frame_count < int(frame_exclusive):
                frame_count += self.profile.horizon_frames
            self.motion_cpu = torch.zeros((1, frame_count, 1), dtype=torch.float32)
            self.outputs = {
                "root_positions": np.arange(frame_count * 3, dtype=np.float32).reshape(1, frame_count, 3)
            }

        session._ensure_generated = MethodType(ensure_generated, session)
        return session


if __name__ == "__main__":
    unittest.main()
