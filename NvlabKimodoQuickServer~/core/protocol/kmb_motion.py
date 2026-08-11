from __future__ import annotations

from dataclasses import dataclass
import json
import math
from typing import Any

import numpy as np

from kimodo.frame_time import seconds_to_frame_count, seconds_to_protocol_frame_index


MAX_KMB_BYTES = 256 * 1024**2


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


@dataclass(frozen=True)
class KmbJointMask:
    joint_name: str
    position: tuple[bool, bool, bool]
    rotation: bool


@dataclass(frozen=True)
class KmbClipMask:
    root_position: tuple[bool, bool, bool]
    root_heading: bool
    root_rotation: bool
    joints: tuple[KmbJointMask, ...]


@dataclass(frozen=True)
class ParsedKmbClip:
    motion: KmbMotion
    target_start_frame: int
    mask: KmbClipMask | None


def parse_kmb1(payload: bytes, error_type: type[ValueError] = ValueError) -> KmbMotion:
    from core.protocol.generated import MotionPacket

    if len(payload or b"") > MAX_KMB_BYTES:
        raise error_type(f"KMB1 payload exceeds the {MAX_KMB_BYTES}-byte limit.")
    if not payload or not MotionPacket.MotionPacket.MotionPacketBufferHasIdentifier(payload, 0):
        raise error_type("Attachment is not a KMB1 MotionPacket.")
    packet = MotionPacket.MotionPacket.GetRootAs(payload, 0)
    version = int(packet.Version())
    num_frames = int(packet.NumFrames())
    num_joints = int(packet.NumJoints())
    if version != 1 or num_frames <= 0 or num_joints <= 0:
        raise error_type(
            f"Invalid KMB1 header: version={version}, frames={num_frames}, joints={num_joints}."
        )

    joint_names = tuple(
        (packet.JointNames(i) or b"").decode("utf-8", errors="strict")
        for i in range(packet.JointNamesLength())
    )
    joint_parents = tuple(int(packet.JointParents(i)) for i in range(packet.JointParentsLength()))
    roots = np.asarray(packet.RootPositionsAsNumpy(), dtype=np.float32).copy()
    quats = np.asarray(packet.LocalRotQuatsAsNumpy(), dtype=np.float32).copy()
    contacts = None
    if packet.FootContactsLength():
        raw_contacts = np.asarray(packet.FootContactsAsNumpy(), dtype=np.uint8).copy()
        if raw_contacts.size != num_frames * 4:
            raise error_type("KMB1 foot-contact length does not match its frame count.")
        contacts = raw_contacts.reshape(num_frames, 4).astype(np.float32)
    if len(joint_names) != num_joints or len(joint_parents) != num_joints:
        raise error_type("KMB1 joint metadata does not match num_joints.")
    if roots.size != num_frames * 3 or quats.size != num_frames * num_joints * 4:
        raise error_type("KMB1 motion vector lengths do not match its header.")
    if not np.isfinite(roots).all() or not np.isfinite(quats).all():
        raise error_type("KMB1 contains non-finite motion values.")
    return KmbMotion(
        payload=bytes(payload),
        model_name=(packet.ModelName() or b"").decode("utf-8", errors="strict"),
        fps=float(packet.Fps()),
        joint_names=joint_names,
        joint_parents=joint_parents,
        root_positions=roots.reshape(num_frames, 3),
        local_rot_quats=quats.reshape(num_frames, num_joints, 4),
        foot_contacts=contacts,
    )


def parse_constraints(value: Any, error_type: type[ValueError] = ValueError) -> list[dict[str, Any]]:
    if value is None or value == "":
        return []
    try:
        parsed = json.loads(value) if isinstance(value, str) else value
    except Exception as exc:
        raise error_type(f"Invalid constraints_json: {exc}") from exc
    if isinstance(parsed, dict):
        parsed = [parsed]
    if not isinstance(parsed, list) or any(not isinstance(item, dict) for item in parsed):
        raise error_type("constraints_json must be a JSON array or object.")
    return [dict(item) for item in parsed]


def attachment_payload(
    item: dict[str, Any],
    attachments: tuple[bytes, ...],
    error_type: type[ValueError] = ValueError,
) -> bytes:
    clip_format = str(item.get("format") or "")
    if clip_format != "kmb_attachment_v1":
        raise error_type(f"Unsupported clip format: {clip_format!r}.")
    index = item.get("attachment")
    if isinstance(index, bool) or not isinstance(index, int) or not 0 <= index < len(attachments):
        raise error_type(f"Invalid KMB attachment index: {index!r}.")
    return attachments[index]


def clip_slice(
    item: dict[str, Any],
    motion: KmbMotion,
    error_type: type[ValueError] = ValueError,
) -> tuple[int, int]:
    start = item.get("start_frame", 0)
    end = item.get("end_frame_exclusive", motion.num_frames)
    if isinstance(start, bool) or isinstance(end, bool) or not isinstance(start, int) or not isinstance(end, int):
        raise error_type("Clip slice bounds must be integers.")
    if not 0 <= start < end <= motion.num_frames:
        raise error_type(f"Invalid KMB clip slice [{start}, {end}) for {motion.num_frames} frames.")
    return start, end


def parse_clip_mask(
    item: dict[str, Any],
    joint_names: tuple[str, ...] | list[str],
    error_type: type[ValueError] = ValueError,
) -> KmbClipMask:
    values = item.get("mask")
    if not isinstance(values, dict):
        raise error_type("Clip constraint mask must be an object.")

    root_position = values.get("root_position", [False, False, False])
    if (
        not isinstance(root_position, list)
        or len(root_position) != 3
        or any(not isinstance(value, bool) for value in root_position)
    ):
        raise error_type("Clip constraint mask root_position must contain three booleans.")
    root_heading = values.get("root_heading", False)
    root_rotation = values.get("root_rotation", False)
    if not isinstance(root_heading, bool) or not isinstance(root_rotation, bool):
        raise error_type("Clip constraint mask root_heading and root_rotation must be booleans.")

    names = tuple(str(name) for name in joint_names)
    by_name = {name.lower(): name for name in names}
    joints_value = values.get("joints", [])
    if not isinstance(joints_value, list):
        raise error_type("Clip constraint mask joints must be an array.")
    joints: list[KmbJointMask] = []
    for index, joint in enumerate(joints_value):
        if not isinstance(joint, dict):
            raise error_type(f"Clip constraint mask joints[{index}] must be an object.")
        requested_name = str(joint.get("joint_name") or "")
        joint_name = by_name.get(requested_name.lower())
        if joint_name is None:
            raise error_type(f"Clip constraint mask contains unknown joint '{requested_name}'.")
        if names and joint_name == names[0]:
            raise error_type("Clip constraint mask root joint must use the root_* fields.")
        position = joint.get("position", [False, False, False])
        if (
            not isinstance(position, list)
            or len(position) != 3
            or any(not isinstance(value, bool) for value in position)
        ):
            raise error_type(f"Clip constraint mask joints[{index}].position must contain three booleans.")
        rotation = joint.get("rotation", False)
        if not isinstance(rotation, bool):
            raise error_type(f"Clip constraint mask joints[{index}].rotation must be a boolean.")
        joints.append(KmbJointMask(joint_name, tuple(position), rotation))
    return KmbClipMask(tuple(root_position), root_heading, root_rotation, tuple(joints))


def parse_kmb_clip(
    item: dict[str, Any],
    attachments: tuple[bytes, ...],
    fps: float,
    error_type: type[ValueError] = ValueError,
) -> ParsedKmbClip:
    motion = parse_kmb1(attachment_payload(item, attachments, error_type), error_type)
    try:
        start_time = float(item["start_time"])
        duration = float(item["duration"])
    except (KeyError, TypeError, ValueError) as exc:
        raise error_type("Clip constraint requires finite start_time and duration seconds.") from exc
    if not math.isfinite(start_time) or not math.isfinite(duration) or duration <= 0.0:
        raise error_type("Clip constraint start_time/duration must be finite and duration must be positive.")
    duration_frames = seconds_to_frame_count(duration, fps)
    if duration_frames != motion.num_frames:
        raise error_type(
            f"Clip constraint duration resolves to {duration_frames} frames but its KMB contains {motion.num_frames}."
        )
    mask = parse_clip_mask(item, motion.joint_names, error_type) if item.get("mask") is not None else None
    return ParsedKmbClip(motion, seconds_to_protocol_frame_index(start_time, fps), mask)
