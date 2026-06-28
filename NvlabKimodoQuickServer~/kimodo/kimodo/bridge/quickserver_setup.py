from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request


DEFAULT_SETUP_LOG_NAME = "setup.log"
BITSANDBYTES_REQUIRED = "0.49.2"


@dataclass
class ProjectPaths:
    root_dir: Path
    source_root: Path
    log_dir: Path
    recycle_dir: Path
    recovery_flag_dir: Path
    wheels_dir: Path
    models_dir: Path
    setup_sentinel: Path
    run_lock: Path
    run_marker: Path

    @property
    def default_setup_log_path(self) -> Path:
        return self.log_dir / DEFAULT_SETUP_LOG_NAME

    @property
    def venv_dir(self) -> Path:
        return self.source_root / ".venv"

    @property
    def venv_python(self) -> Path:
        if os.name == "nt":
            return self.venv_dir / "Scripts" / "python.exe"
        return self.venv_dir / "bin" / "python"


@dataclass
class SetupCliOptions:
    output_mode: str | None
    log_path: str | None
    force: bool
    requested_mode: str | None
    venv_arg: str | None


@dataclass
class SetupCliResult:
    ok: bool
    exit_code: int
    venv_python: str


class SetupError(RuntimeError):
    pass


class SetupLogger:
    def __init__(self, output_mode: str, log_path: Path, append: bool = False):
        self.output_mode = (output_mode or "console").strip().lower() or "console"
        self.log_path = log_path
        self.append = bool(append)
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self._stream = None

    def __enter__(self):
        self._stream = self.log_path.open("a" if self.append else "w", encoding="utf-8", newline="\n")
        return self

    def __exit__(self, exc_type, exc, tb):
        if self._stream is not None:
            self._stream.close()
            self._stream = None
        return False

    def log(self, message: str) -> None:
        text = str(message)
        assert self._stream is not None
        self._stream.write(text + "\n")
        self._stream.flush()
        if self.output_mode == "console":
            print(text, flush=True)


def discover_project_paths(root_dir: str | os.PathLike[str] | None = None) -> ProjectPaths:
    root_path = Path(root_dir).resolve() if root_dir else Path(__file__).resolve().parents[3]
    source_root = root_path / "kimodo"
    if not (source_root / "pyproject.toml").is_file():
        if (root_path / "pyproject.toml").is_file():
            source_root = root_path
        else:
            raise SetupError(f"Invalid project root: {root_path}")
    return ProjectPaths(
        root_dir=root_path,
        source_root=source_root,
        log_dir=root_path / "log",
        recycle_dir=root_path / "archive" / "recycle",
        recovery_flag_dir=root_path / "archive" / "recovery_flags",
        wheels_dir=root_path / "wheels",
        models_dir=root_path / "models",
        setup_sentinel=root_path / ".setup.complete",
        run_lock=root_path / ".run.lock",
        run_marker=root_path / "run",
    )


def setup_mode_from_env(requested_mode: str | None) -> str:
    legacy_value = os.environ.get("KIMODO_TEST_SETUP_DEVICE", "").strip()
    if legacy_value:
        raise SetupError("KIMODO_TEST_SETUP_DEVICE has been removed. Use KIMODO_SETUP_DEVICE.")
    value = str(requested_mode or os.environ.get("KIMODO_SETUP_DEVICE") or "auto").strip().lower()
    if sys.platform == "darwin":
        if value.startswith("cuda"):
            raise SetupError("macOS does not support CUDA setup mode. Use auto, cpu, or mps.")
        return "cpu"
    return "cpu" if value == "cpu" else "auto"


def resolve_venv_python_arg(venv_arg: str, root_dir: str | os.PathLike[str] | None = None) -> str:
    if not venv_arg:
        raise SetupError("--venv requires a path.")
    candidate = Path(venv_arg).expanduser()
    if not candidate.is_absolute():
        base_dir = Path(root_dir).resolve() if root_dir else Path.cwd()
        candidate = (base_dir / candidate).resolve()
    if candidate.name.lower().startswith("python"):
        python_path = candidate
    elif os.name == "nt":
        python_path = candidate / "Scripts" / "python.exe"
    else:
        python_path = candidate / "bin" / "python"
    if not python_path.exists():
        raise SetupError(f"Invalid --venv path, python not found: {python_path}")
    return str(python_path.resolve())


def archive_path(target: Path, recycle_dir: Path) -> None:
    if not target.exists():
        return
    recycle_dir.mkdir(parents=True, exist_ok=True)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    destination = recycle_dir / f"{target.name}.{timestamp}.{os.getpid()}.{int(time.time() * 1000) % 1000}"
    try:
        shutil.move(str(target), str(destination))
    except Exception:
        pass


def _read_sentinel(sentinel: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not sentinel.exists():
        return data
    for line in sentinel.read_text(encoding="utf-8", errors="replace").splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        data[key.strip()] = value.strip().strip('"')
    return data


def _write_sentinel(paths: ProjectPaths, setup_mode: str, torch_runtime: str) -> None:
    content = "\n".join(
        [
            f"setup_time={time.strftime('%Y-%m-%d %H:%M:%S')}",
            f"setup_mode={setup_mode}",
            f"torch_runtime={torch_runtime}",
            f'root_dir="{paths.root_dir}"',
            f'source_root="{paths.source_root}"',
            f'venv_python="{paths.venv_python}"',
            "",
        ]
    )
    paths.setup_sentinel.write_text(content, encoding="utf-8", newline="\n")


def _should_inject_once(paths: ProjectPaths, key: str, env_var: str) -> bool:
    if os.environ.get(env_var, "").strip() != "1":
        return False
    paths.recovery_flag_dir.mkdir(parents=True, exist_ok=True)
    flag_path = paths.recovery_flag_dir / f"{key}.done"
    if flag_path.exists():
        return False
    flag_path.write_text(
        "\n".join(
            [
                f"scenario={os.environ.get('KIMODO_TEST_SCENARIO_NAME', '')}",
                f"key={key}",
                f"time={time.strftime('%Y-%m-%d %H:%M:%S')}",
                "",
            ]
        ),
        encoding="utf-8",
        newline="\n",
    )
    return True


def _find_uv_bin(paths: ProjectPaths) -> str:
    env_override = os.environ.get("KIMODO_UV_BIN", "").strip()
    candidates: list[Path | str] = []
    if env_override:
        candidates.append(Path(env_override))
    candidates.append(paths.root_dir / "program" / "exe" / "uv" / "uv.exe")
    candidates.append(paths.root_dir / "program" / "exe" / "uv" / "uv")
    which_uv = shutil.which("uv")
    if which_uv:
        candidates.append(which_uv)

    for candidate in candidates:
        candidate_path = str(candidate)
        try:
            completed = subprocess.run(
                [candidate_path, "--version"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        except OSError:
            continue
        if completed.returncode == 0:
            return candidate_path
    raise SetupError("uv not found. Set KIMODO_UV_BIN, place a local uv binary under program/exe/uv, or install uv on PATH.")


def _select_uv_default_index() -> str:
    explicit = os.environ.get("KIMODO_PIP_INDEX_URL", "").strip()
    if explicit:
        return explicit
    mirror = "https://pypi.tuna.tsinghua.edu.cn/simple"
    try:
        request = urllib.request.Request(mirror, method="HEAD")
        with urllib.request.urlopen(request, timeout=2):
            return mirror
    except Exception:
        return "https://pypi.org/simple"


def _python_spec() -> str:
    if os.name == "nt":
        if os.environ.get("KIMODO_PYTHON_ARCH", "").strip().lower() == "x86":
            raise SetupError("x86 Python is not supported. Use x64 Python.")
        return "cpython-3.12.13-windows-x86_64-none"
    return "3.12"


def _run_logged(command: list[str], logger: SetupLogger, cwd: Path | None = None, env: dict[str, str] | None = None, check: bool = True) -> int:
    merged_env = os.environ.copy()
    merged_env.setdefault("UV_NO_PROGRESS", "1")
    if env:
        merged_env.update(env)
    process = subprocess.Popen(
        command,
        cwd=str(cwd) if cwd else None,
        env=merged_env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    assert process.stdout is not None
    for line in process.stdout:
        logger.log(line.rstrip("\r\n"))
    return_code = process.wait()
    if check and return_code != 0:
        raise SetupError(f"Command failed ({return_code}): {' '.join(command)}")
    return return_code


def _run_capture(command: list[str], cwd: Path | None = None, env: dict[str, str] | None = None) -> tuple[int, str]:
    merged_env = os.environ.copy()
    merged_env.setdefault("UV_NO_PROGRESS", "1")
    if env:
        merged_env.update(env)
    completed = subprocess.run(
        command,
        cwd=str(cwd) if cwd else None,
        env=merged_env,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    return completed.returncode, completed.stdout or ""


def _mirror_is_fast(findlinks_url: str, threshold_ms: int) -> bool:
    started = time.time()
    try:
        request = urllib.request.Request(findlinks_url, method="HEAD")
        with urllib.request.urlopen(request, timeout=2):
            elapsed_ms = int((time.time() - started) * 1000)
            return elapsed_ms <= threshold_ms
    except Exception:
        return False


def _install_cuda_torch(paths: ProjectPaths, uv_bin: str, default_index: str, logger: SetupLogger) -> None:
    findlinks = os.environ.get("KIMODO_TORCH_FINDLINKS", "https://mirrors.aliyun.com/pytorch-wheels/cu128").strip()
    threshold_ms = int(os.environ.get("KIMODO_TORCH_MIRROR_MAX_PING_MS", "50") or "50")
    if findlinks and _mirror_is_fast(findlinks, threshold_ms):
        logger.log(f"[STEP] Installing cu128 torch from mirror: {findlinks}")
        try:
            _run_logged(
                [
                    uv_bin,
                    "pip",
                    "install",
                    "--python",
                    str(paths.venv_python),
                    "--default-index",
                    default_index,
                    "--find-links",
                    findlinks,
                    "--reinstall-package",
                    "torch",
                    "--reinstall-package",
                    "torchvision",
                    "--reinstall-package",
                    "torchaudio",
                    "torch==2.11.0",
                    "torchvision==0.26.0",
                    "torchaudio==2.11.0",
                ],
                logger,
            )
            return
        except SetupError:
            logger.log("[WARN] cu128 torch install from mirror failed; falling back to the official index.")

    logger.log("[STEP] Installing cu128 torch from official index (torch-backend cu128)...")
    _run_logged(
        [
            uv_bin,
            "pip",
            "install",
            "--python",
            str(paths.venv_python),
            "--default-index",
            default_index,
            "--torch-backend",
            "cu128",
            "--reinstall-package",
            "torch",
            "--reinstall-package",
            "torchvision",
            "--reinstall-package",
            "torchaudio",
            "torch",
            "torchvision",
            "torchaudio",
        ],
        logger,
    )


def _validate_torch_env(paths: ProjectPaths, logger: SetupLogger, mode: str) -> int:
    mode_key = str(mode or "cpu").strip().lower()
    if mode_key not in {"cpu", "cuda", "mps"}:
        mode_key = "cpu"
    logger.log(f"[STEP] Validating {mode_key.upper()} torch runtime...")
    import_script = "import importlib,torch,sys; importlib.import_module('torch._jit_internal'); print('torch='+torch.__version__); print('cuda='+str(torch.version.cuda)); sys.exit(0)"
    rc, output = _run_capture([str(paths.venv_python), "-c", import_script])
    for line in output.splitlines():
        logger.log(line)
    if rc != 0:
        raise SetupError("torch cannot be loaded in this environment.")

    if mode_key == "cpu":
        return 0

    if mode_key == "mps":
        mps_check = "import torch,sys; sys.exit(0 if torch.backends.mps.is_available() else 1)"
        rc, _output = _run_capture([str(paths.venv_python), "-c", mps_check])
        if rc != 0:
            logger.log("[WARN] MPS not available on this machine; torch will run on CPU.")
            return 0
        logger.log("[OK] MPS torch runtime validated.")
        return 0

    cuda_check = "import torch,sys; sys.exit(0 if torch.cuda.is_available() else 1)"
    rc, _output = _run_capture([str(paths.venv_python), "-c", cuda_check])
    if rc != 0:
        logger.log("[WARN] CUDA not available on this machine; torch will run on CPU.")
        return 0

    kernel_check = "import torch,sys; t=torch.zeros(8,device='cuda'); (t+1).sum().item(); torch.cuda.synchronize(); print('kernel_ok'); sys.exit(0)"
    rc, output = _run_capture([str(paths.venv_python), "-c", kernel_check])
    for line in output.splitlines():
        logger.log(line)
    if rc != 0:
        logger.log("[WARN] GPU kernel launch test failed despite cuda being reported available.")
        return 3
    logger.log("[OK] CUDA torch runtime validated (kernel launch succeeded).")
    return 0


def _install_torch_via_torchruntime(paths: ProjectPaths, uv_bin: str, default_index: str, logger: SetupLogger) -> None:
    logger.log("[STEP] Ensuring torchruntime helper...")
    rc, _ = _run_capture([str(paths.venv_python), "-c", "import torchruntime"])
    if rc != 0:
        _run_logged(
            [uv_bin, "pip", "install", "--python", str(paths.venv_python), "--default-index", default_index, "torchruntime"],
            logger,
        )

    rc, _ = _run_capture([str(paths.venv_python), "-c", "import torch"])
    if rc == 0:
        _run_logged([uv_bin, "pip", "uninstall", "--python", str(paths.venv_python), "torch", "torchvision", "torchaudio"], logger, check=False)

    env = {
        "UV_DEFAULT_INDEX": "",
        "UV_INDEX_URL": "",
        "UV_EXTRA_INDEX_URL": "",
        "PIP_INDEX_URL": "",
        "PIP_EXTRA_INDEX_URL": "",
    }
    logger.log("[STEP] Installing architecture-matched torch via torchruntime --uv...")
    _run_logged([str(paths.venv_python), "-m", "torchruntime", "install", "--uv", "torch", "torchvision", "torchaudio"], logger, env=env)


def _ensure_bitsandbytes(paths: ProjectPaths, uv_bin: str, logger: SetupLogger) -> None:
    logger.log("[STEP] Ensuring bitsandbytes for CUDA mode...")
    version_check = "import bitsandbytes as bnb; from packaging.version import Version as V; import sys; sys.exit(0 if V(getattr(bnb,'__version__','0'))>=V('0.46.1') else 1)"
    rc, _ = _run_capture([str(paths.venv_python), "-c", version_check])
    if rc == 0:
        logger.log("[INFO] bitsandbytes>=0.46.1 already present, skip reinstall.")
        return
    _run_logged([uv_bin, "pip", "install", "--python", str(paths.venv_python), f"bitsandbytes=={BITSANDBYTES_REQUIRED}"], logger)
    rc, _ = _run_capture([str(paths.venv_python), "-c", version_check])
    if rc != 0:
        raise SetupError("bitsandbytes version check failed after install.")


def _uninstall_bitsandbytes(paths: ProjectPaths, uv_bin: str, logger: SetupLogger) -> None:
    logger.log("[STEP] Removing bitsandbytes from runtime environment...")
    _run_logged(
        [uv_bin, "pip", "uninstall", "--python", str(paths.venv_python), "bitsandbytes"],
        logger,
        check=False,
    )


def _validate_bitsandbytes_runtime(paths: ProjectPaths, logger: SetupLogger) -> bool:
    logger.log("[STEP] Validating bitsandbytes runtime...")
    script = (
        "import sys; "
        "import bitsandbytes as bnb; "
        "from bitsandbytes.nn import Linear4bit; "
        "print('bnb=' + str(getattr(bnb,'__version__','unknown'))); "
        "print('linear4bit=' + Linear4bit.__name__); "
        "sys.exit(0)"
    )
    rc, output = _run_capture([str(paths.venv_python), "-c", script])
    for line in output.splitlines():
        logger.log(line)
    return rc == 0


def _ensure_motion_correction(paths: ProjectPaths, uv_bin: str, logger: SetupLogger, default_index: str) -> bool:
    logger.log("[STEP] Ensuring motion_correction...")
    rc, _ = _run_capture([str(paths.venv_python), "-c", "import motion_correction"])
    if rc == 0:
        logger.log("[INFO] motion_correction already present, skip reinstall.")
        return True

    try:
        if sys.platform == "darwin":
            motion_correction_root = paths.source_root / "MotionCorrection"
            if not motion_correction_root.exists():
                raise SetupError(f"Missing MotionCorrection source tree: {motion_correction_root}")
            logger.log("[STEP] macOS detected: installing cmake helper and building motion_correction from source...")
            _run_logged(
                [uv_bin, "pip", "install", "--python", str(paths.venv_python), "--default-index", default_index, "cmake"],
                logger,
            )
            _run_logged(
                [uv_bin, "pip", "install", "--python", str(paths.venv_python), "--default-index", default_index, str(motion_correction_root)],
                logger,
            )
            return True

        if os.name == "nt":
            wheel_path = paths.wheels_dir / "motion_correction-1.0.0-cp312-cp312-win_amd64.whl"
        else:
            wheel_path = paths.wheels_dir / "motion_correction-1.0.0-cp312-cp312-manylinux_2_27_x86_64.manylinux_2_28_x86_64.whl"
        if not wheel_path.exists():
            raise SetupError(f"Missing motion_correction wheel: {wheel_path}")
        _run_logged([uv_bin, "pip", "install", "--python", str(paths.venv_python), str(wheel_path)], logger)
        return True
    except Exception as exc:
        logger.log(f"[WARN] motion_correction setup skipped: {exc}")
        logger.log("[WARN] Bridge will continue without motion_correction postprocessing.")
        return False


def _torch_runtime(paths: ProjectPaths) -> str:
    rc, output = _run_capture([str(paths.venv_python), "-c", "import torch; print('cuda' if torch.version.cuda is not None else 'cpu')"])
    if rc != 0:
        return "unknown"
    value = output.strip()
    return value or "unknown"


def _kimodo_runtime_ready(paths: ProjectPaths) -> bool:
    script = "import importlib.metadata as m; import tqdm, huggingface_hub, modelscope, safetensors; print(m.version('kimodo'))"
    rc, _ = _run_capture([str(paths.venv_python), "-c", script])
    return rc == 0


def _runtime_import_check(paths: ProjectPaths) -> None:
    script = "import numpy, huggingface_hub, modelscope, safetensors; import kimodo.model.load_model"
    rc, output = _run_capture([str(paths.venv_python), "-c", script], cwd=paths.root_dir, env={"PYTHONPATH": str(paths.source_root)})
    if rc != 0:
        raise SetupError(f"Runtime import check failed.\n{output}")


def _setup_buildenv(paths: ProjectPaths, setup_mode: str, logger: SetupLogger) -> None:
    uv_bin = _find_uv_bin(paths)
    default_index = _select_uv_default_index()
    python_spec = _python_spec()

    logger.log("[STEP] Build env (single-thread)...")
    logger.log(f"[INFO] setup mode: {setup_mode}")
    logger.log(f"[INFO] Selected uv default index: {default_index}")
    logger.log(f"[STEP] Ensuring uv-managed Python: {python_spec}")
    _run_logged([uv_bin, "python", "install", python_spec], logger)

    paths.venv_dir.mkdir(parents=True, exist_ok=True)
    logger.log("[STEP] Creating/updating venv with uv...")
    _run_logged([uv_bin, "venv", str(paths.venv_dir), "--python", python_spec, "--allow-existing"], logger)
    if not paths.venv_python.exists():
        raise SetupError(f"Missing venv python: {paths.venv_python}")

    if _should_inject_once(paths, "setup_abort", "KIMODO_TEST_INJECT_SETUP_ABORT_ONCE"):
        logger.log("[TEST] Injected setup interrupt once after venv creation.")
        raise SetupError("Injected setup interrupt once.")
    if _should_inject_once(paths, "setup_net_bad", "KIMODO_TEST_INJECT_SETUP_NET_BAD_ONCE"):
        logger.log("[TEST] Injected setup network failure once before dependency install.")
        raise SetupError("Injected setup network failure once.")

    logger.log("[STEP] Seeding build helpers in venv...")
    _run_logged([uv_bin, "pip", "install", "--python", str(paths.venv_python), "--default-index", default_index, "pip", "setuptools", "wheel"], logger)

    antlr_wheel = paths.wheels_dir / "antlr4_python3_runtime-4.9.3-py3-none-any.whl"
    if not antlr_wheel.exists():
        raise SetupError(f"Missing required local wheel: {antlr_wheel}")
    logger.log("[STEP] Installing local antlr4 runtime wheel...")
    _run_logged(
        [
            uv_bin,
            "pip",
            "install",
            "--python",
            str(paths.venv_python),
            "--default-index",
            default_index,
            "--no-index",
            "--find-links",
            str(paths.wheels_dir),
            "--only-binary",
            "antlr4-python3-runtime",
            "antlr4-python3-runtime==4.9.3",
        ],
        logger,
    )

    logger.log("[STEP] Installing kimodo package with uv pip...")
    if not _kimodo_runtime_ready(paths):
        if setup_mode != "cpu":
            _install_cuda_torch(paths, uv_bin, default_index, logger)
        install_env = {"SKIP_MOTION_CORRECTION_IN_SETUP": "1"}
        _run_logged(
            [
                uv_bin,
                "pip",
                "install",
                "--python",
                str(paths.venv_python),
                "--default-index",
                default_index,
                "--find-links",
                str(paths.wheels_dir),
                "--only-binary",
                "antlr4-python3-runtime",
                "--editable",
                ".",
                "--no-build-isolation",
            ],
            logger,
            cwd=paths.source_root,
            env=install_env,
        )
        if not _kimodo_runtime_ready(paths):
            raise SetupError("Failed to install kimodo package into the setup environment.")
    else:
        logger.log("[INFO] kimodo already usable, skip reinstall.")

    if setup_mode == "cpu":
        if sys.platform == "darwin":
            logger.log("[STEP] Installing macOS torch runtime via uv...")
            _run_logged(
                [
                    uv_bin,
                    "pip",
                    "install",
                    "--python",
                    str(paths.venv_python),
                    "--default-index",
                    default_index,
                    "--reinstall-package",
                    "torch",
                    "--reinstall-package",
                    "torchvision",
                    "--reinstall-package",
                    "torchaudio",
                    "torch",
                    "torchvision",
                    "torchaudio",
                ],
                logger,
            )
            _validate_torch_env(paths, logger, "mps")
        else:
            logger.log("[STEP] Installing CPU torch runtime via uv...")
            _run_logged(
                [
                    uv_bin,
                    "pip",
                    "install",
                    "--python",
                    str(paths.venv_python),
                    "--default-index",
                    default_index,
                    "--torch-backend",
                    "cpu",
                    "--reinstall-package",
                    "torch",
                    "--reinstall-package",
                    "torchvision",
                    "--reinstall-package",
                    "torchaudio",
                    "torch",
                    "torchvision",
                    "torchaudio",
                ],
                logger,
            )
            _validate_torch_env(paths, logger, "cpu")
        logger.log("[INFO] CPU mode: skip bitsandbytes/4-bit install by policy.")
    else:
        rc, _ = _run_capture([str(paths.venv_python), "-c", "import torch,sys; sys.exit(0 if torch.version.cuda is not None else 1)"])
        if rc != 0:
            _install_cuda_torch(paths, uv_bin, default_index, logger)
        else:
            logger.log("[INFO] torch already a CUDA build, skip cu128 reinstall.")

        triton_package = "triton-windows" if os.name == "nt" else "triton"
        logger.log(f"[STEP] Installing {triton_package}...")
        _run_logged([uv_bin, "pip", "install", "--python", str(paths.venv_python), "--default-index", default_index, triton_package], logger, check=False)

        validate_rc = _validate_torch_env(paths, logger, "cuda")
        if validate_rc == 3:
            logger.log("[WARN] cu128 torch loads but cannot launch GPU kernels on this device.")
            logger.log("[WARN] Falling back to torchruntime to pick an architecture-matched build...")
            _install_torch_via_torchruntime(paths, uv_bin, default_index, logger)
            validate_rc = _validate_torch_env(paths, logger, "cuda")
        if validate_rc != 0:
            raise SetupError("CUDA torch runtime validation failed.")
        _ensure_bitsandbytes(paths, uv_bin, logger)
        if not _validate_bitsandbytes_runtime(paths, logger):
            logger.log("[WARN] bitsandbytes validation failed on the current runtime.")
            _uninstall_bitsandbytes(paths, uv_bin, logger)
            logger.log("[WARN] Falling back to torchruntime to pick an architecture-matched build...")
            _install_torch_via_torchruntime(paths, uv_bin, default_index, logger)
            logger.log("[INFO] torchruntime fallback active; continuing without bitsandbytes.")

    _ensure_motion_correction(paths, uv_bin, logger, default_index)
    paths.models_dir.mkdir(parents=True, exist_ok=True)
    _runtime_import_check(paths)
    paths.run_marker.mkdir(parents=True, exist_ok=True)
    logger.log("[OK] Build environment staged.")


def run_setup_cli(root_dir: str | os.PathLike[str], options: SetupCliOptions) -> SetupCliResult:
    paths = discover_project_paths(root_dir)
    paths.log_dir.mkdir(parents=True, exist_ok=True)
    try:
        requested_mode = setup_mode_from_env(options.requested_mode)
    except SetupError as exc:
        log_path = Path(options.log_path).resolve() if options.log_path else paths.default_setup_log_path
        with SetupLogger(options.output_mode or "console", log_path) as logger:
            logger.log(f"[ERROR] {exc}")
        return SetupCliResult(ok=False, exit_code=1, venv_python="")

    if options.venv_arg:
        python_path = resolve_venv_python_arg(options.venv_arg, root_dir=root_dir)
        return SetupCliResult(ok=True, exit_code=0, venv_python=python_path)

    if options.force:
        archive_path(paths.setup_sentinel, paths.recycle_dir)

    sentinel = _read_sentinel(paths.setup_sentinel)
    if sentinel.get("setup_mode", "").lower() == requested_mode and paths.venv_python.exists():
        return SetupCliResult(ok=True, exit_code=0, venv_python=str(paths.venv_python))
    if paths.setup_sentinel.exists():
        archive_path(paths.setup_sentinel, paths.recycle_dir)

    log_path = Path(options.log_path).resolve() if options.log_path else paths.default_setup_log_path
    try:
        with SetupLogger(options.output_mode or "console", log_path) as logger:
            _setup_buildenv(paths, requested_mode, logger)
            torch_runtime = _torch_runtime(paths)
            _write_sentinel(paths, requested_mode, torch_runtime)
            logger.log("[OK] setup complete.")
    except SetupError as exc:
        with SetupLogger(options.output_mode or "console", log_path) as logger:
            logger.log(f"[ERROR] {exc}")
        return SetupCliResult(ok=False, exit_code=1, venv_python="")
    except Exception as exc:
        with SetupLogger(options.output_mode or "console", log_path) as logger:
            logger.log(f"[ERROR] Unexpected setup failure: {exc}")
        return SetupCliResult(ok=False, exit_code=1, venv_python="")
    return SetupCliResult(ok=True, exit_code=0, venv_python=str(paths.venv_python))
