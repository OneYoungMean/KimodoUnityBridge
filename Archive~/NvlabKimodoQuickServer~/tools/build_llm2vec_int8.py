#!/usr/bin/env python
"""Build a local Torch CPU INT8 LLM2Vec asset for QuickServer."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys


DEFAULT_SOURCE_DIR = r"C:\nvlab\LLMVec-GGUF\KIMODO-Meta3_llm2vec_FP16"
DEFAULT_OUTPUT_DIR_NAME = "KIMODO-Meta3_llm2vec_INT8"
SMOKE_PROMPT = "A woman walk and say hello"


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def _source_root() -> Path:
    return _repo_root() / "kimodo"


def _ensure_import_path() -> None:
    source_root = _source_root()
    if str(source_root) not in sys.path:
        sys.path.insert(0, str(source_root))


def _default_output_dir() -> Path:
    return _repo_root() / "models" / DEFAULT_OUTPUT_DIR_NAME


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Build a local Torch CPU INT8 LLM2Vec asset.")
    parser.add_argument("--source-dir", default=DEFAULT_SOURCE_DIR)
    parser.add_argument("--output-dir", default=str(_default_output_dir()))
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--verify", action="store_true")
    return parser


def main() -> int:
    _ensure_import_path()
    parser = _build_parser()
    args = parser.parse_args()

    from kimodo.model.llm2vec_int8 import LLM2VecInt8Encoder, build_quantized_bundle

    source_dir = Path(args.source_dir).resolve()
    output_dir = Path(args.output_dir).resolve()

    print(f"[STEP] Building Torch CPU INT8 text encoder")
    print(f"[INFO] source_dir={source_dir}")
    print(f"[INFO] output_dir={output_dir}")

    built_dir = build_quantized_bundle(
        source_dir=source_dir,
        output_dir=output_dir,
        force=bool(args.force),
        verify=bool(args.verify),
    )
    print(f"[OK] INT8 asset created: {built_dir}")

    if args.verify:
        print(f"[STEP] Verifying INT8 asset with smoke prompt: {SMOKE_PROMPT}")
        encoder = LLM2VecInt8Encoder(model_name_or_path=str(built_dir))
        embeddings, lengths = encoder(SMOKE_PROMPT)
        print(f"[OK] smoke_test_shape={tuple(embeddings.shape)} lengths={lengths}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
