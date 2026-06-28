# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Bridge-specific local-only Kimodo model loader."""

from __future__ import annotations

from pathlib import Path
from typing import Optional

from omegaconf import OmegaConf

from kimodo.model.device_utils import resolve_runtime_device
from kimodo.model.load_model import _select_text_encoder_conf
from kimodo.model.loading import AVAILABLE_MODELS, DEFAULT_MODEL, DEFAULT_TEXT_ENCODER_URL, get_env_var, instantiate_from_dict
from kimodo.model.registry import get_model_info, resolve_model_name


def _resolve_bridge_model_dir(models_root: str | Path, modelname: str) -> Path:
    root = Path(models_root).resolve()
    info = get_model_info(modelname)
    checkpoint_folder_name = info.display_name if info is not None else modelname
    model_path = root / checkpoint_folder_name
    if not model_path.exists() and modelname != checkpoint_folder_name:
        model_path = root / modelname
    if not model_path.exists():
        raise FileNotFoundError(
            f"Bridge model folder not found at '{model_path}'. "
            "Bridge local loading expects assets to be provisioned under models_root before load."
        )
    return model_path


def load_bridge_model(
    modelname=None,
    *,
    models_root: str | Path,
    device=None,
    eval_mode: bool = True,
    default_family: Optional[str] = "Kimodo",
    return_resolved_name: bool = False,
    text_encoder=None,
    text_encoder_fp32: bool = False,
):
    """Load a Bridge model strictly from models_root without HF/cache fallback."""
    device = resolve_runtime_device(device)

    if modelname is None:
        modelname = DEFAULT_MODEL
    if modelname not in AVAILABLE_MODELS:
        if default_family is not None:
            modelname = resolve_model_name(modelname, default_family)
        else:
            raise ValueError(
                f"""The model is not recognized.
            Please choose between: {AVAILABLE_MODELS}"""
            )

    resolved_modelname = modelname
    model_path = _resolve_bridge_model_dir(models_root, modelname)
    model_config_path = model_path / "config.yaml"
    if not model_config_path.exists():
        raise FileNotFoundError(f"The model checkpoint folder exists but config.yaml is missing: {model_config_path}")

    model_conf = OmegaConf.load(model_config_path)

    if text_encoder is not None:
        runtime_conf = OmegaConf.create({"checkpoint_dir": str(model_path)})
    else:
        text_encoder_url = get_env_var("TEXT_ENCODER_URL", DEFAULT_TEXT_ENCODER_URL)
        runtime_conf = OmegaConf.create(
            {
                "checkpoint_dir": str(model_path),
                "text_encoder": _select_text_encoder_conf(text_encoder_url, device, text_encoder_fp32),
            }
        )

    model_cfg = OmegaConf.to_container(OmegaConf.merge(model_conf, runtime_conf), resolve=True)
    model_cfg.pop("checkpoint_dir", None)

    if text_encoder is not None:
        model_cfg["text_encoder"] = None

    model = instantiate_from_dict(model_cfg, overrides={"device": device})

    if text_encoder is not None:
        model.text_encoder = text_encoder

    if eval_mode:
        model = model.eval()
    if return_resolved_name:
        return model, resolved_modelname
    return model
