from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
from enum import Enum
import os
from pathlib import Path
import shutil
import urllib.error
import urllib.request
import threading
import time
from typing import Protocol


DEFAULT_MODEL_NAME = "Kimodo-SOMA-RP-v1"
INT8_LOCAL_DIR = "KIMODO-Meta3_llm2vec_INT8"
NF4_LOCAL_DIR = "KIMODO-Meta3_llm2vec_NF4"
FP16_LOCAL_DIR = "KIMODO-Meta3_llm2vec_FP16"
LEGACY_FP16_BASE_LOCAL_DIR = "Meta-Llama-3-8B-Instruct"
LEGACY_FP16_PEFT_LOCAL_DIR = "LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised"
ENCODER_ROUTE_INT8 = "int8"
ENCODER_ROUTE_NF4 = "nf4"
ENCODER_ROUTE_FP16 = "fp16"
DOWNLOAD_PROBE_TIMEOUT_SECONDS = 1.0
LEGACY_GGUF_ENV_VARS = (
    "KIMODO_GGUF_MODEL_PATH",
    "KIMODO_GGUF_CTX",
    "KIMODO_GGUF_STARTUP_TIMEOUT_SEC",
    "KIMODO_GGUF_EMBED_MODEL",
    "KIMODO_FORCE_GGUF",
)


class LoggerLike(Protocol):
    def log(self, message: str) -> None: ...


@dataclass(frozen=True)
class MainModelSpec:
    local_name: str
    modelscope_repo: str
    huggingface_repo: str
    aliases: tuple[str, ...] = ()


@dataclass(frozen=True)
class AssetSpec:
    label: str
    local_dir_name: str
    modelscope_repo: str
    huggingface_repo: str | None


@dataclass(frozen=True)
class TextEncoderLayoutSpec:
    layout_id: str
    route: str
    label: str
    primary_local_dir_name: str
    peft_local_dir_name: str | None = None
    download_assets: tuple[AssetSpec, ...] = ()
    preferred_if_ready: bool = False
    text_encoder_device: str = "auto"


@dataclass(frozen=True)
class ResolvedModel:
    requested_name: str
    local_name: str
    modelscope_repo: str
    huggingface_repo: str


@dataclass(frozen=True)
class RuntimeHints:
    normalized_device: str | None


class DownloadSite(str, Enum):
    MODELSCOPE = "modelscope"
    HUGGINGFACE = "huggingface"


@dataclass(frozen=True)
class SiteProbeResult:
    site: DownloadSite
    repo_id: str
    url: str
    ok: bool
    elapsed_ms: int
    status_code: int | None
    error: str = ""


@dataclass(frozen=True)
class DownloadSiteSelection:
    selected_site: DownloadSite
    huggingface_ok: bool
    modelscope_ok: bool
    huggingface_elapsed_ms: int
    modelscope_elapsed_ms: int
    huggingface_error: str = ""
    modelscope_error: str = ""


REMOVED_QUICKSERVER_ENV_VARS: dict[str, str] = {
    "CHECKPOINT_DIR": "QuickServer now uses KIMODO_MODELS_ROOT for local-only model loading; remove CHECKPOINT_DIR.",
    "KIMODO_CPU_TEXT_ENCODER": "QuickServer now auto-selects the local text encoder route; remove this variable.",
    "KIMODO_TEXT_ENCODER_DEVICE_HINT": "QuickServer now drives TEXT_ENCODER_DEVICE directly; remove this variable.",
}
PURGED_RUNTIME_ENV_VARS: tuple[str, ...] = (
    "CHECKPOINT_DIR",
    "KIMODO_CPU_TEXT_ENCODER",
    "KIMODO_TEXT_ENCODER_DEVICE_HINT",
    "KIMODO_BRIDGE_PID",
)


MAIN_MODELS: tuple[MainModelSpec, ...] = (
    MainModelSpec(
        local_name="Kimodo-SOMA-RP-v1",
        modelscope_repo="nv-community/Kimodo-SOMA-RP-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-RP-v1.1",
        aliases=("soma", "soma-rp", "kimodo-soma-rp"),
    ),
    MainModelSpec(
        local_name="Kimodo-SOMA-RP-v1.1",
        modelscope_repo="nv-community/Kimodo-SOMA-RP-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-RP-v1.1",
    ),
    MainModelSpec(
        local_name="Kimodo-SMPLX-RP-v1",
        modelscope_repo="nv-community/Kimodo-SMPLX-RP-v1",
        huggingface_repo="nvidia/Kimodo-SMPLX-RP-v1",
        aliases=("smplx", "smplx-rp", "kimodo-smplx-rp"),
    ),
    MainModelSpec(
        local_name="Kimodo-G1-RP-v1",
        modelscope_repo="nv-community/Kimodo-G1-RP-v1",
        huggingface_repo="nvidia/Kimodo-G1-RP-v1",
        aliases=("g1", "g1-rp", "kimodo-g1-rp"),
    ),
    MainModelSpec(
        local_name="Kimodo-SOMA-SEED-v1",
        modelscope_repo="nv-community/Kimodo-SOMA-SEED-v1",
        huggingface_repo="nvidia/Kimodo-SOMA-SEED-v1",
        aliases=("soma-seed", "kimodo-soma-seed"),
    ),
    MainModelSpec(
        local_name="Kimodo-SOMA-SEED-v1.1",
        modelscope_repo="nv-community/Kimodo-SOMA-SEED-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-SEED-v1.1",
    ),
    MainModelSpec(
        local_name="Kimodo-G1-SEED-v1",
        modelscope_repo="nv-community/Kimodo-G1-SEED-v1",
        huggingface_repo="nvidia/Kimodo-G1-SEED-v1",
        aliases=("g1-seed", "kimodo-g1-seed"),
    ),
)


def _build_main_model_registry() -> dict[str, MainModelSpec]:
    registry: dict[str, MainModelSpec] = {}
    for spec in MAIN_MODELS:
        for key in (spec.local_name, *spec.aliases):
            registry[str(key).lower()] = spec
    return registry


MAIN_MODEL_REGISTRY = _build_main_model_registry()

INT8_ASSET = AssetSpec(
    label="INT8 text encoder",
    local_dir_name=INT8_LOCAL_DIR,
    modelscope_repo="oneyoungmean/KIMODO-Meta3_llm2vec_INT8",
    huggingface_repo="oneyoungmean/KIMODO-Meta3_llm2vec_INT8",
)
NF4_ASSET = AssetSpec(
    label="NF4 text encoder",
    local_dir_name=NF4_LOCAL_DIR,
    modelscope_repo="oneyoungmean/KIMODO-Meta3_llm2vec_NF4",
    huggingface_repo="Aero-Ex/KIMODO-Meta3_llm2vec_NF4",
)
FP16_ASSET = AssetSpec(
    label="FP16 text encoder",
    local_dir_name=FP16_LOCAL_DIR,
    modelscope_repo="oneyoungmean/KIMODO-Meta3_llm2vec_FP16",
    huggingface_repo="Aero-Ex/KIMODO-Meta3_llm2vec_FP16",
)

INT8_LAYOUT = TextEncoderLayoutSpec(
    layout_id="int8_single",
    route=ENCODER_ROUTE_INT8,
    label="INT8 single-dir local layout",
    primary_local_dir_name=INT8_LOCAL_DIR,
    download_assets=(INT8_ASSET,),
    text_encoder_device="cpu",
)
NF4_LAYOUT = TextEncoderLayoutSpec(
    layout_id="nf4_single",
    route=ENCODER_ROUTE_NF4,
    label="NF4 single-dir local layout",
    primary_local_dir_name=NF4_LOCAL_DIR,
    download_assets=(NF4_ASSET,),
)
LEGACY_FP16_LAYOUT = TextEncoderLayoutSpec(
    layout_id="legacy_base_peft",
    route=ENCODER_ROUTE_FP16,
    label="Legacy Meta-Llama-3-8B + LLM2Vec PEFT local layout",
    primary_local_dir_name=LEGACY_FP16_BASE_LOCAL_DIR,
    peft_local_dir_name=LEGACY_FP16_PEFT_LOCAL_DIR,
    preferred_if_ready=True,
)
FP16_SINGLE_LAYOUT = TextEncoderLayoutSpec(
    layout_id="fp16_single",
    route=ENCODER_ROUTE_FP16,
    label="KIMODO Meta3 FP16 single-dir local layout",
    primary_local_dir_name=FP16_LOCAL_DIR,
    download_assets=(FP16_ASSET,),
)

TEXT_ENCODER_LAYOUTS: tuple[TextEncoderLayoutSpec, ...] = (
    INT8_LAYOUT,
    NF4_LAYOUT,
    LEGACY_FP16_LAYOUT,
    FP16_SINGLE_LAYOUT,
)
TEXT_ENCODER_LAYOUT_REGISTRY: dict[str, TextEncoderLayoutSpec] = {
    layout.layout_id: layout for layout in TEXT_ENCODER_LAYOUTS
}
TEXT_ENCODER_LAYOUTS_BY_ROUTE: dict[str, tuple[TextEncoderLayoutSpec, ...]] = {
    ENCODER_ROUTE_INT8: (INT8_LAYOUT,),
    ENCODER_ROUTE_NF4: (NF4_LAYOUT,),
    ENCODER_ROUTE_FP16: (LEGACY_FP16_LAYOUT, FP16_SINGLE_LAYOUT),
}


def resolve_main_model(requested_name: str | None) -> ResolvedModel:
    raw_name = str(requested_name or DEFAULT_MODEL_NAME).strip()
    if not raw_name:
        raise ValueError("Empty model name.")
    lookup = raw_name.lower()
    spec = MAIN_MODEL_REGISTRY.get(lookup)
    if spec is not None:
        return ResolvedModel(
            requested_name=raw_name,
            local_name=spec.local_name,
            modelscope_repo=spec.modelscope_repo,
            huggingface_repo=spec.huggingface_repo,
        )
    if lookup.startswith("kimodo-"):
        return ResolvedModel(
            requested_name=raw_name,
            local_name=raw_name,
            modelscope_repo=f"nv-community/{raw_name}",
            huggingface_repo=f"nvidia/{raw_name}",
        )
    raise ValueError(f"Unsupported model alias: {raw_name}")


def assert_no_legacy_gguf_env() -> None:
    active = [name for name in LEGACY_GGUF_ENV_VARS if os.environ.get(name, "").strip()]
    if active:
        raise ValueError(
            "Legacy GGUF environment variables are no longer supported: "
            + ", ".join(active)
        )


def assert_no_removed_quickserver_env() -> None:
    active = [name for name in REMOVED_QUICKSERVER_ENV_VARS if os.environ.get(name, "").strip()]
    if active:
        details = "; ".join(f"{name}: {REMOVED_QUICKSERVER_ENV_VARS[name]}" for name in active)
        raise ValueError(f"Removed QuickServer environment variables are no longer supported. {details}")


def scrub_removed_runtime_env(env: dict[str, str] | os._Environ[str]) -> None:
    for name in PURGED_RUNTIME_ENV_VARS:
        env.pop(name, None)


def normalize_runtime_hints(run_device: str | None) -> RuntimeHints:
    normalized_device: str | None = None
    assert_no_legacy_gguf_env()
    assert_no_removed_quickserver_env()

    raw_device = str(run_device or "").strip()
    if raw_device:
        lowered = raw_device.lower()
        if lowered == "cpu":
            normalized_device = "cpu"
        elif lowered == "cuda":
            normalized_device = "cuda:0"
        elif lowered.startswith("cuda"):
            normalized_device = raw_device
        elif lowered == "mps" or lowered.startswith("mps"):
            normalized_device = "mps"
        elif lowered == "xpu":
            normalized_device = "xpu:0"
        elif lowered.startswith("xpu"):
            normalized_device = raw_device
        else:
            raise ValueError(f"Invalid --device value: {run_device}")
    return RuntimeHints(
        normalized_device=normalized_device,
    )


def detect_total_vram_gb() -> float:
    try:
        import torch

        if torch.cuda.is_available() and torch.cuda.device_count() > 0:
            props = torch.cuda.get_device_properties(0)
            return float(props.total_memory) / (1024 ** 3)
    except Exception:
        pass
    return 0.0


def should_use_int8(total_vram_gb: float) -> bool:
    return float(total_vram_gb) < 6.0


def choose_prepare_encoder_route(highvram: bool, hints: RuntimeHints, total_vram_gb: float | None = None) -> str:
    if hints.normalized_device == "cpu":
        return ENCODER_ROUTE_INT8
    if hints.normalized_device == "mps" or str(hints.normalized_device or "").startswith("xpu"):
        return ENCODER_ROUTE_FP16 if highvram else ENCODER_ROUTE_INT8
    return ENCODER_ROUTE_FP16 if highvram else ENCODER_ROUTE_NF4


def default_models_root(root_dir: str | os.PathLike[str]) -> Path:
    return (Path(root_dir).resolve() / "models").resolve()


def resolve_models_root(root_dir: str | os.PathLike[str], models_root_arg: str | None) -> tuple[Path, bool]:
    root = Path(root_dir).resolve()
    raw_value = str(models_root_arg or os.environ.get("KIMODO_MODELS_ROOT") or "").strip()
    models_root = Path(raw_value).expanduser() if raw_value else default_models_root(root)
    if not models_root.is_absolute():
        models_root = (root / models_root).resolve()
    else:
        models_root = models_root.resolve()
    return models_root, models_root != default_models_root(root)


def local_model_dir(models_root: str | os.PathLike[str], resolved_model: ResolvedModel) -> Path:
    return Path(models_root).resolve() / resolved_model.local_name


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


def should_inject_once(recovery_flag_dir: Path, key: str, env_var: str) -> bool:
    if os.environ.get(env_var, "").strip() != "1":
        return False
    recovery_flag_dir.mkdir(parents=True, exist_ok=True)
    flag_path = recovery_flag_dir / f"{key}.done"
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


def _has_any_file(model_dir: Path, filenames: tuple[str, ...]) -> bool:
    return any((model_dir / filename).is_file() for filename in filenames)


def _has_any_path(target_dir: Path, names: tuple[str, ...] = (), patterns: tuple[str, ...] = ()) -> bool:
    for name in names:
        if (target_dir / name).exists():
            return True
    for pattern in patterns:
        if any(target_dir.glob(pattern)):
            return True
    return False


def _has_weight_file(model_dir: Path) -> bool:
    patterns = ("*.safetensors", "*.bin")
    return any(any(model_dir.glob(pattern)) for pattern in patterns)


def _main_model_ready(model_dir: Path) -> bool:
    return (model_dir / "config.yaml").is_file() and _has_any_path(
        model_dir,
        names=("model.safetensors", "pytorch_model.bin", "model.ckpt"),
        patterns=("*.pt", "*.safetensors"),
    )


def _llm2vec_ready(model_dir: Path) -> bool:
    return (
        (model_dir / "config.json").is_file()
        and (model_dir / "tokenizer_config.json").is_file()
        and _has_any_file(model_dir, ("tokenizer.json", "tokenizer.model"))
        and _has_weight_file(model_dir)
    )


def _int8_ready(model_dir: Path) -> bool:
    return (
        (model_dir / "config.json").is_file()
        and (model_dir / "tokenizer_config.json").is_file()
        and _has_any_file(model_dir, ("tokenizer.json", "tokenizer.model"))
        and (model_dir / "llm2vec_config.json").is_file()
        and (model_dir / "quantized_state_dict.pt").is_file()
        and (model_dir / "quantization_meta.json").is_file()
    )


def _legacy_peft_ready(model_dir: Path) -> bool:
    return (
        (model_dir / "adapter_config.json").is_file()
        and _has_any_file(
            model_dir,
            ("adapter_model.safetensors", "adapter_model.bin", "model.safetensors", "pytorch_model.bin"),
        )
    )


ASSET_READY_CHECKERS_BY_LOCAL_DIR = {
    INT8_LOCAL_DIR: _int8_ready,
    NF4_LOCAL_DIR: _llm2vec_ready,
    FP16_LOCAL_DIR: _llm2vec_ready,
}


def asset_is_ready(asset: AssetSpec, target_dir: Path) -> bool:
    if asset.label == "main model":
        return _main_model_ready(target_dir)
    checker = ASSET_READY_CHECKERS_BY_LOCAL_DIR.get(asset.local_dir_name)
    if checker is not None:
        return checker(target_dir)
    return target_dir.exists()


def resolve_text_encoder_layout(layout_id: str) -> TextEncoderLayoutSpec:
    layout = TEXT_ENCODER_LAYOUT_REGISTRY.get(str(layout_id or "").strip())
    if layout is None:
        raise ValueError(f"Unsupported text encoder layout: {layout_id}")
    return layout


def resolve_text_encoder_layout_paths(
    layout: TextEncoderLayoutSpec,
    models_root: str | os.PathLike[str],
) -> tuple[Path, Path | None]:
    models_path = Path(models_root).resolve()
    primary_dir = models_path / layout.primary_local_dir_name
    peft_dir = models_path / layout.peft_local_dir_name if layout.peft_local_dir_name else None
    return primary_dir, peft_dir


def text_encoder_layout_ready(layout: TextEncoderLayoutSpec, models_root: str | os.PathLike[str]) -> bool:
    primary_dir, peft_dir = resolve_text_encoder_layout_paths(layout, models_root)
    if layout.route == ENCODER_ROUTE_INT8:
        primary_ready = _int8_ready(primary_dir)
    else:
        primary_ready = _llm2vec_ready(primary_dir)
    if not primary_ready:
        return False
    if peft_dir is not None:
        return _legacy_peft_ready(peft_dir)
    return True


def select_text_encoder_layout_for_route(
    route: str,
    models_root: str | os.PathLike[str],
) -> TextEncoderLayoutSpec:
    layouts = TEXT_ENCODER_LAYOUTS_BY_ROUTE.get(str(route or "").strip())
    if not layouts:
        raise ValueError(f"Unsupported text encoder route: {route}")
    for layout in layouts:
        if layout.preferred_if_ready and text_encoder_layout_ready(layout, models_root):
            return layout
    for layout in layouts:
        if not layout.preferred_if_ready:
            return layout
    return layouts[0]


def select_text_encoder_layout(
    highvram: bool,
    hints: RuntimeHints,
    models_root: str | os.PathLike[str],
    total_vram_gb: float | None = None,
) -> TextEncoderLayoutSpec:
    route = choose_prepare_encoder_route(highvram, hints, total_vram_gb=total_vram_gb)
    return select_text_encoder_layout_for_route(route, models_root)


def _format_bytes(value: int) -> str:
    size = float(max(0, int(value)))
    units = ("B", "KiB", "MiB", "GiB", "TiB")
    for unit in units:
        if size < 1024.0 or unit == units[-1]:
            if unit == "B":
                return f"{int(size)} {unit}"
            return f"{size:.1f} {unit}"
        size /= 1024.0
    return f"{int(value)} B"


def _download_site_repo_id(asset: AssetSpec, site: DownloadSite) -> str:
    if site == DownloadSite.MODELSCOPE:
        return str(asset.modelscope_repo or "").strip()
    return str(asset.huggingface_repo or "").strip()


def _download_site_url(site: DownloadSite, repo_id: str) -> str:
    if site == DownloadSite.MODELSCOPE:
        return f"https://www.modelscope.cn/models/{repo_id}"
    return f"https://huggingface.co/{repo_id}"


def _status_allows_probe_success(status_code: int | None) -> bool:
    return status_code is not None and 200 <= int(status_code) < 400


def _probe_repo_request(url: str, timeout_seconds: float, method: str) -> tuple[bool, int | None, str]:
    request = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            status_code = getattr(response, "status", None) or response.getcode()
            if _status_allows_probe_success(status_code):
                return True, int(status_code), ""
            return False, int(status_code), f"HTTP {status_code}"
    except urllib.error.HTTPError as exc:
        if method == "HEAD" and int(exc.code) in (405, 501):
            return _probe_repo_request(url, timeout_seconds, "GET")
        return False, int(exc.code), f"HTTP {exc.code}"
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        return False, None, f"{type(reason).__name__}: {reason}"
    except Exception as exc:
        return False, None, f"{type(exc).__name__}: {exc}"


def probe_download_site(asset: AssetSpec, site: DownloadSite, timeout_seconds: float = DOWNLOAD_PROBE_TIMEOUT_SECONDS) -> SiteProbeResult:
    repo_id = _download_site_repo_id(asset, site)
    if not repo_id:
        return SiteProbeResult(
            site=site,
            repo_id="",
            url="",
            ok=False,
            elapsed_ms=0,
            status_code=None,
            error="missing repo id",
        )

    url = _download_site_url(site, repo_id)
    started = time.monotonic()
    ok, status_code, error = _probe_repo_request(url, timeout_seconds, "HEAD")
    elapsed_ms = max(0, int((time.monotonic() - started) * 1000))
    return SiteProbeResult(
        site=site,
        repo_id=repo_id,
        url=url,
        ok=ok,
        elapsed_ms=elapsed_ms,
        status_code=status_code,
        error=error,
    )


def _probe_summary(result: SiteProbeResult) -> str:
    status_text = f"status={result.status_code}" if result.status_code is not None else "status=<none>"
    error_text = f" error={result.error}" if result.error else ""
    return (
        f"site={result.site.value} repo={result.repo_id or '<missing>'} "
        f"ok={str(result.ok).lower()} elapsed_ms={result.elapsed_ms} {status_text}{error_text}"
    )


def _select_download_site_from_probe_results(
    huggingface_result: SiteProbeResult,
    modelscope_result: SiteProbeResult,
) -> DownloadSiteSelection:
    if huggingface_result.ok and modelscope_result.ok:
        selected_site = (
            DownloadSite.HUGGINGFACE
            if huggingface_result.elapsed_ms <= modelscope_result.elapsed_ms
            else DownloadSite.MODELSCOPE
        )
    elif huggingface_result.ok:
        selected_site = DownloadSite.HUGGINGFACE
    elif modelscope_result.ok:
        selected_site = DownloadSite.MODELSCOPE
    else:
        raise RuntimeError(
            "Download site probe failed: "
            f"huggingface=({_probe_summary(huggingface_result)}); "
            f"modelscope=({_probe_summary(modelscope_result)})"
        )

    return DownloadSiteSelection(
        selected_site=selected_site,
        huggingface_ok=huggingface_result.ok,
        modelscope_ok=modelscope_result.ok,
        huggingface_elapsed_ms=huggingface_result.elapsed_ms,
        modelscope_elapsed_ms=modelscope_result.elapsed_ms,
        huggingface_error=huggingface_result.error,
        modelscope_error=modelscope_result.error,
    )


def select_download_site(asset: AssetSpec, timeout_seconds: float = DOWNLOAD_PROBE_TIMEOUT_SECONDS) -> DownloadSiteSelection:
    huggingface_result = probe_download_site(asset, DownloadSite.HUGGINGFACE, timeout_seconds)
    modelscope_result = probe_download_site(asset, DownloadSite.MODELSCOPE, timeout_seconds)
    return _select_download_site_from_probe_results(huggingface_result, modelscope_result)


@contextmanager
def _suppress_modelscope_tqdm():
    try:
        import modelscope.hub.file_download as file_download
        import modelscope.hub.snapshot_download as snapshot_download_mod
    except Exception:
        yield
        return

    original_file_tqdm = getattr(file_download, "tqdm", None)
    original_snapshot_tqdm = getattr(snapshot_download_mod, "tqdm", None)
    original_tqdm_callback = getattr(file_download, "TqdmCallback", None)

    class _NoopTqdm:
        def __init__(self, *args, **kwargs):
            self.total = kwargs.get("total", 0)

        def update(self, *args, **kwargs):
            return None

        def refresh(self):
            return None

        def close(self):
            return None

        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc, tb):
            self.close()
            return False

        def set_description(self, *args, **kwargs):
            return None

        def set_postfix(self, *args, **kwargs):
            return None

    class _NoopProgressCallback:
        def __init__(self, *args, **kwargs):
            pass

        def update(self, *args, **kwargs):
            return None

        def end(self):
            return None

    if original_file_tqdm is not None:
        file_download.tqdm = _NoopTqdm
    if original_snapshot_tqdm is not None:
        snapshot_download_mod.tqdm = _NoopTqdm
    if original_tqdm_callback is not None:
        file_download.TqdmCallback = _NoopProgressCallback

    try:
        yield
    finally:
        if original_file_tqdm is not None:
            file_download.tqdm = original_file_tqdm
        if original_snapshot_tqdm is not None:
            snapshot_download_mod.tqdm = original_snapshot_tqdm
        if original_tqdm_callback is not None:
            file_download.TqdmCallback = original_tqdm_callback


@contextmanager
def _suppress_huggingface_progress():
    try:
        from huggingface_hub.utils import disable_progress_bars, enable_progress_bars
    except Exception:
        yield
        return

    disable_progress_bars()
    try:
        yield
    finally:
        enable_progress_bars()


def _make_logged_progress_callback(logger: LoggerLike, label: str):
    log_lock = threading.Lock()

    class _LoggedProgressCallback:
        def __init__(self, filename: str, file_size: int):
            self.filename = str(filename)
            self.file_size = max(0, int(file_size or 0))
            self.downloaded = 0
            self._last_logged_at = 0.0
            self._started_at = time.monotonic()
            self._finished = False
            self._log(
                f"[DOWNLOAD] {label}: {self.filename} started "
                f"({_format_bytes(self.file_size)})"
            )

        def _log(self, message: str) -> None:
            with log_lock:
                logger.log(message)

        def _status_text(self) -> str:
            if self.file_size > 0:
                downloaded = min(self.downloaded, self.file_size)
                percent = min(100, int(downloaded * 100 / self.file_size))
                return f"{_format_bytes(downloaded)}/{_format_bytes(self.file_size)} ({percent}%)"
            return f"{_format_bytes(self.downloaded)} downloaded"

        def _maybe_log(self, final: bool = False) -> None:
            if self._finished and not final:
                return
            now = time.monotonic()
            if not final and now - self._last_logged_at < 5.0:
                return
            self._last_logged_at = now
            self._log(f"[DOWNLOAD] {label}: {self.filename} {self._status_text()}")

        def update(self, size: int):
            self.downloaded += int(size)
            self._maybe_log(final=False)

        def end(self):
            if self._finished:
                return
            self._finished = True
            if self.file_size > 0:
                self.downloaded = max(self.downloaded, self.file_size)
            elapsed = time.monotonic() - self._started_at
            self._log(
                f"[DOWNLOAD] {label}: {self.filename} complete "
                f"({self._status_text()}, {elapsed:.1f}s)"
            )

    return _LoggedProgressCallback


def download_via_modelscope(asset: AssetSpec, target_dir: Path, logger: LoggerLike) -> None:
    repo_id = _download_site_repo_id(asset, DownloadSite.MODELSCOPE)
    if not repo_id:
        raise RuntimeError(f"Missing ModelScope repo id for {asset.label}.")

    from modelscope import snapshot_download as ms_snapshot_download

    try:
        progress_callbacks = [_make_logged_progress_callback(logger, asset.label)]
        with _suppress_modelscope_tqdm():
            ms_snapshot_download(
                model_id=repo_id,
                local_dir=str(target_dir),
                progress_callbacks=progress_callbacks,
            )
    except Exception as exc:
        raise RuntimeError(f"Failed to download {asset.label} via ModelScope: {exc}") from exc


def download_via_huggingface(asset: AssetSpec, target_dir: Path) -> None:
    repo_id = _download_site_repo_id(asset, DownloadSite.HUGGINGFACE)
    if not repo_id:
        raise RuntimeError(f"Missing Hugging Face repo id for {asset.label}.")

    from huggingface_hub import snapshot_download as hf_snapshot_download

    try:
        with _suppress_huggingface_progress():
            hf_snapshot_download(
                repo_id=repo_id,
                local_dir=str(target_dir),
            )
    except Exception as exc:
        raise RuntimeError(f"Failed to download {asset.label} via HuggingFace: {exc}") from exc


def ensure_asset_present(
    asset: AssetSpec,
    target_dir: Path,
    logger: LoggerLike,
    recovery_flag_dir: Path,
    download_counter: list[int],
    *,
    force_site: DownloadSite | None = None,
    allow_download: bool = True,
) -> None:
    if asset_is_ready(asset, target_dir):
        logger.log(f"[OK] {asset.label} already present: {target_dir}")
        return

    if not allow_download:
        raise RuntimeError(f"Missing required {asset.label}: {target_dir}")

    if should_inject_once(recovery_flag_dir, "download_net_bad", "KIMODO_TEST_INJECT_DOWNLOAD_NET_BAD_ONCE"):
        raise RuntimeError("Injected download network failure once.")

    logger.log(f"[STEP] Downloading {asset.label}: {asset.local_dir_name}")
    target_dir.mkdir(parents=True, exist_ok=True)
    selected_site: DownloadSite
    selected_repo_id: str

    if force_site is not None:
        selected_site = force_site
        selected_repo_id = _download_site_repo_id(asset, selected_site)
        if not selected_repo_id:
            raise RuntimeError(
                f"Forced download site '{selected_site.value}' is unavailable for {asset.label}: missing repo id."
            )
        logger.log(
            f"[INFO] {asset.label}: forced download site={selected_site.value} "
            f"repo={selected_repo_id}"
        )
        if selected_site == DownloadSite.HUGGINGFACE:
            download_via_huggingface(asset, target_dir)
        else:
            download_via_modelscope(asset, target_dir, logger)
    else:
        logger.log(
            f"[INFO] {asset.label}: probing download sites before transfer "
            f"(timeout={DOWNLOAD_PROBE_TIMEOUT_SECONDS:.1f}s)"
        )
        huggingface_result = probe_download_site(asset, DownloadSite.HUGGINGFACE, DOWNLOAD_PROBE_TIMEOUT_SECONDS)
        modelscope_result = probe_download_site(asset, DownloadSite.MODELSCOPE, DOWNLOAD_PROBE_TIMEOUT_SECONDS)
        logger.log(f"[PROBE] {asset.label}: {_probe_summary(huggingface_result)}")
        logger.log(f"[PROBE] {asset.label}: {_probe_summary(modelscope_result)}")
        try:
            selection = _select_download_site_from_probe_results(huggingface_result, modelscope_result)
        except RuntimeError as exc:
            raise RuntimeError(f"Download site probe failed for {asset.label}: {exc}") from exc

        selected_site = selection.selected_site
        selected_repo_id = _download_site_repo_id(asset, selected_site)
        logger.log(
            f"[INFO] {asset.label}: selected download site={selected_site.value} "
            f"repo={selected_repo_id or '<missing>'}"
        )
        if selected_site == DownloadSite.HUGGINGFACE:
            download_via_huggingface(asset, target_dir)
        else:
            download_via_modelscope(asset, target_dir, logger)

    if not asset_is_ready(asset, target_dir):
        raise RuntimeError(f"Downloaded asset is incomplete: {target_dir}")

    logger.log(f"[OK] {asset.label} ready via {selected_site.value}: {selected_repo_id}")
    download_counter[0] += 1
    if should_inject_once(recovery_flag_dir, "download_abort", "KIMODO_TEST_INJECT_DOWNLOAD_ABORT_ONCE"):
        raise RuntimeError("Injected download interrupt once.")


def build_runtime_env(
    root_dir: str | os.PathLike[str],
    source_root: str | os.PathLike[str],
    models_root: str | os.PathLike[str],
    highvram: bool,
    hints: RuntimeHints,
    encoder_route: str | None = None,
    encoder_layout_id: str | None = None,
) -> dict[str, str]:
    root = Path(root_dir).resolve()
    models_path = Path(models_root).resolve()
    selected_encoder_route = encoder_route or choose_prepare_encoder_route(highvram, hints)
    selected_layout = (
        resolve_text_encoder_layout(encoder_layout_id)
        if encoder_layout_id
        else select_text_encoder_layout_for_route(selected_encoder_route, models_path)
    )
    if selected_layout.route != selected_encoder_route:
        raise ValueError(
            "Encoder route/layout mismatch: "
            f"route={selected_encoder_route} layout={selected_layout.layout_id}"
        )
    primary_dir, peft_dir = resolve_text_encoder_layout_paths(selected_layout, models_path)

    env: dict[str, str] = {
        "PYTHONPATH": str(Path(source_root).resolve()),
        "KIMODO_ROOT_PATH": str(root),
        "KIMODO_MODELS_ROOT": str(models_path),
        "KIMODO_HIGHVRAM": "1" if highvram else "0",
        "LOCAL_CACHE": "true",
        "TEXT_ENCODER": "llm2vec_int8" if selected_layout.route == ENCODER_ROUTE_INT8 else "llm2vec",
        "TEXT_ENCODER_MODE": "local",
    }
    env["TEXT_ENCODER_DEVICE"] = hints.normalized_device or selected_layout.text_encoder_device
    env["KIMODO_LLM2VEC_DIR"] = str(primary_dir)
    if peft_dir is not None:
        env["TEXT_ENCODERS_DIR"] = str(models_path)
        env["KIMODO_LLM2VEC_PEFT_DIR"] = str(peft_dir)
    else:
        env["TEXT_ENCODERS_DIR"] = ""
        env["KIMODO_LLM2VEC_PEFT_DIR"] = ""
    return env


def build_offline_cache_env(root_dir: str | os.PathLike[str]) -> dict[str, str]:
    root = Path(root_dir).resolve()
    hf_home = root / "hf_cache"
    return {
        "HF_HOME": str(hf_home),
        "TRANSFORMERS_CACHE": str(hf_home / "transformers"),
        "HF_HUB_CACHE": str(hf_home / "hub"),
        "HUGGINGFACE_HUB_CACHE": str(hf_home / "hub"),
        "TRANSFORMERS_OFFLINE": "1",
        "HF_HUB_OFFLINE": "1",
        "HF_DATASETS_OFFLINE": "1",
        "PYTHONUNBUFFERED": "1",
    }
