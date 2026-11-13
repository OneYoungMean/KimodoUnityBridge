import io
import json
import math
import os
from pathlib import Path
import tempfile
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
from core.protocol.kmb_motion import KmbMotion, encode_kmb1, parse_clip_mask, parse_kmb1 as parse_kmb1_payload, parse_kmb_clip
from core.protocol.timeline_segments import TimelineSegment, parse_timeline_segments
from kimodo.frame_time import seconds_to_frame_count, seconds_to_protocol_frame_index
from kimodo.model import kimodo_model


class QuickServerProtocolV2Tests(unittest.TestCase):
    def test_removed_loop_protocol_field_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "generate.loop protocol field has been removed"):
            quickserver_cli._execute_generate({"loop": False}, SimpleNamespace(), threading.Event())

    def test_constraint_duplicate_indices_keep_the_first_value(self):
        from kimodo.motion_rep.conditioning import get_unique_index_and_data

        indices, values = get_unique_index_and_data(
            torch.tensor([[0], [0], [1]], dtype=torch.long),
            torch.tensor([10.0, 20.0, 30.0]),
        )

        self.assertEqual(indices.tolist(), [[0], [1]])
        self.assertEqual(values.tolist(), [10.0, 30.0])

    def test_normal_clip_mask_preserves_position_axes(self):
        mask = parse_clip_mask(
            {
                "mask": {
                    "root_position": [True, False, False],
                    "root_heading": True,
                    "root_rotation": False,
                    "joints": [
                        {
                            "joint_name": "Spine",
                            "position": [False, True, False],
                            "rotation": True,
                        }
                    ],
                }
            },
            ["Hips", "Spine"],
        )
        root_axes, heading, joint_axes, rotation_joints = kimodo_runtime._clip_constraint_mask(
            mask,
            SimpleNamespace(root_idx=0),
            ["Hips", "Spine"],
        )

        self.assertEqual(root_axes, [True, False, False])
        self.assertTrue(heading)
        self.assertEqual(joint_axes, [[False, False, False], [False, True, False]])
        self.assertEqual(rotation_joints, [1])

    def test_clip_mask_projects_unavailable_soma_joints(self):
        mask = parse_clip_mask(
            {
                "mask": {
                    "joints": [
                        {"joint_name": "HeadEnd", "position": [True, True, True], "rotation": True},
                        {"joint_name": "LeftFoot", "position": [False, True, False], "rotation": True},
                    ],
                }
            },
            ["Hips", "HeadEnd", "LeftFoot"],
        )

        root_axes, heading, joint_axes, rotation_joints = kimodo_runtime._clip_constraint_mask(
            mask,
            SimpleNamespace(root_idx=0),
            ["Hips", "LeftFoot"],
        )

        self.assertEqual(root_axes, [False, False, False])
        self.assertFalse(heading)
        self.assertEqual(joint_axes, [[False, False, False], [False, True, False]])
        self.assertEqual(rotation_joints, [1])

    def test_legacy_flat_clip_mask_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "must be an object"):
            parse_clip_mask(
                {"mask": [True, True, True, True, True, True, True]},
                ["Hips", "Spine"],
            )

    def test_protocol_help_lists_model_configuration_command(self):
        help_payload = quickserver_cli._build_protocol_help()
        commands = help_payload["commands"]
        self.assertIn("runtime.list_models", [item["cmd"] for item in commands])
        self.assertIn("help", [item["cmd"] for item in commands])
        self.assertEqual(help_payload["server_version"], "unknown")

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
            {"model": "Kimodo-SOMA-RP-v1.2", "text_encoder_mode": "high_performance"},
            runtime_profile,
        )

        self.assertEqual(
            len(result["configs"]),
            2 * (len(quickserver_cli.assets.MAIN_MODELS) + len(quickserver_cli.assets.MOTION_MODEL_PROFILES)),
        )
        default = next(item for item in result["configs"] if item["default"])
        self.assertEqual(default["model"], "Kimodo-SOMA-RP-v1.2")
        self.assertEqual(default["text_encoder_model"], "high_performance")
        self.assertEqual(default["text_encoder_route"], "nf4")
        self.assertEqual(default["source_fps"], 30.0)
        self.assertEqual(default["max_diffusion_steps"], 1000)
        self.assertFalse(default["supports_streaming"])
        self.assertTrue(default["supports_timeline_segments"])

        ardy_result = quickserver_cli._build_model_configurations(
            "C:/runtime",
            {"model": "ardy-core", "text_encoder_mode": "high_precision"},
            runtime_profile,
        )
        self.assertEqual(ardy_result["default"]["model"], "ARDY-Core-RP-20FPS-Horizon40")
        ardy_default = next(item for item in ardy_result["configs"] if item["default"])
        self.assertEqual(ardy_default["source_fps"], 20.0)
        self.assertTrue(ardy_default["supports_streaming"])

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

    def test_stale_serverport_is_removed_before_starting_a_replacement(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            serverport = Path(temp_dir) / "serverport"
            serverport.write_text("host=127.0.0.1\nport=12345\npid=1234\n", encoding="utf-8")
            logger = SimpleNamespace(log=lambda _message: None)

            with patch.object(quickserver_cli, "_pid_is_running", return_value=False):
                self.assertFalse(quickserver_cli._try_reuse_existing_supervisor(serverport, logger))

            self.assertFalse(serverport.exists())

    def test_seconds_to_frame_count_uses_tolerance_protected_ceiling(self):
        self.assertEqual(seconds_to_frame_count(4.5666666, 30.0), 137)
        self.assertEqual(seconds_to_frame_count(5.0, 30.0), 150)
        self.assertEqual(seconds_to_frame_count(1.00001, 30.0), 31)
        self.assertEqual(seconds_to_frame_count(0.0, 30.0), 0)
        with self.assertRaises(ValueError):
            seconds_to_frame_count(float("nan"), 30.0)

    def test_protocol_frame_index_uses_signed_ceiling(self):
        self.assertEqual(seconds_to_protocol_frame_index(-0.051, 20.0), -1)
        self.assertEqual(seconds_to_protocol_frame_index(-0.01, 20.0), 0)
        self.assertEqual(seconds_to_protocol_frame_index(0.051, 20.0), 2)

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
                {"duration": 11.0, "prompt": "walk", "window_constraints": {"num_transition_frames": 7}},
                model,
                emit_progress=False,
            )

        self.assertEqual(output, {"generated": True})
        self.assertEqual(prompt, "walk.")
        self.assertEqual(model.args[:2], (["walk.", "walk."], [165, 165]))
        self.assertTrue(model.kwargs["multi_prompt"])
        self.assertEqual(model.kwargs["num_transition_frames"], 7)

    def test_kimodo_transition_frame_count_must_be_positive_integer(self):
        class RecordingModel:
            fps = 30.0

            def __call__(self, *args, **kwargs):
                raise AssertionError("invalid window settings must fail before model execution")

        for value in (0, -1, True, 1.5, "3"):
            with self.subTest(value=value), patch.object(kimodo_runtime, "_load_constraints", return_value=[]):
                with self.assertRaisesRegex(ValueError, "positive integer"):
                    kimodo_runtime._run_generate(
                        {"duration": 1.0, "window_constraints": {"num_transition_frames": value}},
                        RecordingModel(),
                        emit_progress=False,
                    )

    def test_stage_plan_crops_final_output_and_preserves_debug_stage_ranges(self):
        class RecordingModel:
            fps = 30.0

            def __init__(self):
                self.call_count = 0

            def __call__(self, prompts, segment_frames, **kwargs):
                self.call_count += 1
                frames = sum(segment_frames)
                return {
                    'posed_joints': np.zeros((1, frames, 2, 3), dtype=np.float32),
                    'smooth_root_pos': np.zeros((1, frames, 3), dtype=np.float32),
                    'global_root_heading': np.tile(
                        np.array([[[0.0, 1.0]]], dtype=np.float32), (1, frames, 1)
                    ),
                }

        model = RecordingModel()
        request = {
            'duration': 5.0 / 30.0,
            'prompt': 'walk',
            'stage_plan': {
                'trajectory': {'enabled': True},
                'output_start_frame': 1,
                'output_frame_count': 3,
            },
        }
        with patch.object(kimodo_runtime, '_load_constraints', return_value=[]), patch.object(kimodo_runtime, '_out'):
            output, _ = kimodo_runtime._run_generate(request, model, emit_progress=False)

        self.assertEqual(model.call_count, 2)
        final, metadata = kimodo_runtime._prepare_generation_output(request, output)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(final), 3)
        self.assertEqual(metadata, {})

        request['debug'] = True
        debug_final, metadata = kimodo_runtime._prepare_generation_output(request, output)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(debug_final), 3)
        self.assertEqual(len(metadata['debug_segments']), 2)
        self.assertEqual(metadata['debug_segments'][0]['debug_start_frame'], 0)
        self.assertEqual(metadata['debug_segments'][1]['debug_start_frame'], 5)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(metadata['_debug_motion_output']), 10)

    def test_stage_plan_does_not_normalize_an_empty_constraint_set(self):
        class RecordingModel:
            fps = 30.0

            def __call__(self, prompts, segment_frames, **kwargs):
                frame_count = sum(segment_frames)
                return {
                    'posed_joints': np.zeros((1, frame_count, 2, 3), dtype=np.float32),
                }

        request = {
            'duration': 1.0,
            'prompt': 'walk',
            'stage_plan': {
                'trajectory': {'enabled': True},
            },
        }
        with patch.object(kimodo_runtime, '_load_constraints', return_value=[]), patch('kimodo.constraints.normalize_constraints_to_anchor') as normalize, patch.object(kimodo_runtime, '_out'):
            kimodo_runtime._run_generate(request, RecordingModel(), emit_progress=False)

        normalize.assert_not_called()

    def test_stage_plan_target_frame_count_controls_every_stage(self):
        class RecordingModel:
            fps = 30.0

            def __init__(self):
                self.frame_counts = []

            def __call__(self, prompts, segment_frames, **kwargs):
                frame_count = sum(segment_frames)
                self.frame_counts.append(frame_count)
                return {
                    'posed_joints': np.zeros((1, frame_count, 2, 3), dtype=np.float32),
                    'smooth_root_pos': np.zeros((1, frame_count, 3), dtype=np.float32),
                }

        model = RecordingModel()
        request = {
            'duration': 5.0 / 30.0,
            'prompt': 'walk',
            'stage_plan': {
                'target_frame_count': 7,
                'trajectory': {'enabled': True},
            },
        }
        with patch.object(kimodo_runtime, '_load_constraints', return_value=[]), patch.object(kimodo_runtime, '_out'):
            output, _ = kimodo_runtime._run_generate(request, model, emit_progress=False)

        self.assertEqual(model.frame_counts, [7, 7])
        self.assertEqual(kimodo_runtime._motion_output_frame_count(output), 7)

    def test_stage_plan_override_distance_runs_without_stage1_output(self):
        class RecordingModel:
            fps = 30.0

            def __init__(self):
                self.call_count = 0

            def __call__(self, prompts, segment_frames, **kwargs):
                self.call_count += 1
                frames = sum(segment_frames)
                return {
                    "posed_joints": np.zeros((1, frames, 2, 3), dtype=np.float32),
                    "smooth_root_pos": np.zeros((1, frames, 3), dtype=np.float32),
                }

        model = RecordingModel()
        loaded_constraints = []

        def capture_constraints(value, *_args, **_kwargs):
            loaded_constraints.append(json.loads(value or "[]"))
            return []

        request = {
            "duration": 5.0 / 30.0,
            "stage_plan": {
                "target_frame_count": 5,
                "trajectory": {
                    "enabled": True,
                    "root_trajectory": True,
                    "override_distance": True,
                    "override_path_angle": True,
                    "distance": 4.0,
                    "path_begin_angle_degrees": 0.0,
                    "path_end_angle_degrees": 90.0,
                },
            },
        }
        with patch.object(kimodo_runtime, "_load_constraints", side_effect=capture_constraints), patch.object(kimodo_runtime, "_out"):
            output, _ = kimodo_runtime._run_generate(request, model, emit_progress=False)

        self.assertEqual(model.call_count, 2)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(output), 5)
        self.assertEqual(len(loaded_constraints), 2)
        stage2_root = next(item for item in loaded_constraints[0] if item.get("type") == "root2d")
        self.assertEqual(stage2_root["frame_indices"], list(range(5)))
        stage3_root = next(item for item in loaded_constraints[1] if item.get("type") == "root2d")
        self.assertEqual(stage3_root["frame_indices"], [0, 4])
        path = np.asarray(loaded_constraints[0][-1]["smooth_root_2d"], dtype=np.float32)
        np.testing.assert_allclose(path[-1], [-8 / math.pi, 8 / math.pi], atol=1e-6)

    def test_stage1_measures_walking_distance_and_stage3_uses_the_actual_stage2_path(self):
        def motion(distance):
            return {
                "posed_joints": np.zeros((1, 2, 2, 3), dtype=np.float32),
                "smooth_root_pos": np.asarray([[[0, 0, 0], [0, 0, distance]]], dtype=np.float32),
            }

        outputs = {
            "Stage 1": motion(2.0),
            "Stage 2": motion(4.0),
            "Stage 3": motion(4.0),
        }
        calls = []

        def invoke(name, constraints_json, _purpose, _frames):
            calls.append((name, json.loads(constraints_json)))
            return outputs[name]

        request = {
            "stage_plan": {
                "target_frame_count": 2,
                "trajectory": {"enabled": True, "override_path_angle": True},
            },
        }

        output, artifacts, distance = kimodo_runtime._execute_generation_plan(
            request, SimpleNamespace(fps=30.0), "[]", invoke
        )

        self.assertEqual([name for name, _ in calls], ["Stage 1", "Stage 2", "Stage 3"])
        self.assertAlmostEqual(distance, 2.0)
        stage2_root = next(item for item in calls[1][1] if item.get("type") == "root2d")
        self.assertEqual(stage2_root["smooth_root_2d"], [[0.0, 0.0], [0.0, 2.0]])
        final_root = next(item for item in calls[-1][1] if item.get("type") == "root2d")
        self.assertEqual(final_root["smooth_root_2d"], [[0.0, 0.0], [0.0, 4.0]])
        self.assertEqual(len(artifacts), 3)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(output), 2)

    def test_stage2_path_angle_uses_unity_yaw_in_model_root_coordinates(self):
        baseline = {
            "posed_joints": np.zeros((1, 3, 2, 3), dtype=np.float32),
            "smooth_root_pos": np.asarray([[[0, 0, 0], [0, 0, 1], [0, 0, 2]]], dtype=np.float32),
        }
        plan = {
            "trajectory": {
                "enabled": True,
                "override_path_angle": True,
                "path_begin_angle_degrees": 0.0,
                "path_end_angle_degrees": 90.0,
            }
        }

        constraint = kimodo_runtime._apply_stage2_trajectory(plan, baseline, 3, None)

        self.assertEqual(constraint["smooth_root_2d"][0], [0.0, 0.0])
        self.assertLess(constraint["smooth_root_2d"][-1][0], 0.0,
                        "Unity yaw +90 faces +X, represented as negative model-space X.")
        np.testing.assert_allclose(constraint["smooth_root_2d"][-1], [-4 / math.pi, 4 / math.pi], atol=1e-6)
        self.assertNotIn("global_root_heading", constraint)

    def test_stage2_bezier_uses_visible_duration_and_shortest_angle_across_context(self):
        plan = {"output_start_frame": 2, "output_frame_count": 5, "trajectory": {
            "override_path_angle": True, "override_distance": True, "distance": 4,
            "path_begin_angle_degrees": 350, "path_end_angle_degrees": 10,
        }}
        actual = kimodo_runtime._apply_stage2_trajectory(plan, None, 9, None)
        points = np.asarray(actual["smooth_root_2d"])
        np.testing.assert_allclose(points[2], [0, 0], atol=1e-6)
        radius = 4 / math.radians(20)
        np.testing.assert_allclose(points[6], [0, 2 * radius * math.sin(math.radians(10))], atol=1e-6)
        self.assertLess(points[0, 1], points[2, 1])
        self.assertGreater(points[8, 1], points[6, 1])

    def test_distance_override_preserves_authored_path_shape_without_a_baseline(self):
        plan = {"output_start_frame": 1, "output_frame_count": 3,
                "trajectory": {"override_distance": True, "distance": 4}}
        authored = {"type": "root2d", "frame_indices": [0, 1, 2, 3, 4],
                    "smooth_root_2d": [[-1, 0], [0, 0], [1, 0], [1, 1], [2, 1]]}
        actual = kimodo_runtime._apply_stage2_trajectory(plan, None, 5, None, json.dumps([authored]))
        np.testing.assert_allclose(actual["smooth_root_2d"], [[-2, 0], [0, 0], [2, 0], [2, 2], [4, 2]])

    def test_stage_plan_root_replacement_keeps_pose_and_clip_constraints(self):
        original = [
            {"type": "fullbody", "frame": 3, "pose": {"track": "Main", "index": 0}},
            {"type": "clip", "purpose": "inside", "attachment": 1},
            {"type": "root2d", "frame_indices": [0], "smooth_root_2d": [[1.0, 2.0]]},
        ]
        replacement = {"type": "root2d", "frame_indices": [0, 5], "smooth_root_2d": [[0, 0], [0, 3]]}

        actual = json.loads(kimodo_runtime._replace_root2d_constraints(json.dumps(original), replacement, {}, 30.0))

        self.assertEqual(actual[:2], original[:2])
        self.assertEqual(actual[2], replacement)

    def test_automatic_straight_path_does_not_lock_the_acceleration_profile(self):
        for fps in (30, 60):
            frames = np.arange(3 * fps + 1)
            positions = np.stack((np.zeros(len(frames)), 2 * (frames / frames[-1]) ** 2), axis=1)
            replacement = {"type": "root2d", "frame_indices": frames.tolist(),
                           "smooth_root_2d": positions.tolist(),
                           "global_root_heading": [[1.0, 0.0] for _ in frames]}
            before = json.dumps(replacement)
            with self.subTest(fps=fps):
                actual = json.loads(kimodo_runtime._replace_root2d_constraints(
                    "[]", replacement, {"output_start_frame": fps, "output_frame_count": fps + 1}, fps))[0]
                self.assertEqual(actual["frame_indices"], [fps, 2 * fps])
                np.testing.assert_allclose(actual["smooth_root_2d"], positions[[fps, 2 * fps]])
                self.assertEqual(actual["global_root_heading"], [[1.0, 0.0], [1.0, 0.0]])
                self.assertEqual(json.dumps(replacement), before)

    def test_sparse_automatic_path_preserves_curvature_and_authored_waypoint_frames(self):
        angles = np.linspace(0, math.pi / 2, 121)
        positions = 2 * np.stack((np.cos(angles), np.sin(angles)), axis=1)
        replacement = {"type": "root2d", "frame_indices": list(range(len(angles))),
                       "smooth_root_2d": positions.tolist()}
        authored = {"type": "root2d", "frame_indices": [37], "smooth_root_2d": [positions[37].tolist()]}
        actual = json.loads(kimodo_runtime._replace_root2d_constraints(
            json.dumps([authored]), replacement, {}, 30))[0]
        indices = actual["frame_indices"]
        self.assertEqual(indices[0], 0)
        self.assertEqual(indices[-1], 120)
        self.assertIn(37, indices)
        self.assertLess(len(indices), 20)
        for start, end in zip(indices, indices[1:]):
            direction = positions[end] - positions[start]
            offsets = positions[start:end + 1] - positions[start]
            progress = np.clip(offsets @ direction / (direction @ direction), 0, 1)
            distances = np.linalg.norm(offsets - progress[:, None] * direction, axis=1)
            self.assertLessEqual(float(distances.max()), 0.05 + 1e-8)

    def test_sparse_automatic_path_keeps_in_place_turns_and_single_frame_ranges(self):
        angles = np.linspace(0, 2 * math.pi, 121)
        replacement = {"type": "root2d", "frame_indices": list(range(len(angles))),
                       "smooth_root_2d": [[0.0, 0.0] for _ in angles],
                       "global_root_heading": np.stack((np.cos(angles), -np.sin(angles)), axis=1).tolist()}
        actual = json.loads(kimodo_runtime._replace_root2d_constraints("[]", replacement, {}, 30))[0]
        self.assertEqual(actual["frame_indices"][0], 0)
        self.assertEqual(actual["frame_indices"][-1], 120)
        self.assertGreaterEqual(len(actual["frame_indices"]), 5)
        self.assertLess(len(actual["frame_indices"]), 20)
        np.testing.assert_allclose(actual["global_root_heading"],
                                   np.asarray(replacement["global_root_heading"])[actual["frame_indices"]])
        single = json.loads(kimodo_runtime._replace_root2d_constraints(
            "[]", replacement, {"output_start_frame": 60, "output_frame_count": 1}, 30))[0]
        self.assertEqual(single["frame_indices"], [60])

    def test_outside_windows_exclude_automatic_trajectory_in_each_stage(self):
        for fps in (30.0, 60.0):
            for post_in, post_out in ((False, False), (True, True), (True, False), (False, True)):
                with self.subTest(fps=fps, post_in=post_in, post_out=post_out):
                    reference = self._small_kmb(3)
                    reference = KmbMotion(**{**reference.__dict__, "fps": fps})
                    reference.root_positions[:] = [0, 1, 0]
                    stage2 = self._small_kmb(12)
                    stage2 = KmbMotion(**{**stage2.__dict__, "fps": fps})
                    attachments = [encode_kmb1(reference), encode_kmb1(reference)]
                    records = [
                        {"type": "clip", "format": "kmb_attachment_v1", "attachment": index,
                         "start_time": targets[0] / fps, "duration": 3 / fps,
                         "purpose": purpose, "post_apply": post, "sample_count": 2,
                         "target_frame_indices": targets}
                        for index, (purpose, post, targets) in enumerate((
                            ("outside_in", post_in, [-1, 0, 4]),
                            ("outside_out", post_out, [8, 9, 10]),
                        ))
                    ]
                    records[1].pop("target_frame_indices")
                    request = {"debug": True, "stage_plan": {
                        "target_frame_count": 12, "output_start_frame": 3, "output_frame_count": 6,
                        "trajectory": {"enabled": True, "override_distance": True,
                                       "override_path_angle": True, "distance": 2.0,
                                       "override_heading": True, "heading_degrees": 0.0}}}
                    output = {"posed_joints": np.zeros((1, 12, 2, 3), dtype=np.float32),
                              "root_positions": stage2.root_positions[None],
                              "global_root_heading": np.tile([[[1.0, 0.0]]], (1, 12, 1))}
                    calls = []

                    def invoke(name, constraints, purpose, frame_count):
                        calls.append((name, json.loads(constraints)))
                        self.assertEqual(frame_count, 12)
                        return dict(output)

                    with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(stage2)):
                        result, artifacts, _ = kimodo_runtime._execute_generation_plan(
                            request, SimpleNamespace(fps=fps), json.dumps(records), invoke,
                            stage_attachments=attachments)
                    self.assertEqual([name for name, _ in calls], ["Stage 2", "Stage 3"])
                    for name, constraints in calls:
                        root = next(item for item in constraints if item.get("type") == "root2d")
                        expected = [5, 7] if name == "Stage 3" else [3 if post_in else 5, 8 if post_out else 7]
                        self.assertEqual(root["frame_indices"], expected)
                        self.assertEqual(len(root["smooth_root_2d"]), len(expected))
                        self.assertEqual(len(root["global_root_heading"]), len(expected))
                        if name == "Stage 3":
                            np.testing.assert_allclose(root["smooth_root_2d"], stage2.root_positions[expected][:, [0, 2]])
                    for purpose, post, anchor, target in (("outside_in", post_in, -1, 4), ("outside_out", post_out, 0, 8)):
                        clip = next(item for item in calls[-1][1] if item.get("purpose") == purpose)
                        self.assertEqual(clip["source_frame_indices"], [0, 2])
                        roots = parse_kmb_clip(clip, tuple(attachments), fps).motion.root_positions
                        np.testing.assert_allclose(roots[anchor], [target if post else 0, 1, 0])
                    debug = json.loads(artifacts[-1].debug_constraints_json)
                    self.assertEqual(next(item for item in debug if item["type"] == "root2d")["frame_indices"], [5, 7])
                    final, metadata = kimodo_runtime._prepare_generation_output(request, result)
                    self.assertEqual(kimodo_runtime._motion_output_frame_count(final), 6)
                    self.assertEqual(kimodo_runtime._motion_output_frame_count(metadata["_debug_motion_output"]), 24)

    def test_post_apply_fullbody_replaces_xz_and_yaw_but_keeps_height_and_body(self):
        from scipy.spatial.transform import Rotation
        trajectory = KmbMotion(**{**self._small_kmb(2).__dict__,
            "root_positions": np.asarray([[10.0, 1.0, 20.0], [11.0, 2.0, 21.0]], dtype=np.float32)})
        trajectory.local_rot_quats[:, 0] = [math.sqrt(0.5), 0, math.sqrt(0.5), 0]
        constraints = [{
            "type": "fullbody",
            "frame_indices": [0, 1],
            "root_positions": [[1.0, 0.3, 2.0], [3.0, 0.4, 4.0]],
            "smooth_root_2d": [[1.0, 2.0], [3.0, 4.0]],
            "local_joints_rot": [[[0.0, 0.0, 0.0], [0.2, 0, 0]], [[0.1, 0.0, 0.0], [0.3, 0, 0]]],
        }]

        actual = json.loads(kimodo_runtime._replace_fullbody_root_tracks(json.dumps(constraints), trajectory))

        self.assertEqual(actual[0]["root_positions"], [[10.0, 0.3, 20.0], [11.0, 0.4, 21.0]])
        self.assertEqual(actual[0]["smooth_root_2d"], [[10.0, 20.0], [11.0, 21.0]])
        for index in range(2):
            self.assertEqual(actual[0]["local_joints_rot"][index][1], constraints[0]["local_joints_rot"][index][1])
            expected = Rotation.from_euler("y", math.pi / 2) * Rotation.from_rotvec(constraints[0]["local_joints_rot"][index][0])
            np.testing.assert_allclose(Rotation.from_rotvec(actual[0]["local_joints_rot"][index][0]).as_matrix(), expected.as_matrix(), atol=1e-6)

    def test_inside_post_apply_rewrites_root_samples_without_changing_body_pose(self):
        names = ("root", "hip")
        parents = (-1, 0)
        source = KmbMotion(
            payload=b"",
            model_name="test",
            fps=30.0,
            joint_names=names,
            joint_parents=parents,
            root_positions=np.asarray([[0, 0, 0], [1, 0, 1]], dtype=np.float32),
            local_rot_quats=np.asarray([
                [[1, 0, 0, 0], [1, 0, 0, 0]],
                [[1, 0, 0, 0], [1, 0, 0, 0]],
            ], dtype=np.float32),
            foot_contacts=None,
        )
        trajectory = KmbMotion(
            payload=b"",
            model_name="test",
            fps=30.0,
            joint_names=names,
            joint_parents=parents,
            root_positions=np.asarray([[10, 1, 20], [11, 2, 21], [12, 3, 22]], dtype=np.float32),
            local_rot_quats=np.asarray([
                [[1, 0, 0, 0], [1, 0, 0, 0]],
                [[math.sqrt(0.5), 0, math.sqrt(0.5), 0], [1, 0, 0, 0]],
                [[math.sqrt(0.5), 0, -math.sqrt(0.5), 0], [1, 0, 0, 0]],
            ], dtype=np.float32),
            foot_contacts=None,
        )
        attachments = [encode_kmb1(source)]
        item = {
            "type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
            "start_time": 1 / 30, "duration": 2 / 30, "purpose": "inside",
            "post_apply": True,
            "target_frame_indices": [1, 2],
            "mask": {"root_position": [True, True, True], "root_heading": True,
                     "root_rotation": True, "joints": []},
        }

        rewritten = kimodo_runtime._replace_inside_clip_root_tracks(
            json.dumps([item]), attachments, trajectory, 30.0
        )
        updated_item = json.loads(rewritten)[0]
        updated = parse_kmb_clip(updated_item, tuple(attachments), 30.0).motion

        self.assertEqual(updated_item["attachment"], 1)
        np.testing.assert_allclose(updated.root_positions[:, [0, 2]], trajectory.root_positions[1:3, [0, 2]])
        np.testing.assert_allclose(updated.root_positions[:, 1], source.root_positions[:, 1])
        np.testing.assert_allclose(updated.local_rot_quats[:, 1], source.local_rot_quats[:, 1])
        np.testing.assert_allclose(updated.local_rot_quats[:, 0], trajectory.local_rot_quats[1:3, 0], atol=1e-6)

    def test_outside_post_apply_offsets_each_window_to_the_matching_stage2_anchor(self):
        names = ("root", "hip")
        parents = (-1, 0)
        source = KmbMotion(
            payload=b"",
            model_name="test",
            fps=30.0,
            joint_names=names,
            joint_parents=parents,
            root_positions=np.asarray([[0, 0, 0], [1, 0, 1]], dtype=np.float32),
            local_rot_quats=np.tile(np.asarray([1, 0, 0, 0], dtype=np.float32), (2, 2, 1)),
            foot_contacts=None,
        )
        attachments = [encode_kmb1(source), encode_kmb1(source), encode_kmb1(source)]
        trajectory = KmbMotion(
            payload=b"",
            model_name="test",
            fps=30.0,
            joint_names=names,
            joint_parents=parents,
            root_positions=np.asarray([[10, 1, 20], [11, 2, 21], [12, 3, 22]], dtype=np.float32),
            local_rot_quats=np.tile(np.asarray([1, 0, 0, 0], dtype=np.float32), (3, 2, 1)),
            foot_contacts=None,
        )
        constraints = [
            {
                "type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                "start_time": 0.0, "duration": 2 / 30, "purpose": "outside_in",
                "post_apply": True, "target_frame_indices": [0, 1],
                "mask": {"root_position": [True, True, True], "root_heading": True,
                         "root_rotation": True, "joints": []},
            },
            {
                "type": "clip", "format": "kmb_attachment_v1", "attachment": 1,
                "start_time": 1 / 30, "duration": 2 / 30, "purpose": "outside_out",
                "post_apply": True, "target_frame_indices": [1, 2],
                "mask": {"root_position": [True, True, True], "root_heading": True,
                         "root_rotation": True, "joints": []},
            },
            {
                "type": "clip", "format": "kmb_attachment_v1", "attachment": 2,
                "start_time": 0.0, "duration": 2 / 30, "purpose": "outside_in",
                "post_apply": False, "target_frame_indices": [0, 1],
                "mask": {"root_position": [True, True, True], "root_heading": True,
                         "root_rotation": True, "joints": []},
            },
        ]

        rewritten = kimodo_runtime._apply_outside_clip_anchor_offsets(
            json.dumps(constraints), attachments, trajectory, 30.0
        )
        updated = json.loads(rewritten)

        in_motion = parse_kmb_clip(updated[0], tuple(attachments), 30.0).motion
        out_motion = parse_kmb_clip(updated[1], tuple(attachments), 30.0).motion
        np.testing.assert_allclose(in_motion.root_positions, [[10, 0, 20], [11, 0, 21]])
        np.testing.assert_allclose(out_motion.root_positions, [[11, 0, 21], [12, 0, 22]])
        self.assertEqual(updated[2], constraints[2])
        np.testing.assert_allclose(in_motion.local_rot_quats, source.local_rot_quats)

    def test_transition_sample_count_selects_sparse_seam_frames(self):
        class RecordingModel:
            fps = 30.0

            def __init__(self):
                self.transition_settings = []

            def __call__(self, prompts, segment_frames, **kwargs):
                self.transition_settings.append((kwargs["num_transition_frames"], kwargs["num_transition_samples"]))
                frame_count = sum(segment_frames)
                return {"posed_joints": np.zeros((1, frame_count, 2, 3), dtype=np.float32)}

        model = RecordingModel()
        request = {
            "duration": 2.0,
            "timeline_segments": [
                {"prompt": "walk", "duration": 1.0},
                {"prompt": "turn", "duration": 1.0},
            ],
            "window_constraints": {"window_frames": 8, "sample_count": 3},
            "stage_plan": {
                "target_frame_count": 60,
                "trajectory": {"enabled": True},
            },
        }
        with patch.object(kimodo_runtime, "_load_constraints", return_value=[]), patch.object(kimodo_runtime, "_out"):
            kimodo_runtime._run_generate(request, model, emit_progress=False)

        self.assertEqual(model.transition_settings, [(8, 3), (8, 3)])

    def test_loop_clip_uses_stage2_body_window_and_configured_axis_locks(self):
        names = ("root", "hip")
        parents = (-1, 0)
        motion = KmbMotion(
            payload=b"",
            model_name="test",
            fps=30.0,
            joint_names=names,
            joint_parents=parents,
            root_positions=np.asarray([[0, 0, 0], [1, 0, 0], [2, 0, 0], [3, 0, 0], [4, 0, 0], [5, 0, 0]], dtype=np.float32),
            local_rot_quats=np.tile(np.asarray([1, 0, 0, 0], dtype=np.float32), (6, 2, 1)),
            foot_contacts=None,
        )
        attachments = []
        model = SimpleNamespace(fps=30.0)
        plan = {
            "target_frame_count": 6,
            "loop": {
                "enabled": True, "window_frames": 4, "sample_count": 2,
                "lock_position_x": True, "lock_position_y": False, "lock_position_z": True,
                "lock_rotation_x": False, "lock_rotation_y": True, "lock_rotation_z": False,
            },
        }
        with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(motion)):
            items, padding = kimodo_runtime._loop_clip_constraints({"posed_joints": np.zeros((1, 6, 2, 3))}, model, plan, attachments)

        self.assertEqual(padding, 2)
        self.assertEqual(items[0]["target_frame_indices"], [0, 1, 2])
        self.assertEqual(items[0]["source_frame_indices"], [0, 2])
        item = items[1]
        parsed = parse_kmb_clip(item, tuple(attachments), 30.0)
        self.assertEqual(parsed.target_frame_indices, (5, 6, 7))
        np.testing.assert_allclose(parsed.motion.root_positions[:, 0], [0, 1, 2])
        self.assertEqual(item["mask"]["root_position"], [True, True, True])
        self.assertTrue(item["mask"]["root_heading"])
        self.assertEqual(item["mask"]["joints"][0]["position"], [True, True, True])
        self.assertTrue(item["mask"]["joints"][0]["rotation"])

    def test_kimodo_timeline_segments_preserve_prompt_boundaries(self):
        class RecordingModel:
            fps = 30.0

            def __call__(self, *args, **kwargs):
                self.args = args
                return {"generated": True}

        model = RecordingModel()
        request = {
            "duration": 12.0,
            "prompt": "unused",
            "timeline_segments": [
                {"prompt": "walk", "duration": 2.0},
                {"prompt": "turn", "duration": 10.0},
            ],
        }
        with patch.object(kimodo_runtime, "_load_constraints", return_value=[]):
            kimodo_runtime._run_generate(request, model, emit_progress=False)

        self.assertEqual(model.args[0], ["walk.", "turn."])
        self.assertEqual(model.args[1], [60, 300])

    def test_backend_stage_selection_uses_settings_and_real_constraints(self):
        for path, distance, expected in ((False, False, False), (True, False, True), (True, True, False)):
            plan = kimodo_runtime._normalize_stage_plan({"stage_plan": {"trajectory": {
                "enabled": True, "override_path_angle": path, "override_distance": distance,
            }}})
            self.assertEqual(plan["stage1"]["enabled"], expected)
        request = {"stage_plan": {"loop": {"enabled": True, "window_frames": 4}},
                   "constraints_json": json.dumps([{"type": "clip", "purpose": "outside_out", "post_apply": True}])}
        plan = kimodo_runtime._normalize_stage_plan(request)
        self.assertFalse(plan["loop"]["enabled"])
        self.assertTrue(plan["post_apply"]["outside"])
        self.assertTrue(plan["stage2"]["enabled"])
        request["constraints_json"] = json.dumps([{"type": "clip", "purpose": "inside", "post_apply": True}])
        self.assertTrue(kimodo_runtime._normalize_stage_plan(request)["loop"]["enabled"])
        request["stage_plan"]["trajectory"] = {"enabled": True}
        request["constraints_json"] = json.dumps([{"type": "clip", "purpose": "inside"}])
        with self.assertRaisesRegex(ValueError, "requires post_apply=true"):
            kimodo_runtime._normalize_stage_plan(request)

    def test_inside_reference_keyframe_modes_and_foot_fallback(self):
        motion = self._small_kmb(8)
        contacts = np.zeros((8, 4), dtype=np.float32)
        contacts[:2, :2] = 1
        contacts[6:, 2:] = 1
        motion = KmbMotion(**{**motion.__dict__, "foot_contacts": contacts})
        item = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                "start_time": 0, "duration": 8 / 30, "purpose": "inside", "sample_count": 2}
        for mode, expected in (("uniform", [0, 7]), ("foot_exchange", [4]), ("mixed", [0, 4, 7])):
            with self.subTest(mode=mode):
                attachments = [encode_kmb1(motion)]
                result = json.loads(kimodo_runtime._sample_reference_keyframes(
                    json.dumps([{**item, "sampling_mode": mode}]), attachments, 30.0,
                    {"target_frame_count": 8}, []))
                self.assertEqual(result[0]["source_frame_indices"], expected)
                self.assertEqual(parse_kmb_clip(result[0], tuple(attachments), 30.0).motion.num_frames, 8)
                self.assertEqual(len(attachments), 1)
        attachments = [encode_kmb1(self._small_kmb(8))]
        warnings = []
        with patch.object(kimodo_runtime, "_out"):
            result = json.loads(kimodo_runtime._sample_reference_keyframes(
                json.dumps([{**item, "sampling_mode": "foot_exchange"}]), attachments, 30.0,
                {"output_frame_count": 5}, warnings))
        self.assertEqual(result[0]["source_frame_indices"], [0, 4])
        self.assertEqual(len(warnings), 1)

    def test_outside_post_apply_alone_is_deferred_until_stage3(self):
        reference = self._small_kmb(2)
        attachments = [encode_kmb1(reference)]
        item = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                "start_time": 0, "duration": 2 / 30, "purpose": "outside_out", "post_apply": True}
        stage2 = KmbMotion(**{**reference.__dict__, "root_positions": reference.root_positions + [10, 1, 20]})
        output = {"posed_joints": np.zeros((1, 2, 2, 3), dtype=np.float32),
                  "root_positions": stage2.root_positions[None]}
        calls = []
        def invoke(name, constraints, purpose, frame_count):
            calls.append((name, json.loads(constraints)))
            return output
        request = {"stage_plan": {"target_frame_count": 2}}
        with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(stage2)):
            kimodo_runtime._execute_generation_plan(request, SimpleNamespace(fps=30.0), json.dumps([item]), invoke,
                                                    stage_attachments=attachments)
        self.assertEqual(calls[0], ("Stage 2", []))
        self.assertEqual([item["type"] for item in calls[1][1]], ["clip"])
        final = next(item for item in calls[1][1] if item.get("purpose") == "outside_out")
        actual = parse_kmb_clip(final, tuple(attachments), 30.0).motion.root_positions
        np.testing.assert_allclose(actual[:, [0, 2]], stage2.root_positions[:, [0, 2]])
        np.testing.assert_allclose(actual[:, 1], reference.root_positions[:, 1])

    def test_loop_preserves_opening_and_crops_tail_with_or_without_debug(self):
        for debug in (False, True):
            for start, suffix in ((0, 0), (2, 0), (2, 2)):
                with self.subTest(debug=debug, output_start=start, suffix=suffix):
                    base_count = start + 6 + suffix
                    reference = self._small_kmb(base_count)
                    attachments = [encode_kmb1(reference)]
                    item = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                            "start_time": 0, "duration": base_count / 30, "purpose": "inside", "post_apply": True}
                    calls = []
                    def invoke(name, constraints, purpose, frame_count):
                        calls.append((name, frame_count, json.loads(constraints)))
                        return {"posed_joints": np.zeros((1, frame_count, 2, 3), dtype=np.float32),
                                "root_positions": np.arange(frame_count * 3, dtype=np.float32).reshape(1, frame_count, 3)}
                    request = {"debug": debug, "stage_plan": {"target_frame_count": base_count,
                               "output_start_frame": start, "output_frame_count": 6,
                               "loop": {"enabled": True, "window_frames": 3, "sample_count": 2}}}
                    with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(reference)):
                        output, artifacts, _ = kimodo_runtime._execute_generation_plan(
                            request, SimpleNamespace(fps=30.0), json.dumps([item]), invoke, stage_attachments=attachments)
                    self.assertEqual([(name, frames) for name, frames, _ in calls],
                                     [("Stage 2", base_count), ("Stage 3", base_count + 2)])
                    self.assertEqual([item.get("purpose") for item in calls[1][2] if item.get("type") == "clip"],
                                     ["loop_begin", "loop_end", "inside"])
                    final, metadata = kimodo_runtime._prepare_generation_output(request, output)
                    expected = artifacts[-1].output["root_positions"][:, start:start + 6]
                    np.testing.assert_array_equal(final["root_positions"], expected)
                    self.assertEqual(kimodo_runtime._motion_output_frame_count(final), 6)
                    self.assertEqual(artifacts[-1].frame_count, base_count + 2)
                    if debug:
                        self.assertEqual(kimodo_runtime._motion_output_frame_count(metadata["_debug_motion_output"]), base_count * 2 + 2)
                        self.assertEqual(metadata["debug_segments"][1]["debug_end_frame_exclusive"], base_count * 2 + 2)
                    else:
                        self.assertEqual(metadata, {})
                    self.assertEqual(request["stage_plan"]["output_start_frame"], start)
                    self.assertEqual(request["stage_plan"]["target_frame_count"], base_count)

    def test_debug_reference_constraints_include_poses_and_their_exact_target_frames(self):
        motion = self._small_kmb(2)
        item = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                "start_time": 1 / 30, "duration": 2 / 30, "purpose": "inside",
                "target_frame_indices": [1, 8]}
        records = json.loads(kimodo_runtime._debug_constraint_records(json.dumps([item]), [encode_kmb1(motion)], 30.0))
        self.assertEqual(records[0]["frame_indices"], [1, 8])
        self.assertEqual(records[0]["type"], "fullbody")
        self.assertEqual(records[0]["reference"], item)
        np.testing.assert_allclose(records[0]["root_positions"], motion.root_positions)
        self.assertEqual(np.asarray(records[0]["local_joints_rot"]).shape, (2, 2, 3))

    def test_final_output_is_cropped_from_the_complete_result_instead_of_last_debug_window(self):
        final = {"posed_joints": np.zeros((1, 8, 2, 3), dtype=np.float32)}
        last_window = {"posed_joints": np.zeros((1, 3, 2, 3), dtype=np.float32)}
        request = {"debug": True, "stage_plan": {"target_frame_count": 8}}
        final["_generation_artifacts"] = [kimodo_runtime.GenerationArtifact(
            "last window", 5, 8, last_window, "[]", debug_constraints_json="[]")]
        output, metadata = kimodo_runtime._prepare_generation_output(request, final)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(output), 8)
        self.assertEqual(kimodo_runtime._motion_output_frame_count(metadata["_debug_motion_output"]), 3)

    def test_loop_window_limit_uses_sample_density_and_reports_clamping(self):
        model = SimpleNamespace(fps=30)
        for samples, expected in ((1, 1), (2, 4), (4, 6)):
            plan = {"target_frame_count": 8, "loop": {"enabled": True, "window_frames": 8, "sample_count": samples}}
            warnings = []
            with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(self._small_kmb(8))), patch.object(kimodo_runtime, "_out"):
                items, padding = kimodo_runtime._loop_clip_constraints({}, model, plan, [], warnings)
            self.assertEqual(padding, expected - 1)
            self.assertEqual(len(warnings), 1)
            self.assertEqual(len(items[0]["source_frame_indices"]), min(samples, expected))

    def test_heading_only_override_reaches_inside_fullbody_and_loop_references(self):
        from scipy.spatial.transform import Rotation

        reference = self._small_kmb(6)
        attachments = [encode_kmb1(reference)]
        clip = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0,
                "start_time": 0, "duration": 6 / 30, "purpose": "inside", "post_apply": True}
        point = {"type": "fullbody", "frame_indices": [2], "root_positions": [[0, 0.3, 0]],
                 "local_joints_rot": [[[0.2, 0, 0], [0.1, 0, 0]]]}
        output = {"posed_joints": np.zeros((1, 6, 2, 3), dtype=np.float32),
                  "root_positions": reference.root_positions[None]}
        calls = []

        def invoke(name, constraints, purpose, frame_count):
            calls.append((name, json.loads(constraints)))
            return output

        request = {"stage_plan": {"target_frame_count": 6, "trajectory": {
            "enabled": True, "override_heading": True, "heading_degrees": 90},
            "loop": {"enabled": True, "window_frames": 3, "sample_count": 2}}}
        with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(reference)):
            kimodo_runtime._execute_generation_plan(request, SimpleNamespace(fps=30), json.dumps([clip, point]),
                                                    invoke, stage_attachments=attachments)
        self.assertEqual([name for name, _ in calls], ["Stage 2", "Stage 3"])
        final = calls[-1][1]
        yaw = Rotation.from_euler("y", -math.pi / 2)
        for purpose in ("inside", "loop_begin", "loop_end"):
            item = next(item for item in final if item.get("purpose") == purpose)
            motion = parse_kmb_clip(item, tuple(attachments), 30).motion
            actual = Rotation.from_quat(motion.local_rot_quats[0, 0, [1, 2, 3, 0]])
            np.testing.assert_allclose(actual.as_matrix(), yaw.as_matrix(), atol=1e-6)
        body = next(item for item in final if item.get("type") == "fullbody")
        actual = Rotation.from_rotvec(body["local_joints_rot"][0][0])
        np.testing.assert_allclose(actual.as_matrix(), (yaw * Rotation.from_euler("x", 0.2)).as_matrix(), atol=1e-6)
        self.assertEqual(body["root_positions"][0][1], 0.3)
        self.assertEqual(body["local_joints_rot"][0][1], [0.1, 0, 0])
        np.testing.assert_allclose(reference.local_rot_quats[:, 0], np.tile([1, 0, 0, 0], (6, 1)))

    def test_loop_opening_window_and_alignment_use_frame_zero_with_or_without_foot_events(self):
        from scipy.spatial.transform import Rotation

        for context in (0, 2):
            motion = self._small_kmb(context + 8)
            rotations = motion.local_rot_quats.copy()
            rotations[:, 0] = Rotation.from_euler("y", np.arange(motion.num_frames) * 0.1).as_quat()[:, [3, 0, 1, 2]]
            contacts = np.zeros((motion.num_frames, 4), dtype=np.float32)
            contacts[:context + 2, :2] = 1
            contacts[context + 6:, 2:] = 1
            self.assertEqual(kimodo_runtime._foot_exchange_midpoints(contacts[context:]), [4])
            for foot_contacts in (None, contacts):
                with self.subTest(context=context, has_foot_events=foot_contacts is not None):
                    reference = KmbMotion(**{**motion.__dict__, "local_rot_quats": rotations, "foot_contacts": foot_contacts})
                    attachments = []
                    plan = {"target_frame_count": motion.num_frames, "output_start_frame": context,
                            "output_frame_count": 8, "loop": {"enabled": True, "window_frames": 3, "sample_count": 2}}
                    with patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=encode_kmb1(reference)):
                        items, padding = kimodo_runtime._loop_clip_constraints({}, SimpleNamespace(fps=30), plan, attachments)
                    opening, tail = [parse_kmb_clip(item, tuple(attachments), 30).motion for item in items]
                    for item, start in zip(items, (context, context + 7)):
                        self.assertEqual(item["anchor_source_frame"], context)
                        self.assertEqual(item["source_start_frame"], context)
                        self.assertEqual(item["source_end_frame_exclusive"], context + 3)
                        self.assertEqual(item["target_frame_indices"], list(range(start, start + 3)))
                        self.assertEqual(item["source_frame_indices"], [0, 2])
                    self.assertEqual(padding, 2)
                    np.testing.assert_allclose(opening.root_positions, reference.root_positions[context:context + 3])
                    np.testing.assert_allclose(opening.local_rot_quats, reference.local_rot_quats[context:context + 3])
                    np.testing.assert_allclose(tail.root_positions[0], reference.root_positions[context + 7], atol=1e-6)
                    rotation = Rotation.from_euler("y", 0.7)
                    expected = rotation.apply(opening.root_positions - opening.root_positions[0]) + reference.root_positions[context + 7]
                    np.testing.assert_allclose(tail.root_positions, expected, atol=1e-6)
                    np.testing.assert_allclose(tail.local_rot_quats[0, 0], reference.local_rot_quats[context + 7, 0], atol=1e-6)

    def test_ardy_single_frame_reference_has_one_conditioning_frame_after_velocity_encoding(self):
        calls = []
        class MotionRep:
            def __call__(self, *, root_positions, **kwargs):
                calls.append(len(root_positions))
                self.assertion = len(root_positions) >= 2
                return root_positions.clone()
        motion_rep = MotionRep()
        tensor = ardy_backend._motion_to_tensor(self._small_kmb(1), SimpleNamespace(device="cpu", motion_rep=motion_rep), 0, 1)
        self.assertEqual(calls, [2])
        self.assertTrue(motion_rep.assertion)
        self.assertEqual(tuple(tensor.shape), (1, 1, 3))

    def test_ardy_staged_clip_and_point_constraints_share_one_anchor_and_restore(self):
        from ardy.geometry import quaternion_to_matrix
        skeleton = SimpleNamespace(device=torch.device("cpu"), root_idx=0, hip_joint_idx=(1, 2),
                                   bone_order_names=("root", "right", "left"), joint_parents=torch.tensor([-1, 0, 0]))
        def fk(local, roots):
            offsets = torch.tensor([[0., 0., 0.], [-1., 0., 0.], [1., 0., 0.]])
            positions = offsets @ local[:, 0].transpose(-1, -2) + roots[:, None]
            return local, positions, None
        skeleton.fk = fk
        quats = np.tile(np.asarray([1, 0, 0, 0], dtype=np.float32), (4, 3, 1))
        quats[:, 0] = [math.sqrt(0.5), 0, math.sqrt(0.5), 0]
        motion = KmbMotion(b"", "test", 20., skeleton.bone_order_names, (-1, 0, 0),
                           np.asarray([[10 + frame, 2, 20] for frame in range(4)], dtype=np.float32), quats, None)
        original = encode_kmb1(motion)
        item = {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0, "start_time": -0.2, "duration": 0.2}
        point = {"type": "root2d", "frame_indices": [0], "smooth_root_2d": [[14, 20]]}
        model = SimpleNamespace(device="cpu", skeleton=skeleton, motion_rep=SimpleNamespace(skeleton=skeleton))
        value, attachments, origin = ardy_backend._normalize_fixed_clip_constraints(
            json.dumps([item, point]), (original,), model, SimpleNamespace(source_fps=20., model_name="test"))
        records = json.loads(value)
        normalized = parse_kmb_clip(records[-1], attachments, 20.).motion
        np.testing.assert_allclose(normalized.root_positions[0], [0, 2, 0], atol=1e-5)
        normalized_root_rotation = quaternion_to_matrix(torch.from_numpy(normalized.local_rot_quats[:, 0]))
        torch.testing.assert_close(normalized_root_rotation, torch.eye(3).expand(4, 3, 3), atol=1e-5, rtol=1e-5)
        output = {"root_positions": normalized.root_positions[None], "posed_joints": normalized.root_positions[None, :, None]}
        restored = kimodo_runtime._restore_kimodo_output_origin(output, origin, model)
        np.testing.assert_allclose(restored["root_positions"][0], motion.root_positions, atol=1e-5)
        self.assertEqual(attachments[0], original)

    @staticmethod
    def _small_kmb(frames):
        return KmbMotion(b"", "test", 30., ("root", "hip"), (-1, 0),
                         np.column_stack((np.arange(frames), np.zeros((frames, 2)))).astype(np.float32),
                         np.tile(np.asarray([1, 0, 0, 0], dtype=np.float32), (frames, 2, 1)), None)

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

    def test_fixed_ardy_root2d_constraints_normalize_and_keep_the_restore_origin(self):
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

        np.testing.assert_allclose(session.constraints[0].root_2d.cpu(), [[0.0, 0.0]], atol=1e-7)
        np.testing.assert_allclose(session.constraint_origin[0].cpu(), [-0.539, 0.0], atol=1e-7)
        self.assertAlmostEqual(float(session.constraint_origin[1]), math.pi / 2, places=6)

    def test_mp4_is_an_optional_extra_output_not_a_motion_format(self):
        self.assertEqual(
            kimodo_runtime._resolve_requested_output_format({"output_format": "kmb_v1"}),
            "kmb_v1",
        )
        with patch.dict(os.environ, {"KIMODO_BRIDGE_OUTPUT_FORMAT": "json_compact"}):
            self.assertEqual(kimodo_runtime._resolve_requested_output_format({"output_format": "mp4"}), "json_compact")

    def test_finalize_returns_mp4_path_without_changing_motion_payload(self):
        model = SimpleNamespace(fps=30.0)
        output = {"posed_joints": np.zeros((1, 1, 1, 3), dtype=np.float32)}
        mp4_path = "C:/quickserver/generated_animation_test.mp4"
        with (
            patch.object(kimodo_runtime, "_build_generate_flatbuffer_payload", return_value=b"kmb"),
            patch.object(
                kimodo_runtime,
                "_build_generate_mp4_file",
                return_value=mp4_path,
            ),
            patch("core.animation_analysis.build_generation_analysis", return_value=None),
        ):
            response, payload = kimodo_runtime._finalize_generation_result(
                {"output_format": "kmb_v1", "output_mp4": True},
                model,
                output,
                "walk",
            )

        self.assertEqual(response["output_format"], "kmb_v1")
        self.assertEqual(response["byte_length"], 3)
        self.assertEqual(response["mp4_path"], mp4_path)
        self.assertNotIn("binary_attachments", response)
        self.assertEqual(payload, b"kmb")

    def test_generate_mp4_file_saves_under_quickserver_root(self):
        model = SimpleNamespace(fps=30.0, skeleton=None)
        output = {"posed_joints": np.zeros((1, 1, 1, 3), dtype=np.float32)}
        with (
            patch("core.motion_rendering.render_skeleton_mp4", return_value=b"mp4"),
            patch.object(Path, "write_bytes", autospec=True, return_value=3) as write_bytes,
        ):
            mp4_path = Path(kimodo_runtime._build_generate_mp4_file(model, output, "walk"))

        self.assertEqual(mp4_path.parent, Path(kimodo_runtime.__file__).resolve().parent.parent)
        self.assertRegex(mp4_path.name, r"^generated_animation_[0-9a-f]{32}\.mp4$")
        write_bytes.assert_called_once_with(mp4_path, b"mp4")

    def test_ardy_clip_mask_accepts_object_and_rejects_legacy_array(self):
        names = ("Hips", "Spine", "LeftArm")
        mask = parse_clip_mask(
            {
                "mask": {
                    "root_position": [True, False, True],
                    "root_heading": True,
                    "root_rotation": True,
                    "joints": [
                        {
                            "joint_name": "Spine",
                            "position": [True, True, True],
                            "rotation": True,
                        }
                    ],
                }
            },
            names,
            ardy_backend.ArdyBackendError,
        )
        current = ardy_backend._future_clip_mask(mask, names)
        self.assertEqual(current["root_position"], [True, False, True])
        self.assertTrue(current["root_heading"])
        self.assertEqual(current["joint_rotation"].tolist(), [True, True, False])
        self.assertEqual(current["joint_position"].tolist(), [[True, True, True], [False, False, False]])
        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "must be an object"):
            parse_clip_mask(
                {"mask": [True, False, True, True, True, True, True, False, False, False]},
                names,
                ardy_backend.ArdyBackendError,
            )

    def test_kimodo_clip_constraints_receive_generate_attachments(self):
        model = SimpleNamespace(skeleton=object())
        payload = b"clip-kmb"
        item = {"type": "clip", "attachment": 0}
        with patch("kimodo.constraints.load_constraints_lst", return_value=[]), patch.object(
            kimodo_runtime,
            "_load_clip_constraint",
            return_value="clip-constraint",
        ) as load_clip:
            constraints = kimodo_runtime._load_constraints(json.dumps([item]), model, attachments=(payload,))

        self.assertEqual(constraints, ["clip-constraint"])
        load_clip.assert_called_once_with(item, model, (payload,))

    def test_normal_generate_passes_kmb_attachments_to_runtime(self):
        model = SimpleNamespace()
        expected_response = {"status": "done"}
        with patch.object(kimodo_runtime, "_run_generate", return_value=({}, "walk.")) as run_generate, patch.object(
            kimodo_runtime, "_finalize_generation_result", return_value=(expected_response, None)
        ):
            response, payload = quickserver_cli._execute_generate(
                {"duration": 1.0},
                model,
                threading.Event(),
                attachments=(b"clip-kmb",),
            )

        self.assertEqual(response, expected_response)
        self.assertIsNone(payload)
        self.assertEqual(run_generate.call_args.kwargs, {"emit_progress": False, "attachments": (b"clip-kmb",)})

    def test_kimodo_root2d_keeps_explicit_points_and_heading_pairs(self):
        model = SimpleNamespace(fps=30.0, skeleton=object())
        with patch("kimodo.constraints.load_constraints_lst", side_effect=lambda items, _skeleton, **_kwargs: items):
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
        with self.assertRaisesRegex(ValueError, "ARDY-only navigation constraint"):
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

    def test_ardy_batch_capacity_only_grows_with_session_count(self):
        batcher = ardy_backend._ArdyInferenceBatcher(max_batch_size=8)

        self.assertEqual(batcher.wait_seconds, 0.032)
        self.assertEqual(
            [batcher.set_session_count(count) for count in (1, 2, 3, 4, 5, 8)],
            [1, 2, 4, 4, 8, 8],
        )
        self.assertEqual(batcher.set_session_count(4), 8)
        self.assertEqual(batcher.set_session_count(3), 8)
        self.assertEqual(batcher.set_session_count(0), 8)

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

    def test_motion_runtime_signature_is_independent_of_text_encoder_mode(self):
        base = {
            "model": "Kimodo-SOMA-RP-v1",
            "text_encoder_mode": "high_precision",
            "models_root": "C:/runtime/models",
            "simulate_free_vram_gb": None,
        }
        changed_encoder = {**base, "text_encoder_mode": "high_performance"}
        changed_motion_model = {**base, "model": "Kimodo-SOMA-RP-v1.1"}

        self.assertEqual(
            quickserver_cli._build_motion_runtime_signature(base),
            quickserver_cli._build_motion_runtime_signature(changed_encoder),
        )
        self.assertNotEqual(
            quickserver_cli._build_motion_runtime_signature(base),
            quickserver_cli._build_motion_runtime_signature(changed_motion_model),
        )

    def test_motion_runtime_eviction_is_deferred_until_new_model_needs_vram(self):
        old_runtime = {"model": object(), "resolved_model_name": "old", "last_used_at": 1.0}
        newer_runtime = {"model": object(), "resolved_model_name": "newer", "last_used_at": 2.0}
        shared_encoder = object()
        state = {
            "kimodo_runtimes": {"old": old_runtime, "newer": newer_runtime},
            "active_runtime": newer_runtime,
            "shared_text_encoder": shared_encoder,
        }
        config = {"model": "Kimodo-SOMA-RP-v1", "simulate_free_vram_gb": None}
        gpu_profile = lambda free: SimpleNamespace(runtime_device="cuda:0", free_vram_gb=free)

        with patch.object(quickserver_cli.assets, "motion_model_min_free_vram_gb", return_value=4.0), patch.object(
            quickserver_cli.runtime_helpers,
            "_runtime_self_check",
            side_effect=[gpu_profile(2.0), gpu_profile(5.0)],
        ), patch.object(quickserver_cli, "_unload_runtime_model") as unload_runtime:
            quickserver_cli._evict_cached_kimodo_runtimes_for_request(
                state, config, SimpleNamespace(log=lambda _message: None)
            )

        unload_runtime.assert_called_once_with(old_runtime, ANY)
        self.assertNotIn("old", state["kimodo_runtimes"])
        self.assertIn("newer", state["kimodo_runtimes"])
        self.assertIs(state["active_runtime"], newer_runtime)
        self.assertIs(state["shared_text_encoder"], shared_encoder)

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
        cached_model = SimpleNamespace(text_encoder=encoder)
        session_model = SimpleNamespace(text_encoder=encoder)
        retired_model = SimpleNamespace(text_encoder=encoder)
        state = {
            "active_runtime": {"model": active_model},
            "kimodo_runtimes": {"cached": {"model": cached_model}},
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
        self.assertIsNone(cached_model.text_encoder)
        self.assertIsNone(session_model.text_encoder)
        self.assertIsNone(retired_model.text_encoder)
        self.assertIsNone(state["shared_text_encoder"])
        self.assertEqual(state["shared_text_encoder_signature"], "")

    def test_missing_encoder_is_rebuilt_without_reloading_the_motion_runtime(self):
        config = {
            "model": "ARDY-Core-RP-20FPS-Horizon40",
            "text_encoder_mode": "high_performance",
            "models_root": "D:/editor/models",
            "force_hf_download": False,
            "simulate_free_vram_gb": None,
        }
        previous_config = {**config, "text_encoder_mode": "high_precision"}
        model = SimpleNamespace(text_encoder=None)
        runtime = {
            "model": model,
            "runtime_signature": quickserver_cli._build_signature(previous_config),
            "motion_runtime_signature": quickserver_cli._build_motion_runtime_signature(config),
            "text_encoder_signature": quickserver_cli._build_text_encoder_signature(previous_config),
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
        contacts = np.asarray([[1, 0, 0, 0], [0, 0, 1, 0]], dtype=np.float32)

        def parse(payload, _error_type=ValueError):
            return ardy_backend.KmbMotion(
                payload=payload,
                model_name="test",
                fps=20.0,
                joint_names=("root",),
                joint_parents=(-1,),
                root_positions=roots,
                local_rot_quats=quats,
                foot_contacts=contacts,
            )

        request = {
            "analysis_option": {"analysis_only": True, "keyframes": {"max_count": 4}},
            "constraints_json": json.dumps([
                {"type": "clip", "format": "kmb_attachment_v1", "attachment": 0, "start_frame": 0, "end_frame_exclusive": 2},
                {"type": "clip", "format": "kmb_attachment_v1", "attachment": 1, "start_frame": 0, "end_frame_exclusive": 2},
            ]),
        }
        with patch.object(quickserver_cli, "parse_kmb1", side_effect=parse):
            response, payload = quickserver_cli._execute_analysis_only(request, (b"first", b"second"))

        self.assertEqual(response["output_format"], "kmb_attachments_v1")
        self.assertEqual([item["index"] for item in response["kmb_attachments"]], [0, 1])
        self.assertEqual([item["start_frame"] for item in response["kmb_attachments"]], [0, 0])
        self.assertEqual([item["end_frame_exclusive"] for item in response["kmb_attachments"]], [2, 2])
        self.assertEqual(4, len(response["analysis"]["keyframes"]))
        self.assertEqual(
            [
                (0, 0), (0, 1),
                (1, 0), (1, 1),
            ],
            sorted((item["clip_index"], item["frame"]) for item in response["analysis"]["keyframes"]),
        )
        self.assertTrue(all("saliency" in item for item in response["analysis"]["keyframes"]))
        self.assertEqual([], response["analysis"]["foot_contact_changes"])
        for item in response["kmb_attachments"]:
            clip_payload = payload[item["offset"] : item["offset"] + item["byte_length"]]
            dense = parse_kmb1_payload(clip_payload)
            self.assertEqual(2, dense.num_frames)
            self.assertTrue(np.array_equal(contacts, dense.foot_contacts))

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

    def test_generated_horizon_keeps_explicit_history_for_debug_and_rollover(self):
        history = torch.arange(12, dtype=torch.float32).reshape(1, 4, 3)
        generated = torch.arange(24, 30, dtype=torch.float32).reshape(1, 2, 3)
        combined = ardy_backend._join_explicit_history_and_generated(history, generated)

        self.assertTrue(torch.equal(combined[:, :4], history))
        self.assertTrue(torch.equal(combined[:, 4:], generated))
        self.assertTrue(torch.equal(ardy_backend._join_explicit_history_and_generated(None, generated), generated))

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
            "start_time": -8.3,
            "duration": 8.3,
        }

        with (
            patch("core.protocol.kmb_motion.parse_kmb1", return_value=motion),
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

    def test_cross_zero_clip_is_split_into_history_and_future(self):
        session = self._fake_ardy_session()
        session.quickserver_root = Path.cwd()
        motion = ardy_backend.KmbMotion(
            payload=b"kmb",
            model_name="test",
            fps=20.0,
            joint_names=("root",),
            joint_parents=(-1,),
            root_positions=np.zeros((8, 3), dtype=np.float32),
            local_rot_quats=np.zeros((8, 1, 4), dtype=np.float32),
            foot_contacts=None,
        )
        encoded = torch.arange(8, dtype=torch.float32).reshape(1, 8, 1)
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        item = {
            "type": "clip",
            "format": "kmb_attachment_v1",
            "attachment": 0,
            "start_time": -0.2,
            "duration": 0.4,
            "mask": {
                "root_position": [True, False, False],
                "root_heading": False,
                "root_rotation": False,
                "joints": [],
            },
        }

        with (
            patch("core.protocol.kmb_motion.parse_kmb1", return_value=motion),
            patch.object(ardy_backend, "_validate_kmb"),
            patch.object(ardy_backend, "_motion_to_tensor", return_value=encoded),
            patch("ardy.constraints.load_constraints_lst", return_value=[]),
        ):
            session._set_constraints([item], (b"kmb",), model, apply_from=0, initial=True)

        self.assertTrue(torch.equal(session.initial_history_cpu, encoded[:, :4]))
        self.assertEqual(len(session.future_clips), 1)
        target_start, future, mask = session.future_clips[0]
        self.assertEqual(target_start, 0)
        self.assertTrue(torch.equal(future, encoded[:, 4:]))
        self.assertEqual(mask["root_position"], [True, False, False])

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

    def test_untimed_root_target_builds_a_dense_acceleration_limited_horizon(self):
        target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, False)

        planned = ardy_backend._plan_root_2d_target(
            target,
            (0.0, 0.0),
            (0.0, 0.0),
            39,
            20.0,
            future_horizon_frames=40,
            extend_prediction_to_horizon=True,
        )

        self.assertEqual(planned["frame_indices"], list(range(40, 80)))
        positions = np.asarray(planned["smooth_root_2d"])
        velocities = np.diff(np.vstack(([0.0, 0.0], positions)), axis=0) * 20.0
        accelerations = np.diff(np.vstack(([0.0, 0.0], velocities)), axis=0) * 20.0
        self.assertLessEqual(float(np.linalg.norm(velocities, axis=1).max()), target.max_speed + 1e-6)
        self.assertLessEqual(
            float(np.linalg.norm(accelerations, axis=1).max()), target.max_acceleration + 1e-6
        )

    def test_untimed_root_target_decelerates_from_over_limit_speed_without_clamping(self):
        target = ardy_backend.Root2DTarget((10.0, 0.0), 1.25, 1.5, 0.1, False)

        planned = ardy_backend._plan_root_2d_target(
            target,
            (0.0, 0.0),
            (2.5, 0.0),
            39,
            20.0,
            future_horizon_frames=40,
            extend_prediction_to_horizon=True,
        )

        positions = np.asarray(planned["smooth_root_2d"])
        velocities = np.diff(np.vstack(([0.0, 0.0], positions)), axis=0) * 20.0
        self.assertGreater(float(np.linalg.norm(velocities[0])), target.max_speed)
        self.assertLess(float(np.linalg.norm(velocities[0])), 2.5)
        self.assertLessEqual(
            float(np.linalg.norm(velocities[0] - [2.5, 0.0]) * 20.0),
            target.max_acceleration + 1e-6,
        )

    def test_untimed_root_target_does_not_snap_when_near_goal_at_high_speed(self):
        target = ardy_backend.Root2DTarget((0.1, 0.0), 1.25, 1.5, 0.1, False)

        planned = ardy_backend._plan_root_2d_target(
            target,
            (0.0, 0.0),
            (1.0, 0.0),
            39,
            20.0,
            future_horizon_frames=40,
            extend_prediction_to_horizon=True,
        )

        self.assertIsNotNone(planned)
        self.assertGreater(planned["smooth_root_2d"][0][0], 0.0)
        self.assertNotEqual(planned["smooth_root_2d"][0], list(target.position))

    def test_root_target_is_only_released_after_actual_state_reaches_and_stops(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 40, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 40, 3), dtype=np.float32)}
        target = ardy_backend.Root2DTarget((0.5, 0.0), 2.0, 2.0, 0.1, False)
        session.root_2d_targets = [target]
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        captured = []

        def load_constraints(items, _skeleton):
            captured.append(items)
            return items

        with patch("ardy.constraints.load_constraints_lst", side_effect=load_constraints):
            session._refresh_root_2d_target_constraints(model, 40)
            self.assertEqual(session.root_2d_targets, [target])
            planned = next(
                item
                for item in captured[-1]
                if item["type"] == "root2d" and len(item["frame_indices"]) > 1
            )
            self.assertEqual(planned["frame_indices"], list(range(40, 80)))
            self.assertEqual(planned["smooth_root_2d"][-1], planned["smooth_root_2d"][-2])

            stopped_position = planned["smooth_root_2d"][-1]
            session.motion_cpu = torch.zeros((1, 80, 1), dtype=torch.float32)
            session.outputs["root_positions"] = np.zeros((1, 80, 3), dtype=np.float32)
            session.outputs["root_positions"][0, -2:, 0] = stopped_position[0]
            session.outputs["root_positions"][0, -2:, 2] = stopped_position[1]
            session._refresh_root_2d_target_constraints(model, 80)

        self.assertEqual(session.root_2d_targets, [target])
        hold = next(
            item
            for item in captured[-1]
            if item["type"] == "root2d" and len(item["frame_indices"]) > 1
        )
        self.assertEqual(hold["frame_indices"], list(range(80, 121)))
        np.testing.assert_allclose(
            hold["smooth_root_2d"],
            np.repeat([stopped_position], 41, axis=0),
            atol=1e-7,
        )

    def test_root_target_queue_advances_at_the_next_horizon_boundary(self):
        session = self._fake_ardy_session()
        session.motion_cpu = torch.zeros((1, 80, 1), dtype=torch.float32)
        session.outputs = {"root_positions": np.zeros((1, 80, 3), dtype=np.float32)}
        session.outputs["root_positions"][0, -2:, 0] = 0.37
        first = ardy_backend.Root2DTarget((0.5, 0.0), 2.0, 2.0, 0.1, False)
        following = ardy_backend.Root2DTarget((2.0, 0.0), 2.0, 2.0, 0.1, False)
        session.root_2d_targets = [first, following]
        model = SimpleNamespace(motion_rep=SimpleNamespace(skeleton=object()))
        captured = []

        def load_constraints(items, _skeleton):
            captured.append(items)
            return items

        with patch("ardy.constraints.load_constraints_lst", side_effect=load_constraints):
            session._refresh_root_2d_target_constraints(model, 80)

        self.assertEqual(session.root_2d_targets, [following])
        planned = next(
            item
            for item in captured[-1]
            if item["type"] == "root2d" and len(item["frame_indices"]) > 1
        )
        self.assertEqual(planned["frame_indices"], list(range(80, 120)))
        self.assertGreater(planned["smooth_root_2d"][0][0], 0.37)

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
        self.assertLessEqual(math.dist(planned["smooth_root_2d"][2], target.position), 0.15)

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
        self.assertEqual(
            len({tuple(point) for point in planned["smooth_root_2d"]}),
            len(planned["smooth_root_2d"]),
        )
        self.assertLessEqual(math.dist(planned["smooth_root_2d"][-1], target.position), 0.15)

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
            self.assertEqual(session.root_2d_targets, [])
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d"])
            self.assertEqual(loaded[-1][0]["frame_indices"][-1], 69)
            self.assertEqual(loaded[-1][0]["smooth_root_2d"][-1], [6.0, 2.0])

            session._set_constraints(
                [{"type": "root2d", "frame_indices": [12], "smooth_root_2d": [[-3.0, 2.0]]}],
                (),
                model,
                apply_from=40,
                initial=False,
            )
            self.assertEqual(session.root_2d_targets, [])
            self.assertEqual([item["type"] for item in loaded[-1]], ["root2d"])
            self.assertEqual(loaded[-1][0]["frame_indices"][-1], 52)
            self.assertEqual(loaded[-1][0]["smooth_root_2d"][-1], [-3.0, 2.0])

            session._set_constraints([], (), model, apply_from=40, initial=False)
            self.assertEqual(session.root_2d_targets, [])
            self.assertEqual(session.constraints, [])

    def test_streaming_root2d_protocol_preserves_explicit_trajectory_coordinates(self):
        session = self._fake_ardy_session()
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
        self.assertEqual(session.root_2d_targets, [])
        np.testing.assert_allclose(session.constraints[0].root_2d[[0, -1]].cpu(), [[10.0, 20.0], [13.0, 24.0]])

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
        segments = parse_timeline_segments(
            [
                {"prompt": "walk", "duration": 1.0},
                {"prompt": "turn left", "duration": 2.0},
            ],
            20.0,
            60,
        )

        self.assertEqual(
            segments,
            (
                TimelineSegment("walk", 0, 20),
                TimelineSegment("turn left", 20, 60),
            ),
        )

    def test_timeline_segments_allow_unaligned_boundary_and_reject_mismatched_duration(self):
        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "resolves to 20 frames"):
            parse_timeline_segments(
                [{"prompt": "walk", "duration": 1.0}], 20.0, 40, ardy_backend.ArdyBackendError
            )
        segments = parse_timeline_segments(
            [
                {"prompt": "walk", "duration": 0.1},
                {"prompt": "turn", "duration": 1.9},
            ],
            20.0,
            40,
        )
        self.assertEqual(
            segments,
            (
                TimelineSegment("walk", 0, 2),
                TimelineSegment("turn", 2, 40),
            ),
        )

    def test_timeline_prompt_boundary_aligns_to_generation_horizon(self):
        session = self._fake_ardy_session()
        session.profile.frames_per_token = 4
        session.profile.horizon_frames = 40
        session.timeline_segments = (
            TimelineSegment("walk", 0, 101),
            TimelineSegment("turn", 101, 192),
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
            _, response, payload = ardy_backend.execute_stream_generate(
                None,
                {"duration": 0.05, "prompt": "walk"},
                (),
                SimpleNamespace(),
                profile,
                threading.Event(),
                ".",
            )

        self.assertEqual(response["output_format"], "kmb_v1")
        self.assertEqual(payload, b"kmb")
        restored = build_payload.call_args.args[1]
        np.testing.assert_allclose(restored["root_positions"][0, 0], [-0.539, 0.0, 0.0], atol=1e-6)

    def test_fixed_duration_ardy_preserves_kmb_and_forwards_output_mp4(self):
        class FakeSession:
            resolved_seed = 7
            effective_playback_reserve_frames = 0
            settings = SimpleNamespace(adaptive_playback_reserve=False)
            constraint_origin = None

            def generate(self, _request, _attachments, _model, _cancel_event):
                return {"start_frame": 0, "end_frame_exclusive": 1}, {"posed_joints": np.zeros((1, 1, 1, 3))}

            def record_response_duration(self, _elapsed, _delivered_frames):
                pass

            def close(self):
                pass

        profile = SimpleNamespace(source_fps=20.0, motion_rep_fingerprint="test")
        with (
            patch.object(ardy_backend, "ArdySession", return_value=FakeSession()),
            patch.object(
                kimodo_runtime,
                "_finalize_generation_result",
                return_value=(
                    {
                        "status": "done",
                        "output_format": "kmb_v1",
                        "mp4_path": "C:/quickserver/generated_animation_test.mp4",
                    },
                    b"kmb",
                ),
            ) as finalize,
        ):
            _, response, payload = ardy_backend.execute_stream_generate(
                None,
                {"duration": 0.05, "prompt": "walk", "output_mp4": True},
                (),
                SimpleNamespace(),
                profile,
                threading.Event(),
                ".",
            )

        self.assertEqual(response["output_format"], "kmb_v1")
        self.assertEqual(payload, b"kmb")
        self.assertEqual(finalize.call_args.kwargs["output_format"], "kmb_v1")
        self.assertTrue(finalize.call_args.args[0]["output_mp4"])

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

    def test_auto_history_uses_previous_root_speed(self):
        profile = SimpleNamespace(
            source_fps=20.0,
            horizon_frames=40,
            frames_per_token=4,
            max_context_frames=200,
        )
        idle = ardy_backend._auto_history_frames_from_root_speed(
            profile,
            (0.0, 0.0),
        )
        walking = ardy_backend._auto_history_frames_from_root_speed(
            profile,
            (1.0, 0.0),
        )
        running = ardy_backend._auto_history_frames_from_root_speed(
            profile,
            (5.5, 0.0),
        )
        saturated = ardy_backend._auto_history_frames_from_root_speed(
            profile,
            (10.0, 0.0),
        )

        self.assertEqual(idle, 40)
        self.assertEqual(walking, 40)
        self.assertEqual(running, 76)
        self.assertEqual(saturated, 160)

    def test_auto_history_reads_sparse_fullbody_without_adding_root2d(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(
            160,
            160,
            20,
            False,
            auto_history=True,
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

        self.assertEqual(history_limit, 40)
        self.assertEqual(session._auto_history_frames, 40)
        self.assertEqual([constraint.name for constraint in session.constraints], ["fullbody"])
        self.assertEqual(session.root_2d_targets, [])

    def test_auto_history_uses_idle_history_for_timed_root2d_points(self):
        session = self._fake_ardy_session()
        session.settings = ardy_backend.ArdySettings(
            160,
            160,
            20,
            False,
            auto_history=True,
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

        self.assertEqual(history_limit, 40)
        self.assertEqual(session._auto_history_frames, 40)

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
        session.progress = None
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
