import io
import json
import math
import os
from pathlib import Path
import threading
from types import SimpleNamespace
from types import MethodType
import unittest
from unittest.mock import ANY, patch
import warnings

import numpy as np
import torch

from core import ardy_backend
from core import kimodo_runtime
from core import quickserver_cli
from kimodo.frame_time import seconds_to_frame_count
from kimodo.model import kimodo_model


class QuickServerProtocolV2Tests(unittest.TestCase):
    def test_protocol_help_lists_model_configuration_command(self):
        commands = quickserver_cli._build_protocol_help()["commands"]
        self.assertIn("runtime.list_models", [item["cmd"] for item in commands])
        self.assertIn("help", [item["cmd"] for item in commands])

    def test_model_configurations_are_flattened_by_model_and_encoder(self):
        runtime_profile = SimpleNamespace(
            runtime_device="cuda:0",
            free_vram_gb=32.0,
            nf4_available=True,
            int8_accelerator_available=True,
            fp16_accelerator_available=True,
        )
        result = quickserver_cli._build_model_configurations(
            "C:/runtime",
            {"model": "Kimodo-SOMA-RP-v1", "text_encoder_mode": "high_performance"},
            runtime_profile,
        )

        self.assertEqual(
            len(result["configs"]),
            2 * (len(quickserver_cli.assets.MAIN_MODELS) + len(quickserver_cli.assets.MOTION_MODEL_PROFILES)),
        )
        default = next(item for item in result["configs"] if item["default"])
        self.assertEqual(default["model"], "Kimodo-SOMA-RP-v1")
        self.assertEqual(default["text_encoder_model"], "high_performance")
        self.assertEqual(default["text_encoder_route"], "nf4")

        ardy_result = quickserver_cli._build_model_configurations(
            "C:/runtime",
            {"model": "ardy-core", "text_encoder_mode": "high_precision"},
            runtime_profile,
        )
        self.assertEqual(ardy_result["default"]["model"], "ARDY-Core-RP-20FPS-Horizon40")

    def test_posix_pid_check_uses_signal_zero(self):
        with patch.object(quickserver_cli.os, "name", "posix"), patch.object(
            quickserver_cli.os, "kill"
        ) as kill:
            self.assertTrue(quickserver_cli._pid_is_running(1234))

        kill.assert_called_once_with(1234, 0)

    def test_posix_pid_check_treats_missing_process_as_stopped(self):
        with patch.object(quickserver_cli.os, "name", "posix"), patch.object(
            quickserver_cli.os, "kill", side_effect=ProcessLookupError()
        ):
            self.assertFalse(quickserver_cli._pid_is_running(1234))

    def test_posix_pid_check_treats_permission_denied_as_running(self):
        with patch.object(quickserver_cli.os, "name", "posix"), patch.object(
            quickserver_cli.os, "kill", side_effect=PermissionError()
        ):
            self.assertTrue(quickserver_cli._pid_is_running(1234))

    def test_seconds_to_frame_count_uses_tolerance_protected_ceiling(self):
        self.assertEqual(seconds_to_frame_count(4.5666666, 30.0), 137)
        self.assertEqual(seconds_to_frame_count(5.0, 30.0), 150)
        self.assertEqual(seconds_to_frame_count(1.00001, 30.0), 31)
        self.assertEqual(seconds_to_frame_count(0.0, 30.0), 0)
        with self.assertRaises(ValueError):
            seconds_to_frame_count(float("nan"), 30.0)

    def test_kimodo_long_generation_segments_are_equal_and_never_exceed_ten_seconds(self):
        self.assertEqual(kimodo_runtime._generation_segment_frames(300, 30.0), [300])
        self.assertEqual(kimodo_runtime._generation_segment_frames(360, 30.0), [180, 180])
        self.assertEqual(kimodo_runtime._generation_segment_frames(630, 30.0), [210, 210, 210])
        self.assertEqual(kimodo_runtime._generation_segment_frames(301, 30.0), [151, 150])

    def test_kimodo_static_graph_requires_explicit_opt_in(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertFalse(kimodo_model._kimodo_static_graph_enabled())
        with patch.dict(os.environ, {"KIMODO_STATIC_GRAPH": "1"}, clear=True):
            self.assertTrue(kimodo_model._kimodo_static_graph_enabled())

    def test_kimodo_long_generation_uses_transition_connected_segments(self):
        class RecordingModel:
            fps = 30.0

            def __call__(self, *args, **kwargs):
                self.args = args
                self.kwargs = kwargs
                return {"generated": True}

        model = RecordingModel()
        with patch.object(kimodo_runtime, "_load_constraints", return_value=[]), patch.object(kimodo_runtime, "_out"):
            output, prompt = kimodo_runtime._run_generate(
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
        with patch.object(kimodo_runtime, "_run_generate", return_value=(expected_output, "walk.")) as run_generate, patch.object(
            kimodo_runtime, "_resolve_requested_output_format", return_value="json_compact"
        ), patch.object(kimodo_runtime, "_build_generate_response", return_value=expected_response):
            response, payload = quickserver_cli._execute_generate({"duration": 11.0}, model, threading.Event())

        self.assertEqual(response, expected_response)
        self.assertIsNone(payload)
        run_generate.assert_called_once_with({"duration": 11.0}, model, ANY, emit_progress=False)

    def test_loop_generation_runs_twice_and_reuses_seed_pose_at_both_boundaries(self):
        model = SimpleNamespace()
        seed_output = {"seed": True}
        final_output = {"final": True}
        loop_constraints = ["loop-pose"]
        expected_response = {"status": "done"}
        messages = []
        with patch.object(
            kimodo_runtime,
            "_run_generate",
            side_effect=[(seed_output, "walk."), (final_output, "walk.")],
        ) as run_generate, patch.object(
            kimodo_runtime,
            "_build_loop_body_pose_constraints",
            return_value=loop_constraints,
        ) as build_constraints, patch.object(
            kimodo_runtime,
            "_resolve_requested_output_format",
            return_value="json_compact",
        ), patch.object(kimodo_runtime, "_build_generate_response", return_value=expected_response):
            response, payload = quickserver_cli._execute_generate(
                {"duration": 2.0, "loop": True},
                model,
                threading.Event(),
                messages.append,
            )

        self.assertEqual(response, expected_response)
        self.assertIsNone(payload)
        build_constraints.assert_called_once_with(seed_output, model)
        self.assertEqual(run_generate.call_count, 2)
        self.assertEqual(run_generate.call_args_list[0].kwargs, {"emit_progress": False})
        self.assertEqual(
            run_generate.call_args_list[1].kwargs,
            {"emit_progress": False, "additional_constraints": loop_constraints},
        )
        self.assertEqual(messages, ["Generating loop seed motion...", "Generating loop-constrained motion..."])

    def test_loop_boundary_constraint_reuses_seed_frame_zero_without_root_position_condition(self):
        class Skeleton:
            nbjoints = 2
            root_idx = 0
            device = torch.device("cpu")

            def fk(self, local_rotations, root_positions):
                posed = torch.zeros((len(root_positions), 2, 3), dtype=local_rotations.dtype)
                posed[:, 0] = root_positions
                posed[:, 1] = root_positions + torch.tensor([1.0, 0.0, 0.0])
                return local_rotations, posed, None

        identity = np.eye(3, dtype=np.float32)
        output = {
            "local_rot_mats": np.broadcast_to(identity, (1, 3, 2, 3, 3)).copy(),
            "root_positions": np.asarray([[[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [2.0, 0.0, 0.0]]]),
            "smooth_root_pos": np.asarray([[[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [2.0, 0.0, 0.0]]]),
        }
        constraint = kimodo_runtime._build_loop_body_pose_constraints(
            output,
            SimpleNamespace(skeleton=Skeleton()),
        )[0]

        self.assertTrue(torch.equal(constraint.frame_indices, torch.tensor([0, 2])))
        self.assertTrue(torch.equal(constraint.local_joints_positions[0], constraint.local_joints_positions[1]))
        self.assertTrue(torch.equal(constraint.global_joints_rots[0], constraint.global_joints_rots[1]))
        self.assertTrue(torch.equal(constraint.joint_indices, torch.tensor([1])))

    def test_loop_generation_stops_when_cancelled_between_passes(self):
        cancel = threading.Event()
        model = SimpleNamespace()

        def seed_then_cancel(*_args, **_kwargs):
            cancel.set()
            return {"seed": True}, "walk."

        with patch.object(kimodo_runtime, "_run_generate", side_effect=seed_then_cancel) as run_generate, patch.object(
            kimodo_runtime,
            "_build_loop_body_pose_constraints",
        ) as build_constraints, self.assertRaises(kimodo_runtime.GenerateCancelledError):
            quickserver_cli._execute_generate({"loop": True}, model, cancel)

        run_generate.assert_called_once()
        build_constraints.assert_not_called()

    def test_ardy_imports_from_the_bundled_runtime(self):
        import ardy

        self.assertTrue(Path(ardy.__file__).resolve().is_relative_to(ardy_backend.BUNDLED_ARDY_ROOT))

    def test_ardy_fullbody_constraint_indices_follow_the_skeleton_device(self):
        from collections import defaultdict
        from ardy.constraints import FullBodyConstraintSet

        skeleton = SimpleNamespace(
            device=torch.device("meta"), nbjoints=2, root_idx=0, hip_joint_idx=(0, 1)
        )
        constraint = FullBodyConstraintSet(
            skeleton,
            frame_indices=torch.tensor([1]),
            global_joints_positions=torch.zeros((1, 2, 3)),
            global_joints_rots=torch.eye(3).reshape(1, 1, 3, 3).repeat(1, 2, 1, 1),
        )
        index_dict = defaultdict(list)
        constraint.update_constraints(defaultdict(list), index_dict)

        self.assertEqual(constraint.frame_indices.device.type, "meta")
        self.assertEqual(index_dict["global_joints_positions"][0].device.type, "meta")

    def test_fixed_ardy_root2d_constraints_preserve_the_wire_coordinates(self):
        session = self._fake_ardy_session()
        session._normalize_constraint_origin = True
        skeleton = SimpleNamespace(device=torch.device("cpu"), root_idx=0)
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=skeleton), skeleton=skeleton)

        session._set_constraints(
            [
                {
                    "type": "root2d",
                    "frame_indices": [0],
                    "smooth_root_2d": [[-0.539, 0.0]],
                    "global_root_heading": [[0.0, 1.0]],
                }
            ],
            (),
            model,
            apply_from=0,
            initial=True,
        )

        np.testing.assert_allclose(session.constraints[0].root_2d.cpu(), [[-0.539, 0.0]], atol=1e-7)
        self.assertIsNone(session.constraint_origin)

    def test_direct_kmb_is_the_only_binary_motion_format(self):
        self.assertEqual(
            kimodo_runtime._resolve_requested_output_format({"output_format": "kmb_v1"}),
            "kmb_v1",
        )
        self.assertNotEqual(
            kimodo_runtime._resolve_requested_output_format({"output_format": "removed_format"}),
            "removed_format",
        )

    def test_kimodo_root2d_keeps_explicit_points_and_heading_pairs(self):
        model = SimpleNamespace(fps=30.0, skeleton=object())
        with patch("kimodo.constraints.load_constraints_lst", side_effect=lambda items, _skeleton: items):
            constraints = kimodo_runtime._load_constraints(
                '[{"type":"root2d","frame_indices":[0,149],"smooth_root_2d":[[0.0,0.0],[100.0,0.0]],"global_root_heading":[[0.0,1.0],[1.0,0.0]]}]',
                model,
                horizon_frames=150,
            )

        self.assertEqual([item["type"] for item in constraints], ["root2d"])
        self.assertEqual(constraints[0]["frame_indices"], [0, 149])
        self.assertEqual(constraints[0]["global_root_heading"], [[0.0, 1.0], [1.0, 0.0]])

    def test_root2d_target_protocol_is_rejected(self):
        model = SimpleNamespace(fps=30.0, skeleton=object())
        with self.assertRaisesRegex(ValueError, "root2d_target was removed"):
            kimodo_runtime._load_constraints(
                '[{"type":"root2d_target","target_root_2d":[100.0,0.0]}]',
                model,
                horizon_frames=150,
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

    def test_queued_task_stays_queued_while_another_worker_loads(self):
        self.assertEqual(
            quickserver_cli._build_streaming_status_message(
                "loading_runtime", 1, "queued-task", "Preparing motion runtime..."
            ),
            ("queued", "Task 'queued-task' waiting in queue. queue_index=1"),
        )

    def test_ardy_batcher_merges_compatible_session_contexts(self):
        calls = []

        class FakeModel:
            device = "cpu"
            _kimodo_runtime_signature = "shared-ardy-runtime"
            denoiser = SimpleNamespace()
            motion_rep = SimpleNamespace(motion_rep_dim=6)

            @staticmethod
            def autoregressive_step(**kwargs):
                calls.append(kwargs)
                return kwargs["initial_noise"]

        batcher = ardy_backend._ArdyInferenceBatcher(max_batch_size=2, wait_seconds=0.05)
        batcher.set_session_count(2)
        barrier = threading.Barrier(3)
        results = [None, None]

        def submit(index, text_length):
            barrier.wait()
            results[index] = batcher.run(
                FakeModel(),
                {
                    "num_frames": 8,
                    "num_denoising_steps": 10,
                    "motion_mask": None,
                    "observed_motion": None,
                    "cfg_weight": (1.0, 1.0),
                    "texts": [f"prompt-{index}"],
                    "text_feat": torch.full((1, text_length, 4), float(index + 1)),
                    "text_pad_mask": torch.ones((1, text_length), dtype=torch.bool),
                    "init_history_sequence": torch.full((1, 4, 5), float(index + 1)),
                    "initial_noise": torch.full((1, 2, 3), float(index + 1)),
                },
            )

        threads = [
            threading.Thread(target=submit, args=(0, 2)),
            threading.Thread(target=submit, args=(1, 3)),
        ]
        for thread in threads:
            thread.start()
        barrier.wait()
        for thread in threads:
            thread.join(timeout=2.0)

        self.assertEqual(len(calls), 1)
        self.assertEqual(tuple(calls[0]["text_feat"].shape), (2, 3, 4))
        self.assertEqual(tuple(calls[0]["init_history_sequence"].shape), (2, 4, 5))
        self.assertEqual(sorted(float(result[0, 0, 0]) for result in results), [1.0, 2.0])

    def test_ardy_batch_capacity_grows_and_shrinks_with_session_count(self):
        batcher = ardy_backend._ArdyInferenceBatcher(max_batch_size=8)

        self.assertEqual(
            [batcher.set_session_count(count) for count in (1, 2, 3, 4, 5, 8)],
            [1, 2, 4, 4, 8, 8],
        )
        self.assertEqual(batcher.set_session_count(4), 8)
        self.assertEqual(batcher.set_session_count(3), 4)

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
        with self.assertRaises(kimodo_runtime.GenerateCancelledError):
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
        with self.assertRaises(kimodo_runtime.GenerateCancelledError):
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

    def test_text_encoder_execution_gate_allows_matching_ardy_and_serializes_kimodo(self):
        gate = quickserver_cli._TextEncoderExecutionGate()
        ardy_key = ("text_encoder_mode=high_precision", "ardy")
        kimodo_key = ("text_encoder_mode=high_performance", "kimodo")
        kimodo_entered = threading.Event()

        gate.acquire(ardy_key)
        gate.acquire(ardy_key)

        def enter_kimodo():
            gate.acquire(kimodo_key)
            try:
                kimodo_entered.set()
            finally:
                gate.release(kimodo_key)

        thread = threading.Thread(target=enter_kimodo)
        thread.start()
        self.assertFalse(kimodo_entered.wait(0.05))

        gate.release(ardy_key)
        self.assertFalse(kimodo_entered.wait(0.05))
        gate.release(ardy_key)

        self.assertTrue(kimodo_entered.wait(1.0))
        thread.join(timeout=1.0)
        self.assertFalse(thread.is_alive())

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
            patch.object(quickserver_cli.runtime_helpers, "_runtime_self_check", return_value=profile),
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
            self.assertTrue(settings.auto_history)
            expected_reserve = int(math.ceil(fps / 4) * 4)
            self.assertEqual(settings.playback_reserve_frames, expected_reserve)
            self.assertTrue(settings.adaptive_playback_reserve)

            ignored_legacy_fields = ardy_backend.ArdySettings.from_request(
                {
                    "ardy_history_crop_seconds": 2.0,
                    "ardy_future_crop_seconds": 2.0,
                    "ardy_history_transition_weight": 0.0,
                    "ardy_adaptive_playback_reserve": False,
                },
                profile,
            )
            self.assertTrue(ignored_legacy_fields.auto_history)
            self.assertEqual(ignored_legacy_fields.history_crop_frames, expected_history)
            self.assertTrue(ignored_legacy_fields.adaptive_playback_reserve)

    def test_analysis_only_returns_one_manifest_entry_per_clip_without_a_model(self):
        roots = np.asarray([[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]], dtype=np.float32)
        quats = np.zeros((2, 1, 4), dtype=np.float32)
        quats[..., 3] = 1.0

        def parse(payload):
            return ardy_backend.KmbMotion(
                payload=payload,
                model_name="test",
                fps=20.0,
                joint_names=("root",),
                joint_parents=(-1,),
                root_positions=roots,
                local_rot_quats=quats,
                foot_contacts=None,
            )

        request = {
            "analysis_option": {"analysis_only": True, "keyframes": {"max_count": 4}},
            "constraints_json": json.dumps([
                {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0, "start_frame": 0, "end_frame_exclusive": 2},
                {"type": "clip", "format": "kmb_attachment_v1", "attachment": 1, "start_frame": 0, "end_frame_exclusive": 2},
            ]),
        }
        with patch.object(ardy_backend, "parse_kmb1", side_effect=parse):
            response, payload = quickserver_cli._execute_analysis_only(request, (b"first", b"second"), ".")

        self.assertEqual(response["output_format"], "kmb_attachments_v1")
        self.assertEqual(payload, b"firstsecond")
        self.assertEqual([item["index"] for item in response["kmb_attachments"]], [0, 1])
        self.assertEqual([item["start_frame"] for item in response["kmb_attachments"]], [0, 0])
        self.assertEqual(response["analysis"]["clips"][0]["frame_count"], 2)

    def test_history_weight_maps_to_token_aligned_window(self):
        profile = SimpleNamespace(
            source_fps=20.0,
            horizon_frames=40,
            frames_per_token=4,
            max_context_frames=200,
        )

        for weight, expected_frames in ((0.0, 4), (0.5, 84), (1.0, 160)):
            settings = ardy_backend.ArdySettings.from_request(
                {"ardy_history_weight": weight},
                profile,
            )
            self.assertEqual(settings.history_crop_frames, expected_frames)
            self.assertFalse(settings.auto_history)
            self.assertFalse(settings.adaptive_playback_reserve)

        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "ardy_history_weight"):
            ardy_backend.ArdySettings.from_request({"ardy_history_weight": 1.01}, profile)

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
        self.assertEqual(session.cfg_text_weight, 1.0)

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
        session._normalize_constraint_origin = True
        session.quickserver_root = Path.cwd()
        root_positions = np.zeros((166, 3), dtype=np.float32)
        root_positions[:, 0] = np.arange(166, dtype=np.float32) * 0.01
        root_positions[:, 2] = np.arange(166, dtype=np.float32) * 0.02
        motion = ardy_backend.KmbMotion(
            payload=b"kmb",
            model_name="test",
            fps=20.0,
            joint_names=("root",),
            joint_parents=(-1,),
            root_positions=root_positions,
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
            patch("ardy.constraints.load_constraints_lst", return_value=[object()]),
        ):
            session._set_constraints([item], (b"kmb",), model, apply_from=0, initial=True)

        self.assertEqual(tuple(session.initial_history_cpu.shape), (1, 160, 1))
        self.assertTrue(torch.equal(session.initial_history_cpu, encoded[:, 6:]))
        self.assertIsNone(session.constraint_origin)
        np.testing.assert_allclose(session.initial_history_root_2d, [1.65, 3.3], atol=1e-6)
        np.testing.assert_allclose(session.initial_history_velocity_2d, [0.2, 0.4], atol=1e-5)
        root_2d, velocity_2d = session._root_state_at_boundary(0)
        np.testing.assert_allclose(root_2d, [1.65, 3.3], atol=1e-6)
        np.testing.assert_allclose(velocity_2d, [0.2, 0.4], atol=1e-5)

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

    def test_root_target_reserves_the_first_quarter_of_each_future_horizon(self):
        target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)

        for horizon in (8, 52):
            with self.subTest(horizon=horizon):
                planned = ardy_backend._plan_root_2d_target(
                    target,
                    (0.0, 0.0),
                    (0.0, 0.0),
                    39,
                    20.0,
                    horizon,
                )
                guard_frames = math.ceil(horizon / 4)
                expected_first = max(49, 40 + guard_frames)
                self.assertEqual(planned["frame_indices"][0], expected_first)
                self.assertEqual(planned["frame_indices"][1:], [59, 69, 79])

    def test_root_target_moves_core40_terminal_waypoint_past_the_horizon(self):
        target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, True)

        planned = ardy_backend._plan_root_2d_target(
            target,
            (0.0, 0.0),
            (0.0, 0.0),
            39,
            20.0,
            40,
        )

        self.assertEqual(planned["frame_indices"], [50, 59, 69, 83])

    def test_horizon_trace_reports_protected_hits_and_generated_trajectory(self):
        session = self._fake_ardy_session()
        session.session_trace_id = "session:test"
        session.request_trace_id = "task:test"
        session.root_2d_targets = [
            ardy_backend.Root2DTarget((5.0, 2.0), 1.25, 1.5, 0.0, True, heading=0.25, arrival_frame=60)
        ]
        session.constraints = [
            SimpleNamespace(
                name="root2d",
                frame_indices=torch.tensor([40, 50], dtype=torch.long),
                root_2d=torch.tensor([[1.0, 2.0], [3.0, 4.0]], dtype=torch.float32),
                global_root_heading=torch.tensor([0.0, 0.25], dtype=torch.float32),
            )
        ]
        output = {
            "root_positions": torch.tensor(
                [[[1.0, 0.0, 2.0], [3.0, 0.0, 4.0]]], dtype=torch.float32
            ),
            "global_root_heading": torch.tensor(
                [[[1.0, 0.0], [0.0, 1.0]]], dtype=torch.float32
            ),
        }

        with patch("builtins.print") as print_mock:
            session._log_horizon_trace(40, 40, 20, 20, 60, output)

        trace = json.loads(print_mock.call_args.args[0].removeprefix("[ARDY_HORIZON] "))
        self.assertEqual(trace["horizon"], [40, 80])
        self.assertEqual(trace["protected"], [40, 50])
        self.assertEqual(trace["protected_hits"], [{"type": "root2d", "frame": 40}])
        self.assertEqual(trace["constraints"][0]["gaps"], [10])
        self.assertEqual(trace["generated_root_xz"], [[1.0, 2.0], [3.0, 4.0]])
        self.assertEqual(trace["generated_heading_rad"], [0.0, 1.5708])

    def test_timed_root2d_frames_are_offset_with_the_constraint_patch(self):
        targets = ardy_backend._parse_root_2d_targets(
            {
                "type": "root2d",
                "frame_indices": [12],
                "smooth_root_2d": [[1.0, 2.0]],
            },
            ardy_backend.ArdySettings(160, 160, 20, True),
            frame_offset=8,
        )

        self.assertEqual(targets[0].arrival_frame, 20)

    def test_timed_root_target_uses_the_full_arrival_window(self):
        start = (-0.6220054, 1.545403)
        goal = (-0.5491493, 0.00175527157)
        target = ardy_backend.Root2DTarget(
            goal,
            1.25,
            1.5,
            0.001,
            True,
            arrival_frame=101,
        )

        planned = ardy_backend._plan_root_2d_target(target, start, (0.0, 0.0), -1, 20.0)

        self.assertEqual(planned["frame_indices"], [9, 19, 29, 39])
        self.assertLess(math.dist(start, planned["smooth_root_2d"][0]), 0.1)

        tail = ardy_backend._plan_root_2d_target(
            target,
            (-0.55, 0.05),
            (0.0, 0.0),
            91,
            20.0,
        )
        self.assertEqual(tail["frame_indices"], [101])
        np.testing.assert_allclose(tail["smooth_root_2d"][-1], goal, atol=1e-7)

    def test_timed_root_target_silently_relaxes_motion_limits(self):
        speed_limited = ardy_backend.Root2DTarget(
            (1.0, 0.0),
            0.1,
            1.5,
            0.001,
            True,
            arrival_frame=99,
        )
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            planned = ardy_backend._plan_root_2d_target(
                speed_limited, (0.0, 0.0), (0.0, 0.0), -1, 20.0
            )
        self.assertIsNotNone(planned)
        self.assertEqual(caught, [])

        acceleration_limited = ardy_backend.Root2DTarget(
            (1.0, 0.0),
            2.0,
            0.1,
            0.001,
            True,
            arrival_frame=99,
        )
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            planned = ardy_backend._plan_root_2d_target(
                acceleration_limited, (0.0, 0.0), (0.0, 0.0), -1, 20.0
            )
        self.assertIsNotNone(planned)
        self.assertEqual(caught, [])

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

    def test_root_target_keeps_motion_heading_until_the_last_forty_frames(self):
        target = ardy_backend.Root2DTarget((-10.0, 0.0), 1.25, 1.5, 0.1, True, heading=0.25)
        motion_target = ardy_backend.Root2DTarget((-10.0, 0.0), 1.25, 1.5, 0.1, True)

        planned = ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (1.0, 0.0), -1, 20.0)
        motion_planned = ardy_backend._plan_root_2d_target(
            motion_target, (0.0, 0.0), (1.0, 0.0), -1, 20.0
        )

        np.testing.assert_allclose(
            planned["global_root_heading"], motion_planned["global_root_heading"], atol=1e-7
        )

    def test_root_target_smoothly_reaches_final_heading_over_the_last_forty_frames(self):
        target = ardy_backend.Root2DTarget(
            (0.0, -2.0),
            10.0,
            10.0,
            0.001,
            True,
            heading=-math.pi + 0.2,
            arrival_frame=39,
        )

        planned = ardy_backend._plan_root_2d_target(
            target, (0.0, 0.0), (0.0, 0.0), -1, 20.0
        )

        headings = planned["global_root_heading"]
        self.assertEqual(planned["frame_indices"], [9, 19, 29, 39])
        self.assertLess(headings[0], -3.0)
        remaining_angles = [
            abs(math.atan2(math.sin(target.heading - heading), math.cos(target.heading - heading)))
            for heading in headings
        ]
        self.assertTrue(all(a > b for a, b in zip(remaining_angles, remaining_angles[1:])))
        self.assertAlmostEqual(headings[-1], target.heading, places=7)

    def test_untimed_root_target_uses_its_discrete_arrival_prediction_for_heading(self):
        target = ardy_backend.Root2DTarget(
            (1.0, 0.0), 1.25, 1.5, 0.001, True, heading=0.0
        )

        planned = ardy_backend._plan_root_2d_target(
            target, (0.0, 0.0), (0.0, 0.0), -1, 20.0
        )

        headings = planned["global_root_heading"]
        self.assertGreater(headings[0], headings[1])
        self.assertGreater(headings[1], headings[2])
        self.assertAlmostEqual(headings[2], target.heading, places=7)
        np.testing.assert_allclose(planned["smooth_root_2d"][2], target.position, atol=1e-7)

    def test_root_target_omits_heading_when_disabled_even_with_final_heading(self):
        target = ardy_backend.Root2DTarget(
            (1.0, 0.0), 1.25, 1.5, 0.1, False, heading=0.25
        )

        planned = ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (0.0, 0.0), -1, 20.0)

        self.assertNotIn("global_root_heading", planned)

    def test_root2d_parses_a_heading_vector_per_point(self):
        targets = ardy_backend._parse_root_2d_targets(
            {
                "type": "root2d",
                "frame_indices": [4],
                "smooth_root_2d": [[1.0, 2.0]],
                "global_root_heading": [[0.0, 1.0]],
            },
            ardy_backend.ArdySettings(160, 160, 20, True),
        )

        self.assertAlmostEqual(targets[0].heading, math.pi / 2)

    def test_root_target_stops_inside_arrival_threshold(self):
        target = ardy_backend.Root2DTarget((0.05, 0.0), 1.25, 1.5, 0.1, True)
        self.assertIsNone(
            ardy_backend._plan_root_2d_target(target, (0.0, 0.0), (0.0, 0.0), -1, 20.0)
        )

    def test_untimed_root_target_releases_near_arrival_before_an_extra_horizon(self):
        target = ardy_backend.Root2DTarget((0.102, 0.0), 1.25, 1.5, 0.1, False)

        self.assertIsNone(
            ardy_backend._plan_root_2d_target(
                target,
                (0.0, 0.0),
                (0.0, 0.0),
                -1,
                20.0,
                40,
            )
        )

    def test_root_target_omits_repeated_arrival_waypoints_when_heading_is_disabled(self):
        target = ardy_backend.Root2DTarget((1.0, 0.0), 1.25, 1.5, 0.1, False)

        planned = ardy_backend._plan_root_2d_target(
            target,
            (0.0, 0.0),
            (0.0, 0.0),
            -1,
            20.0,
            40,
        )

        self.assertEqual(planned["frame_indices"], [10, 19, 29])
        self.assertEqual(planned["smooth_root_2d"].count([1.0, 0.0]), 1)

    def test_root2d_protocol_keeps_multiple_ordered_points_and_replaces_the_snapshot(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 40, 3), dtype=np.float32)}
        session.outputs["root_positions"][0, 38, [0, 2]] = [0.95, 1.9]
        session.outputs["root_positions"][0, 39, [0, 2]] = [1.0, 2.0]
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        loaded = []

        def load_constraints(items, _skeleton):
            loaded.append(items)
            return items

        with patch("ardy.constraints.load_constraints_lst", side_effect=load_constraints):
            session._set_constraints(
                [{
                    "type": "root2d",
                    "frame_indices": [10, 29],
                    "smooth_root_2d": [[5.0, 0.0], [6.0, 2.0]],
                    "global_root_heading": [[0.0, 1.0], [1.0, 0.0]],
                }],
                (),
                model,
                apply_from=40,
                initial=False,
            )
            self.assertEqual([target.position for target in session.root_2d_targets], [(5.0, 0.0), (6.0, 2.0)])
            self.assertEqual([target.arrival_frame for target in session.root_2d_targets], [50, 69])
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d", "root2d"])
            self.assertEqual(loaded[-1][0]["frame_indices"][-1], 50)
            self.assertEqual(loaded[-1][1]["frame_indices"][-1], 69)

            session._set_constraints(
                [{"type": "root2d", "frame_indices": [12], "smooth_root_2d": [[-3.0, 2.0]]}],
                (),
                model,
                apply_from=40,
                initial=False,
            )
            self.assertEqual([target.position for target in session.root_2d_targets], [(-3.0, 2.0)])
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d"])

            session._set_constraints([], (), model, apply_from=40, initial=False)
            self.assertEqual(session.root_2d_targets, [])
            self.assertEqual(session.constraints, [])

    def test_root2d_protocol_uses_global_settings_and_does_not_normalize_coordinates(self):
        session = self._fake_ardy_session()
        session._normalize_constraint_origin = True
        session.settings = ardy_backend.ArdySettings(160, 160, 20, True, max_speed=2.25, max_acceleration=3.5)
        skeleton = SimpleNamespace(device=torch.device("cpu"))
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=skeleton))

        session._set_constraints(
            [
                {
                    "type": "root2d",
                    "frame_indices": [0, 16],
                    "smooth_root_2d": [[10.0, 20.0], [13.0, 24.0]],
                    "global_root_heading": [[0.0, 1.0], [1.0, 0.0]],
                },
            ],
            (),
            model,
            apply_from=0,
            initial=True,
        )

        self.assertIsNone(session.constraint_origin)
        self.assertEqual([target.position for target in session.root_2d_targets], [(10.0, 20.0), (13.0, 24.0)])
        self.assertEqual([target.max_speed for target in session.root_2d_targets], [2.25, 2.25])
        self.assertEqual([target.max_acceleration for target in session.root_2d_targets], [3.5, 3.5])

    def test_root_target_cursor_sync_preserves_cached_future(self):
        session = self._fake_ardy_session()
        session.root_2d_targets = [ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.0, True, arrival_frame=120)]
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
        session.root_2d_targets = [ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.0, True, arrival_frame=120)]
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 40, 3), dtype=np.float32)}
        replanned_from = []

        def refresh(self, _model, boundary_frame):
            replanned_from.append(boundary_frame)

        def generate_horizon(self, _model, _cancel_event=None):
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

    def test_timeline_prompt_boundary_aligns_to_generation_horizon(self):
        session = self._fake_ardy_session()
        session.profile.frames_per_token = 4
        session.profile.horizon_frames = 40
        session.timeline_segments = (
            ardy_backend.ArdyTimelineSegment("walk", 0, 101),
            ardy_backend.ArdyTimelineSegment("turn", 101, 192),
        )
        activated = []
        session._activate_prompt = lambda _model, prompt, **_kwargs: activated.append(prompt)

        self.assertEqual(
            session._activate_timeline_prompt(SimpleNamespace(), 80, threading.Event()),
            120,
        )
        self.assertEqual(activated[-1], "walk")
        self.assertEqual(
            session._activate_timeline_prompt(SimpleNamespace(), 120, threading.Event()),
            200,
        )
        self.assertEqual(activated[-1], "turn")

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

    def test_fixed_duration_restores_constraint_origin_before_building_kmb(self):
        class FakeSession:
            resolved_seed = 7
            effective_playback_reserve_frames = 0
            settings = SimpleNamespace(adaptive_playback_reserve=False)
            constraint_origin = (torch.tensor([-0.539, 0.0]), torch.tensor(0.0))

            def generate(self, _request, _attachments, _model, _cancel_event):
                return (
                    {"start_frame": 0, "end_frame_exclusive": 1},
                    {"root_positions": np.zeros((1, 1, 3), dtype=np.float32)},
                )

            def record_response_duration(self, _elapsed, _delivered_frames):
                pass

            def close(self):
                pass

        fixed = FakeSession()
        profile = SimpleNamespace(source_fps=20.0, motion_rep_fingerprint="test")
        with (
            patch.object(ardy_backend, "ArdySession", return_value=fixed),
            patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=b"kmb") as build_payload,
        ):
            _, _, payload = ardy_backend.execute_stream_generate(
                None,
                {"duration": 0.05, "prompt": "walk"},
                (),
                SimpleNamespace(),
                profile,
                threading.Event(),
                ".",
            )

        self.assertEqual(payload, b"kmb")
        restored = build_payload.call_args.args[1]
        np.testing.assert_allclose(restored["root_positions"][0, 0], [-0.539, 0.0, 0.0], atol=1e-6)

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

    def test_auto_history_uses_motion_limits_and_forces_four_frames_when_unreachable(self):
        profile = SimpleNamespace(
            source_fps=20.0,
            horizon_frames=40,
            frames_per_token=4,
            max_context_frames=200,
        )
        settings = ardy_backend.ArdySettings(
            160,
            160,
            20,
            False,
            auto_history=True,
            max_speed=1.25,
            max_acceleration=1.5,
            history_transition_weight=0.5,
        )

        unreachable = ardy_backend._auto_history_target(
            profile,
            settings,
            0,
            40,
            (0.0, 0.0),
            (0.0, 0.0),
            (10.0, 0.0),
        )
        far_in_time = ardy_backend._auto_history_target(
            profile,
            settings,
            0,
            220,
            (0.0, 0.0),
            (0.0, 0.0),
            (1.0, 0.0),
        )
        planning = ardy_backend._auto_history_target(
            profile,
            settings,
            0,
            70,
            (0.0, 0.0),
            (0.0, 0.0),
            (1.0, 0.0),
        )

        self.assertEqual(unreachable, (4, True))
        self.assertEqual(far_in_time, (160, False))
        self.assertEqual(planning, (128, False))
        self.assertEqual(ardy_backend._transition_history_frames(160, 64, 0.5, 4, 160), 112)

    def test_auto_history_reads_sparse_fullbody_without_adding_root2d(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(
            160,
            160,
            20,
            False,
            auto_history=True,
            max_speed=1.25,
            max_acceleration=1.5,
            history_transition_weight=0.5,
        )
        session._auto_history_frames = 160
        session.constraints = [
            SimpleNamespace(
                name="fullbody",
                frame_indices=torch.tensor([0, 40], dtype=torch.long),
                root_2d=torch.tensor([[0.0, 0.0], [10.0, 0.0]], dtype=torch.float32),
            )
        ]

        history_limit = session._resolve_history_limit(0)

        self.assertEqual(history_limit, 4)
        self.assertEqual(session._auto_history_frames, 4)
        self.assertEqual([constraint.name for constraint in session.constraints], ["fullbody"])
        self.assertEqual(session.root_2d_targets, [])

    def test_auto_history_keeps_full_history_for_timed_root2d_points(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(
            160,
            160,
            20,
            False,
            auto_history=True,
            max_speed=1.25,
            max_acceleration=1.5,
            history_transition_weight=0.5,
        )
        session._auto_history_frames = 160
        session.root_2d_targets = [
            ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.0, True, arrival_frame=20)
        ]
        session.constraints = [
            SimpleNamespace(
                name="root2d",
                frame_indices=torch.tensor([20], dtype=torch.long),
                root_2d=torch.tensor([[10.0, 0.0]], dtype=torch.float32),
            )
        ]

        history_limit = session._resolve_history_limit(0)

        self.assertEqual(history_limit, 160)
        self.assertEqual(session._auto_history_frames, 160)

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
        session.initial_history_root_2d = None
        session.initial_history_velocity_2d = None
        session.history_cpu = None
        session.constraints = []
        session.constraint_items = []
        session.constraint_origin = None
        session._normalize_constraint_origin = False
        session.root_2d_targets = []
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
