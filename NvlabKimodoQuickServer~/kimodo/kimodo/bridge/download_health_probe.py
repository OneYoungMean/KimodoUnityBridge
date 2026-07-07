from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import subprocess
import sys
import time
import urllib.request
from typing import Any


DEFAULT_DURATION_SEC = 60 * 5
STREAM_CHUNK_SIZE = 1024 * 1024


def _runtime_root() -> Path:
    return Path(__file__).resolve().parents[3]


def _source_root(runtime_root: Path) -> Path:
    candidate = runtime_root / "kimodo"
    if (candidate / "pyproject.toml").is_file():
        return candidate
    return runtime_root


def _resolve_shared_env_python(runtime_root: Path) -> str:
    for env_name in ("Env", "Env~"):
        candidate = runtime_root / env_name
        if os.name == "nt":
            python_path = candidate / "Scripts" / "python.exe"
        else:
            python_path = candidate / "bin" / "python"
        if python_path.exists():
            return str(python_path)
    raise RuntimeError("Shared Env/Env~ python was not found.")


def _resolve_uv_bin(runtime_root: Path) -> str:
    candidates = []
    if os.name == "nt":
        candidates.extend(
            [
                runtime_root / "program" / "exe" / "uv" / "uv.exe",
                runtime_root / "program" / "exe" / "uv" / "uv",
            ]
        )
    else:
        candidates.extend(
            [
                runtime_root / "program" / "exe" / "uv" / "uv",
                runtime_root / "program" / "exe" / "uv" / "uv.exe",
            ]
        )

    for candidate in candidates:
        if candidate.exists():
            return str(candidate)

    uv_from_path = shutil.which("uv")
    if uv_from_path:
        return uv_from_path
    raise RuntimeError("uv binary was not found.")


def _resolve_uv_artifact() -> str:
    system = platform.system()
    machine = platform.machine().lower()
    if system == "Windows":
        if machine in {"amd64", "x86_64"}:
            return "uv-x86_64-pc-windows-msvc.zip"
        raise RuntimeError(f"Unsupported Windows architecture for uv probe: {machine}")
    if system == "Linux":
        if machine in {"x86_64", "amd64"}:
            return "uv-x86_64-unknown-linux-gnu.tar.gz"
        if machine in {"arm64", "aarch64"}:
            return "uv-aarch64-unknown-linux-gnu.tar.gz"
        raise RuntimeError(f"Unsupported Linux architecture for uv probe: {machine}")
    if system == "Darwin":
        if machine in {"x86_64", "amd64"}:
            return "uv-x86_64-apple-darwin.tar.gz"
        if machine in {"arm64", "aarch64"}:
            return "uv-aarch64-apple-darwin.tar.gz"
        raise RuntimeError(f"Unsupported macOS architecture for uv probe: {machine}")
    raise RuntimeError(f"Unsupported platform for uv probe: {system}")


def _torch_probe_definitions() -> list[dict[str, Any]]:
    system = platform.system()
    if system == "Darwin":
        return [
            {
                "name": "torch_default_index",
                "kind": "command",
                "command_type": "torch_default_index",
                "target_subdir": "torch_default_index",
            }
        ]

    return [
        {
            "name": "torch_official_cu128",
            "kind": "command",
            "command_type": "torch_official_cu128",
            "target_subdir": "torch_official_cu128",
        },
        {
            "name": "torch_aliyun_mirror",
            "kind": "command",
            "command_type": "torch_aliyun_mirror",
            "target_subdir": "torch_aliyun_mirror",
        },
    ]


def _default_output_dir(runtime_root: Path) -> Path:
    stamp = time.strftime("%Y%m%d_%H%M%S")
    return runtime_root / "download_probe_runs" / stamp


def _write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")


def _dir_size_bytes(path: Path) -> int:
    if not path.exists():
        return 0
    total = 0
    for child in path.rglob("*"):
        if child.is_file():
            try:
                total += child.stat().st_size
            except OSError:
                pass
    return total


def _terminate_process(proc: subprocess.Popen[Any], timeout_sec: float = 10.0) -> None:
    if proc.poll() is not None:
        return
    try:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(proc.pid), "/F", "/T"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            proc.wait(timeout=timeout_sec)
            return
        proc.terminate()
        proc.wait(timeout=timeout_sec)
    except Exception:
        try:
            proc.kill()
        except Exception:
            pass


def _probe_stream(name: str, url: str, output_path: Path, duration_sec: int) -> dict[str, Any]:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + duration_sec
    bytes_written = 0
    status = "failed"
    message = ""
    started = time.monotonic()
    try:
        with urllib.request.urlopen(url, timeout=30) as response, output_path.open("wb") as stream:
            status_code = getattr(response, "status", None) or response.getcode()
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    status = "passed"
                    message = f"Timed probe window reached with active download from {url}"
                    break
                chunk = response.read(STREAM_CHUNK_SIZE)
                if not chunk:
                    status = "passed"
                    message = f"Download completed before timeout from {url}"
                    break
                stream.write(chunk)
                bytes_written += len(chunk)
            stream.flush()
    except Exception as exc:
        message = f"{type(exc).__name__}: {exc}"
        status = "failed"

    if status == "passed" and bytes_written <= 0:
        status = "failed"
        message = "No bytes were written during the probe window."

    return {
        "name": name,
        "kind": "stream",
        "url": url,
        "output_path": str(output_path),
        "bytes_written": bytes_written,
        "elapsed_sec": round(time.monotonic() - started, 3),
        "status": status,
        "message": message,
    }


def _probe_curl_download(name: str, url: str, output_path: Path, log_dir: Path, duration_sec: int) -> dict[str, Any]:
    curl_bin = shutil.which("curl")
    if not curl_bin:
        return {
            "name": name,
            "kind": "command",
            "command": [],
            "stdout_log": "",
            "stderr_log": "",
            "bytes_observed": 0,
            "elapsed_sec": 0.0,
            "status": "failed",
            "message": "curl was not found on PATH.",
        }

    output_path.parent.mkdir(parents=True, exist_ok=True)
    command = [
        curl_bin,
        "-L",
        "--fail",
        "--silent",
        "--show-error",
        "--output",
        str(output_path),
        url,
    ]
    return _probe_command(
        name=name,
        command=command,
        env=os.environ.copy(),
        working_dir=output_path.parent,
        monitor_dirs=[output_path.parent],
        log_dir=log_dir,
        duration_sec=duration_sec,
    )


def _torch_command(command_type: str, uv_bin: str, python_exe: str, target_dir: Path, cache_dir: Path) -> tuple[list[str], dict[str, str]]:
    default_index = os.environ.get("KIMODO_PIP_INDEX_URL", "https://pypi.tuna.tsinghua.edu.cn/simple").strip()
    base = [
        uv_bin,
        "pip",
        "install",
        "--python",
        python_exe,
        "--target",
        str(target_dir),
        "--link-mode",
        "copy",
        "--default-index",
        default_index,
        "--no-config",
        "--no-deps",
    ]
    env = os.environ.copy()
    env["UV_CACHE_DIR"] = str(cache_dir)
    env["UV_NO_CONFIG"] = "1"

    if command_type == "torch_official_cu128":
        return (
            base
            + [
                "--torch-backend",
                "cu128",
                "torch==2.11.0",
            ],
            env,
        )
    if command_type == "torch_aliyun_mirror":
        return (
            base
            + [
                "--find-links",
                "https://mirrors.aliyun.com/pytorch-wheels/cu128",
                "torch==2.11.0",
            ],
            env,
        )
    if command_type == "torch_default_index":
        return (
            base
            + [
                "torch",
            ],
            env,
        )
    raise RuntimeError(f"Unsupported torch probe command type: {command_type}")


def _probe_command(
    name: str,
    command: list[str],
    env: dict[str, str],
    working_dir: Path,
    monitor_dirs: list[Path],
    log_dir: Path,
    duration_sec: int,
) -> dict[str, Any]:
    log_dir.mkdir(parents=True, exist_ok=True)
    stdout_path = log_dir / f"{name}.stdout.log"
    stderr_path = log_dir / f"{name}.stderr.log"
    started = time.monotonic()
    with stdout_path.open("w", encoding="utf-8", newline="\n") as stdout_stream, stderr_path.open(
        "w", encoding="utf-8", newline="\n"
    ) as stderr_stream:
        proc = subprocess.Popen(
            command,
            cwd=str(working_dir),
            env=env,
            stdout=stdout_stream,
            stderr=stderr_stream,
            text=True,
        )
        deadline = time.monotonic() + duration_sec
        max_bytes = 0
        while time.monotonic() < deadline:
            rc = proc.poll()
            max_bytes = max(max_bytes, sum(_dir_size_bytes(path) for path in monitor_dirs))
            if rc is not None:
                if rc == 0:
                    return {
                        "name": name,
                        "kind": "command",
                        "command": command,
                        "stdout_log": str(stdout_path),
                        "stderr_log": str(stderr_path),
                        "bytes_observed": max_bytes,
                        "elapsed_sec": round(time.monotonic() - started, 3),
                        "status": "passed" if max_bytes > 0 else "failed",
                        "message": "Command completed successfully." if max_bytes > 0 else "Command exited cleanly but no download output was observed.",
                    }
                return {
                    "name": name,
                    "kind": "command",
                    "command": command,
                    "stdout_log": str(stdout_path),
                    "stderr_log": str(stderr_path),
                    "bytes_observed": max_bytes,
                    "elapsed_sec": round(time.monotonic() - started, 3),
                    "status": "failed",
                    "message": f"Command exited with rc={rc}",
                }
            time.sleep(1.0)

        max_bytes = max(max_bytes, sum(_dir_size_bytes(path) for path in monitor_dirs))
        stderr_size = stderr_path.stat().st_size if stderr_path.exists() else 0
        stdout_size = stdout_path.stat().st_size if stdout_path.exists() else 0
        _terminate_process(proc)
        timed_out_ok = max_bytes > 0 or stdout_size > 0 or stderr_size == 0
        return {
            "name": name,
            "kind": "command",
            "command": command,
            "stdout_log": str(stdout_path),
            "stderr_log": str(stderr_path),
            "bytes_observed": max_bytes,
            "elapsed_sec": round(time.monotonic() - started, 3),
            "status": "passed" if timed_out_ok else "failed",
            "message": (
                "Timed probe window reached with active download."
                if max_bytes > 0
                else "Timed probe window reached without explicit download errors."
            )
            if timed_out_ok
            else "Timed probe window reached without observable output and with error logs present.",
        }


def _internal_model_download(runtime_root: Path, site_name: str, target_dir: Path) -> int:
    sys.path.insert(0, str(_source_root(runtime_root)))
    from kimodo.bridge import quickserver_assets as assets

    model = assets.resolve_main_model(assets.DEFAULT_MODEL_NAME)
    asset = assets.AssetSpec(
        label="main model",
        local_dir_name=model.local_name,
        modelscope_repo=model.modelscope_repo,
        huggingface_repo=model.huggingface_repo,
    )
    site = assets.DownloadSite(site_name)
    target_dir.mkdir(parents=True, exist_ok=True)
    if site == assets.DownloadSite.HUGGINGFACE:
        assets.download_via_huggingface(asset, target_dir)
    else:
        class _Logger:
            def log(self, message: str) -> None:
                print(message, flush=True)

        assets.download_via_modelscope(asset, target_dir, _Logger())
    return 0


def _probe_model_download(
    runtime_root: Path,
    shared_env_python: str,
    site_name: str,
    target_dir: Path,
    log_dir: Path,
    duration_sec: int,
) -> dict[str, Any]:
    command = [
        shared_env_python,
        str(Path(__file__).resolve()),
        "--internal-model-download",
        "--runtime-root",
        str(runtime_root),
        "--site",
        site_name,
        "--target-dir",
        str(target_dir),
    ]
    return _probe_command(
        name=f"main_model_{site_name}",
        command=command,
        env=os.environ.copy(),
        working_dir=runtime_root,
        monitor_dirs=[target_dir],
        log_dir=log_dir,
        duration_sec=duration_sec,
    )


def run_suite(runtime_root: Path, output_dir: Path, duration_sec: int) -> dict[str, Any]:
    output_dir.mkdir(parents=True, exist_ok=True)
    log_dir = output_dir / "logs"
    stream_dir = output_dir / "streams"
    torch_dir = output_dir / "torch"
    model_dir = output_dir / "models"

    shared_env_python = _resolve_shared_env_python(runtime_root)
    uv_bin = _resolve_uv_bin(runtime_root)
    uv_artifact = _resolve_uv_artifact()
    uv_version = "0.11.25"

    results: list[dict[str, Any]] = []

    uv_sources = [
        ("uv_github", f"https://github.com/astral-sh/uv/releases/download/{uv_version}/{uv_artifact}"),
    ]
    for name, url in uv_sources:
        results.append(_probe_curl_download(name, url, stream_dir / f"{name}.partial", log_dir, duration_sec))

    for definition in _torch_probe_definitions():
        target_dir = torch_dir / definition["target_subdir"] / "target"
        cache_dir = torch_dir / definition["target_subdir"] / "cache"
        target_dir.mkdir(parents=True, exist_ok=True)
        cache_dir.mkdir(parents=True, exist_ok=True)
        command, env = _torch_command(definition["command_type"], uv_bin, shared_env_python, target_dir, cache_dir)
        results.append(
            _probe_command(
                name=definition["name"],
                command=command,
                env=env,
                working_dir=runtime_root,
                monitor_dirs=[target_dir, cache_dir],
                log_dir=log_dir,
                duration_sec=duration_sec,
            )
        )

    for site_name in ("modelscope", "huggingface"):
        results.append(
            _probe_model_download(
                runtime_root=runtime_root,
                shared_env_python=shared_env_python,
                site_name=site_name,
                target_dir=model_dir / site_name,
                log_dir=log_dir,
                duration_sec=duration_sec,
            )
        )

    ok = all(item["status"] == "passed" for item in results)
    payload = {
        "ok": ok,
        "duration_sec": duration_sec,
        "runtime_root": str(runtime_root),
        "output_dir": str(output_dir),
        "results": results,
    }
    _write_json(output_dir / "summary.json", payload)
    return payload


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Kimodo download health probe")
    parser.add_argument("--runtime-root", default=str(_runtime_root()))
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--duration-sec", type=int, default=int(os.environ.get("KIMODO_DOWNLOAD_PROBE_DURATION_SEC", str(DEFAULT_DURATION_SEC))))
    parser.add_argument("--internal-model-download", action="store_true")
    parser.add_argument("--site", default="")
    parser.add_argument("--target-dir", default="")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(list(sys.argv[1:] if argv is None else argv))
    runtime_root = Path(args.runtime_root).resolve()

    if args.internal_model_download:
        if not args.site or not args.target_dir:
            raise RuntimeError("--internal-model-download requires --site and --target-dir")
        return _internal_model_download(runtime_root, args.site, Path(args.target_dir).resolve())

    output_dir = Path(args.output_dir).resolve() if args.output_dir else _default_output_dir(runtime_root)
    payload = run_suite(runtime_root=runtime_root, output_dir=output_dir, duration_sec=max(1, int(args.duration_sec)))
    print(json.dumps(payload, indent=2, ensure_ascii=False))
    return 0 if payload["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
