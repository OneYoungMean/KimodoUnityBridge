# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Torch CPU INT8 LLM2Vec helpers and runtime wrapper."""

from __future__ import annotations

import gc
import json
import os
from pathlib import Path
import shutil
from typing import Any

import torch
from torch import nn
from transformers import AutoConfig, AutoTokenizer

from kimodo.bridge.quickserver_assets import INT8_LOCAL_DIR

from .llm2vec import LLM2Vec


DEFAULT_LLM2VEC_CONFIG: dict[str, Any] = {
    "pooling_mode": "mean",
    "max_length": 512,
    "doc_max_length": 400,
    "skip_instruction": True,
}
QUANTIZED_STATE_FILENAME = "quantized_state_dict.pt"
QUANTIZATION_META_FILENAME = "quantization_meta.json"
LLM2VEC_CONFIG_FILENAME = "llm2vec_config.json"
TOKENIZER_PRIMARY_FILES = ("tokenizer.json", "tokenizer.model")
TOKENIZER_SUPPORT_FILES = (
    "tokenizer_config.json",
    "special_tokens_map.json",
    "chat_template.jinja",
    "generation_config.json",
    "added_tokens.json",
)


def _dedupe_paths(candidates: list[Path]) -> list[Path]:
    seen: set[str] = set()
    ordered: list[Path] = []
    for candidate in candidates:
        key = str(candidate)
        if key in seen:
            continue
        seen.add(key)
        ordered.append(candidate)
    return ordered


def _has_any_file(model_dir: Path, filenames: tuple[str, ...]) -> bool:
    return any((model_dir / filename).exists() for filename in filenames)


def _has_weight_file(model_dir: Path) -> bool:
    patterns = ("*.safetensors", "*.bin")
    return any(any(model_dir.glob(pattern)) for pattern in patterns)


def _common_model_dir_missing_items(model_dir: Path) -> list[str]:
    missing: list[str] = []
    if not (model_dir / "config.json").is_file():
        missing.append("config.json")
    if not (model_dir / "tokenizer_config.json").is_file():
        missing.append("tokenizer_config.json")
    if not _has_any_file(model_dir, TOKENIZER_PRIMARY_FILES):
        missing.append("tokenizer.json or tokenizer.model")
    return missing


def _load_llm2vec_config(model_dir: Path) -> dict[str, Any]:
    config_path = model_dir / LLM2VEC_CONFIG_FILENAME
    if not config_path.is_file():
        return dict(DEFAULT_LLM2VEC_CONFIG)
    with config_path.open("r", encoding="utf-8") as stream:
        data = json.load(stream)
    merged = dict(DEFAULT_LLM2VEC_CONFIG)
    merged.update(data)
    return merged


def _write_llm2vec_config(model_dir: Path, config: dict[str, Any]) -> None:
    model_dir.mkdir(parents=True, exist_ok=True)
    config_path = model_dir / LLM2VEC_CONFIG_FILENAME
    with config_path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(config, stream, indent=2, sort_keys=True)
        stream.write("\n")


def source_dir_missing_items(model_dir: str | os.PathLike[str]) -> list[str]:
    path = Path(model_dir).resolve()
    missing = _common_model_dir_missing_items(path)
    if not _has_weight_file(path):
        missing.append("model weights (*.safetensors or *.bin)")
    return missing


def int8_dir_missing_items(model_dir: str | os.PathLike[str]) -> list[str]:
    path = Path(model_dir).resolve()
    missing = _common_model_dir_missing_items(path)
    if not (path / LLM2VEC_CONFIG_FILENAME).is_file():
        missing.append(LLM2VEC_CONFIG_FILENAME)
    if not (path / QUANTIZED_STATE_FILENAME).is_file():
        missing.append(QUANTIZED_STATE_FILENAME)
    if not (path / QUANTIZATION_META_FILENAME).is_file():
        missing.append(QUANTIZATION_META_FILENAME)
    return missing


def source_dir_is_ready(model_dir: str | os.PathLike[str]) -> bool:
    return not source_dir_missing_items(model_dir)


def int8_dir_is_ready(model_dir: str | os.PathLike[str]) -> bool:
    return not int8_dir_missing_items(model_dir)


def assert_source_dir_ready(model_dir: str | os.PathLike[str]) -> Path:
    path = Path(model_dir).resolve()
    missing = source_dir_missing_items(path)
    if missing:
        raise FileNotFoundError(
            "Incomplete FP16 source directory: "
            f"{path}. Missing: {', '.join(missing)}"
        )
    return path


def assert_int8_dir_ready(model_dir: str | os.PathLike[str]) -> Path:
    path = Path(model_dir).resolve()
    missing = int8_dir_missing_items(path)
    if missing:
        raise FileNotFoundError(
            "Incomplete INT8 model directory: "
            f"{path}. Missing: {', '.join(missing)}"
        )
    return path


def _copy_non_weight_files(source_dir: Path, output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    for item in source_dir.iterdir():
        if not item.is_file():
            continue
        if item.name in {QUANTIZED_STATE_FILENAME, QUANTIZATION_META_FILENAME}:
            continue
        if item.suffix in {".safetensors", ".bin", ".pt"}:
            continue
        shutil.copy2(item, output_dir / item.name)


def _load_source_llm2vec(source_dir: Path) -> LLM2Vec:
    encoder = LLM2Vec.from_pretrained(
        base_model_name_or_path=str(source_dir),
        torch_dtype=torch.float16,
    )
    encoder.model = encoder.model.cpu().float()
    encoder.model.eval()
    return encoder


def _instantiate_empty_quantized_llm2vec(model_dir: Path) -> LLM2Vec:
    tokenizer = AutoTokenizer.from_pretrained(str(model_dir))
    if tokenizer.pad_token is None and tokenizer.eos_token is not None:
        tokenizer.pad_token = tokenizer.eos_token
    tokenizer.padding_side = "left"

    config = AutoConfig.from_pretrained(str(model_dir))
    model_class = LLM2Vec._get_model_class(config.__class__.__name__, enable_bidirectional=True)
    model = model_class(config)
    model.eval()
    model = torch.ao.quantization.quantize_dynamic(
        model,
        {nn.Linear},
        dtype=torch.qint8,
        inplace=False,
    )
    return LLM2Vec(model=model, tokenizer=tokenizer, **_load_llm2vec_config(model_dir))


def load_quantized_llm2vec(model_dir: str | os.PathLike[str]) -> LLM2Vec:
    path = assert_int8_dir_ready(model_dir)
    meta_path = path / QUANTIZATION_META_FILENAME
    with meta_path.open("r", encoding="utf-8") as stream:
        metadata = json.load(stream)
    if metadata.get("format") != "torch_dynamic_int8":
        raise ValueError(f"Unsupported INT8 model format in {meta_path}: {metadata.get('format')!r}")

    payload = torch.load(path / QUANTIZED_STATE_FILENAME, map_location="cpu", weights_only=False)
    if isinstance(payload, nn.Module):
        model = payload
        encoder = LLM2Vec(model=model, tokenizer=AutoTokenizer.from_pretrained(str(path)), **_load_llm2vec_config(path))
        if encoder.tokenizer.pad_token is None and encoder.tokenizer.eos_token is not None:
            encoder.tokenizer.pad_token = encoder.tokenizer.eos_token
        encoder.tokenizer.padding_side = "left"
        encoder.model.eval()
        return encoder

    encoder = _instantiate_empty_quantized_llm2vec(path)
    incompatible = encoder.model.load_state_dict(payload, strict=False)
    if incompatible.missing_keys or incompatible.unexpected_keys:
        raise RuntimeError(
            "Failed to restore INT8 text encoder state. "
            f"missing={incompatible.missing_keys}, unexpected={incompatible.unexpected_keys}"
        )
    encoder.model.eval()
    return encoder


def build_quantized_bundle(
    source_dir: str | os.PathLike[str],
    output_dir: str | os.PathLike[str],
    *,
    force: bool = False,
    verify: bool = False,
) -> Path:
    source_path = assert_source_dir_ready(source_dir)
    output_path = Path(output_dir).resolve()

    if output_path.exists():
        if not force:
            raise FileExistsError(
                f"Output directory already exists: {output_path}. Use --force to overwrite."
            )
        shutil.rmtree(output_path)

    output_path.mkdir(parents=True, exist_ok=True)
    source_llm2vec_config = _load_llm2vec_config(source_path)

    encoder = _load_source_llm2vec(source_path)
    encoder.model = torch.ao.quantization.quantize_dynamic(
        encoder.model,
        {nn.Linear},
        dtype=torch.qint8,
        inplace=False,
    )
    encoder.model.eval()

    _copy_non_weight_files(source_path, output_path)
    _write_llm2vec_config(output_path, source_llm2vec_config)

    torch.save(encoder.model, output_path / QUANTIZED_STATE_FILENAME)
    metadata = {
        "format": "torch_dynamic_int8",
        "source_dir": str(source_path),
        "source_had_llm2vec_config": (source_path / LLM2VEC_CONFIG_FILENAME).is_file(),
        "quantized_state_file": QUANTIZED_STATE_FILENAME,
        "storage": "torch_serialized_model",
        "quantized_module_types": ["torch.nn.Linear"],
    }
    with (output_path / QUANTIZATION_META_FILENAME).open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(metadata, stream, indent=2, sort_keys=True)
        stream.write("\n")

    if verify:
        smoke = LLM2VecInt8Encoder(model_name_or_path=str(output_path))
        smoke("A woman walk and say hello")

    return output_path


def resolve_local_int8_dir(model_name_or_path: str | None = None) -> str:
    candidates: list[Path] = []
    raw_value = str(model_name_or_path or "").strip()
    if raw_value:
        candidate = Path(raw_value).expanduser()
        if not candidate.is_absolute():
            candidate = candidate.resolve()
        candidates.append(candidate)

    env_override = os.environ.get("KIMODO_LLM2VEC_DIR", "").strip()
    if env_override:
        candidates.append(Path(env_override).expanduser().resolve())

    kimodo_root = os.environ.get("KIMODO_ROOT_PATH", "").strip()
    if kimodo_root:
        candidates.append((Path(kimodo_root).resolve() / "models" / INT8_LOCAL_DIR).resolve())

    this_dir = Path(__file__).resolve().parent
    candidates.append((this_dir.parents[3] / "models" / INT8_LOCAL_DIR).resolve())
    candidates.append((this_dir.parents[2] / "models" / INT8_LOCAL_DIR).resolve())

    for candidate in _dedupe_paths(candidates):
        if candidate.is_dir() and int8_dir_is_ready(candidate):
            return str(candidate)

    checked = [str(path) for path in _dedupe_paths(candidates)]
    raise FileNotFoundError(
        "[LLM2VecInt8Encoder] Missing local INT8 text encoder directory. "
        f"Checked: {checked}"
    )


class LLM2VecInt8Encoder(nn.Module):
    """CPU-only local INT8 LLM2Vec wrapper."""

    def __init__(
        self,
        model_name_or_path: str | None = None,
        llm_dim: int = 4096,
    ) -> None:
        super().__init__()
        self.llm_dim = llm_dim
        self.model_name_or_path = model_name_or_path
        self.custom_dir = resolve_local_int8_dir(model_name_or_path)
        print(f"[LLM2VecInt8Encoder] Loading INT8 model from {self.custom_dir}...")
        self.model: LLM2Vec | None = load_quantized_llm2vec(self.custom_dir)
        print("[LLM2VecInt8Encoder] Ready.")

    def unload(self):
        return None

    def reload(self):
        if self.model is None:
            self.model = load_quantized_llm2vec(self.custom_dir)

    def get_device(self):
        return torch.device("cpu")

    def delete(self):
        self.model = None
        gc.collect()

    def __call__(self, text: list[str] | str):
        self.reload()
        assert self.model is not None

        is_string = isinstance(text, str)
        texts = [text] if is_string else list(text)
        embeddings = self.model.encode(
            texts,
            show_progress_bar=False,
            convert_to_tensor=True,
            device="cpu",
        )
        if len(embeddings.shape) == 1:
            embeddings = embeddings.unsqueeze(0)
        assert self.llm_dim == embeddings.shape[-1]

        encoded_text = embeddings[:, None]
        lengths = [1 for _ in texts]
        if is_string:
            return encoded_text[0], lengths[0]
        return encoded_text, lengths
