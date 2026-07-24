from __future__ import annotations

import os
from pathlib import Path
import socket
import tempfile
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import patch

import numpy as np
import torch

from kimodo.bridge import ardy_backend
from kimodo.bridge import animation_handles
from kimodo.bridge import bridge_server
from kimodo.bridge import quickserver_assets
from kimodo.bridge import quickserver_setup


class _MotionRep:
    def __init__(self, skeleton):
        self.skeleton = skeleton

    def __call__(self, *, local_joint_rots, root_positions, to_normalize):
        assert to_normalize
        return root_positions.unsqueeze(0)


class _ContactMotionRep:
    def __init__(self, skeleton):
        self.skeleton = skeleton
        self.slice_dict = {"foot_contacts": slice(3, 7)}

    def __call__(self, *, local_joint_rots, root_positions, to_normalize):
        assert not to_normalize
        roots = root_positions.unsqueeze(0)
        return torch.cat((roots, torch.zeros(1, roots.shape[1], 4)), dim=-1)

    def normalize(self, features):
        return features


class _FutureMotionRep:
    def __init__(self, skeleton):
        self.skeleton = skeleton
        self.motion_rep_dim = 11
        self.slice_dict = {
            "root_pos": slice(0, 3),
            "global_root_heading": slice(3, 5),
            "local_joints_positions": slice(5, 11),
        }

    def __call__(self, *, local_joint_rots, root_positions, to_normalize):
        assert to_normalize
        frames = int(local_joint_rots.shape[0])
        global_rots = torch.empty_like(local_joint_rots)
        positions = torch.zeros(frames, 3, 3, dtype=local_joint_rots.dtype)
        offsets = torch.tensor([[0, 0, 0], [0, 1, 0], [1, 0, 0]], dtype=local_joint_rots.dtype)
        global_rots[:, 0] = local_joint_rots[:, 0]
        for joint in (1, 2):
            parent = int(self.skeleton.joint_parents[joint])
            global_rots[:, joint] = global_rots[:, parent] @ local_joint_rots[:, joint]
            positions[:, joint] = positions[:, parent] + (global_rots[:, parent] @ offsets[joint].reshape(3, 1)).reshape(frames, 3)
        ground = torch.zeros_like(root_positions)
        ground[:, 1] = root_positions[:, 1]
        local_positions = positions[:, 1:] + ground[:, None]
        heading = torch.stack((global_rots[:, 0, 0, 0], global_rots[:, 0, 0, 2]), dim=-1)
        return torch.cat((root_positions, heading, local_positions.reshape(frames, -1)), dim=-1)


class _StreamingMotionRep:
    def __init__(self):
        self.skeleton = SimpleNamespace(
            bone_order_names=["Root", "Spine"],
            joint_parents=torch.tensor([-1, 0]),
        )

    def inverse(self, motion, *, is_normalized):
        assert is_normalized
        frames = int(motion.shape[1])
        posed = torch.zeros(1, frames, 2, 3)
        posed[0, :, 0, 0] = motion[0, :, 0]
        rotations = torch.eye(3).reshape(1, 1, 1, 3, 3).expand(1, frames, 2, 3, 3).clone()
        return {
            "posed_joints": posed,
            "root_positions": posed[:, :, 0],
            "local_rot_mats": rotations,
            "foot_contacts": torch.zeros(1, frames, 4),
        }

    root_slice = slice(0, 1)


class _StreamingModel:
    def __init__(self):
        self.device = torch.device("cpu")
        self.motion_rep = _StreamingMotionRep()
        self.skeleton = self.motion_rep.skeleton
        self.name = "ardy-stream-test"
        self.fps = 20.0
        self.requested_frames = []
        self.text_encode_count = 0

    def _encode_text(self, _texts):
        self.text_encode_count += 1
        return torch.ones(1, 2, 3), torch.ones(1, 2, dtype=torch.bool)

    def autoregressive_step(self, *, num_frames, init_history_sequence, cancel_callback, **_kwargs):
        assert cancel_callback is None
        self.requested_frames.append(int(num_frames))
        result = torch.zeros(1, int(num_frames), 3)
        result[0, :, 0] = torch.arange(int(num_frames))
        if init_history_sequence is not None:
            result[:, : init_history_sequence.shape[1]] = init_history_sequence
        return result


class _TwoPassModel:
    def __init__(self, first_pass_frames=None):
        self.motion_rep = SimpleNamespace(root_slice=slice(0, 5))
        self.calls = []
        self.first_pass_frames = first_pass_frames

    def autoregressive_step(self, *, motion_mask, observed_motion, **kwargs):
        self.calls.append((motion_mask, observed_motion))
        if len(self.calls) == 1:
            value = torch.zeros(1, self.first_pass_frames or kwargs["num_frames"], 8)
            value[..., :5] = 7
            return value
        return observed_motion.clone()


def _stream_profile():
    return SimpleNamespace(
        model_name="ardy-stream-test",
        source_fps=20.0,
        motion_rep_fingerprint="stream-rep-v1",
        frames_per_token=2,
        horizon_frames=4,
        max_context_frames=12,
        max_diffusion_steps=10,
        cfg_constraint_weight=2.0,
        postprocess=False,
    )


def _payload(model, root_x: float, foot_contacts=None) -> bytes:
    frames = 4
    joints = len(model.skeleton.bone_order_names)
    posed = np.zeros((1, frames, joints, 3), dtype=np.float32)
    posed[0, :, 0, 0] = np.arange(frames, dtype=np.float32) + root_x
    rotations = np.broadcast_to(np.eye(3, dtype=np.float32), (1, frames, joints, 3, 3)).copy()
    output = {"posed_joints": posed, "local_rot_mats": rotations}
    if foot_contacts is not None:
        output["foot_contacts"] = foot_contacts
    return bridge_server._build_generate_flatbuffer_payload(model, output)


class ArdyBackendSelfCheck(unittest.TestCase):
    def test_ardy_generate_returns_kmb(self):
        model = _StreamingModel()
        response, payload = ardy_backend.execute_generate(
            {
                "prompt": "T pose",
                "duration": 0.25,
                "output_format": "flatbuf_motion_v1",
                "diffusion_steps": 1,
                "seed": 7,
            },
            model,
            _stream_profile(),
            threading.Event(),
            animation_handles.AnimationHandleStore(byte_quota=1024 * 1024),
            Path(__file__).parent,
        )
        motion = ardy_backend.parse_kmb1(payload)
        self.assertEqual(response["status"], "done")
        self.assertEqual((motion.num_frames, motion.model_name), (4, "ardy-stream-test"))
        self.assertEqual(model.requested_frames, [4])

    def test_kmb_export_rejects_non_finite_motion(self):
        model = _StreamingModel()
        frames = 2
        posed = np.zeros((1, frames, 2, 3), dtype=np.float32)
        rotations = np.broadcast_to(np.eye(3, dtype=np.float32), (1, frames, 2, 3, 3)).copy()
        posed[0, -1, 0, 0] = np.nan
        with self.assertRaisesRegex(ValueError, "NaN or Infinity"):
            bridge_server._build_generate_flatbuffer_payload(
                model,
                {"posed_joints": posed, "local_rot_mats": rotations},
            )

    def test_ardy_stream_generate_retains_official_history_window_and_rounds_capacity(self):
        model = _StreamingModel()
        generator = ardy_backend.ArdyStreamGenerator(
            {"prompt": "T pose", "diffusion_steps": 1, "seed": 7},
            model,
            _stream_profile(),
            animation_handles.AnimationHandleStore(byte_quota=1024),
            Path(__file__).parent,
        )
        first = generator.generate_horizon(model)
        second = generator.generate_horizon(model)
        third = generator.generate_horizon(model)
        fourth = generator.generate_horizon(model)
        self.assertFalse(hasattr(generator, "model"))
        self.assertEqual(first["posed_joints"].shape[1], 4)
        self.assertEqual(second["posed_joints"].shape[1], 4)
        self.assertEqual(third["posed_joints"].shape[1], 4)
        self.assertEqual(fourth["posed_joints"].shape[1], 4)
        self.assertEqual(model.requested_frames, [4, 8, 12, 12])
        self.assertEqual(generator.history.shape[1], 8)
        self.assertEqual(ardy_backend.resolve_history_frame_limit(_stream_profile()), 8)
        self.assertEqual(model.text_encode_count, 1)
        self.assertEqual(
            ardy_backend.resolve_stream_capacity_frames({"duration": 0.25}, _stream_profile()),
            8,
        )

    def test_ardy_stream_slices_future_constraints_by_horizon(self):
        model = _StreamingModel()
        profile = _stream_profile()
        generator = ardy_backend.ArdyStreamGenerator.__new__(ardy_backend.ArdyStreamGenerator)
        generator.profile = profile
        generator.prompt = "test."
        generator.diffusion_steps = 1
        generator.cfg_text_weight = 2.0
        generator.history = None
        generator.history_len = 0
        generator.total_frames = 6
        generator.postprocess_constraints = []
        generator._first_horizon = True
        generator._root_body_pass = False
        generator._future_clip = True
        generator._windowed_constraints = True
        generator._future_offset = 0
        generator.observed_motion = torch.zeros(1, 6, 3)
        generator.observed_motion[0, :, 0] = torch.arange(6) + 10
        generator.motion_mask = torch.ones_like(generator.observed_motion)
        generator._cpu_rng_state = torch.Generator(device="cpu").manual_seed(1).get_state()
        generator._cuda_rng_state = None
        masks = []
        original = model.autoregressive_step

        def capture(**kwargs):
            masks.append(kwargs["motion_mask"].clone())
            return original(**kwargs)

        model.autoregressive_step = capture
        generator.generate_horizon(model)
        generator.generate_horizon(model)
        generator.generate_horizon(model)
        np.testing.assert_array_equal(masks[0][0, :, 0].numpy(), [1, 1, 1, 1])
        np.testing.assert_array_equal(masks[1][0, -4:, 0].numpy(), [1, 1, 0, 0])
        np.testing.assert_array_equal(masks[2][0, -4:, 0].numpy(), [0, 0, 0, 0])

    def test_ardy_stream_slices_standard_constraints_by_horizon(self):
        generator = ardy_backend.ArdyStreamGenerator.__new__(ardy_backend.ArdyStreamGenerator)
        model = _StreamingModel()
        generator.profile = _stream_profile()
        generator.prompt = "test."
        generator.diffusion_steps = 1
        generator.cfg_text_weight = 2.0
        generator.history = None
        generator.history_len = 0
        generator.total_frames = 8
        generator.postprocess_constraints = []
        generator._first_horizon = True
        generator._root_body_pass = False
        generator._future_clip = False
        generator._windowed_constraints = True
        generator._future_offset = 0
        generator.observed_motion = torch.zeros(1, 8, 3)
        generator.motion_mask = torch.ones_like(generator.observed_motion)
        generator._cpu_rng_state = torch.Generator(device="cpu").manual_seed(1).get_state()
        generator._cuda_rng_state = None
        masks = []
        original = model.autoregressive_step

        def capture(**kwargs):
            masks.append(kwargs["motion_mask"].clone())
            return original(**kwargs)

        model.autoregressive_step = capture
        generator.generate_horizon(model)
        generator.generate_horizon(model)
        np.testing.assert_array_equal(masks[0][0, -4:, 0].numpy(), [1, 1, 1, 1])
        np.testing.assert_array_equal(masks[1][0, -4:, 0].numpy(), [1, 1, 1, 1])

    def test_animation_handle_store_lifecycle_and_pending_release(self):
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(name="handle-test", fps=20.0, skeleton=skeleton)
        payload = _payload(model, 1.0)
        store = animation_handles.AnimationHandleStore(
            byte_quota=len(payload) * 2,
            server_instance_id="test-server",
        )
        info = store.publish(payload, description="test", motion_rep_fingerprint="rep-v1")
        self.assertEqual(info["server_instance_id"], "test-server")
        self.assertEqual(info["num_frames"], 4)
        self.assertEqual(info["joint_count"], 2)
        self.assertEqual(store.read(info["handle"]), payload)
        with self.assertRaises(animation_handles.AnimationHandleNotFoundError):
            store.pin([info["handle"], "animation:missing"])
        pinned = store.pin([info["handle"]])
        self.assertTrue(store.release(info["handle"]))
        self.assertEqual(store.read(info["handle"]), payload)
        store.unpin(pinned)
        with self.assertRaises(animation_handles.AnimationHandleNotFoundError):
            store.info(info["handle"])

    def test_animation_handle_store_rejects_invalid_kmb(self):
        store = animation_handles.AnimationHandleStore(byte_quota=1024)
        with self.assertRaises(animation_handles.AnimationHandleError):
            store.publish(b"not-kmb")

    def test_stream_handle_double_buffer_is_destructive_and_resumable(self):
        store = animation_handles.AnimationHandleStore(byte_quota=1024)
        cancelled = []
        resumed = []
        serialized_frames = []

        def serialize(output):
            serialized_frames.append(output["posed_joints"].shape[1])
            return b"KMB"

        info = store.create_stream(
            task_id="task-1",
            session_id="session-1",
            capacity_frames=8,
            horizon_frames=4,
            fps=20.0,
            model_name="ardy-test",
            joint_names=["Root", "Spine"],
            joint_parents=[-1, 0],
            motion_rep_fingerprint="rep-v1",
            description="test",
            serializer=serialize,
            cancel=lambda: cancelled.append(True),
            resume=lambda: resumed.append(True),
        )
        output = {
            "posed_joints": np.zeros((1, 4, 2, 3), dtype=np.float32),
            "local_rot_mats": np.broadcast_to(
                np.eye(3, dtype=np.float32), (1, 4, 2, 3, 3)
            ).copy(),
        }
        self.assertTrue(store.append_stream(info["handle"], output))
        self.assertTrue(store.append_stream(info["handle"], output))
        self.assertTrue(store.stream_is_full(info["handle"]))

        payload, read_info = store.download(info["handle"])
        self.assertEqual(payload, b"KMB")
        self.assertEqual(read_info["num_frames"], 8)
        self.assertEqual(serialized_frames, [8])
        self.assertEqual(len(resumed), 1)
        self.assertEqual(store.download(info["handle"])[0], b"")
        self.assertTrue(store.append_stream(info["handle"], output))
        self.assertTrue(store.release(info["handle"]))
        self.assertEqual(cancelled, [True])

    def test_stream_handle_rejects_a_second_concurrent_download(self):
        store = animation_handles.AnimationHandleStore(byte_quota=1024)
        serializing = threading.Event()
        release_serializer = threading.Event()

        def serialize(_output):
            serializing.set()
            release_serializer.wait(timeout=2)
            return b"KMB"

        info = store.create_stream(
            task_id="task-1",
            session_id="session-1",
            capacity_frames=4,
            horizon_frames=4,
            fps=20.0,
            model_name="ardy-test",
            joint_names=["Root"],
            joint_parents=[-1],
            motion_rep_fingerprint="rep-v1",
            description="test",
            serializer=serialize,
            cancel=lambda: None,
            resume=lambda: None,
        )
        store.append_stream(
            info["handle"],
            {
                "posed_joints": np.zeros((1, 4, 1, 3), dtype=np.float32),
                "local_rot_mats": np.broadcast_to(
                    np.eye(3, dtype=np.float32), (1, 4, 1, 3, 3)
                ).copy(),
            },
        )
        first_result = []
        first = threading.Thread(target=lambda: first_result.append(store.download(info["handle"])))
        first.start()
        self.assertTrue(serializing.wait(timeout=1))
        with self.assertRaises(animation_handles.AnimationHandleBusyError):
            store.download(info["handle"])
        release_serializer.set()
        first.join(timeout=1)
        self.assertEqual(first_result[0][0], b"KMB")

    def test_extract_handle_refs_accepts_new_and_legacy_formats(self):
        self.assertEqual(
            ardy_backend.extract_handle_refs(
                json_text(
                    {"type": "clip", "format": "kmb_handle_v1", "handle": "animation:new"},
                    {"type": "clip", "format": "ardy_handle_v1", "handle": "animation:old"},
                    {"type": "clip", "format": "ardy_file_v1", "path": "ignored"},
                )
            ),
            ("animation:new", "animation:old"),
        )

    def test_kimodo_rejects_ardy_clip_constraint(self):
        with self.assertRaisesRegex(ValueError, "only by ARDY"):
            bridge_server._load_constraints(
                json_text(
                    {
                        "type": "clip",
                        "format": "kmb_handle_v1",
                        "handle": "animation:test",
                        "is_history": False,
                        "mask": [],
                    }
                ),
                SimpleNamespace(skeleton=SimpleNamespace()),
            )

    def test_text_weight_protocol_maps_exponent_and_rejects_out_of_range(self):
        self.assertEqual(bridge_server._resolve_cfg_text_weight({}), 2.0)
        self.assertEqual(bridge_server._resolve_cfg_text_weight({"text_weight": 0}), 1.0)
        self.assertEqual(bridge_server._resolve_cfg_text_weight({"text_weight": 4}), 16.0)
        for value in (-1, 4.1, float("nan")):
            with self.assertRaises(ValueError):
                bridge_server._resolve_cfg_text_weight({"text_weight": value})

    def test_core_and_g1_profiles_route_postprocess(self):
        core = quickserver_assets.resolve_motion_model_profile("ardy-core")
        core8 = quickserver_assets.resolve_motion_model_profile("ardy-core8")
        g1 = quickserver_assets.resolve_motion_model_profile("ardy-g1")
        g18 = quickserver_assets.resolve_motion_model_profile("ardy-g18")
        self.assertEqual((core.source_fps, core.horizon_frames, core.rig_profile), (20.0, 40, "cskel27"))
        self.assertEqual((core8.source_fps, core8.horizon_frames, core8.rig_profile), (20.0, 8, "cskel27"))
        self.assertEqual((g18.source_fps, g18.horizon_frames, g18.rig_profile), (25.0, 8, "g1skel34"))
        self.assertEqual({core.max_diffusion_steps, core8.max_diffusion_steps, g1.max_diffusion_steps, g18.max_diffusion_steps}, {10})
        self.assertEqual(core.max_context_frames, 200)
        self.assertEqual(ardy_backend.resolve_history_frame_limit(core), 160)
        self.assertTrue(core.postprocess)
        self.assertFalse(g1.postprocess)

        output = {
            "local_rot_mats": torch.eye(3).reshape(1, 1, 1, 3, 3),
            "root_positions": torch.zeros(1, 1, 3),
            "foot_contacts": torch.zeros(1, 1, 1),
            "posed_joints": torch.zeros(1, 1, 1, 3),
        }
        corrected = {
            "local_rot_mats": output["local_rot_mats"],
            "root_positions": output["root_positions"],
            "posed_joints": torch.ones(1, 1, 1, 3),
        }
        with patch("ardy.postprocess.post_process_motion", return_value=corrected) as postprocess:
            finalized = ardy_backend._finalize_output(
                output.copy(), SimpleNamespace(skeleton=object()), [object()], core.postprocess
            )
        postprocess.assert_called_once()
        self.assertIsInstance(finalized["posed_joints"], np.ndarray)
        np.testing.assert_allclose(finalized["posed_joints"], 1.0)

    def test_cancelled_request_does_not_publish_handle(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / f"cancel-{os.getpid()}"
        spool = animation_handles.AnimationHandleStore(byte_quota=1024 * 1024)
        profile = SimpleNamespace(
            model_name="ARDY-G1-RP-25FPS-Horizon52",
            source_fps=25.0,
            motion_rep_fingerprint="self-check",
            frames_per_token=4,
            horizon_frames=52,
            max_context_frames=248,
            max_diffusion_steps=10,
            cfg_text_weight=2.0,
            cfg_constraint_weight=2.0,
        )
        cancelled = threading.Event()
        cancelled.set()
        with self.assertRaises(bridge_server.GenerateCancelledError):
            ardy_backend.execute_generate(
                {"output_format": "flatbuf_motion_v1", "diffusion_steps": 1, "seed": 1},
                SimpleNamespace(),
                profile,
                cancelled,
                spool,
                archive,
            )
        self.assertEqual(spool._entries, {})

    def test_duplicate_conditioning_uses_later_value(self):
        from ardy.motion_rep.conditioning import get_unique_index_and_data

        indices, values = get_unique_index_and_data(
            torch.tensor([[0], [0], [1]]),
            torch.tensor([1.0, 2.0, 3.0]),
        )
        np.testing.assert_array_equal(indices.numpy(), [[0], [1]])
        np.testing.assert_allclose(values.numpy(), [2.0, 3.0])

    def test_root_body_pass_locks_generated_root_and_preserves_body_mask(self):
        model = _TwoPassModel()
        observed = torch.zeros(1, 4, 8)
        observed[..., 6] = 3
        mask = torch.zeros_like(observed)
        mask[..., 6] = 1
        result = ardy_backend._autoregressive_step(
            model,
            num_frames=4,
            num_denoising_steps=1,
            motion_mask=mask,
            observed_motion=observed,
            cfg_weight=(2.0, 2.0),
            texts=["test"],
            init_history_sequence=None,
            root_body_pass=True,
        )
        self.assertEqual(len(model.calls), 2)
        self.assertIsNone(model.calls[0][0])
        np.testing.assert_allclose(model.calls[1][1][..., :5].numpy(), 7)
        np.testing.assert_allclose(result[..., 6].numpy(), 3)
        np.testing.assert_array_equal(model.calls[1][0][0, 0].numpy(), [1, 1, 1, 1, 1, 0, 1, 0])

    def test_root_body_pass_only_locks_root_frames_produced_by_current_horizon(self):
        model = _TwoPassModel(first_pass_frames=4)
        observed = torch.ones(1, 8, 8)
        mask = torch.ones_like(observed)
        ardy_backend._autoregressive_step(
            model,
            num_frames=8,
            num_denoising_steps=1,
            motion_mask=mask,
            observed_motion=observed,
            cfg_weight=(2.0, 2.0),
            texts=["test"],
            init_history_sequence=None,
            root_body_pass=True,
        )
        np.testing.assert_allclose(model.calls[1][1][0, :4, :5].numpy(), 7)
        np.testing.assert_allclose(model.calls[1][1][0, 4:, :5].numpy(), 0)
        np.testing.assert_allclose(model.calls[1][0][0, :4, :5].numpy(), 1)
        np.testing.assert_allclose(model.calls[1][0][0, 4:, :5].numpy(), 0)

    def test_handle_and_file_round_trip_and_terminal_miss(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / str(os.getpid())
        file_root = archive / "files"
        file_root.mkdir(parents=True, exist_ok=True)

        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(
            name="ARDY-G1-RP-25FPS-Horizon52",
            fps=25.0,
            skeleton=skeleton,
            motion_rep=_MotionRep(skeleton),
            device="cpu",
        )
        profile = SimpleNamespace(
            model_name=model.name,
            source_fps=25.0,
            motion_rep_fingerprint="self-check",
            frames_per_token=4,
            horizon_frames=4,
            max_context_frames=16,
        )
        payload = _payload(model, 10.0)
        spool = animation_handles.AnimationHandleStore(byte_quota=1024 * 1024)
        handle = spool.publish(
            payload,
            motion_rep_fingerprint=profile.motion_rep_fingerprint,
        )["handle"]
        handle_json = json_text(
            {"type": "clip", "format": "kmb_handle_v1", "handle": handle}
        )
        history, _, _, _, history_len, _ = ardy_backend.prepare_generation_inputs(
            model, profile, handle_json, spool, archive
        )
        self.assertEqual(history_len, 4)
        np.testing.assert_allclose(history[0, :, 0].numpy(), [10, 11, 12, 13])

        file_path = file_root / "history.kmb"
        file_path.write_bytes(payload)
        file_json = json_text(
            {"type": "clip", "format": "ardy_file_v1", "path": str(file_path)}
        )
        with self.assertRaises(ardy_backend.ArdyBackendError):
            ardy_backend.prepare_generation_inputs(
                model, profile, file_json, spool, archive
            )
        with patch.dict(
            os.environ,
            {
                "KIMODO_ARDY_ALLOW_TEST_FILES": "1",
                "KIMODO_ARDY_TEST_FILE_ROOTS": str(file_root),
            },
        ):
            file_history, _, _, _, file_history_len, _ = ardy_backend.prepare_generation_inputs(
                model, profile, file_json, spool, archive
            )
        self.assertEqual(file_history_len, 4)
        np.testing.assert_allclose(file_history.numpy(), history.numpy())

        clip_items = []
        for root_x in (20.0, 30.0, 40.0, 50.0):
            clip_handle = spool.publish(
                _payload(model, root_x),
                motion_rep_fingerprint=profile.motion_rep_fingerprint,
            )["handle"]
            clip_items.append(
                {"type": "clip", "format": "kmb_handle_v1", "handle": clip_handle}
            )
        cropped, _, _, _, cropped_len, _ = ardy_backend.prepare_generation_inputs(
            model, profile, json_text(*clip_items), spool, archive
        )
        self.assertEqual(cropped_len, 12)
        np.testing.assert_allclose(
            cropped[0, :, 0].numpy(),
            [30, 31, 32, 33, 40, 41, 42, 43, 50, 51, 52, 53],
        )

        self.assertEqual(
            spool.read(
                handle,
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            ),
            payload,
        )

        with self.assertRaises(animation_handles.AnimationHandleNotFoundError) as caught:
            spool.read(
                "animation:missing",
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            )
        self.assertEqual(caught.exception.code, "animation_handle_not_found")

        with self.assertRaises(animation_handles.AnimationHandleNotFoundError):
            spool.read(
                handle,
                model_name=profile.model_name,
                fingerprint="wrong-fingerprint",
                fps=profile.source_fps,
            )

    def test_future_kmb_mask_uses_root_relative_positions_and_later_clip_overrides(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / f"future-{os.getpid()}"
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "Spine", "Hand"],
            joint_parents=torch.tensor([-1, 0, 1]),
        )
        model = SimpleNamespace(
            name="ardy-future-test",
            fps=20.0,
            skeleton=skeleton,
            motion_rep=_FutureMotionRep(skeleton),
            device="cpu",
        )
        profile = SimpleNamespace(
            model_name=model.name,
            source_fps=20.0,
            motion_rep_fingerprint="future-rep-v1",
            frames_per_token=4,
            horizon_frames=4,
            max_context_frames=12,
        )
        spool = animation_handles.AnimationHandleStore(byte_quota=1024 * 1024)
        first = spool.publish(_payload(model, 10.0), motion_rep_fingerprint=profile.motion_rep_fingerprint)["handle"]
        second = spool.publish(_payload(model, 20.0), motion_rep_fingerprint=profile.motion_rep_fingerprint)["handle"]
        constraints = json_text(
            {
                "type": "clip",
                "format": "kmb_handle_v1",
                "handle": first,
                "is_history": False,
                "start_frame": 0,
                "end_frame_exclusive": 4,
                "mask": [True] * 10,
            },
            {
                "type": "clip",
                "format": "kmb_handle_v1",
                "handle": second,
                "is_history": False,
                "start_frame": 0,
                "end_frame_exclusive": 4,
                "mask": [True, False, False, False, False, True, False, False, False, False],
            },
        )
        history, observed, mask, total, history_len, _ = ardy_backend.prepare_generation_inputs(
            model, profile, constraints, spool, archive
        )
        self.assertIsNone(history)
        self.assertEqual((total, history_len), (4, 0))
        np.testing.assert_allclose(observed[0, :, 0].numpy(), [20, 21, 22, 23])
        np.testing.assert_allclose(observed[0, :, 6].numpy(), 1.0)
        np.testing.assert_array_equal(
            mask[0, 0].numpy(),
            [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0],
        )

        missing_mask = json_text(
            {"type": "clip", "format": "kmb_handle_v1", "handle": first, "is_history": False}
        )
        with self.assertRaisesRegex(ardy_backend.ArdyBackendError, "Future clip mask"):
            ardy_backend.prepare_generation_inputs(model, profile, missing_mask, spool, archive)

    def test_kmb_preserves_foot_contacts(self):
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(name="foot-contact-test", fps=20.0, skeleton=skeleton)
        posed = np.zeros((1, 4, 2, 3), dtype=np.float32)
        rotations = np.broadcast_to(np.eye(3, dtype=np.float32), (1, 4, 2, 3, 3)).copy()
        contacts = np.asarray([[[1, 0, 0, 1], [1, 1, 0, 0], [0, 1, 1, 0], [0, 0, 1, 1]]], dtype=np.float32)
        payload = bridge_server._build_generate_flatbuffer_payload(
            model,
            {"posed_joints": posed, "local_rot_mats": rotations, "foot_contacts": contacts},
        )
        parsed = ardy_backend.parse_kmb1(payload)
        np.testing.assert_array_equal(parsed.foot_contacts, contacts[0])

    def test_kmb_maps_soma_foot_contacts_to_four_channels(self):
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(name="foot-contact-test", fps=20.0, skeleton=skeleton)
        contacts = np.asarray(
            [[1, 0, 9, 0, 1, 9], [0, 1, 9, 1, 0, 9], [1, 1, 9, 0, 0, 9], [0, 0, 9, 1, 1, 9]],
            dtype=np.float32,
        )
        parsed = ardy_backend.parse_kmb1(_payload(model, 0.0, contacts))
        np.testing.assert_array_equal(parsed.foot_contacts, contacts[:, [0, 1, 3, 4]])

    def test_force_setup_stops_active_quickserver(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            listener.bind(("127.0.0.1", 0))
            listener.listen(1)
            host, port = listener.getsockname()
            serverport = root / "serverport"
            serverport.write_text(f"host={host}\nport={port}\n", encoding="utf-8")
            received: list[bytes] = []

            def serve() -> None:
                with listener:
                    conn, _ = listener.accept()
                    with conn:
                        received.append(conn.recv(64))
                        conn.sendall(b'{"status":"bye"}\n')
                serverport.unlink()

            thread = threading.Thread(target=serve)
            thread.start()
            quickserver_setup._stop_active_quickserver(SimpleNamespace(root_dir=root), timeout_seconds=1)
            thread.join(timeout=1)
            self.assertFalse(thread.is_alive())
            self.assertEqual(received, [b'{"cmd":"quit"}\n'])

    def test_ardy_history_uses_stored_foot_contacts(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / f"contacts-{os.getpid()}"
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(
            name="ARDY-G1-RP-25FPS-Horizon52",
            fps=25.0,
            skeleton=skeleton,
            motion_rep=_ContactMotionRep(skeleton),
            device="cpu",
        )
        profile = SimpleNamespace(
            model_name=model.name,
            source_fps=25.0,
            motion_rep_fingerprint="self-check",
            frames_per_token=4,
            horizon_frames=4,
            max_context_frames=16,
        )
        contacts = np.asarray([[[1, 0, 0, 1], [1, 1, 0, 0], [0, 1, 1, 0], [0, 0, 1, 1]]], dtype=np.float32)
        spool = animation_handles.AnimationHandleStore(byte_quota=1024 * 1024)
        handle = spool.publish(
            _payload(model, 0.0, contacts),
            motion_rep_fingerprint=profile.motion_rep_fingerprint,
        )["handle"]
        history, _, _, _, history_len, _ = ardy_backend.prepare_generation_inputs(
            model,
            profile,
            json_text({"type": "clip", "format": "kmb_handle_v1", "handle": handle}),
            spool,
            archive,
        )
        self.assertEqual(history_len, 4)
        np.testing.assert_array_equal(history[0, :, 3:].numpy(), contacts[0])

    def test_lru_keeps_pinned_and_current_handles(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / f"lru-{os.getpid()}"
        skeleton = SimpleNamespace(
            bone_order_names=["Hips", "LeftFoot"],
            joint_parents=torch.tensor([-1, 0]),
        )
        model = SimpleNamespace(name="ARDY-G1-RP-25FPS-Horizon52", fps=25.0, skeleton=skeleton)
        spool = animation_handles.AnimationHandleStore(byte_quota=1024 * 1024)

        def publish(root_x):
            return spool.publish(
                _payload(model, root_x),
                motion_rep_fingerprint="self-check",
            )["handle"]

        first = publish(10.0)
        record_size = len(spool.read(first))
        spool.byte_quota = record_size * 2 + 32
        pinned = spool.pin([first])
        second = publish(20.0)
        third = publish(30.0)

        self.assertTrue(spool.info(first))
        self.assertTrue(spool.info(third))
        with self.assertRaises(animation_handles.AnimationHandleNotFoundError):
            spool.read(
                second,
                model_name=model.name,
                fingerprint="self-check",
                fps=model.fps,
            )
        spool.unpin(pinned)


def json_text(*items):
    import json

    return json.dumps(list(items))


if __name__ == "__main__":
    unittest.main()
