from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class FlowHmrMemoryOptions:
    vggt_interval: int
    sam_chunk_size: int
    static_camera: bool = False


CONSERVATIVE_FLOWHMR_OPTIONS = FlowHmrMemoryOptions(
    vggt_interval=30,
    sam_chunk_size=1,
    static_camera=True,
)


def options_for_free_vram_gb(free_vram_gb: float) -> FlowHmrMemoryOptions:
    """Select raw-video settings that fit the currently free GPU memory."""
    free = max(0.0, float(free_vram_gb))
    if free >= 28.0:
        return FlowHmrMemoryOptions(vggt_interval=1, sam_chunk_size=16)
    if free >= 20.0:
        return FlowHmrMemoryOptions(vggt_interval=5, sam_chunk_size=8)
    if free >= 14.0:
        return FlowHmrMemoryOptions(vggt_interval=10, sam_chunk_size=4)
    if free >= 8.0:
        return FlowHmrMemoryOptions(vggt_interval=20, sam_chunk_size=2)
    return CONSERVATIVE_FLOWHMR_OPTIONS


def device_for_free_vram_gb(free_vram_gb: float) -> str:
    # FlowHMR already reduces VGGT frequency and SAM chunking below 8 GB.
    # Allow that conservative profile to try CUDA from 4 GB free; mocap_worker
    # retries OOMs with the conservative settings and ultimately falls back to CPU.
    return "cuda" if float(free_vram_gb) >= 4.0 else "cpu"


def is_cuda_oom(output: str) -> bool:
    text = str(output or "").lower()
    return "out of memory" in text or "cuda error: memory" in text or "alloc_failed" in text
