from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import tempfile
import threading
import time
from typing import Any, Iterable

import numpy as np


HANDLE_PREFIX = "ardy:sha256:"
MAX_KMB_BYTES = 256 * 1024**2


class ArdyBackendError(ValueError):
    code = "ardy_backend_error"


class ClipHandleNotFoundError(ArdyBackendError):
    code = "clip_handle_not_found"


@dataclass(frozen=True)
class KmbMotion:
    payload: bytes
    model_name: str
    fps: float
    joint_names: tuple[str, ...]
    joint_parents: tuple[int, ...]
    root_positions: np.ndarray
    local_rot_quats: np.ndarray
    foot_contacts: np.ndarray | None

    @property
    def num_frames(self) -> int:
        return int(self.root_positions.shape[0])


def parse_kmb1(payload: bytes) -> KmbMotion:
    from kimodo.bridge.protocol.generated import MotionPacket

    if len(payload or b"") > MAX_KMB_BYTES:
        raise ArdyBackendError(f"KMB1 payload exceeds the {MAX_KMB_BYTES}-byte limit.")
    if not payload or not MotionPacket.MotionPacket.MotionPacketBufferHasIdentifier(payload, 0):
        raise ArdyBackendError("Clip is not a flatbuf_motion_v1 / KMB1 MotionPacket.")
    packet = MotionPacket.MotionPacket.GetRootAs(payload, 0)
    version = int(packet.Version())
    num_frames = int(packet.NumFrames())
    num_joints = int(packet.NumJoints())
    if version != 1 or num_frames <= 0 or num_joints <= 0:
        raise ArdyBackendError(
            f"Invalid KMB1 header: version={version}, frames={num_frames}, joints={num_joints}."
        )

    joint_names = tuple(
        (packet.JointNames(i) or b"").decode("utf-8", errors="strict")
        for i in range(packet.JointNamesLength())
    )
    joint_parents = tuple(int(packet.JointParents(i)) for i in range(packet.JointParentsLength()))
    root_positions = np.asarray(packet.RootPositionsAsNumpy(), dtype=np.float32).copy()
    local_rot_quats = np.asarray(packet.LocalRotQuatsAsNumpy(), dtype=np.float32).copy()
    foot_contacts = None
    if packet.FootContactsLength():
        raw_contacts = np.asarray(packet.FootContactsAsNumpy(), dtype=np.uint8).copy()
        expected_contacts = num_frames * 4
        if raw_contacts.size != expected_contacts:
            raise ArdyBackendError(
                f"KMB1 foot_contacts length mismatch: contacts={raw_contacts.size}/{expected_contacts}."
            )
        foot_contacts = raw_contacts.reshape(num_frames, 4).astype(np.float32)
    expected_roots = num_frames * 3
    expected_quats = num_frames * num_joints * 4
    if len(joint_names) != num_joints or len(joint_parents) != num_joints:
        raise ArdyBackendError("KMB1 joint metadata length does not match num_joints.")
    if root_positions.size != expected_roots or local_rot_quats.size != expected_quats:
        raise ArdyBackendError(
            "KMB1 vector length mismatch: "
            f"roots={root_positions.size}/{expected_roots}, quats={local_rot_quats.size}/{expected_quats}."
        )
    if not np.isfinite(root_positions).all() or not np.isfinite(local_rot_quats).all():
        raise ArdyBackendError("KMB1 contains non-finite motion values.")
    model_name_raw = packet.ModelName() or b""
    return KmbMotion(
        payload=bytes(payload),
        model_name=model_name_raw.decode("utf-8", errors="strict"),
        fps=float(packet.Fps()),
        joint_names=joint_names,
        joint_parents=joint_parents,
        root_positions=root_positions.reshape(num_frames, 3),
        local_rot_quats=local_rot_quats.reshape(num_frames, num_joints, 4),
        foot_contacts=foot_contacts,
    )


def extract_handle_refs(constraints_json: Any) -> tuple[str, ...]:
    try:
        value = json.loads(constraints_json) if isinstance(constraints_json, str) else constraints_json
    except Exception:
        return ()
    if isinstance(value, dict):
        value = [value]
    if not isinstance(value, list):
        return ()
    return tuple(
        str(item.get("handle") or "").strip()
        for item in value
        if isinstance(item, dict)
        and item.get("type") == "clip"
        and item.get("format") == "ardy_handle_v1"
        and str(item.get("handle") or "").strip()
    )


class ArdyClipSpool:
    def __init__(self, root: str | Path, *, byte_quota: int, minimum_retention: int = 8):
        self.root = Path(root).resolve()
        self.archive = self.root / "archive"
        self.root.mkdir(parents=True, exist_ok=True)
        self.archive.mkdir(parents=True, exist_ok=True)
        self.byte_quota = max(1, int(byte_quota))
        self.minimum_retention = max(0, int(minimum_retention))
        self._pins: dict[str, int] = {}
        self._lock = threading.RLock()
        self._archive_orphans()

    def pin(self, handles: Iterable[str]) -> tuple[str, ...]:
        normalized = tuple(dict.fromkeys(str(handle).strip() for handle in handles if str(handle).strip()))
        with self._lock:
            for handle in normalized:
                self._pins[handle] = self._pins.get(handle, 0) + 1
        return normalized

    def unpin(self, handles: Iterable[str]) -> None:
        with self._lock:
            for handle in handles:
                count = self._pins.get(handle, 0)
                if count <= 1:
                    self._pins.pop(handle, None)
                else:
                    self._pins[handle] = count - 1

    def publish(self, payload: bytes, *, model_name: str, fingerprint: str, fps: float) -> str:
        digest = hashlib.sha256(fingerprint.encode("utf-8") + b"\0" + payload).hexdigest()
        handle = HANDLE_PREFIX + digest
        kmb_path, meta_path = self._paths(handle)
        with self._lock:
            if kmb_path.is_file() and meta_path.is_file():
                try:
                    self.read(
                        handle,
                        model_name=model_name,
                        fingerprint=fingerprint,
                        fps=fps,
                    )
                    return handle
                except ClipHandleNotFoundError:
                    self._archive_file(kmb_path, "corrupted")
                    self._archive_file(meta_path, "corrupted")

            record = {
                "handle": handle,
                "model_name": model_name,
                "motion_rep_fingerprint": fingerprint,
                "fps": float(fps),
                "byte_length": len(payload),
                "created_unix": time.time(),
            }
            self._atomic_write(kmb_path, payload)
            try:
                self._atomic_write(meta_path, json.dumps(record, separators=(",", ":")).encode("utf-8"))
            except Exception:
                self._archive_file(kmb_path, "unpublished")
                raise
            self._evict(protected={handle})
        return handle

    def read(self, handle: str, *, model_name: str, fingerprint: str, fps: float) -> bytes:
        with self._lock:
            try:
                kmb_path, meta_path = self._paths(handle)
            except ValueError as exc:
                raise ClipHandleNotFoundError(f"Unknown or invalid clip handle: {handle!r}.") from exc
            if not kmb_path.is_file() or not meta_path.is_file():
                raise ClipHandleNotFoundError(f"Clip handle is not available: {handle!r}.")
            try:
                record = json.loads(meta_path.read_text(encoding="utf-8"))
            except Exception as exc:
                raise ClipHandleNotFoundError(f"Clip handle metadata is unreadable: {handle!r}.") from exc
            if (
                record.get("handle") != handle
                or record.get("model_name") != model_name
                or record.get("motion_rep_fingerprint") != fingerprint
                or not math.isclose(float(record.get("fps", -1)), float(fps), rel_tol=0.0, abs_tol=1e-5)
            ):
                raise ClipHandleNotFoundError(f"Clip handle is incompatible with model {model_name!r}: {handle!r}.")
            payload = kmb_path.read_bytes()
            if len(payload) != int(record.get("byte_length", -1)):
                raise ClipHandleNotFoundError(f"Clip handle payload is incomplete: {handle!r}.")
            actual_handle = HANDLE_PREFIX + hashlib.sha256(
                fingerprint.encode("utf-8") + b"\0" + payload
            ).hexdigest()
            if actual_handle != handle:
                raise ClipHandleNotFoundError(f"Clip handle payload is corrupted: {handle!r}.")
            os.utime(kmb_path, None)
            return payload

    def _paths(self, handle: str) -> tuple[Path, Path]:
        if not handle.startswith(HANDLE_PREFIX):
            raise ValueError("invalid handle prefix")
        digest = handle[len(HANDLE_PREFIX) :]
        if len(digest) != 64 or any(ch not in "0123456789abcdef" for ch in digest):
            raise ValueError("invalid handle digest")
        return self.root / f"{digest}.kmb", self.root / f"{digest}.json"

    def _atomic_write(self, destination: Path, data: bytes) -> None:
        fd, temp_name = tempfile.mkstemp(prefix=f".{destination.name}.", suffix=".tmp", dir=self.root)
        temp_path = Path(temp_name)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp_path, destination)
        except Exception:
            try:
                os.close(fd)
            except OSError:
                pass
            if temp_path.exists():
                self._archive_file(temp_path, "incomplete")
            raise

    def _archive_file(self, path: Path, reason: str) -> None:
        if not path.exists():
            return
        destination = self.archive / f"{path.name}.{reason}.{time.time_ns()}"
        os.replace(path, destination)

    def _archive_orphans(self) -> None:
        for kmb_path in self.root.glob("*.kmb"):
            if not kmb_path.with_suffix(".json").is_file():
                self._archive_file(kmb_path, "orphan")
        for meta_path in self.root.glob("*.json"):
            if not meta_path.with_suffix(".kmb").is_file():
                self._archive_file(meta_path, "orphan")

    def _evict(self, *, protected: set[str]) -> None:
        records: list[tuple[float, int, str, Path, Path]] = []
        for kmb_path in self.root.glob("*.kmb"):
            meta_path = kmb_path.with_suffix(".json")
            if not meta_path.is_file():
                continue
            digest = kmb_path.stem
            handle = HANDLE_PREFIX + digest
            stat = kmb_path.stat()
            records.append((stat.st_mtime, stat.st_size + meta_path.stat().st_size, handle, kmb_path, meta_path))
        total = sum(item[1] for item in records)
        if total <= self.byte_quota:
            return
        keep = {item[2] for item in sorted(records, reverse=True)[: self.minimum_retention]}
        pinned = set(self._pins) | protected | keep
        for _, size, handle, kmb_path, meta_path in sorted(records):
            if total <= self.byte_quota:
                break
            if handle in pinned:
                continue
            self._archive_file(kmb_path, "evicted")
            self._archive_file(meta_path, "evicted")
            total -= size


def create_spool(quickserver_root: str | Path) -> ArdyClipSpool:
    root = Path(
        os.environ.get("KIMODO_ARDY_SPOOL_ROOT")
        or (Path(quickserver_root).resolve() / "cache" / "ardy_spool")
    )
    quota = int(os.environ.get("KIMODO_ARDY_SPOOL_BYTES", str(2 * 1024**3)))
    minimum = int(os.environ.get("KIMODO_ARDY_MIN_RETAINED_HANDLES", "8"))
    return ArdyClipSpool(root, byte_quota=quota, minimum_retention=minimum)


def _allowed_file_roots(quickserver_root: str | Path) -> tuple[Path, ...]:
    roots = [Path(quickserver_root).resolve() / "cache" / "ardy_files"]
    roots.extend(
        Path(value).expanduser().resolve()
        for value in os.environ.get("KIMODO_ARDY_FILE_ROOTS", "").split(os.pathsep)
        if value.strip()
    )
    if os.environ.get("KIMODO_ARDY_ALLOW_TEST_FILES", "").strip().lower() in {"1", "true", "yes"}:
        roots.extend(
            Path(value).expanduser().resolve()
            for value in os.environ.get("KIMODO_ARDY_TEST_FILE_ROOTS", "").split(os.pathsep)
            if value.strip()
        )
    return tuple(dict.fromkeys(roots))


def _read_managed_file(path_value: Any, quickserver_root: str | Path) -> bytes:
    path = Path(str(path_value or "")).expanduser().resolve(strict=True)
    if not path.is_file() or not any(path.is_relative_to(root) for root in _allowed_file_roots(quickserver_root)):
        raise ArdyBackendError(f"ardy_file_v1 path is outside the configured managed cache roots: {path}.")
    return path.read_bytes()


def _validate_kmb(motion: KmbMotion, model: Any, profile: Any) -> None:
    skeleton = model.motion_rep.skeleton
    expected_names = tuple(str(name) for name in skeleton.bone_order_names)
    expected_parents = tuple(int(value) for value in skeleton.joint_parents.detach().cpu().tolist())
    if motion.model_name != profile.model_name:
        raise ArdyBackendError(
            f"KMB1 model mismatch: expected {profile.model_name!r}, got {motion.model_name!r}."
        )
    if not math.isclose(motion.fps, profile.source_fps, rel_tol=0.0, abs_tol=1e-5):
        raise ArdyBackendError(f"KMB1 FPS mismatch: expected {profile.source_fps}, got {motion.fps}.")
    if motion.joint_names != expected_names or motion.joint_parents != expected_parents:
        raise ArdyBackendError("KMB1 rig does not match the registered ARDY skeleton.")


def _parse_constraints(constraints_json: Any) -> list[dict[str, Any]]:
    if not constraints_json:
        return []
    try:
        value = json.loads(constraints_json) if isinstance(constraints_json, str) else constraints_json
    except Exception as exc:
        raise ArdyBackendError(f"Invalid inline constraints_json payload: {exc}") from exc
    if isinstance(value, dict):
        value = [value]
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise ArdyBackendError("constraints_json must be a JSON array/object.")
    return [dict(item) for item in value]


def _future_frame_indices(constraints: list[dict[str, Any]]) -> list[int]:
    result: list[int] = []
    for item in constraints:
        if item.get("type") == "clip":
            continue
        indices = item.get("frame_indices", [])
        if not isinstance(indices, list):
            raise ArdyBackendError("Constraint frame_indices must be an array.")
        for value in indices:
            if isinstance(value, bool) or int(value) != value or int(value) < 0:
                raise ArdyBackendError(f"Constraint frame index must be a non-negative integer; got {value!r}.")
            result.append(int(value))
    return result


def _clip_payload(item: dict[str, Any], spool: ArdyClipSpool, profile: Any, quickserver_root: str | Path) -> bytes:
    clip_format = str(item.get("format") or "")
    if clip_format == "ardy_handle_v1":
        return spool.read(
            str(item.get("handle") or ""),
            model_name=profile.model_name,
            fingerprint=profile.motion_rep_fingerprint,
            fps=profile.source_fps,
        )
    if clip_format == "ardy_file_v1":
        return _read_managed_file(item.get("path"), quickserver_root)
    raise ArdyBackendError(f"Unsupported clip format: {clip_format!r}.")


def _normalize_root_heading(item: dict[str, Any]) -> None:
    if item.get("type") != "root2d" or "global_root_heading" not in item:
        return
    headings = item["global_root_heading"]
    if not isinstance(headings, list):
        raise ArdyBackendError("global_root_heading must be an array.")
    converted = []
    for value in headings:
        if isinstance(value, (list, tuple)) and len(value) == 2:
            converted.append(math.atan2(float(value[1]), float(value[0])))
        else:
            converted.append(float(value))
    item["global_root_heading"] = converted


def prepare_generation_inputs(
    model: Any,
    profile: Any,
    constraints_json: Any,
    spool: ArdyClipSpool,
    quickserver_root: str | Path,
):
    import torch
    from ardy.constraints import load_constraints_lst
    from ardy.geometry import quaternion_to_matrix

    items = _parse_constraints(constraints_json)
    future_items = [item for item in items if item.get("type") != "clip"]
    future_indices = _future_frame_indices(future_items)
    patch = int(profile.frames_per_token)
    future_frames = max(int(profile.horizon_frames), max(future_indices, default=-1) + 1)
    future_frames = int(math.ceil(future_frames / patch) * patch)
    max_history = ((int(profile.max_context_frames) - future_frames) // patch) * patch
    if max_history < 0:
        raise ArdyBackendError("Future constraints exceed the registered ARDY context window.")

    roots: list[np.ndarray] = []
    quats: list[np.ndarray] = []
    contacts: list[np.ndarray] = []
    has_stored_contacts = True
    for item in items:
        if item.get("type") != "clip":
            continue
        motion = parse_kmb1(_clip_payload(item, spool, profile, quickserver_root))
        _validate_kmb(motion, model, profile)
        start_value = item.get("start_frame", 0)
        end_value = item.get("end_frame_exclusive", motion.num_frames)
        if (
            isinstance(start_value, bool)
            or isinstance(end_value, bool)
            or not isinstance(start_value, int)
            or not isinstance(end_value, int)
        ):
            raise ArdyBackendError("Clip slice bounds must be integers.")
        start = int(start_value)
        end = int(end_value)
        if not 0 <= start < end <= motion.num_frames:
            raise ArdyBackendError(
                f"Invalid half-open clip slice [{start}, {end}) for {motion.num_frames} frames."
            )
        roots.append(motion.root_positions[start:end])
        quats.append(motion.local_rot_quats[start:end])
        if motion.foot_contacts is None:
            has_stored_contacts = False
        else:
            contacts.append(motion.foot_contacts[start:end])

    init_history = None
    history_len = 0
    if roots:
        all_roots = np.concatenate(roots, axis=0)
        all_quats = np.concatenate(quats, axis=0)
        history_len = min(len(all_roots), max_history)
        history_len -= history_len % patch
        if history_len <= 0:
            raise ArdyBackendError(f"Clip history must contain at least {patch} usable frames.")
        device = model.device
        local_quats = torch.as_tensor(all_quats, dtype=torch.float32, device=device)
        norms = torch.linalg.vector_norm(local_quats, dim=-1, keepdim=True)
        if (norms < 1e-6).any():
            raise ArdyBackendError("KMB1 contains a zero-length local rotation quaternion.")
        local_rot_mats = quaternion_to_matrix(local_quats / norms)
        root_positions = torch.as_tensor(all_roots, dtype=torch.float32, device=device)
        rebuilt = model.motion_rep(
            local_joint_rots=local_rot_mats,
            root_positions=root_positions,
            to_normalize=not has_stored_contacts,
        )
        if has_stored_contacts:
            if "foot_contacts" not in model.motion_rep.slice_dict:
                raise ArdyBackendError("ARDY motion representation has no foot_contacts feature slice.")
            rebuilt[..., model.motion_rep.slice_dict["foot_contacts"]] = torch.as_tensor(
                np.concatenate(contacts, axis=0), dtype=torch.float32, device=device
            )
            rebuilt = model.motion_rep.normalize(rebuilt)
        if rebuilt.ndim == 2:
            rebuilt = rebuilt.unsqueeze(0)
        init_history = rebuilt[:, -history_len:]

    ordered: list[dict[str, Any]] = []
    for item in future_items:
        copied = dict(item)
        _normalize_root_heading(copied)
        ordered.append(copied)

    # Stable sorting plus ARDY's last-write behavior gives the requested channel priority.
    priority = {
        "end-effector": 0,
        "left-hand": 0,
        "right-hand": 0,
        "left-foot": 0,
        "right-foot": 0,
        "root2d": 1,
        "fullbody": 2,
    }
    try:
        ordered.sort(key=lambda item: priority[str(item.get("type"))])
    except KeyError as exc:
        raise ArdyBackendError(f"Unsupported ARDY constraint type: {exc.args[0]!r}.") from exc

    postprocess_constraints = load_constraints_lst(ordered, model.motion_rep.skeleton) if ordered else []
    rebased: list[dict[str, Any]] = []
    for item in ordered:
        copied = dict(item)
        copied["frame_indices"] = [int(value) + history_len for value in copied.get("frame_indices", [])]
        rebased.append(copied)

    total_frames = history_len + future_frames
    observed_motion = motion_mask = None
    if rebased:
        constraints = load_constraints_lst(rebased, model.motion_rep.skeleton)
        lengths = torch.tensor([total_frames], dtype=torch.long, device=model.device)
        observed_motion, motion_mask = model.motion_rep.create_conditions_from_constraints_batched(
            constraints,
            lengths,
            to_normalize=True,
            device=model.device,
        )
        if history_len:
            observed_motion[:, :history_len] = 0
            motion_mask[:, :history_len] = 0
    return init_history, observed_motion, motion_mask, total_frames, history_len, postprocess_constraints


def _finalize_output(output: dict[str, Any], model: Any, constraints: list[Any], postprocess: bool) -> dict[str, Any]:
    from ardy.tools import to_numpy

    if postprocess:
        from ardy.postprocess import post_process_motion

        output.update(
            post_process_motion(
                output["local_rot_mats"],
                output["root_positions"],
                output["foot_contacts"],
                model.skeleton,
                constraint_lst=constraints or None,
            )
        )
    return to_numpy(output)


def execute_generate(
    task_request: dict[str, Any],
    model: Any,
    profile: Any,
    cancel_event: threading.Event,
    spool: ArdyClipSpool,
    quickserver_root: str | Path,
) -> tuple[dict[str, Any], bytes]:
    import torch

    from kimodo.bridge import bridge_server as bridge_runtime_helpers
    from kimodo.tools import seed_everything

    if bridge_runtime_helpers._resolve_requested_output_format(task_request) != "flatbuf_motion_v1":
        raise ArdyBackendError("ARDY generate requires output_format='flatbuf_motion_v1'.")
    diffusion_steps = int(task_request.get("diffusion_steps", profile.max_diffusion_steps))
    if not 1 <= diffusion_steps <= int(profile.max_diffusion_steps):
        raise ArdyBackendError(
            f"diffusion_steps must be in [1, {profile.max_diffusion_steps}]; got {diffusion_steps}."
        )
    requested_seed = task_request.get("seed")
    resolved_seed = secrets.randbelow(2**31) if requested_seed is None else int(requested_seed)
    seed_everything(resolved_seed)

    prompt = str(task_request.get("prompt") or "A person walks forward.").strip()
    if not prompt.endswith("."):
        prompt += "."
    init_history, observed_motion, motion_mask, total_frames, history_len, postprocess_constraints = prepare_generation_inputs(
        model,
        profile,
        task_request.get("constraints_json", ""),
        spool,
        quickserver_root,
    )

    def check_cancel() -> None:
        if cancel_event.is_set():
            raise bridge_runtime_helpers.GenerateCancelledError("Generation canceled.")

    check_cancel()
    with torch.no_grad():
        motion = model.autoregressive_step(
            num_frames=total_frames,
            num_denoising_steps=diffusion_steps,
            motion_mask=motion_mask,
            observed_motion=observed_motion,
            cfg_weight=(profile.cfg_text_weight, profile.cfg_constraint_weight),
            texts=[prompt],
            init_history_sequence=init_history,
            cancel_callback=check_cancel,
        )
        generated = motion[:, history_len : history_len + int(profile.horizon_frames)]
        output = model.motion_rep.inverse(generated, is_normalized=True)
        output = _finalize_output(
            output,
            model,
            postprocess_constraints,
            bool(getattr(profile, "postprocess", False)),
        )
    check_cancel()
    payload = bridge_runtime_helpers._build_generate_flatbuffer_payload(model, output, sample_index=0)
    check_cancel()
    handle = spool.publish(
        payload,
        model_name=profile.model_name,
        fingerprint=profile.motion_rep_fingerprint,
        fps=profile.source_fps,
    )
    return {
        "status": "done",
        "output_format": "flatbuf_motion_v1",
        "byte_length": len(payload),
        "clip_handle": handle,
        "motion_rep_fingerprint": profile.motion_rep_fingerprint,
        "resolved_seed": resolved_seed,
    }, payload


def load_runtime(profile: Any, config: dict[str, Any], quickserver_root: str | Path, device: str):
    from ardy.model import load_model
    from kimodo.model.load_model import _select_text_encoder_conf
    from kimodo.model.loading import DEFAULT_TEXT_ENCODER_URL, get_env_var, instantiate_from_dict

    models_root = Path(config.get("models_root") or (Path(quickserver_root).resolve() / "models")).resolve()
    checkpoint_dir = models_root / profile.model_name
    required_checkpoint_files = (
        "config.yaml",
        "tokenizer.safetensors",
        "denoiser.safetensors",
        "stats/motion/mean.npy",
        "stats/motion/std.npy",
    )
    if not all((checkpoint_dir / relative).is_file() for relative in required_checkpoint_files):
        models_root.mkdir(parents=True, exist_ok=True)
        try:
            from modelscope import snapshot_download as modelscope_snapshot_download

            modelscope_snapshot_download(model_id=profile.modelscope_repo, local_dir=str(checkpoint_dir))
        except Exception as modelscope_error:
            try:
                from huggingface_hub import snapshot_download as huggingface_snapshot_download

                huggingface_snapshot_download(
                    repo_id=f"nvidia/{profile.model_name}",
                    local_dir=str(checkpoint_dir),
                )
            except Exception as huggingface_error:
                raise RuntimeError(
                    "ARDY checkpoint download failed via both ModelScope and Hugging Face: "
                    f"modelscope={modelscope_error}; huggingface={huggingface_error}"
                ) from huggingface_error

    text_encoder = instantiate_from_dict(
        _select_text_encoder_conf(
            get_env_var("TEXT_ENCODER_URL", DEFAULT_TEXT_ENCODER_URL),
            device,
        )
    )
    model = load_model(
        profile.model_name,
        device=device,
        text_encoder=text_encoder,
        checkpoints_dir=str(models_root),
    )
    actual = (
        float(model.motion_rep.fps),
        int(model.gen_horizon_len),
        int(model.num_frames_per_token),
        int(model.diffusion.num_base_steps),
    )
    expected = (
        float(profile.source_fps),
        int(profile.horizon_frames),
        int(profile.frames_per_token),
        int(profile.max_diffusion_steps),
    )
    if actual != expected:
        raise ArdyBackendError(f"ARDY checkpoint/profile mismatch: expected {expected}, got {actual}.")
    model.fps = float(profile.source_fps)
    model.skeleton = model.motion_rep.skeleton
    model.name = profile.model_name
    return model
