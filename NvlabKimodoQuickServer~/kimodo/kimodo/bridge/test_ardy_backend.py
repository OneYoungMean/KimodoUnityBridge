from __future__ import annotations

import os
from pathlib import Path
from types import SimpleNamespace
import threading
import unittest
from unittest.mock import patch

import numpy as np
import torch

from kimodo.bridge import ardy_backend
from kimodo.bridge import bridge_server
from kimodo.bridge import quickserver_assets


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
    def test_core_and_g1_profiles_route_postprocess(self):
        core = quickserver_assets.resolve_motion_model_profile("ardy-core")
        g1 = quickserver_assets.resolve_motion_model_profile("ardy-g1")
        self.assertEqual((core.source_fps, core.horizon_frames, core.rig_profile), (20.0, 40, "cskel27"))
        self.assertEqual(core.max_context_frames, 200)
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
        spool = ardy_backend.ArdyClipSpool(archive, byte_quota=1024 * 1024, minimum_retention=0)
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
        self.assertEqual(list(archive.glob("*.kmb")), [])

    def test_duplicate_conditioning_uses_later_value(self):
        from ardy.motion_rep.conditioning import get_unique_index_and_data

        indices, values = get_unique_index_and_data(
            torch.tensor([[0], [0], [1]]),
            torch.tensor([1.0, 2.0, 3.0]),
        )
        np.testing.assert_array_equal(indices.numpy(), [[0], [1]])
        np.testing.assert_allclose(values.numpy(), [2.0, 3.0])

    def test_handle_and_file_round_trip_and_terminal_miss(self):
        archive = Path(__file__).resolve().parents[3] / "archive" / "test_artifacts" / str(os.getpid())
        spool_root = archive / "spool"
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
        spool = ardy_backend.ArdyClipSpool(spool_root, byte_quota=1024 * 1024, minimum_retention=1)
        handle = spool.publish(
            payload,
            model_name=profile.model_name,
            fingerprint=profile.motion_rep_fingerprint,
            fps=profile.source_fps,
        )
        handle_json = json_text(
            {"type": "clip", "format": "ardy_handle_v1", "handle": handle}
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
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            )
            clip_items.append(
                {"type": "clip", "format": "ardy_handle_v1", "handle": clip_handle}
            )
        cropped, _, _, _, cropped_len, _ = ardy_backend.prepare_generation_inputs(
            model, profile, json_text(*clip_items), spool, archive
        )
        self.assertEqual(cropped_len, 12)
        np.testing.assert_allclose(
            cropped[0, :, 0].numpy(),
            [30, 31, 32, 33, 40, 41, 42, 43, 50, 51, 52, 53],
        )

        kmb_path, _ = spool._paths(handle)
        corrupted = bytearray(payload)
        corrupted[-1] ^= 1
        kmb_path.write_bytes(corrupted)
        with self.assertRaises(ardy_backend.ClipHandleNotFoundError):
            spool.read(
                handle,
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            )
        self.assertEqual(
            spool.publish(
                payload,
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            ),
            handle,
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

        with self.assertRaises(ardy_backend.ClipHandleNotFoundError) as caught:
            spool.read(
                "ardy:sha256:" + "0" * 64,
                model_name=profile.model_name,
                fingerprint=profile.motion_rep_fingerprint,
                fps=profile.source_fps,
            )
        self.assertEqual(caught.exception.code, "clip_handle_not_found")

        with self.assertRaises(ardy_backend.ClipHandleNotFoundError):
            spool.read(
                handle,
                model_name=profile.model_name,
                fingerprint="wrong-fingerprint",
                fps=profile.source_fps,
            )

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
        spool = ardy_backend.ArdyClipSpool(archive, byte_quota=1024 * 1024, minimum_retention=0)
        handle = spool.publish(
            _payload(model, 0.0, contacts),
            model_name=profile.model_name,
            fingerprint=profile.motion_rep_fingerprint,
            fps=profile.source_fps,
        )
        history, _, _, _, history_len, _ = ardy_backend.prepare_generation_inputs(
            model,
            profile,
            json_text({"type": "clip", "format": "ardy_handle_v1", "handle": handle}),
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
        spool = ardy_backend.ArdyClipSpool(
            archive,
            byte_quota=1024 * 1024,
            minimum_retention=0,
        )

        def publish(root_x):
            return spool.publish(
                _payload(model, root_x),
                model_name=model.name,
                fingerprint="self-check",
                fps=model.fps,
            )

        first = publish(10.0)
        first_paths = spool._paths(first)
        record_size = sum(path.stat().st_size for path in first_paths)
        spool.byte_quota = record_size * 2 + 32
        pinned = spool.pin([first])
        second = publish(20.0)
        third = publish(30.0)

        self.assertTrue(all(path.is_file() for path in spool._paths(first)))
        self.assertTrue(all(path.is_file() for path in spool._paths(third)))
        with self.assertRaises(ardy_backend.ClipHandleNotFoundError):
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
