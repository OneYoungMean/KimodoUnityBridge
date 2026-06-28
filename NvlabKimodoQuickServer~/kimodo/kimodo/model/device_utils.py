from __future__ import annotations

import torch


def has_cuda() -> bool:
    try:
        return bool(torch.cuda.is_available())
    except Exception:
        return False


def has_mps() -> bool:
    try:
        return bool(torch.backends.mps.is_available())
    except Exception:
        return False


def normalize_device_name(device: str | None) -> str | None:
    raw = str(device or "").strip()
    if not raw:
        return None

    lowered = raw.lower()
    if lowered == "cpu":
        return "cpu"
    if lowered == "cuda":
        return "cuda:0"
    if lowered.startswith("cuda:"):
        return lowered
    if lowered == "mps" or lowered.startswith("mps:"):
        return "mps"
    raise ValueError(f"Unsupported device: {device}")


def resolve_runtime_device(device: str | None) -> str:
    normalized = normalize_device_name(device)
    if normalized == "cpu":
        return "cpu"
    if normalized is not None:
        if normalized.startswith("cuda"):
            return normalized if has_cuda() else "cpu"
        if normalized == "mps":
            return "mps" if has_mps() else "cpu"

    if has_cuda():
        return "cuda:0"
    if has_mps():
        return "mps"
    return "cpu"


def preferred_text_encoder_dtype(device: str | None) -> str:
    runtime_device = resolve_runtime_device(device)
    if runtime_device == "mps":
        return "float16"
    return "bfloat16"
