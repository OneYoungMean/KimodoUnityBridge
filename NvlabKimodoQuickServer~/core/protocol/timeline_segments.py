from __future__ import annotations

from dataclasses import dataclass
import math
from typing import Any

from kimodo.frame_time import seconds_to_frame_count


@dataclass(frozen=True)
class TimelineSegment:
    prompt: str
    start_frame: int
    end_frame_exclusive: int

    @property
    def frame_count(self) -> int:
        return self.end_frame_exclusive - self.start_frame


def parse_timeline_segments(
    value: Any,
    fps: float,
    total_frames: int,
    error_type: type[ValueError] = ValueError,
) -> tuple[TimelineSegment, ...]:
    if value is None:
        return ()
    if total_frames <= 0:
        raise error_type("timeline_segments requires a fixed positive duration.")
    if not isinstance(value, list) or not value:
        raise error_type("timeline_segments must be a non-empty array.")

    segments: list[TimelineSegment] = []
    cursor = 0
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise error_type(f"timeline_segments[{index}] must be an object.")
        prompt = str(item.get("prompt") or "").strip() or "idle"
        try:
            duration_seconds = float(item.get("duration"))
        except (TypeError, ValueError) as exc:
            raise error_type(
                f"timeline_segments[{index}].duration must be a finite positive number."
            ) from exc
        if not math.isfinite(duration_seconds) or duration_seconds <= 0.0:
            raise error_type(f"timeline_segments[{index}].duration must be a finite positive number.")
        frame_count = seconds_to_frame_count(duration_seconds, fps)
        if frame_count <= 0:
            raise error_type(f"timeline_segments[{index}] resolves to zero frames.")
        segments.append(TimelineSegment(prompt, cursor, cursor + frame_count))
        cursor += frame_count

    if cursor != total_frames:
        raise error_type(
            f"timeline_segments resolves to {cursor} frames, but duration resolves to {total_frames}."
        )
    return tuple(segments)
