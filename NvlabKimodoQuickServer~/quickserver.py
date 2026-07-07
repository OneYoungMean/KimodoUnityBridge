#!/usr/bin/env python
from __future__ import annotations

import argparse
import importlib.util
from pathlib import Path
import sys
from typing import Sequence


def _root_dir() -> Path:
    return Path(__file__).resolve().parent


def _source_root(root_dir: Path) -> Path:
    candidate = root_dir / "kimodo"
    if (candidate / "pyproject.toml").is_file():
        return candidate
    if (root_dir / "pyproject.toml").is_file():
        return root_dir
    raise RuntimeError(f"Unable to locate source root from {root_dir}")


def _setup_module_path(root_dir: Path) -> Path:
    return _source_root(root_dir) / "kimodo" / "bridge" / "quickserver_setup.py"


def _load_setup_module(root_dir: Path):
    module_path = _setup_module_path(root_dir)
    module_name = "_quickserver_setup_bootstrap"
    spec = importlib.util.spec_from_file_location(module_name, module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load setup module from {module_path}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Kimodo QuickServer setup entry")
    subparsers = parser.add_subparsers(dest="action", required=True)

    setup_parser = subparsers.add_parser("setup")
    setup_parser.add_argument("--venv")
    setup_parser.add_argument("--force", action="store_true")
    setup_parser.add_argument("--force-setup", action="store_true")
    setup_parser.add_argument("--output")
    setup_parser.add_argument("--log")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _build_parser().parse_args(list(sys.argv[1:] if argv is None else argv))
    if args.action != "setup":
        raise RuntimeError(f"Unsupported action: {args.action}")

    root_dir = _root_dir()
    setup_mod = _load_setup_module(root_dir)
    options = setup_mod.SetupCliOptions(
        output_mode=args.output,
        log_path=args.log,
        force=bool(args.force or args.force_setup),
        requested_mode=None,
        venv_arg=args.venv,
    )
    result = setup_mod.run_setup_cli(root_dir=str(root_dir), options=options)
    return 0 if result.ok else int(result.exit_code)


if __name__ == "__main__":
    raise SystemExit(main())
