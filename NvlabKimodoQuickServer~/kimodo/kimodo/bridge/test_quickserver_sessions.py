from __future__ import annotations

import json
from pathlib import Path
import socket
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np

from kimodo.bridge import bridge_server
from kimodo.bridge import animation_handles
from kimodo.bridge import quickserver_cli


class _Client:
    def __init__(self, port: int):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=5)
        self.socket.settimeout(10)
        self.file = self.socket.makefile("rwb")
        self.messages: list[tuple[dict, bytes]] = []

    def close(self) -> None:
        try:
            self.file.close()
        finally:
            self.socket.close()

    def send(self, request: dict, binary: bytes = b"") -> None:
        self.file.write(json.dumps(request, separators=(",", ":")).encode() + b"\n" + binary)
        self.file.flush()

    def receive(self) -> tuple[dict, bytes]:
        line = self.file.readline()
        if not line:
            raise EOFError("QuickServer closed the TCP connection.")
        header = json.loads(line)
        byte_length = int(header.get("byte_length") or 0)
        return header, self.file.read(byte_length) if byte_length else b""

    def request(self, request_id: str, cmd: str, **values) -> tuple[dict, bytes]:
        self.send({"cmd": cmd, "request_id": request_id, **values})
        while True:
            header, binary = self.receive()
            if header.get("request_id") == request_id and header.get("status") not in {
                "queued",
                "loading",
                "progress",
            }:
                return header, binary
            self.messages.append((header, binary))

    def wait_for(self, predicate) -> tuple[dict, bytes]:
        for index, item in enumerate(self.messages):
            if predicate(item[0]):
                return self.messages.pop(index)
        while True:
            item = self.receive()
            if predicate(item[0]):
                return item
            self.messages.append(item)


class _FakeArdyStream:
    def __init__(self, request: dict, model, profile, *_args):
        self.request = request
        self.model = model
        self.profile = profile
        self.prompt = str(request.get("prompt") or "test")
        self.resolved_seed = int(request.get("seed") or 1)
        self.closed = False

    def generate_horizon(self):
        time.sleep(0.03)
        frames = int(self.profile.horizon_frames)
        joints = len(self.model.motion_rep.skeleton.bone_order_names)
        return {
            "posed_joints": np.zeros((1, frames, joints, 3), dtype=np.float32),
            "local_rot_mats": np.broadcast_to(
                np.eye(3, dtype=np.float32),
                (1, frames, joints, 3, 3),
            ).copy(),
            "foot_contacts": np.zeros((1, frames, 4), dtype=np.float32),
        }

    def close(self) -> None:
        self.closed = True


class _SupervisorHarness:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.slow_kimodo_gate = threading.Event()
        self.store = animation_handles.AnimationHandleStore(byte_quota=64 * 1024 * 1024)
        self.thread = None
        self.port = 0
        self._patches = []

    def start(self) -> None:
        def ensure_runtime(runtime: dict, config: dict, *_args):
            model_name = str(config["model"])
            skeleton = SimpleNamespace(
                bone_order_names=["Root", "Spine"],
                joint_parents=[-1, 0],
            )
            model = SimpleNamespace(
                name=model_name,
                fps=20.0,
                skeleton=skeleton,
                motion_rep=SimpleNamespace(skeleton=skeleton),
                device="cpu",
            )
            profile = None
            if model_name.startswith("ardy"):
                profile = SimpleNamespace(
                    backend="ardy",
                    model_name=model_name,
                    source_fps=20.0,
                    horizon_frames=4,
                    frames_per_token=2,
                    max_context_frames=12,
                    max_diffusion_steps=10,
                    cfg_constraint_weight=2.0,
                    motion_rep_fingerprint="test-rep-v1",
                    postprocess=False,
                )
            runtime.update(
                model=model,
                motion_profile=profile,
                text_encoder_decision=None,
                runtime_signature=model_name,
                resolved_model_name=model_name,
                runtime_device="cpu",
                fps=20,
                runtime_config=dict(config),
            )
            return {"model": model_name, "device": "cpu", "fps": 20, "reused": False}

        def execute_generate(request, model, cancel_event, *_args):
            if model.name == "kimodo-slow":
                self.slow_kimodo_gate.wait(timeout=5)
            if cancel_event.is_set():
                raise bridge_server.GenerateCancelledError("Generation canceled.")
            return {
                "status": "done",
                "output_format": "json_compact",
                "motion_json_compact": "{}",
            }, None

        self._patches = [
            patch.object(quickserver_cli, "_ensure_runtime", ensure_runtime),
            patch.object(quickserver_cli, "_execute_generate", execute_generate),
            patch.object(quickserver_cli.ardy_backend, "ArdyStreamGenerator", _FakeArdyStream),
            patch.object(quickserver_cli.animation_handles, "create_store", return_value=self.store),
            patch.dict("os.environ", {"KIMODO_SESSION_QUEUE_LIMIT": "2", "KIMODO_IDLE_TIMEOUT_SEC": "0"}),
        ]
        for item in self._patches:
            item.start()
        args = SimpleNamespace(
            watchpid=0,
            model="kimodo-test",
            text_encoder_mode="high_precision",
            models_root="",
            force_hf_download=False,
            device="cpu",
        )
        logger = SimpleNamespace(log=lambda _message: None)
        self.thread = threading.Thread(
            target=quickserver_cli._run_supervisor,
            args=(args, str(self.root), logger),
            daemon=True,
        )
        self.thread.start()
        serverport = self.root / "serverport"
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if serverport.exists():
                values = serverport.read_text(encoding="utf-8").splitlines()
                ports = [line for line in values if line.startswith("port=")]
                if ports:
                    self.port = int(ports[0].split("=", 1)[1])
                    return
            time.sleep(0.01)
        raise TimeoutError("QuickServer test supervisor did not start.")

    def stop(self) -> None:
        self.slow_kimodo_gate.set()
        if self.port:
            try:
                client = _Client(self.port)
                client.request("shutdown", "session.close")
                client.close()
            except Exception:
                pass
        if self.thread is not None:
            self.thread.join(timeout=5)
        for item in reversed(self._patches):
            item.stop()
        self.temp.cleanup()


class QuickServerSessionTests(unittest.TestCase):
    def setUp(self):
        self.server = _SupervisorHarness()
        self.server.start()
        self.clients: list[_Client] = []

    def tearDown(self):
        for client in self.clients:
            try:
                client.close()
            except Exception:
                pass
        self.server.stop()

    def client(self, *, explicit: bool = True) -> _Client:
        client = _Client(self.server.port)
        self.clients.append(client)
        if explicit:
            header, _ = client.request("open", "session.open")
            self.assertTrue(header["session_id"].startswith("session:"))
        return client

    def generate_stream(self, client: _Client, request_id: str, task_id: str, duration: float = 0.2) -> dict:
        header, _ = client.request(
            request_id,
            "generate",
            task_id=task_id,
            model="ardy-test",
            prompt="T pose",
            duration=duration,
            diffusion_steps=1,
            output_format="kmb_handle_v1",
            seed=1,
        )
        self.assertEqual(header["status"], "done")
        self.assertTrue(header["handle_info"]["is_stream"])
        return header

    def test_cancel_emits_task_closed_before_next_fifo_task_runs(self):
        client = self.client()
        first = self.generate_stream(client, "generate-1", "task-1")
        client.send({
            "cmd": "generate",
            "request_id": "generate-2",
            "task_id": "task-2",
            "model": "ardy-test",
            "duration": 0.2,
            "output_format": "kmb_handle_v1",
            "diffusion_steps": 1,
        })
        cancel, _ = client.request("cancel", "cancel", task_id="task-1")
        self.assertEqual(cancel["status"], "cancelling")
        ordered = []
        while len(ordered) < 2:
            header, binary = client.receive()
            if header.get("event") == "task.closed" or (
                header.get("request_id") == "generate-2" and header.get("status") == "done"
            ):
                ordered.append((header, binary))
            else:
                client.messages.append((header, binary))
        closed, _ = ordered[0]
        second, _ = ordered[1]
        self.assertEqual(closed["event"], "task.closed")
        self.assertNotIn("request_id", closed)
        self.assertEqual(
            (closed["task_id"], closed["task_status"], closed["handle"]),
            ("task-1", "cancelled", first["handle_info"]["handle"]),
        )
        self.assertEqual(second["task_id"], "task-2")

    def test_session_queue_limit_counts_active_and_queued_tasks(self):
        client = self.client()
        self.generate_stream(client, "generate-1", "task-1")
        client.send({
            "cmd": "generate",
            "request_id": "generate-2",
            "task_id": "task-2",
            "model": "ardy-test",
            "duration": 0.2,
            "output_format": "kmb_handle_v1",
        })
        rejected, _ = client.request(
            "generate-3",
            "generate",
            task_id="task-3",
            model="ardy-test",
            duration=0.2,
            output_format="kmb_handle_v1",
        )
        self.assertEqual(rejected["error_code"], "session_queue_full")

    def test_two_sessions_both_produce_and_static_handle_is_global(self):
        left = self.client()
        right = self.client()
        left_stream = self.generate_stream(left, "left-generate", "left-task", duration=0.4)
        right_stream = self.generate_stream(right, "right-generate", "right-task", duration=0.4)
        left_download, left_bytes = left.request(
            "left-download", "animation.download", handle=left_stream["handle_info"]["handle"]
        )
        while not left_bytes:
            left_download, left_bytes = left.request(
                "left-download-next", "animation.download", handle=left_stream["handle_info"]["handle"]
            )
        right_download, right_bytes = right.request(
            "right-download", "animation.download", handle=right_stream["handle_info"]["handle"]
        )
        while not right_bytes:
            right_download, right_bytes = right.request(
                "right-download-next", "animation.download", handle=right_stream["handle_info"]["handle"]
            )
        self.assertGreater(left_download["handle_info"]["num_frames"], 0)
        self.assertGreater(right_download["handle_info"]["num_frames"], 0)

        model = SimpleNamespace(
            name="static-test",
            fps=20.0,
            skeleton=SimpleNamespace(bone_order_names=["Root", "Spine"], joint_parents=[-1, 0]),
        )
        payload = bridge_server._build_generate_flatbuffer_payload(
            model,
            {
                "posed_joints": np.zeros((1, 2, 2, 3), dtype=np.float32),
                "local_rot_mats": np.broadcast_to(
                    np.eye(3, dtype=np.float32), (1, 2, 2, 3, 3)
                ).copy(),
            },
        )
        left.send({
            "cmd": "animation.upload",
            "request_id": "upload",
            "format": "flatbuf_motion_v1",
            "byte_length": len(payload),
        }, payload)
        uploaded, _ = left.wait_for(lambda header: header.get("request_id") == "upload")
        downloaded, downloaded_bytes = right.request(
            "cross-session-download",
            "animation.download",
            handle=uploaded["handle_info"]["handle"],
        )
        self.assertEqual(downloaded["status"], "done")
        self.assertEqual(downloaded_bytes, payload)

    def test_explicit_disconnect_reclaims_stream_and_other_session_survives(self):
        disconnected = self.client()
        stream = self.generate_stream(disconnected, "generate", "orphan-task")
        disconnected.close()
        self.clients.remove(disconnected)
        survivor = self.client()
        deadline = time.monotonic() + 2
        handle = stream["handle_info"]["handle"]
        while time.monotonic() < deadline:
            if handle not in self.server.store._streams:
                break
            time.sleep(0.02)
        self.assertNotIn(handle, self.server.store._streams)
        self.generate_stream(survivor, "survivor-generate", "survivor-task")

    def test_kimodo_one_shot_blocks_ardy_until_atomic_task_finishes(self):
        kimodo = self.client()
        ardy = self.client()
        kimodo_result = {}
        ardy_result = {}

        def run_kimodo():
            kimodo_result["value"] = kimodo.request(
                "kimodo-generate",
                "generate",
                task_id="kimodo-task",
                model="kimodo-slow",
                duration=1,
                output_format="json_compact",
            )

        def run_ardy():
            ardy_result["value"] = self.generate_stream(ardy, "ardy-generate", "ardy-task")

        kimodo_thread = threading.Thread(target=run_kimodo)
        ardy_thread = threading.Thread(target=run_ardy)
        kimodo_thread.start()
        time.sleep(0.05)
        ardy_thread.start()
        time.sleep(0.1)
        self.assertNotIn("value", ardy_result)
        self.server.slow_kimodo_gate.set()
        kimodo_thread.join(timeout=2)
        ardy_thread.join(timeout=2)
        self.assertEqual(kimodo_result["value"][0]["status"], "done")
        self.assertEqual(ardy_result["value"]["status"], "done")


if __name__ == "__main__":
    unittest.main()
