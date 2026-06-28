#!/usr/bin/env python
from __future__ import annotations

import argparse
import importlib.util
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
from typing import Sequence


SUBCOMMANDS = {"setup", "run"}
RUN_WAIT_TIMEOUT_SEC = 60 * 60
RUN_WAIT_POLL_SEC = 1
RUN_LOCK_HELD_ENV = "KIMODO_RUN_LOCK_HELD"


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


def _normalize_argv(argv: Sequence[str]) -> list[str]:
    items = list(argv)
    if items and items[0] == "__inner__":
        return items
    if items and items[0] in SUBCOMMANDS:
        return items
    return ["run", *items]


def _outer_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("action", choices=sorted(SUBCOMMANDS))
    parser.add_argument("--venv")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--force-setup", action="store_true")
    parser.add_argument("--force-hf-download", action="store_true")
    parser.add_argument("--output")
    parser.add_argument("--log")
    return parser


def _print_help() -> int:
    print("Usage: python quickserver.py [setup|run] [options]")
    print("  setup           build or reuse the venv")
    print("  run             setup + launch bridge (bridge provisions models on demand)")
    return 0


def _invoke_inner(root_dir: Path, normalized_args: Sequence[str], python_path: str) -> int:
    command = [python_path, str(root_dir / "quickserver.py"), "__inner__", *normalized_args]
    env = os.environ.copy()
    source_root = _source_root(root_dir)
    env["PYTHONPATH"] = str(source_root)
    env[RUN_LOCK_HELD_ENV] = "1"
    completed = subprocess.run(command, cwd=str(root_dir), env=env)
    return int(completed.returncode)


def _read_key_value_file(path: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not path.exists():
        return data
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except Exception:
        return data
    for line in lines:
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip().strip('"')
    return data


def _pid_is_running(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        import ctypes.wintypes

        handle = ctypes.windll.kernel32.OpenProcess(0x1000, False, int(pid))
        if not handle:
            return False
        try:
            code = ctypes.wintypes.DWORD()
            if not ctypes.windll.kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return False
            return int(code.value) == 259
        finally:
            ctypes.windll.kernel32.CloseHandle(handle)
    return Path(f"/proc/{pid}").exists()


def _try_read_port_file(port_file: Path) -> tuple[str, int] | None:
    if not port_file.exists():
        return None
    try:
        line = port_file.read_text(encoding="utf-8", errors="replace").splitlines()[0].strip()
    except Exception:
        return None
    if ":" not in line:
        return None
    host, port_text = line.rsplit(":", 1)
    host = host.strip()
    try:
        port = int(port_text.strip())
    except Exception:
        return None
    if not host or port <= 0:
        return None
    return host, port


def _probe_server(host: str, port: int, timeout_sec: float = 1.5) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout_sec) as sock:
            sock.settimeout(timeout_sec)
            sock.sendall(b'{"cmd":"ping"}\n')
            response = sock.recv(4096)
        return b'"pong"' in response or b'"ok"' in response
    except Exception:
        return False


def _write_run_lock(paths, owner_pid: int) -> None:
    paths.run_lock.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(paths.run_lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            "\n".join(
                [
                    f"started={time.strftime('%Y-%m-%d %H:%M:%S')}",
                    f"started_epoch={int(time.time())}",
                    f"owner_pid={owner_pid}",
                    "bridge_pid=0",
                    f'root="{paths.root_dir}"',
                    "",
                ]
            )
        )


def _wait_for_run_lock(paths, setup_mod) -> int | None:
    wait_deadline = time.time() + float(os.environ.get("KIMODO_RUN_WAIT_TIMEOUT_SEC", str(RUN_WAIT_TIMEOUT_SEC)))
    wait_poll_sec = float(os.environ.get("KIMODO_RUN_WAIT_POLL_SEC", str(RUN_WAIT_POLL_SEC)))
    port_file = paths.root_dir / "serverport"
    while time.time() < wait_deadline:
        if not paths.run_lock.exists():
            port_info = _try_read_port_file(port_file)
            if port_info is not None and _probe_server(*port_info):
                return 0
            return None

        metadata = _read_key_value_file(paths.run_lock)
        owner_pid = int(metadata.get("owner_pid", "0") or "0")
        if owner_pid > 0 and not _pid_is_running(owner_pid):
            setup_mod.archive_path(paths.run_lock, paths.recycle_dir)
            return None

        time.sleep(wait_poll_sec)

    if not paths.run_lock.exists():
        port_info = _try_read_port_file(port_file)
        if port_info is not None and _probe_server(*port_info):
            return 0
    return None


def _run_outer(normalized_args: Sequence[str]) -> int:
    root_dir = _root_dir()
    setup_mod = _load_setup_module(root_dir)
    if any(arg in ("-h", "--help") for arg in normalized_args):
        return _print_help()
    parsed, _unknown = _outer_parser().parse_known_args(list(normalized_args))
    action = parsed.action
    setup_log_path = None if action != "setup" else parsed.log

    if action == "setup":
        options = setup_mod.SetupCliOptions(
            output_mode=parsed.output,
            log_path=setup_log_path,
            force=bool(parsed.force or parsed.force_setup),
            requested_mode=None,
            venv_arg=parsed.venv,
        )
        result = setup_mod.run_setup_cli(root_dir=str(root_dir), options=options)
        return 0 if result.ok else int(result.exit_code)

    if parsed.venv:
        venv_python = setup_mod.resolve_venv_python_arg(parsed.venv, root_dir=str(root_dir))
        return _invoke_inner(root_dir, normalized_args, venv_python)

    setup_mode = setup_mod.setup_mode_from_env(None)
    run_device = None
    for index, value in enumerate(normalized_args):
        if value == "--device" and index + 1 < len(normalized_args):
            run_device = normalized_args[index + 1]
    if str(run_device or "").strip().lower() == "cpu":
        setup_mode = "cpu"

    options = setup_mod.SetupCliOptions(
        output_mode=parsed.output,
        log_path=str(root_dir / "log" / "setup.log"),
        force=bool(parsed.force_setup),
        requested_mode=setup_mode,
        venv_arg=None,
    )
    paths = setup_mod.discover_project_paths(str(root_dir))
    try:
        try:
            _write_run_lock(paths, os.getpid())
        except FileExistsError:
            wait_rc = _wait_for_run_lock(paths, setup_mod)
            if wait_rc is not None:
                return wait_rc
            _write_run_lock(paths, os.getpid())

        result = setup_mod.run_setup_cli(root_dir=str(root_dir), options=options)
        if not result.ok:
            return int(result.exit_code)
        return _invoke_inner(root_dir, normalized_args, result.venv_python)
    finally:
        if paths.run_lock.exists():
            metadata = _read_key_value_file(paths.run_lock)
            owner_pid = int(metadata.get("owner_pid", "0") or "0")
            if owner_pid <= 0 or owner_pid == os.getpid():
                setup_mod.archive_path(paths.run_lock, paths.recycle_dir)


def _run_inner(normalized_args: Sequence[str]) -> int:
    root_dir = _root_dir()
    source_root = _source_root(root_dir)
    if str(source_root) not in sys.path:
        sys.path.insert(0, str(source_root))

    from kimodo.bridge.quickserver_cli import main as inner_main

    return int(inner_main(list(normalized_args), root_dir=str(root_dir), source_root=str(source_root)))


def main(argv: Sequence[str] | None = None) -> int:
    raw_args = list(sys.argv[1:] if argv is None else argv)
    if raw_args and raw_args[0] == "__inner__":
        return _run_inner(raw_args[1:])
    if raw_args in (["-h"], ["--help"]):
        return _print_help()
    if not raw_args:
        raw_args = ["run"]
    normalized_args = _normalize_argv(raw_args)
    return _run_outer(normalized_args)


if __name__ == "__main__":
    raise SystemExit(main())
