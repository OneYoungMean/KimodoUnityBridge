from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import os
import secrets
import threading
import time
from typing import Any, Callable, Iterable

import numpy as np


HANDLE_PREFIX = "animation:"
STREAM_HANDLE_PREFIX = "animation-stream:"
MAX_KMB_BYTES = 256 * 1024**2


class AnimationHandleError(ValueError):
    code = "animation_handle_error"


class AnimationHandleNotFoundError(AnimationHandleError):
    code = "animation_handle_not_found"


class AnimationHandleBusyError(AnimationHandleError):
    code = "animation_handle_busy"


@dataclass
class _Entry:
    payload: bytes
    info: dict[str, Any]
    last_access: float
    pins: int = 0
    release_pending: bool = False


class _StreamEntry:
    def __init__(
        self,
        *,
        handle: str,
        task_id: str,
        session_id: str,
        capacity_frames: int,
        horizon_frames: int,
        fps: float,
        model_name: str,
        joint_names: Iterable[str],
        joint_parents: Iterable[int],
        motion_rep_fingerprint: str,
        description: str,
        server_instance_id: str,
        serializer: Callable[[dict[str, np.ndarray]], bytes],
        cancel: Callable[[], None],
        resume: Callable[[], None],
    ):
        self.handle = handle
        self.task_id = str(task_id)
        self.session_id = str(session_id)
        self.capacity_frames = int(capacity_frames)
        self.horizon_frames = int(horizon_frames)
        self.fps = float(fps)
        self.model_name = str(model_name)
        self.joint_names = tuple(str(value) for value in joint_names)
        self.joint_parents = tuple(int(value) for value in joint_parents)
        self.motion_rep_fingerprint = str(motion_rep_fingerprint or "")
        self.description = str(description or "")
        self.server_instance_id = str(server_instance_id)
        self._serializer = serializer
        self._cancel = cancel
        self._resume = resume
        self._buffers: list[dict[str, np.ndarray] | None] = [None, None]
        self._counts = [0, 0]
        self._producer = 0
        self._read_in_progress = False
        self._closed = False
        self._lock = threading.RLock()
        self._created_utc = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        self._skeleton_id = hashlib.sha256(
            ("\0".join(self.joint_names) + "\0" + ",".join(map(str, self.joint_parents))).encode("utf-8")
        ).hexdigest()

    def info(self, *, num_frames: int | None = None, byte_length: int = 0) -> dict[str, Any]:
        with self._lock:
            available = self._counts[self._producer] if num_frames is None else int(num_frames)
            closed = self._closed
        return {
            "handle": self.handle,
            "format": "flatbuf_motion_v1",
            "description": self.description,
            "byte_length": int(byte_length),
            "num_frames": int(available),
            "duration_seconds": int(available) / self.fps,
            "fps": self.fps,
            "created_utc": self._created_utc,
            "model_name": self.model_name,
            "skeleton_id": self._skeleton_id,
            "joint_count": len(self.joint_names),
            "joint_names": list(self.joint_names),
            "motion_rep_fingerprint": self.motion_rep_fingerprint,
            "server_instance_id": self.server_instance_id,
            "sha256": "",
            "is_stream": True,
            "task_id": self.task_id,
            "session_id": self.session_id,
            "capacity_frames": self.capacity_frames,
            "horizon_frames": self.horizon_frames,
            "closed": closed,
        }

    def is_full(self) -> bool:
        with self._lock:
            return self._closed or self._counts[self._producer] + self.horizon_frames > self.capacity_frames

    def append(self, output: dict[str, Any]) -> bool:
        arrays = {
            "posed_joints": np.asarray(output["posed_joints"]),
            "local_rot_mats": np.asarray(output["local_rot_mats"]),
        }
        contacts = np.asarray(output.get("foot_contacts", []))
        if contacts.size:
            arrays["foot_contacts"] = contacts
        frame_count = int(arrays["posed_joints"].shape[1])
        if frame_count != self.horizon_frames:
            raise AnimationHandleError(
                f"Stream Horizon mismatch: expected {self.horizon_frames}, got {frame_count}."
            )

        with self._lock:
            if self._closed or self._counts[self._producer] + frame_count > self.capacity_frames:
                return False
            buffer = self._buffers[self._producer]
            if buffer is None:
                buffer = {
                    key: np.empty(
                        (value.shape[0], self.capacity_frames, *value.shape[2:]),
                        dtype=value.dtype,
                    )
                    for key, value in arrays.items()
                }
                self._buffers[self._producer] = buffer
            elif set(buffer) != set(arrays):
                raise AnimationHandleError("Stream output fields changed between Horizons.")
            offset = self._counts[self._producer]
            for key, value in arrays.items():
                buffer[key][:, offset : offset + frame_count] = value
            self._counts[self._producer] += frame_count
            return True

    def download(self) -> tuple[bytes, dict[str, Any]]:
        with self._lock:
            if self._closed:
                raise AnimationHandleNotFoundError(f"Animation stream is closed: {self.handle!r}.")
            if self._read_in_progress:
                raise AnimationHandleBusyError(f"Animation stream already has a read in progress: {self.handle!r}.")
            read_index = self._producer
            frame_count = self._counts[read_index]
            if frame_count == 0:
                return b"", self.info(num_frames=0)
            next_producer = 1 - read_index
            if self._counts[next_producer] != 0:
                raise AnimationHandleBusyError("Animation stream has no idle output buffer.")
            self._producer = next_producer
            self._counts[read_index] = 0
            self._read_in_progress = True
            buffer = self._buffers[read_index]

        self._resume()
        try:
            if buffer is None:
                raise AnimationHandleError("Animation stream buffer was not initialized.")
            snapshot = {key: value[:, :frame_count] for key, value in buffer.items()}
            payload = self._serializer(snapshot)
            return payload, self.info(num_frames=frame_count, byte_length=len(payload))
        finally:
            with self._lock:
                self._read_in_progress = False

    def close(self, *, notify: bool) -> bool:
        with self._lock:
            if self._closed:
                return False
            self._closed = True
            self._counts[0] = 0
            self._counts[1] = 0
        if notify:
            self._cancel()
        return True


class AnimationHandleStore:
    """Process-local KMB resource store with explicit release and LRU fallback."""

    def __init__(self, *, byte_quota: int, server_instance_id: str | None = None):
        self.byte_quota = max(1, int(byte_quota))
        self.server_instance_id = server_instance_id or secrets.token_hex(16)
        self._entries: dict[str, _Entry] = {}
        self._streams: dict[str, _StreamEntry] = {}
        self._lock = threading.RLock()

    def create_stream(
        self,
        *,
        task_id: str,
        session_id: str,
        capacity_frames: int,
        horizon_frames: int,
        fps: float,
        model_name: str,
        joint_names: Iterable[str],
        joint_parents: Iterable[int],
        motion_rep_fingerprint: str,
        description: str,
        serializer: Callable[[dict[str, np.ndarray]], bytes],
        cancel: Callable[[], None],
        resume: Callable[[], None],
    ) -> dict[str, Any]:
        if capacity_frames <= 0 or horizon_frames <= 0 or capacity_frames % horizon_frames:
            raise AnimationHandleError("Stream capacity must be a positive Horizon multiple.")
        handle = STREAM_HANDLE_PREFIX + secrets.token_urlsafe(24)
        stream = _StreamEntry(
            handle=handle,
            task_id=task_id,
            session_id=session_id,
            capacity_frames=capacity_frames,
            horizon_frames=horizon_frames,
            fps=fps,
            model_name=model_name,
            joint_names=joint_names,
            joint_parents=joint_parents,
            motion_rep_fingerprint=motion_rep_fingerprint,
            description=description,
            server_instance_id=self.server_instance_id,
            serializer=serializer,
            cancel=cancel,
            resume=resume,
        )
        with self._lock:
            self._streams[handle] = stream
        return stream.info()

    def append_stream(self, handle: str, output: dict[str, Any]) -> bool:
        return self._get_stream(handle).append(output)

    def stream_is_full(self, handle: str) -> bool:
        return self._get_stream(handle).is_full()

    def close_stream(self, handle: str, *, notify: bool = False) -> bool:
        normalized = str(handle or "").strip()
        with self._lock:
            stream = self._streams.pop(normalized, None)
        return stream.close(notify=notify) if stream is not None else False

    def publish(
        self,
        payload: bytes,
        *,
        description: str = "",
        motion_rep_fingerprint: str = "",
        **_compat: Any,
    ) -> dict[str, Any]:
        from kimodo.bridge.ardy_backend import parse_kmb1

        if not payload or len(payload) > MAX_KMB_BYTES:
            raise AnimationHandleError(f"KMB payload size must be in [1, {MAX_KMB_BYTES}] bytes.")
        try:
            motion = parse_kmb1(payload)
        except Exception as exc:
            raise AnimationHandleError(f"Invalid KMB upload: {exc}") from exc
        handle = HANDLE_PREFIX + secrets.token_urlsafe(24)
        now = time.time()
        with self._lock:
            created_utc = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
            skeleton_id = hashlib.sha256(
                ("\0".join(motion.joint_names) + "\0" + ",".join(map(str, motion.joint_parents))).encode("utf-8")
            ).hexdigest()
            info = {
                "handle": handle,
                "format": "flatbuf_motion_v1",
                "description": str(description or ""),
                "byte_length": len(payload),
                "num_frames": motion.num_frames,
                "duration_seconds": motion.num_frames / motion.fps,
                "fps": motion.fps,
                "created_utc": created_utc,
                "model_name": motion.model_name,
                "skeleton_id": skeleton_id,
                "joint_count": len(motion.joint_names),
                "joint_names": list(motion.joint_names),
                "motion_rep_fingerprint": str(motion_rep_fingerprint or ""),
                "server_instance_id": self.server_instance_id,
                "sha256": hashlib.sha256(payload).hexdigest(),
            }
            self._entries[handle] = _Entry(bytes(payload), info, now)
            self._collect_locked(protected={handle})
            if self._total_bytes_locked() > self.byte_quota:
                self._entries.pop(handle, None)
                raise AnimationHandleError("Animation handle store byte quota is exhausted by pinned resources.")
            return dict(info)

    def info(self, handle: str) -> dict[str, Any]:
        with self._lock:
            stream = self._streams.get(str(handle or "").strip())
            if stream is not None:
                return stream.info()
            entry = self._get_locked(handle)
            entry.last_access = time.time()
            return dict(entry.info)

    def read(self, handle: str, **_compat: Any) -> bytes:
        payload, _ = self.download(handle, **_compat)
        return payload

    def download(self, handle: str, **_compat: Any) -> tuple[bytes, dict[str, Any]]:
        normalized = str(handle or "").strip()
        with self._lock:
            stream = self._streams.get(normalized)
        if stream is not None:
            return stream.download()
        with self._lock:
            entry = self._get_locked(handle)
            expected_model = str(_compat.get("model_name") or "")
            expected_fingerprint = str(_compat.get("fingerprint") or "")
            expected_fps = _compat.get("fps")
            if expected_model and entry.info["model_name"] != expected_model:
                raise AnimationHandleNotFoundError(f"Animation handle is incompatible with model {expected_model!r}.")
            if expected_fingerprint and entry.info["motion_rep_fingerprint"] not in {"", expected_fingerprint}:
                raise AnimationHandleNotFoundError("Animation handle motion representation is incompatible.")
            if expected_fps is not None and abs(float(entry.info["fps"]) - float(expected_fps)) > 1e-5:
                raise AnimationHandleNotFoundError("Animation handle FPS is incompatible.")
            entry.last_access = time.time()
            return entry.payload, dict(entry.info)

    def pin(self, handles: Iterable[str]) -> tuple[str, ...]:
        normalized = tuple(dict.fromkeys(str(value).strip() for value in handles if str(value).strip()))
        with self._lock:
            entries = [self._get_locked(handle) for handle in normalized]
            for handle, entry in zip(normalized, entries):
                if entry.release_pending:
                    raise AnimationHandleNotFoundError(f"Animation handle is pending release: {handle!r}.")
            for entry in entries:
                entry.pins += 1
        return normalized

    def unpin(self, handles: Iterable[str]) -> None:
        with self._lock:
            for handle in handles:
                entry = self._entries.get(str(handle))
                if entry is None:
                    continue
                entry.pins = max(0, entry.pins - 1)
                if entry.pins == 0 and entry.release_pending:
                    self._entries.pop(str(handle), None)

    def release(self, handle: str) -> bool:
        normalized = str(handle or "").strip()
        with self._lock:
            stream = self._streams.pop(normalized, None)
        if stream is not None:
            return stream.close(notify=True)
        with self._lock:
            entry = self._entries.get(normalized)
            if entry is None:
                return False
            if entry.pins:
                entry.release_pending = True
            else:
                self._entries.pop(normalized, None)
            return True

    def _get_stream(self, handle: str) -> _StreamEntry:
        normalized = str(handle or "").strip()
        with self._lock:
            stream = self._streams.get(normalized)
        if stream is None:
            raise AnimationHandleNotFoundError(f"Animation stream is not available: {normalized!r}.")
        return stream

    def _get_locked(self, handle: str) -> _Entry:
        normalized = str(handle or "").strip()
        entry = self._entries.get(normalized)
        if entry is None:
            raise AnimationHandleNotFoundError(f"Animation handle is not available: {normalized!r}.")
        return entry

    def _total_bytes_locked(self) -> int:
        return sum(len(entry.payload) for entry in self._entries.values())

    def _collect_locked(self, protected: set[str]) -> None:
        total = self._total_bytes_locked()
        for handle, entry in sorted(self._entries.items(), key=lambda item: item[1].last_access):
            if total <= self.byte_quota:
                break
            if handle in protected or entry.pins:
                continue
            total -= len(entry.payload)
            self._entries.pop(handle, None)


def create_store() -> AnimationHandleStore:
    return AnimationHandleStore(
        byte_quota=int(os.environ.get("KIMODO_ANIMATION_HANDLE_BYTES", str(2 * 1024**3))),
    )
