from __future__ import annotations

import math


FRAME_TOLERANCE = 1e-4


def seconds_to_frame_count(seconds: float, fps: float) -> int:
    seconds = float(seconds)
    fps = float(fps)
    if not math.isfinite(seconds) or seconds < 0.0:
        raise ValueError("seconds must be a finite non-negative number")
    if not math.isfinite(fps) or fps <= 0.0:
        raise ValueError("fps must be a finite positive number")
    if seconds == 0.0:
        return 0
    return max(0, int(math.ceil(seconds * fps - FRAME_TOLERANCE)))


def seconds_to_protocol_frame_index(seconds: float, fps: float) -> int:
    seconds = float(seconds)
    fps = float(fps)
    if not math.isfinite(seconds):
        raise ValueError("seconds must be finite")
    if not math.isfinite(fps) or fps <= 0.0:
        raise ValueError("fps must be a finite positive number")
    return int(math.ceil(seconds * fps - FRAME_TOLERANCE))
