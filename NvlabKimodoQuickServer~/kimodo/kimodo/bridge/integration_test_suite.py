from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import platform
import re
import shutil
import socket
import subprocess
import sys
import time
import traceback
from dataclasses import dataclass, field
from typing import Any


TEST_TIMEOUT_SEC = 60 * 20
START_TIMEOUT_SEC = 60 * 20
PORT_POLL_SEC = 0.5
TPROMPT = "tpose"


@dataclass(frozen=True)
class ResourcePolicy:
    env_policy: str
    model_policy: str
    uv_policy: str = "reuse-available-uv"


@dataclass(frozen=True)
class TestCase:
    case_id: str
    name: str
    tags: tuple[str, ...]
    kind: str
    resources: ResourcePolicy
    params: dict[str, Any] = field(default_factory=dict)


POLICY_REUSE_EXISTING_ENV_AND_MODELS = ResourcePolicy(
    env_policy="reuse-existing-env",
    model_policy="reuse-existing-model-cache",
)
POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS = ResourcePolicy(
    env_policy="isolated-env-setup",
    model_policy="reuse-existing-model-cache",
)
POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS = ResourcePolicy(
    env_policy="reuse-existing-env",
    model_policy="isolated-models",
)
POLICY_FORCE_DOWNLOADED_UV = ResourcePolicy(
    env_policy="reuse-existing-env",
    model_policy="reuse-existing-model-cache",
    uv_policy="force-download-uv",
)
POLICY_DOWNLOAD_PROBE = ResourcePolicy(
    env_policy="reuse-existing-env",
    model_policy="probe-output-only",
    uv_policy="probe-only",
)


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[4]


def _runtime_root(repo_root: Path) -> Path:
    return repo_root / "NvlabKimodoQuickServer~"


def _read_serverport(path: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not path.exists():
        return data
    text = path.read_text(encoding="utf-8", errors="replace").strip()
    if not text:
        return data
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if not lines:
        return data
    first = lines[0]
    if ":" in first:
        host, port = first.rsplit(":", 1)
        data["host"] = host.strip()
        data["port"] = port.strip()
    for line in lines[1:]:
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip()
    return data


def _wait_for(predicate, timeout_sec: float, description: str) -> Any:
    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(PORT_POLL_SEC)
    raise TimeoutError(f"Timed out waiting for {description}.")


def _send_json(
    host: str,
    port: int,
    payload: dict[str, Any],
    read_binary: bool = False,
    timeout_sec: float = 30.0,
) -> tuple[dict[str, Any], bytes]:
    with socket.create_connection((host, port), timeout=30) as conn:
        conn.settimeout(timeout_sec)
        conn.sendall((json.dumps(payload) + "\n").encode("utf-8"))
        file = conn.makefile("rb")
        header_line = file.readline()
        if not header_line:
            raise RuntimeError("No response from server.")
        header = json.loads(header_line.decode("utf-8").strip())
        payload_bytes = b""
        if read_binary:
            length = int(header.get("byte_length") or 0)
            while length > 0:
                chunk = file.read(length)
                if not chunk:
                    break
                payload_bytes += chunk
                length -= len(chunk)
        return header, payload_bytes


def _list_cases() -> list[TestCase]:
    cases: list[TestCase] = [
        TestCase("T01", "Basic T-Pose Generate", ("basic", "smoke"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T02", "Double Start Same Params", ("basic", "multi-start"), "double_start_same", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T03", "Double Start Different Params", ("basic", "multi-start"), "double_start_diff", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T04", "Queue Order", ("basic", "queue"), "queue_order", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T05", "Stop Idle", ("basic", "stop"), "stop_idle", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T06", "Stop Generating", ("basic", "stop"), "stop_generating", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T07", "Cancel NonCurrent Boot", ("cancel", "boot"), "skip_placeholder", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T08", "Cancel NonCurrent CLI", ("cancel", "cli"), "cancel_queued", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T09", "Cancel Current Boot", ("cancel", "boot"), "abort_phase", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "boot"}),
        TestCase("T10", "Cancel Current SettingUpEnv Immediate", ("cancel", "setting_up_env"), "abort_phase", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env", "delay_sec": 0}),
        TestCase("T11", "Cancel Current SettingUpEnv 1s", ("cancel", "setting_up_env"), "abort_phase", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env", "delay_sec": 1}),
        TestCase("T12", "Cancel Current SettingUpEnv 61s", ("cancel", "setting_up_env", "slow"), "abort_phase", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env", "delay_sec": 61}),
        TestCase("T13", "Cancel Current SettingUpEnv 301s", ("cancel", "setting_up_env", "slow"), "abort_phase", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env", "delay_sec": 301}),
        TestCase("T14", "Cancel Current SettingUpEnv 601s", ("cancel", "setting_up_env", "slow"), "abort_phase", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env", "delay_sec": 601}),
        TestCase("T15", "Cancel Current Download Immediate", ("cancel", "download"), "abort_phase", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download", "delay_sec": 0}),
        TestCase("T16", "Cancel Current Download 1s", ("cancel", "download"), "abort_phase", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download", "delay_sec": 1}),
        TestCase("T17", "Cancel Current Download 61s", ("cancel", "download", "slow"), "abort_phase", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download", "delay_sec": 61}),
        TestCase("T18", "Cancel Current Download 301s", ("cancel", "download", "slow"), "abort_phase", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download", "delay_sec": 301}),
        TestCase("T19", "Cancel Current Download 601s", ("cancel", "download", "slow"), "abort_phase", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download", "delay_sec": 601}),
        TestCase("T20", "Cancel Current LoadingRuntime", ("cancel", "loading_runtime"), "abort_phase", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "loading_runtime", "delay_sec": 0}),
        TestCase("T21", "Cancel Current Generating", ("cancel", "generating"), "cancel_active", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T22", "Cancel Empty Task Id", ("cancel", "invalid"), "cancel_invalid", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"mode": "empty"}),
        TestCase("T23", "Cancel Unknown Task Id", ("cancel", "invalid"), "cancel_invalid", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"mode": "unknown"}),
        TestCase("T24", "Cancel Finished Task Id", ("cancel", "invalid"), "cancel_finished", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T25", "Kill Owner Boot", ("owner-kill", "boot"), "owner_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "boot"}),
        TestCase("T26", "Kill Owner SettingUpEnv", ("owner-kill", "setting_up_env"), "owner_kill", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env"}),
        TestCase("T27", "Kill Owner Download", ("owner-kill", "download"), "owner_kill", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download"}),
        TestCase("T28", "Kill Owner LoadingRuntime", ("owner-kill", "loading_runtime"), "owner_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "loading_runtime"}),
        TestCase("T29", "Kill Owner Generating", ("owner-kill", "generating"), "owner_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "generating"}),
        TestCase("T30", "Owner Kill Recovery", ("owner-kill", "recovery"), "owner_kill_recovery", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T31", "Kill CLI Boot", ("cli-kill", "boot"), "cli_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "boot"}),
        TestCase("T32", "Kill CLI SettingUpEnv", ("cli-kill", "setting_up_env"), "cli_kill", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS, {"phase": "setting_up_env"}),
        TestCase("T33", "Kill CLI Download", ("cli-kill", "download"), "cli_kill", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"phase": "download"}),
        TestCase("T34", "Kill CLI LoadingRuntime", ("cli-kill", "loading_runtime"), "cli_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "loading_runtime"}),
        TestCase("T35", "Kill CLI Generating", ("cli-kill", "generating"), "cli_kill", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"phase": "generating"}),
        TestCase("T36", "No Cached Models", ("cache", "models"), "basic", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS),
        TestCase("T37", "No Cached UV", ("cache", "uv"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"uncached_uv": True}),
        TestCase("T38", "High VRAM", ("runtime", "highvram"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"highvram": True}),
        TestCase("T39", "Simulate VRAM 1G", ("runtime", "simulate-vram"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"simulate_vram_gb": 1}),
        TestCase("T40", "Simulate VRAM 4G", ("runtime", "simulate-vram"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"simulate_vram_gb": 4}),
        TestCase("T41", "Simulate VRAM 6G", ("runtime", "simulate-vram"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"simulate_vram_gb": 6}),
        TestCase("T42", "Force HuggingFace Download", ("download", "hf"), "basic", POLICY_REUSE_EXISTING_ENV_ISOLATED_MODELS, {"force_hf_download": True}),
        TestCase("T43", "No Existing Env", ("env", "setup"), "basic", POLICY_ISOLATED_ENV_REUSE_EXISTING_MODELS),
        TestCase("T44", "Reuse Existing Env", ("env", "reusable"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T45", "Reuse Existing Models", ("models", "reusable"), "basic", POLICY_REUSE_EXISTING_ENV_AND_MODELS),
        TestCase("T46", "Download Source Health Probe", ("probe", "network", "manual"), "download_probe", POLICY_DOWNLOAD_PROBE),
        TestCase("T47", "Force Downloaded UV", ("cache", "uv", "download", "network", "manual"), "basic", POLICY_FORCE_DOWNLOADED_UV, {"uncached_uv": True, "force_download_uv": True}),
        TestCase("T48", "Short Idle Timeout Override", ("timeout", "idle"), "idle_timeout_override", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"idle_timeout_sec": 3}),
        TestCase("T49", "Example Default T-Pose Batch", ("example", "smoke"), "example_tpose_batch", ResourcePolicy(
            env_policy="isolated-env-setup",
            model_policy="isolated-models",
            uv_policy="reuse-available-uv",
        )),
        TestCase("T50", "Example Default Startup Batch", ("example", "startup"), "example_startup_batch", ResourcePolicy(
            env_policy="isolated-env-setup",
            model_policy="isolated-models",
            uv_policy="reuse-available-uv",
        )),
        TestCase("T51", "Reject Legacy Start Command", ("protocol", "legacy"), "legacy_command_reject", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"cmd": "start"}),
        TestCase("T52", "Reject Legacy Stop Command", ("protocol", "legacy"), "legacy_command_reject", POLICY_REUSE_EXISTING_ENV_AND_MODELS, {"cmd": "stop"}),
    ]
    return cases


def _case_sort_key(case_id: str) -> int:
    match = re.fullmatch(r"[Tt](\d+)", case_id.strip())
    if not match:
        raise RuntimeError(f"Invalid test id: {case_id}")
    return int(match.group(1))


def _find_host_python() -> str:
    if os.name == "nt":
        for candidate in ("py", "python"):
            try:
                completed = subprocess.run([candidate, "--version"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            except OSError:
                continue
            if completed.returncode == 0:
                return candidate
    return sys.executable


def _pid_is_running(pid: int) -> bool:
    if pid <= 0:
        return False
    if os.name == "nt":
        completed = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            check=False,
        )
        return completed.returncode == 0 and str(pid) in (completed.stdout or "")
    return Path(f"/proc/{pid}").exists()


def _tracked_workspace_copy(repo_root: Path, target_dir: Path) -> None:
    files_blob = subprocess.check_output(["git", "-C", str(repo_root), "-c", "core.quotePath=false", "ls-files", "-z"])
    for rel in [entry.decode("utf-8", errors="surrogateescape") for entry in files_blob.split(b"\x00") if entry]:
        src = repo_root / rel
        dst = target_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        if src.exists():
            shutil.copy2(src, dst)
            continue

        blob = subprocess.check_output(["git", "-C", str(repo_root), "show", f"HEAD:{rel}"])
        dst.write_bytes(blob)


def _looks_like_reusable_env(candidate: Path) -> bool:
    if not candidate.exists() or not candidate.is_dir():
        return False
    if os.name == "nt":
        return (candidate / "Scripts" / "python.exe").exists()
    return (candidate / "bin" / "python").exists()


def _looks_like_reusable_models(candidate: Path) -> bool:
    if not candidate.exists() or not candidate.is_dir():
        return False
    for child in candidate.iterdir():
        if child.is_dir():
            return True
    return False


def _resolve_reusable_path(runtime_root: Path, names: tuple[str, ...], validator) -> str:
    for name in names:
        candidate = runtime_root / name
        if validator(candidate):
            return str(candidate)
    return ""


def _policy_uses_reusable_env(policy: ResourcePolicy) -> bool:
    return policy.env_policy == "reuse-existing-env"


def _policy_uses_reusable_models(policy: ResourcePolicy) -> bool:
    return policy.model_policy == "reuse-existing-model-cache"


class TestContext:
    def __init__(self, case: TestCase):
        self.case = case
        self.repo_root = _repo_root()
        self.runtime_root = _runtime_root(self.repo_root)
        self.run_root = self.runtime_root / "test_runs" / (time.strftime("%Y%m%d_%H%M%S") + "_" + case.case_id)
        self.workspace_root = self.run_root / "workspace"
        self.workspace_runtime = self.workspace_root / "NvlabKimodoQuickServer~"
        self.logs_dir = self.run_root / "logs"
        self.summary_path = self.run_root / "summary.json"
        self.logs_dir.mkdir(parents=True, exist_ok=True)
        self.launcher_proc: subprocess.Popen[str] | None = None
        self.owner_proc: subprocess.Popen[str] | None = None
        self.last_task_id = ""
        self.python_host = _find_host_python()
        self.reusable_env_path = _resolve_reusable_path(self.runtime_root, ("Env~", "Env"), _looks_like_reusable_env)
        self.reusable_models_path = _resolve_reusable_path(self.runtime_root, ("models~", "models"), _looks_like_reusable_models)

    def prepare_workspace(self) -> None:
        _tracked_workspace_copy(self.repo_root, self.workspace_root)

    @property
    def serverport_path(self) -> Path:
        return self.workspace_runtime / "serverport"

    @property
    def setup_log_path(self) -> Path:
        return self.workspace_runtime / "log" / "setup.log"

    @property
    def bootstrap_wait_log_path(self) -> Path:
        return self.workspace_runtime / "log" / "bootstrap_wait.log"

    @property
    def bridge_log_path(self) -> Path:
        return self.workspace_runtime / "log" / "bridge_server.log"

    def launcher_command(self) -> list[str]:
        if os.name == "nt":
            return ["cmd.exe", "/d", "/c", "call", str(self.workspace_runtime / "run_server.bat")]
        return ["bash", str(self.workspace_runtime / "run_server.sh")]

    def start_owner(self) -> int:
        self.owner_proc = subprocess.Popen([self.python_host, "-c", "import time; time.sleep(3600)"])
        return int(self.owner_proc.pid)

    def cleanup(self) -> None:
        for proc in (self.launcher_proc, self.owner_proc):
            if proc is None:
                continue
            if proc.poll() is None:
                _terminate_process_tree(proc)


def _terminate_process_tree(proc: subprocess.Popen[Any] | None, timeout_sec: float = 10.0) -> None:
    if proc is None:
        return
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


def _wait_for_bootstrap_phase(ctx: TestContext, phase: str) -> None:
    if phase == "boot":
        time.sleep(1.0)
        return

    if phase == "setting_up_env":
        _wait_for(lambda: ctx.setup_log_path.exists(), START_TIMEOUT_SEC, "setup log creation")
        return

    if phase == "download":
        def _download_seen() -> bool:
            if not ctx.bridge_log_path.exists():
                return False
            text = ctx.bridge_log_path.read_text(encoding="utf-8", errors="replace")
            return "asset plan:" in text or "Text encoder layout selected:" in text
        _wait_for(_download_seen, START_TIMEOUT_SEC, "download stage")
        return

    if phase == "loading_runtime":
        _wait_for(lambda: _read_serverport(ctx.serverport_path).get("state") == "loading_runtime", START_TIMEOUT_SEC, "loading_runtime stage")
        return

    if phase == "generating":
        _wait_for(lambda: _read_serverport(ctx.serverport_path).get("state") == "generating", START_TIMEOUT_SEC, "generating stage")
        return

    raise ValueError(f"Unsupported phase: {phase}")


def _start_launcher(
    ctx: TestContext,
    *,
    reuse_existing_env: bool,
    uncached_uv: bool = False,
    force_download_uv: bool = False,
    bootstrap_hold_sec: int | None = None,
    idle_timeout_sec: int | None = None,
) -> None:
    env = os.environ.copy()
    env["KIMODO_IDLE_TIMEOUT_SEC"] = str(int(idle_timeout_sec if idle_timeout_sec is not None else 120))
    if reuse_existing_env and ctx.reusable_env_path:
        env["KIMODO_VENV_PATH"] = ctx.reusable_env_path
    else:
        env.pop("KIMODO_VENV_PATH", None)
    if uncached_uv:
        env["KIMODO_UV_BIN"] = ""
        env["UV_NO_CACHE"] = "1"
    if force_download_uv:
        env["KIMODO_FORCE_DOWNLOAD_UV"] = "1"
        env["KIMODO_AUTO_INSTALL_UV"] = "1"
        env["KIMODO_UV_PROBE_TIMEOUT_SEC"] = "15"
    if bootstrap_hold_sec and bootstrap_hold_sec > 0:
        env["KIMODO_BOOTSTRAP_HOLD_SEC"] = str(int(bootstrap_hold_sec))

    stdout_path = ctx.logs_dir / "launcher.out.log"
    stderr_path = ctx.logs_dir / "launcher.err.log"
    stdout_stream = stdout_path.open("w", encoding="utf-8", newline="\n")
    stderr_stream = stderr_path.open("w", encoding="utf-8", newline="\n")
    ctx.launcher_proc = subprocess.Popen(
        ctx.launcher_command(),
        cwd=str(ctx.workspace_runtime),
        env=env,
        stdout=stdout_stream,
        stderr=stderr_stream,
        text=True,
    )


def _wait_for_server(ctx: TestContext) -> tuple[str, int]:
    def _endpoint():
        data = _read_serverport(ctx.serverport_path)
        host = data.get("host", "").strip()
        port_text = data.get("port", "").strip()
        if not host or not port_text:
            return None
        try:
            port = int(port_text)
        except ValueError:
            return None
        if port <= 0:
            return None
        pid_text = data.get("pid", "").strip()
        if pid_text:
            try:
                if not _pid_is_running(int(pid_text)):
                    return None
            except ValueError:
                return None
        try:
            with socket.create_connection((host, port), timeout=1.0):
                pass
        except OSError:
            return None
        return host, port
    return _wait_for(_endpoint, START_TIMEOUT_SEC, "serverport endpoint")


def _wait_for_server_shutdown(ctx: TestContext, timeout_sec: float = 60.0) -> None:
    def _stopped() -> bool:
        data = _read_serverport(ctx.serverport_path)
        if not data:
            return True
        pid_text = data.get("pid", "").strip()
        if pid_text:
            try:
                if not _pid_is_running(int(pid_text)):
                    return True
            except ValueError:
                return True
        host = data.get("host", "").strip()
        port_text = data.get("port", "").strip()
        if not host or not port_text:
            return True
        try:
            port = int(port_text)
        except ValueError:
            return True
        try:
            with socket.create_connection((host, port), timeout=1.0):
                return False
        except OSError:
            return True

    _wait_for(_stopped, timeout_sec, "server shutdown")


def _start_runtime(ctx: TestContext, *, highvram: bool = False, force_hf_download: bool = False, simulate_vram_gb: int | None = None, reuse_existing_models: bool = True, owner_pid: int = 0) -> tuple[str, int]:
    host, port = _wait_for_server(ctx)
    ctx.runtime_request_defaults = {
        "model": "Kimodo-SOMA-RP-v1",
        "highvram": bool(highvram),
        "force_cpu": False,
        "force_hf_download": bool(force_hf_download),
        "owner_pid": int(owner_pid),
    }
    if reuse_existing_models and ctx.reusable_models_path:
        ctx.runtime_request_defaults["models_root"] = ctx.reusable_models_path
    if simulate_vram_gb is not None:
        ctx.runtime_request_defaults["simulate_vram_gb"] = int(simulate_vram_gb)
    return host, port


def _generate_tpose(ctx: TestContext, host: str, port: int, *, task_id: str | None = None, duration: float = 1.0) -> dict[str, Any]:
    task_id = task_id or f"{ctx.case.case_id}_{int(time.time() * 1000)}"
    ctx.last_task_id = task_id
    request_payload = dict(getattr(ctx, "runtime_request_defaults", {}) or {})
    request_payload.update(
        {
            "cmd": "generate",
            "task_id": task_id,
            "prompt": TPROMPT,
            "duration": duration,
            "diffusion_steps": 20,
            "output_format": "flatbuf_motion_v1",
            "constraints_json": "",
            "seed": 42,
        }
    )
    header, payload = _send_json(
        host,
        port,
        request_payload,
        read_binary=True,
        timeout_sec=TEST_TIMEOUT_SEC,
    )
    if str(header.get("status", "")).lower() != "done":
        raise RuntimeError(f"Generate failed: {header}")
    if not payload:
        raise RuntimeError("Generate returned no binary payload.")
    return header


def _stop_server(host: str, port: int) -> None:
    _send_json(host, port, {"cmd": "quit"})


def _post_recovery_generate(ctx: TestContext, params: dict[str, Any]) -> None:
    _start_launcher(
        ctx,
        reuse_existing_env=params.get("reuse_existing_env", True),
        uncached_uv=params.get("uncached_uv", False),
        force_download_uv=params.get("force_download_uv", False),
        idle_timeout_sec=params.get("idle_timeout_sec"),
    )
    host, port = _start_runtime(
        ctx,
        highvram=params.get("highvram", False),
        force_hf_download=params.get("force_hf_download", False),
        simulate_vram_gb=params.get("simulate_vram_gb"),
        reuse_existing_models=params.get("reuse_existing_models", True),
    )
    _generate_tpose(ctx, host, port, duration=1.0)
    _stop_server(host, port)


def _run_download_probe(ctx: TestContext) -> dict[str, Any]:
    probe_script = ctx.runtime_root / "kimodo" / "kimodo" / "bridge" / "download_health_probe.py"
    probe_output_dir = ctx.run_root / "download_probe"
    command = [
        ctx.python_host,
        str(probe_script),
        "--runtime-root",
        str(ctx.runtime_root),
        "--output-dir",
        str(probe_output_dir),
    ]
    completed = subprocess.run(
        command,
        cwd=str(ctx.runtime_root),
        text=True,
        capture_output=True,
        check=False,
        timeout=TEST_TIMEOUT_SEC * 3,
    )
    summary_path = probe_output_dir / "summary.json"
    summary: dict[str, Any] = {}
    if summary_path.exists():
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
    if completed.returncode != 0:
        raise RuntimeError(
            "Download probe failed: "
            + (summary_path.read_text(encoding="utf-8", errors="replace") if summary_path.exists() else (completed.stderr or completed.stdout or "unknown error"))
        )
    return {
        "status": "passed",
        "summary_path": str(summary_path),
        "probe_results": summary.get("results", []),
    }


def _run_example_tpose_batch(ctx: TestContext) -> dict[str, Any]:
    if os.name != "nt":
        return {"status": "skipped", "reason": "example_run_server_tpose.bat is Windows-only."}

    example_script = ctx.workspace_runtime / "example" / "example_run_server_tpose.bat"
    stdout_path = ctx.logs_dir / "example_tpose_batch.out.log"
    stderr_path = ctx.logs_dir / "example_tpose_batch.err.log"
    env = os.environ.copy()

    completed = subprocess.run(
        ["cmd.exe", "/d", "/c", "call", f".\\{example_script.name}"],
        cwd=str(example_script.parent),
        env=env,
        stdout=stdout_path.open("w", encoding="utf-8", newline="\n"),
        stderr=stderr_path.open("w", encoding="utf-8", newline="\n"),
        text=True,
        timeout=TEST_TIMEOUT_SEC,
        check=False,
    )

    stdout_text = stdout_path.read_text(encoding="utf-8", errors="replace") if stdout_path.exists() else ""
    stderr_text = stderr_path.read_text(encoding="utf-8", errors="replace") if stderr_path.exists() else ""
    if completed.returncode != 0:
        raise RuntimeError(
            "example_run_server_tpose.bat failed with "
            f"exit code {completed.returncode}.\nstdout:\n{stdout_text[-4000:]}\nstderr:\n{stderr_text[-4000:]}"
        )

    if "[OK] QuickServer T-pose example passed." not in stdout_text:
        raise RuntimeError(
            "example_run_server_tpose.bat did not emit the expected success marker.\n"
            f"stdout:\n{stdout_text[-4000:]}"
        )

    return {
        "status": "passed",
        "exit_code": completed.returncode,
        "stdout_log": str(stdout_path),
        "stderr_log": str(stderr_path),
    }


def _run_example_startup_batch(ctx: TestContext) -> dict[str, Any]:
    if os.name != "nt":
        return {"status": "skipped", "reason": "example_run_server_startup.bat is Windows-only."}

    example_script = ctx.workspace_runtime / "example" / "example_run_server_startup.bat"
    stdout_path = ctx.logs_dir / "example_startup_batch.out.log"
    stderr_path = ctx.logs_dir / "example_startup_batch.err.log"
    env = os.environ.copy()

    completed = subprocess.run(
        ["cmd.exe", "/d", "/c", "call", f".\\{example_script.name}"],
        cwd=str(example_script.parent),
        env=env,
        stdout=stdout_path.open("w", encoding="utf-8", newline="\n"),
        stderr=stderr_path.open("w", encoding="utf-8", newline="\n"),
        text=True,
        timeout=TEST_TIMEOUT_SEC,
        check=False,
    )

    stdout_text = stdout_path.read_text(encoding="utf-8", errors="replace") if stdout_path.exists() else ""
    stderr_text = stderr_path.read_text(encoding="utf-8", errors="replace") if stderr_path.exists() else ""
    if completed.returncode != 0:
        raise RuntimeError(
            "example_run_server_startup.bat failed with "
            f"exit code {completed.returncode}.\nstdout:\n{stdout_text[-4000:]}\nstderr:\n{stderr_text[-4000:]}"
        )

    if "[OK] QuickServer startup ready:" not in stdout_text:
        raise RuntimeError(
            "example_run_server_startup.bat did not emit the expected success marker.\n"
            f"stdout:\n{stdout_text[-4000:]}"
        )

    return {
        "status": "passed",
        "exit_code": completed.returncode,
        "stdout_log": str(stdout_path),
        "stderr_log": str(stderr_path),
    }


def _run_legacy_command_reject(ctx: TestContext, cmd_name: str) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(
        ctx,
        reuse_existing_env=params.get("reuse_existing_env", True),
        uncached_uv=params.get("uncached_uv", False),
        force_download_uv=params.get("force_download_uv", False),
        idle_timeout_sec=params.get("idle_timeout_sec"),
    )
    host, port = _wait_for_server(ctx)
    header, _ = _send_json(host, port, {"cmd": cmd_name})
    _stop_server(host, port)
    if str(header.get("status", "")).lower() != "error":
        raise RuntimeError(f"Legacy command '{cmd_name}' should fail, got: {header}")
    message = str(header.get("message", ""))
    if "Unknown cmd" not in message:
        raise RuntimeError(f"Legacy command '{cmd_name}' returned unexpected error: {header}")
    return {"status": "passed", "response": header}


def _start_phase_driver(
    ctx: TestContext,
    phase: str,
    *,
    owner_pid: int = 0,
) -> tuple[Any, list[str]]:
    phase_errors: list[str] = []
    phase_thread = None
    if phase not in {"download", "loading_runtime", "generating"}:
        return phase_thread, phase_errors

    import threading

    def _run() -> None:
        try:
            host, port = _start_runtime(
                ctx,
                reuse_existing_models=(phase != "download"),
                owner_pid=owner_pid,
            )
            if phase == "generating":
                _generate_tpose(ctx, host, port, task_id=f"{ctx.case.case_id}_phase_driver", duration=20.0)
        except Exception as exc:
            phase_errors.append(str(exc))

    phase_thread = threading.Thread(target=_run, daemon=True)
    phase_thread.start()
    return phase_thread, phase_errors


def _resolved_case_params(case: TestCase) -> dict[str, Any]:
    params = dict(case.params)
    params.setdefault("reuse_existing_env", _policy_uses_reusable_env(case.resources))
    params.setdefault("reuse_existing_models", _policy_uses_reusable_models(case.resources))
    if case.resources.uv_policy == "force-download-uv":
        params.setdefault("force_download_uv", True)
        params.setdefault("uncached_uv", True)
    return params


def _run_basic(ctx: TestContext, params: dict[str, Any]) -> dict[str, Any]:
    _start_launcher(
        ctx,
        reuse_existing_env=params.get("reuse_existing_env", True),
        uncached_uv=params.get("uncached_uv", False),
        force_download_uv=params.get("force_download_uv", False),
        idle_timeout_sec=params.get("idle_timeout_sec"),
    )
    host, port = _start_runtime(
        ctx,
        highvram=params.get("highvram", False),
        force_hf_download=params.get("force_hf_download", False),
        simulate_vram_gb=params.get("simulate_vram_gb"),
        reuse_existing_models=params.get("reuse_existing_models", True),
    )
    header = _generate_tpose(ctx, host, port)
    _stop_server(host, port)
    return {"status": "passed", "header": header}


def _run_double_start(ctx: TestContext, params: dict[str, Any], different: bool) -> dict[str, Any]:
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), bootstrap_hold_sec=10, idle_timeout_sec=params.get("idle_timeout_sec"))
    second_log = ctx.logs_dir / "launcher_second.out.log"
    env = os.environ.copy()
    second_proc = subprocess.Popen(ctx.launcher_command(), cwd=str(ctx.workspace_runtime), env=env, stdout=second_log.open("w", encoding="utf-8"), stderr=subprocess.STDOUT, text=True)
    try:
        bootstrap_wait_text = _wait_for(
            lambda: (
                ctx.bootstrap_wait_log_path.read_text(encoding="utf-8", errors="replace")
                if ctx.bootstrap_wait_log_path.exists() and "waiting_on=" in ctx.bootstrap_wait_log_path.read_text(encoding="utf-8", errors="replace")
                else None
            ),
            20.0,
            "bootstrap wait log",
        )
        host, port = _start_runtime(
            ctx,
            highvram=different,
            reuse_existing_models=True,
        )
        _generate_tpose(ctx, host, port)
        _stop_server(host, port)
        second_log_text = second_log.read_text(encoding="utf-8", errors="replace") if second_log.exists() else ""
        wait_detected = "waiting_on=" in bootstrap_wait_text
        if not wait_detected:
            raise RuntimeError(f"Second bootstrap did not emit a wait log. Inspect: {second_log}")
        return {
            "status": "passed",
            "second_pid": second_proc.pid,
            "second_log_path": str(second_log),
            "bootstrap_wait_log_path": str(ctx.bootstrap_wait_log_path),
            "second_bootstrap_wait_detected": wait_detected,
            "second_launcher_log_excerpt": second_log_text[:500],
        }
    finally:
        if second_proc.poll() is None:
            second_proc.terminate()


def _run_queue_order(ctx: TestContext) -> dict[str, Any]:
    _start_launcher(ctx, reuse_existing_env=_policy_uses_reusable_env(ctx.case.resources))
    host, port = _start_runtime(ctx)
    import threading

    results: dict[str, Any] = {}
    errors: list[str] = []

    def worker(task_id: str, duration: float):
        try:
            results[task_id] = _generate_tpose(ctx, host, port, task_id=task_id, duration=duration)
        except Exception as exc:
            errors.append(f"{task_id}: {exc}")

    t1 = threading.Thread(target=worker, args=("queue_a", 2.0))
    t2 = threading.Thread(target=worker, args=("queue_b", 1.0))
    t1.start()
    time.sleep(0.2)
    t2.start()
    t1.join()
    t2.join()
    _stop_server(host, port)
    if errors:
        raise RuntimeError("; ".join(errors))
    return {"status": "passed", "tasks": list(results.keys())}


def _run_stop_generating(ctx: TestContext) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
    host, port = _start_runtime(ctx)
    import threading

    error_holder: list[str] = []

    def _gen():
        try:
            _generate_tpose(ctx, host, port, task_id="stop_generating", duration=10.0)
        except Exception as exc:
            error_holder.append(str(exc))

    thread = threading.Thread(target=_gen)
    thread.start()
    _wait_for(lambda: _read_serverport(ctx.serverport_path).get("state") == "generating", 120, "generating state")
    _stop_server(host, port)
    thread.join(timeout=30)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "errors": error_holder}


def _run_idle_timeout_override(ctx: TestContext, params: dict[str, Any]) -> dict[str, Any]:
    idle_timeout_sec = int(params.get("idle_timeout_sec") or 3)
    _start_launcher(
        ctx,
        reuse_existing_env=params.get("reuse_existing_env", True),
        uncached_uv=params.get("uncached_uv", False),
        force_download_uv=params.get("force_download_uv", False),
        idle_timeout_sec=idle_timeout_sec,
    )
    host, port = _start_runtime(
        ctx,
        highvram=params.get("highvram", False),
        force_hf_download=params.get("force_hf_download", False),
        simulate_vram_gb=params.get("simulate_vram_gb"),
        reuse_existing_models=params.get("reuse_existing_models", True),
    )
    _generate_tpose(ctx, host, port)
    _wait_for_server_shutdown(ctx, timeout_sec=max(20, idle_timeout_sec * 5))
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "idle_timeout_sec": idle_timeout_sec}


def _run_cancel_queued(ctx: TestContext) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
    host, port = _start_runtime(ctx)
    import threading

    error_holder: list[str] = []

    def _gen(task_id: str, duration: float):
        try:
            _generate_tpose(ctx, host, port, task_id=task_id, duration=duration)
        except Exception as exc:
            error_holder.append(f"{task_id}: {exc}")

    t1 = threading.Thread(target=_gen, args=("active_task", 5.0))
    t2 = threading.Thread(target=_gen, args=("queued_task", 1.0))
    t1.start()
    time.sleep(0.2)
    t2.start()
    _wait_for(lambda: _read_serverport(ctx.serverport_path).get("state") == "generating", 120, "generating state")
    header, _ = _send_json(host, port, {"cmd": "cancel", "task_id": "queued_task"})
    t1.join()
    t2.join(timeout=10)
    _stop_server(host, port)
    if str(header.get("status", "")).lower() not in {"cancelled", "cancelling", "idle"}:
        raise RuntimeError(f"Unexpected cancel response: {header}")
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "cancel": header, "errors": error_holder}


def _run_cancel_active(ctx: TestContext) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
    host, port = _start_runtime(ctx)
    import threading

    result: dict[str, Any] = {}

    def _gen():
        try:
            result["header"] = _generate_tpose(ctx, host, port, task_id="active_cancel", duration=20.0)
        except Exception as exc:
            result["error"] = str(exc)

    thread = threading.Thread(target=_gen)
    thread.start()
    _wait_for(lambda: _read_serverport(ctx.serverport_path).get("state") == "generating", 120, "generating state")
    header, _ = _send_json(host, port, {"cmd": "cancel", "task_id": "active_cancel"})
    thread.join(timeout=60)
    _stop_server(host, port)
    if str(header.get("status", "")).lower() not in {"cancelling", "cancelled"}:
        raise RuntimeError(f"Unexpected cancel response: {header}")
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "cancel": header, "result": result}


def _run_cancel_invalid(ctx: TestContext, mode: str) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
    host, port = _start_runtime(ctx)
    task_id = "" if mode == "empty" else "does_not_exist"
    header, _ = _send_json(host, port, {"cmd": "cancel", "task_id": task_id})
    _stop_server(host, port)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "cancel": header}


def _run_cancel_finished(ctx: TestContext) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
    host, port = _start_runtime(ctx)
    _generate_tpose(ctx, host, port, task_id="finished_task")
    header, _ = _send_json(host, port, {"cmd": "cancel", "task_id": "finished_task"})
    _stop_server(host, port)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "cancel": header}


def _run_abort_phase(ctx: TestContext, phase: str, delay_sec: int) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    reuse_existing_env = params.get("reuse_existing_env", phase not in {"boot", "setting_up_env"})
    _start_launcher(ctx, reuse_existing_env=reuse_existing_env, idle_timeout_sec=params.get("idle_timeout_sec"))

    phase_thread, phase_error = _start_phase_driver(ctx, phase)

    _wait_for_bootstrap_phase(ctx, phase)
    if delay_sec > 0:
        time.sleep(delay_sec)
    if ctx.launcher_proc is None:
        raise RuntimeError("Launcher process missing.")
    _terminate_process_tree(ctx.launcher_proc, timeout_sec=30)
    if phase_thread is not None:
        phase_thread.join(timeout=30)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "phase": phase, "delay_sec": delay_sec, "phase_errors": phase_error}


def _run_owner_kill(ctx: TestContext, phase: str) -> dict[str, Any]:
    owner_pid = ctx.start_owner()
    params = _resolved_case_params(ctx.case)
    reuse_existing_env = params.get("reuse_existing_env", phase not in {"boot", "setting_up_env"})
    _start_launcher(ctx, reuse_existing_env=reuse_existing_env, idle_timeout_sec=params.get("idle_timeout_sec"))
    phase_thread, phase_errors = _start_phase_driver(ctx, phase, owner_pid=owner_pid)
    _wait_for_bootstrap_phase(ctx, phase)
    if ctx.owner_proc is None:
        raise RuntimeError("Owner process missing.")
    ctx.owner_proc.kill()
    ctx.owner_proc.wait(timeout=10)
    _wait_for_server_shutdown(ctx, timeout_sec=120)
    if phase_thread is not None:
        phase_thread.join(timeout=30)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "owner_pid": owner_pid, "phase": phase, "phase_errors": phase_errors}


def _run_owner_kill_recovery(ctx: TestContext) -> dict[str, Any]:
    return _run_owner_kill(ctx, "generating")


def _run_cli_kill(ctx: TestContext, phase: str) -> dict[str, Any]:
    params = _resolved_case_params(ctx.case)
    reuse_existing_env = params.get("reuse_existing_env", phase not in {"boot", "setting_up_env"})
    _start_launcher(ctx, reuse_existing_env=reuse_existing_env, idle_timeout_sec=params.get("idle_timeout_sec"))
    phase_thread, phase_errors = _start_phase_driver(ctx, phase)
    if phase != "boot":
        _wait_for_bootstrap_phase(ctx, phase)
    else:
        time.sleep(1)
    data = _read_serverport(ctx.serverport_path)
    pid = int(data.get("pid") or "0")
    if pid > 0:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(pid), "/F", "/T"], check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            os.kill(pid, 9)
    else:
        if ctx.launcher_proc is None:
            raise RuntimeError("CLI pid missing from serverport and launcher process is unavailable.")
        _terminate_process_tree(ctx.launcher_proc, timeout_sec=30)
    _wait_for_server_shutdown(ctx, timeout_sec=120)
    if phase_thread is not None:
        phase_thread.join(timeout=30)
    _post_recovery_generate(ctx, params)
    return {"status": "passed", "cli_pid": pid, "phase": phase, "phase_errors": phase_errors}


def _run_case(ctx: TestContext) -> dict[str, Any]:
    kind = ctx.case.kind
    params = _resolved_case_params(ctx.case)
    if kind == "basic":
        return _run_basic(ctx, params)
    if kind == "double_start_same":
        return _run_double_start(ctx, params, different=False)
    if kind == "double_start_diff":
        return _run_double_start(ctx, params, different=True)
    if kind == "queue_order":
        return _run_queue_order(ctx)
    if kind == "stop_idle":
        _start_launcher(ctx, reuse_existing_env=params.get("reuse_existing_env", True), idle_timeout_sec=params.get("idle_timeout_sec"))
        host, port = _start_runtime(ctx)
        _stop_server(host, port)
        _post_recovery_generate(ctx, params)
        return {"status": "passed"}
    if kind == "stop_generating":
        return _run_stop_generating(ctx)
    if kind == "cancel_queued":
        return _run_cancel_queued(ctx)
    if kind == "cancel_active":
        return _run_cancel_active(ctx)
    if kind == "cancel_invalid":
        return _run_cancel_invalid(ctx, params["mode"])
    if kind == "cancel_finished":
        return _run_cancel_finished(ctx)
    if kind == "abort_phase":
        return _run_abort_phase(ctx, params["phase"], int(params.get("delay_sec", 0)))
    if kind == "owner_kill":
        return _run_owner_kill(ctx, params["phase"])
    if kind == "owner_kill_recovery":
        return _run_owner_kill_recovery(ctx)
    if kind == "cli_kill":
        return _run_cli_kill(ctx, params["phase"])
    if kind == "download_probe":
        return _run_download_probe(ctx)
    if kind == "idle_timeout_override":
        return _run_idle_timeout_override(ctx, params)
    if kind == "example_tpose_batch":
        return _run_example_tpose_batch(ctx)
    if kind == "example_startup_batch":
        return _run_example_startup_batch(ctx)
    if kind == "legacy_command_reject":
        return _run_legacy_command_reject(ctx, str(params["cmd"]))
    if kind == "skip_placeholder":
        return {"status": "skipped", "reason": "Not represented cleanly in the new lifecycle yet."}
    raise RuntimeError(f"Unsupported test kind: {kind}")


def _write_summary(ctx: TestContext, result: dict[str, Any]) -> None:
    ctx.summary_path.write_text(json.dumps(result, indent=2), encoding="utf-8")


def _select_cases(case_ids: list[str], tag: str | None, full: bool, case_range: tuple[str, str] | None) -> list[TestCase]:
    cases = _list_cases()
    if sum(1 for enabled in (full, bool(case_ids), bool(tag), bool(case_range)) if enabled) > 1:
        raise RuntimeError("Use only one selector among --full, --case/--cases, --tag, or --range.")
    if full:
        return [case for case in cases if "hf" not in case.tags and "probe" not in case.tags and "manual" not in case.tags]
    if case_ids:
        case_map = {case.case_id.lower(): case for case in cases}
        selected: list[TestCase] = []
        seen: set[str] = set()
        for case_id in case_ids:
            key = case_id.lower()
            if key not in case_map:
                raise RuntimeError(f"Unknown test id: {case_id}")
            if key in seen:
                continue
            selected.append(case_map[key])
            seen.add(key)
        return selected
    if case_range:
        start_key = _case_sort_key(case_range[0])
        end_key = _case_sort_key(case_range[1])
        if start_key > end_key:
            start_key, end_key = end_key, start_key
        selected = [case for case in cases if start_key <= _case_sort_key(case.case_id) <= end_key]
        if not selected:
            raise RuntimeError(f"Empty test range: {case_range[0]}..{case_range[1]}")
        return selected
    if tag:
        selected = [case for case in cases if tag in case.tags]
        if not selected:
            raise RuntimeError(f"Unknown/empty tag selection: {tag}")
        return selected
    return cases


def _archive_legacy_tests(repo_root: Path) -> None:
    legacy_dir = repo_root / "NvlabKimodoQuickServer~" / "test"
    if not legacy_dir.exists():
        return
    archive_root = repo_root / "archive"
    archive_root.mkdir(parents=True, exist_ok=True)
    destination = archive_root / f"test_archive_{time.strftime('%Y%m%d_%H%M%S')}"
    shutil.move(str(legacy_dir), str(destination))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Kimodo QuickServer integration test suite")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--cases", default="")
    parser.add_argument("--range", nargs=2, metavar=("START", "END"))
    parser.add_argument("--tag")
    parser.add_argument("--full", action="store_true")
    parser.add_argument("--archive-legacy", action="store_true")
    args = parser.parse_args(argv)

    if args.archive_legacy:
        _archive_legacy_tests(_repo_root())
        return 0

    cases = _list_cases()
    if args.list:
        print("testfull")
        for case in cases:
            print(
                f"{case.case_id}\t{case.name}\t"
                f"[{', '.join(case.tags)}]\t"
                f"env_policy={case.resources.env_policy},model_policy={case.resources.model_policy},uv_policy={case.resources.uv_policy}"
            )
        return 0

    case_ids = list(args.case or [])
    if args.cases:
        case_ids.extend([part.strip() for part in re.split(r"[\s,]+", args.cases) if part.strip()])

    selected = _select_cases(case_ids, args.tag, args.full, tuple(args.range) if args.range else None)
    suite_results: list[dict[str, Any]] = []
    overall_ok = True
    for case in selected:
        ctx = TestContext(case)
        case_result: dict[str, Any]
        started = time.time()
        try:
            ctx.prepare_workspace()
            case_result = _run_case(ctx)
        except Exception as exc:
            overall_ok = False
            case_result = {"status": "failed", "error": str(exc), "traceback": traceback.format_exc()}
        finally:
            ctx.cleanup()
        case_result.update(
            {
                "case_id": case.case_id,
                "name": case.name,
                "tags": list(case.tags),
                "resources": {
                    "env_policy": case.resources.env_policy,
                    "model_policy": case.resources.model_policy,
                    "uv_policy": case.resources.uv_policy,
                },
                "elapsed_sec": round(time.time() - started, 3),
                "run_root": str(ctx.run_root),
            }
        )
        _write_summary(ctx, case_result)
        suite_results.append(case_result)
        print(f"{case.case_id}: {case_result['status']}")

    suite_summary = {
        "ok": overall_ok,
        "cases": suite_results,
    }
    print(json.dumps(suite_summary, indent=2))
    return 0 if overall_ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
