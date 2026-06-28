from __future__ import annotations

import argparse
import ctypes
import os
import signal
from pathlib import Path
import socket
import subprocess
import sys
import time
from typing import Sequence

from . import quickserver_assets as assets
from .quickserver_setup import ProjectPaths, SetupLogger, archive_path, discover_project_paths


BRIDGE_LOG_NAME = "bridge_server.log"
WATCHDOG_LOG_NAME = "watchdog.log"
ALLOW_MULTI_SERVER_ENV_KEYS = ("KIMODO_ALLOW_MULTI_SERVER", "ALLOWMULTISERVER", "allowmultiserver")
RUN_LOCK_HELD_ENV = "KIMODO_RUN_LOCK_HELD"


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Kimodo QuickServer unified Python entrypoint")
    subparsers = parser.add_subparsers(dest="action", required=True)

    def add_common(run_parser: argparse.ArgumentParser) -> None:
        run_parser.add_argument("--model", default=assets.DEFAULT_MODEL_NAME)
        run_parser.add_argument("--highvram", action="store_true")
        run_parser.add_argument("--force-hf-download", action="store_true")
        run_parser.add_argument("--output", choices=("console", "file"), default="console")
        run_parser.add_argument("--log")
        run_parser.add_argument("--models-root")
        run_parser.add_argument("--venv")
        run_parser.add_argument("--device")
        run_parser.add_argument("--force-setup", action="store_true")
        run_parser.add_argument("--watchpid")
        run_parser.add_argument("--unlock-stale", action="store_true")
        run_parser.add_argument("--force", action="store_true")

    add_common(subparsers.add_parser("run"))
    return parser


def _build_bridge_command(
    paths: ProjectPaths,
    resolved_model: assets.ResolvedModel,
    device: str | None,
    *,
    force_hf_download: bool,
) -> list[str]:
    command = [
        sys.executable,
        "-u",
        "-m",
        "kimodo.bridge.bridge_server",
        "--model",
        resolved_model.local_name,
        "--kimodo-root",
        str(paths.root_dir),
    ]
    if device:
        command.extend(["--device", device])
    if force_hf_download:
        command.append("--force-hf-download")
    return command


def _project_paths(root_dir: str) -> ProjectPaths:
    return discover_project_paths(root_dir)


def _prepare_logger(
    paths: ProjectPaths,
    output_mode: str,
    log_path: str | None,
    default_name: str,
    append: bool = False,
) -> SetupLogger:
    final_log_path = Path(log_path).resolve() if log_path else paths.log_dir / default_name
    paths.log_dir.mkdir(parents=True, exist_ok=True)
    return SetupLogger(output_mode, final_log_path, append=append)


def _runtime_import_preflight(logger: SetupLogger) -> None:
    import torch
    import kimodo  # noqa: F401

    logger.log(f"torch={torch.__version__}")
    logger.log(f"cuda={torch.version.cuda}")
    try:
        import motion_correction  # noqa: F401

        logger.log("motion_correction=available")
    except Exception as exc:
        logger.log(f"[WARN] motion_correction unavailable: {exc}")


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


def _allow_multi_server() -> bool:
    for key in ALLOW_MULTI_SERVER_ENV_KEYS:
        value = os.environ.get(key, "").strip().lower()
        if value in {"1", "true", "yes", "on"}:
            return True
    return False


def _server_signature(resolved_model: assets.ResolvedModel, models_root: Path, runtime_hints: assets.RuntimeHints, highvram: bool) -> str:
    return "|".join(
        [
            f"model={resolved_model.local_name}",
            f"models_root={models_root.resolve()}",
            f"device={runtime_hints.normalized_device or 'auto'}",
            f"highvram={int(bool(highvram))}",
        ]
    )


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


def _write_run_lock(paths: ProjectPaths, owner_pid: int, bridge_pid: int, signature: str) -> None:
    paths.run_lock.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(str(paths.run_lock), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(
            "\n".join(
                [
                    f"started={time.strftime('%Y-%m-%d %H:%M:%S')}",
                    f"started_epoch={int(time.time())}",
                    f"owner_pid={owner_pid}",
                    f"bridge_pid={bridge_pid}",
                    f'signature="{signature}"',
                    f'root="{paths.root_dir}"',
                    "",
                ]
            )
        )


def _refresh_run_lock(paths: ProjectPaths, owner_pid: int, bridge_pid: int, signature: str) -> None:
    paths.run_lock.write_text(
        "\n".join(
            [
                f"started={time.strftime('%Y-%m-%d %H:%M:%S')}",
                f"started_epoch={int(time.time())}",
                f"owner_pid={owner_pid}",
                f"bridge_pid={bridge_pid}",
                f'signature="{signature}"',
                f'root="{paths.root_dir}"',
                "",
            ]
        ),
        encoding="utf-8",
        newline="\n",
    )


def _wait_for_existing_server(paths: ProjectPaths, signature: str, logger: SetupLogger) -> int | None:
    startup_interval = max(1, int(os.environ.get("KIMODO_WATCHDOG_STARTUP_INTERVAL_SEC", "1")))
    wait_timeout = startup_interval * max(10, int(os.environ.get("KIMODO_WATCHDOG_STARTUP_MAX_FAILS", "180")))
    wait_until = time.time() + wait_timeout
    while time.time() < wait_until:
        metadata = _read_key_value_file(paths.run_lock)
        lock_signature = metadata.get("signature", "")
        owner_pid = int(metadata.get("owner_pid", "0") or "0")
        bridge_pid = int(metadata.get("bridge_pid", "0") or "0")
        if lock_signature and lock_signature != signature:
            logger.log("[ERROR] Existing server is running with different params. Set KIMODO_ALLOW_MULTI_SERVER=1 to bypass the singleton lock.")
            return 1

        port_info = _try_read_port_file(paths.root_dir / "serverport")
        if not paths.run_lock.exists():
            if port_info is not None and _probe_server(*port_info):
                logger.log("[INFO] Shared startup lock released and server is ready.")
                return 0
            return None

        if owner_pid > 0 and not _pid_is_running(owner_pid):
            logger.log(f"[WARN] Found stale run lock owned by pid={owner_pid}. Cleaning it up.")
            archive_path(paths.run_lock, paths.recycle_dir)
            return None

        if bridge_pid > 0 and not _pid_is_running(bridge_pid) and port_info is None:
            logger.log(f"[WARN] Found stale bridge pid={bridge_pid} without serverport. Cleaning run lock.")
            archive_path(paths.run_lock, paths.recycle_dir)
            return None

        logger.log("[INFO] Another run_server launcher is bringing up the bridge. Waiting for the shared instance...")
        time.sleep(startup_interval)

    port_info = _try_read_port_file(paths.root_dir / "serverport")
    if port_info is not None and _probe_server(*port_info):
        logger.log("[WARN] Shared startup lock wait timed out, but server is already responding. Reusing shared instance.")
        return 0

    logger.log("[WARN] Existing server signature matches, but probe failed. Restarting...")
    if paths.run_lock.exists():
        archive_path(paths.run_lock, paths.recycle_dir)
    return None


def _launch_bridge(paths: ProjectPaths, args: argparse.Namespace, logger: SetupLogger) -> int:
    bridge_log_path = logger.log_path
    watchdog_log_path = paths.log_dir / WATCHDOG_LOG_NAME
    bridge_pid_file = paths.root_dir / ".bridge.pid"
    port_file = paths.root_dir / "serverport"
    allow_multi_server = _allow_multi_server()
    run_lock_held_from_outer = os.environ.get(RUN_LOCK_HELD_ENV, "").strip() == "1"
    resolved_model = assets.resolve_main_model(args.model)
    models_root, _using_external_models = assets.resolve_models_root(paths.root_dir, args.models_root)
    runtime_hints = assets.normalize_runtime_hints(args.device)
    encoder_route = assets.choose_prepare_encoder_route(bool(args.highvram), runtime_hints)
    encoder_layout = assets.select_text_encoder_layout_for_route(encoder_route, models_root)
    signature = _server_signature(resolved_model, models_root, runtime_hints, bool(args.highvram))

    if not allow_multi_server and not run_lock_held_from_outer:
        existing_port = _try_read_port_file(port_file)
        if existing_port is not None and _probe_server(*existing_port):
            logger.log("[INFO] Existing server already responding. Reusing shared instance.")
            return 0
        existing_rc = _wait_for_existing_server(paths, signature, logger) if paths.run_lock.exists() else None
        if existing_rc is not None:
            return existing_rc
        try:
            _write_run_lock(paths, os.getpid(), 0, signature)
        except FileExistsError:
            existing_rc = _wait_for_existing_server(paths, signature, logger)
            if existing_rc is not None:
                return existing_rc
            _write_run_lock(paths, os.getpid(), 0, signature)

    archive_path(port_file, paths.recycle_dir)
    archive_path(bridge_pid_file, paths.recycle_dir)
    if args.output == "file":
        archive_path(bridge_log_path, paths.recycle_dir)
        archive_path(watchdog_log_path, paths.recycle_dir)

    runtime_env = assets.build_runtime_env(
        root_dir=paths.root_dir,
        source_root=paths.source_root,
        models_root=models_root,
        highvram=bool(args.highvram),
        hints=runtime_hints,
        encoder_route=encoder_route,
        encoder_layout_id=encoder_layout.layout_id,
    )
    runtime_env.update(assets.build_runtime_cache_env(paths.root_dir))
    runtime_env["KIMODO_IDLE_TIMEOUT_SEC"] = os.environ.get("KIMODO_IDLE_TIMEOUT_SEC", "600")

    for cache_dir in (
        Path(runtime_env["HF_HOME"]),
        Path(runtime_env["TRANSFORMERS_CACHE"]),
        Path(runtime_env["HF_HUB_CACHE"]),
    ):
        cache_dir.mkdir(parents=True, exist_ok=True)

    launch_env = os.environ.copy()
    assets.scrub_removed_runtime_env(launch_env)
    launch_env.update(runtime_env)
    launch_env["KIMODO_BRIDGE_LOG"] = str(bridge_log_path)
    launch_env["KIMODO_BRIDGE_LOG_DIRECT_ONLY"] = "1" if args.output == "file" else "0"
    bridge_log_path.parent.mkdir(parents=True, exist_ok=True)
    bridge_log_path.touch(exist_ok=True)

    command = _build_bridge_command(
        paths,
        resolved_model,
        runtime_hints.normalized_device,
        force_hf_download=bool(args.force_hf_download),
    )

    logger.log("[STEP] Launching bridge...")
    popen_kwargs = {
        "cwd": str(paths.root_dir),
        "env": launch_env,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
    }
    run_lock_released = False

    def _release_run_lock_after_startup() -> None:
        nonlocal run_lock_released
        if allow_multi_server or run_lock_released:
            return
        archive_path(paths.run_lock, paths.recycle_dir)
        run_lock_released = True

    try:
        if args.output == "file":
            with bridge_log_path.open("a", encoding="utf-8", newline="\n") as bridge_log_stream:
                popen_kwargs["stdout"] = bridge_log_stream
                popen_kwargs["stderr"] = bridge_log_stream
                launch_proc = subprocess.Popen(command, **popen_kwargs)
                bridge_pid_file.write_text(f"{launch_proc.pid}\n", encoding="utf-8", newline="\n")
                if not allow_multi_server:
                    _refresh_run_lock(paths, os.getpid(), launch_proc.pid, signature)
                logger.log(f"[INFO] Model: {resolved_model.local_name}")
                logger.log(f"[INFO] Models root: {models_root}")
                logger.log(f"[INFO] Runtime device: {runtime_hints.normalized_device or '<auto>'}")
                logger.log(f"[INFO] Text encoder route: {encoder_route}")
                logger.log(f"[INFO] Text encoder layout: {encoder_layout.layout_id}")
                logger.log(f"[INFO] Force HF download: {bool(args.force_hf_download)}")
                logger.log(f"[INFO] Text encoder dir: {runtime_env['KIMODO_LLM2VEC_DIR']}")
                if runtime_env["KIMODO_LLM2VEC_PEFT_DIR"]:
                    logger.log(f"[INFO] Text encoder PEFT dir: {runtime_env['KIMODO_LLM2VEC_PEFT_DIR']}")
                logger.log(f"[INFO] Bridge PID: {launch_proc.pid}")
                watchdog_rc = _run_watchdog(paths, launch_proc.pid, port_file, bridge_log_path, launch_env, args.watchpid, on_started=_release_run_lock_after_startup)
                launch_proc.poll()
        else:
            launch_proc = subprocess.Popen(command, **popen_kwargs)
            bridge_pid_file.write_text(f"{launch_proc.pid}\n", encoding="utf-8", newline="\n")
            if not allow_multi_server:
                _refresh_run_lock(paths, os.getpid(), launch_proc.pid, signature)
            logger.log(f"[INFO] Model: {resolved_model.local_name}")
            logger.log(f"[INFO] Models root: {models_root}")
            logger.log(f"[INFO] Runtime device: {runtime_hints.normalized_device or '<auto>'}")
            logger.log(f"[INFO] Text encoder route: {encoder_route}")
            logger.log(f"[INFO] Text encoder layout: {encoder_layout.layout_id}")
            logger.log(f"[INFO] Force HF download: {bool(args.force_hf_download)}")
            logger.log(f"[INFO] Text encoder dir: {runtime_env['KIMODO_LLM2VEC_DIR']}")
            if runtime_env["KIMODO_LLM2VEC_PEFT_DIR"]:
                logger.log(f"[INFO] Text encoder PEFT dir: {runtime_env['KIMODO_LLM2VEC_PEFT_DIR']}")
            logger.log(f"[INFO] Bridge PID: {launch_proc.pid}")
            watchdog_rc = _run_watchdog(paths, launch_proc.pid, port_file, bridge_log_path, launch_env, args.watchpid, on_started=_release_run_lock_after_startup)
            launch_proc.poll()
        return watchdog_rc
    finally:
        archive_path(bridge_pid_file, paths.recycle_dir)
        if not allow_multi_server:
            archive_path(paths.run_lock, paths.recycle_dir)


def _run_watchdog(
    paths: ProjectPaths,
    bridge_pid: int,
    port_file: Path,
    bridge_log_path: Path,
    launch_env: dict[str, str],
    watchpid: str | None,
    on_started=None,
) -> int:
    startup_interval = int(os.environ.get("KIMODO_WATCHDOG_STARTUP_INTERVAL_SEC", "1"))
    startup_max_fails = int(os.environ.get("KIMODO_WATCHDOG_STARTUP_MAX_FAILS", "180"))
    runtime_interval = int(os.environ.get("KIMODO_WATCHDOG_RUNTIME_INTERVAL_SEC", "1"))
    idle_no_log_max = int(os.environ.get("KIMODO_WATCHDOG_IDLE_NOLOG_MAX", "300"))
    watchdog_log_path = paths.log_dir / WATCHDOG_LOG_NAME
    watchdog_log_path.parent.mkdir(parents=True, exist_ok=True)
    last_mtime = 0
    startup_fails = 0
    started_ok = False

    def _log_mtime_ns(path: Path) -> int:
        try:
            return int(path.stat().st_mtime_ns)
        except Exception:
            try:
                return int(path.stat().st_mtime * 1_000_000_000)
            except Exception:
                return 0

    def _write_watchdog(line: str) -> None:
        with watchdog_log_path.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(line + "\n")
            stream.flush()

    def _is_running(pid: int) -> bool:
        return _pid_is_running(pid)

    def _kill_bridge() -> None:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(bridge_pid), "/T", "/F"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            try:
                os.kill(bridge_pid, signal.SIGTERM)
            except Exception:
                pass

    def _kill_bridge_and_cleanup_endpoint() -> None:
        _kill_bridge()
        for _ in range(50):
            if not _is_running(bridge_pid):
                break
            time.sleep(0.1)
        _cleanup_endpoint_file()

    def _cleanup_endpoint_file() -> None:
        try:
            if port_file.exists():
                port_file.unlink()
                _write_watchdog(f"[INFO] Removed stale endpoint file: {port_file}")
        except Exception as exc:
            _write_watchdog(f"[WARN] Failed to remove stale endpoint file {port_file}: {exc}")

    _write_watchdog(f"[INFO] Bridge watchdog started. pid={bridge_pid} startup_interval={startup_interval}s startup_max_fails={startup_max_fails} runtime_interval={runtime_interval}s idle_nolog_max={idle_no_log_max}")

    while True:
        if not _is_running(bridge_pid):
            return 0 if started_ok else 1

        if not started_ok:
            port_info = _try_read_port_file(port_file)
            if port_info is not None and _probe_server(*port_info):
                started_ok = True
                if on_started is not None:
                    try:
                        on_started()
                    except Exception:
                        pass
                last_mtime = _log_mtime_ns(bridge_log_path)
                time.sleep(runtime_interval)
                continue
            startup_fails += 1
            if startup_fails >= startup_max_fails:
                _write_watchdog(f"[ERROR] server not ready within {startup_max_fails} checks. Killing pid={bridge_pid}")
                _kill_bridge_and_cleanup_endpoint()
                return 1
            _write_watchdog(f"[INFO] Waiting server readiness ({startup_fails}/{startup_max_fails})")
            time.sleep(startup_interval)
            continue

        if watchpid and os.name == "nt":
            if not _is_running(int(watchpid)):
                _write_watchdog(f"[WARN] Owner pid missing. owner_pid={watchpid} bridge_pid={bridge_pid}")
                _kill_bridge_and_cleanup_endpoint()
                return 0

        now_mtime = _log_mtime_ns(bridge_log_path)
        if now_mtime <= 0:
            now_mtime = last_mtime
        if now_mtime == last_mtime:
            idle_no_log_max -= 1
        else:
            idle_no_log_max = int(os.environ.get("KIMODO_WATCHDOG_IDLE_NOLOG_MAX", "300"))
            last_mtime = now_mtime
        if idle_no_log_max <= 0:
            _write_watchdog(f"[INFO] No bridge log update for runtime window. Killing pid={bridge_pid}")
            _kill_bridge_and_cleanup_endpoint()
            return 0
        time.sleep(runtime_interval)


def main(argv: Sequence[str] | None = None, *, root_dir: str | None = None, source_root: str | None = None) -> int:
    raw_args = list(argv or [])
    parser = _build_parser()
    if any(arg in ("-h", "--help") for arg in raw_args):
        parser.print_help()
        return 0
    args = parser.parse_args(raw_args)
    paths = _project_paths(root_dir or str(Path(__file__).resolve().parents[3]))
    bridge_log_path = Path(args.log).resolve() if getattr(args, "log", None) else paths.log_dir / BRIDGE_LOG_NAME
    watchdog_log_path = paths.log_dir / WATCHDOG_LOG_NAME

    if args.output == "file":
        archive_path(bridge_log_path, paths.recycle_dir)
        archive_path(watchdog_log_path, paths.recycle_dir)

    with _prepare_logger(paths, args.output, args.log, BRIDGE_LOG_NAME, append=False) as run_logger:
        try:
            run_logger.log("[STEP] Preflight runtime import check...")
            _runtime_import_preflight(run_logger)
            run_logger.log("[STEP] Bridge will provision required model assets on demand.")
            return _launch_bridge(paths, args, run_logger)
        except Exception as exc:
            run_logger.log(f"[ERROR] run failed: {exc}")
            return 1
