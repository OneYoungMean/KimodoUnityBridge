from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
from enum import Enum
import multiprocessing
import os
from pathlib import Path
from queue import Empty
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
TEXT_ENCODER_MODE_HIGH_PERFORMANCE = "high_performance"
TEXT_ENCODER_MODE_HIGH_PRECISION = "high_precision"
DEFAULT_TEXT_ENCODER_MODE = TEXT_ENCODER_MODE_HIGH_PRECISION
MOTION_MODEL_MIN_FREE_GB = 2.0
NF4_ENCODER_MIN_FREE_GB = 6.0
INT8_ENCODER_MIN_FREE_GB = 8.0
FP16_ENCODER_MIN_FREE_GB = 16.0
KIMODO_ACCELERATOR_MIN_GB = MOTION_MODEL_MIN_FREE_GB
NF4_ACCELERATOR_MIN_GB = NF4_ENCODER_MIN_FREE_GB
INT8_ACCELERATOR_MIN_GB = INT8_ENCODER_MIN_FREE_GB
FP16_ACCELERATOR_MIN_GB = FP16_ENCODER_MIN_FREE_GB
DOWNLOAD_PROBE_TIMEOUT_SECONDS = 1.0
LEGACY_GGUF_ENV_VARS = (
    "KIMODO_GGUF_MODEL_PATH",
    "KIMODO_GGUF_CTX",
    "KIMODO_GGUF_STARTUP_TIMEOUT_SEC",
    "KIMODO_GGUF_EMBED_MODEL",
    "KIMODO_FORCE_GGUF",
)


class DownloadCancelledError(RuntimeError):
    pass


def raise_if_download_cancelled(cancel_event: threading.Event | None) -> None:
    if cancel_event is not None and cancel_event.is_set():
        raise DownloadCancelledError("Download canceled.")


class LoggerLike(Protocol):
    def log(self, message: str) -> None: ...


@dataclass(frozen=True)
class ModelSpec:
    model_name: str
    modelscope_repo: str
    huggingface_repo: str
    backend: str = "kimodo"
    source_fps: float = 30.0
    horizon_frames: int = 0
    frames_per_token: int = 1
    max_context_frames: int = 0
    rig_profile: str = "somaskel77"
    joint_count: int = 77
    max_diffusion_steps: int = 1000
    default_diffusion_steps: int = 100
    cfg_text_weight: float = 2.0
    cfg_constraint_weight: float = 2.0
    motion_rep_fingerprint: str = ""
    postprocess: bool = True
    supports_streaming: bool = False
    supports_timeline_segments: bool = True
    aliases: tuple[str, ...] = ()

    @property
    def local_name(self) -> str:
        return self.model_name


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


@dataclass(frozen=True)
class ResolvedModel:
    requested_name: str
    local_name: str
    modelscope_repo: str
    huggingface_repo: str


@dataclass(frozen=True)
class TextEncoderRuntimeDecision:
    mode: str
    motion_device: str
    encoder_route: str
    encoder_device: str
    reason: str
    effective_free_vram_gb: float

    @property
    def effective_vram_gb(self) -> float:
        """Compatibility alias; the value has always meant the routing budget."""
        return self.effective_free_vram_gb


class DownloadSite(str, Enum):
    MODELSCOPE = "modelscope"
    HUGGINGFACE = "huggingface"


def normalize_text_encoder_mode(value: str | None) -> str:
    normalized = str(value or DEFAULT_TEXT_ENCODER_MODE).strip().lower().replace("-", "_")
    if normalized not in {TEXT_ENCODER_MODE_HIGH_PERFORMANCE, TEXT_ENCODER_MODE_HIGH_PRECISION}:
        raise ValueError(
            "text_encoder_mode must be 'high_performance' or 'high_precision'."
        )
    return normalized


def resolve_text_encoder_runtime(
    mode: str | None,
    runtime_device: str | None,
    effective_free_vram_gb: float,
    *,
    nf4_available: bool,
    int8_accelerator_available: bool,
    fp16_accelerator_available: bool,
) -> TextEncoderRuntimeDecision:
    resolved_mode = normalize_text_encoder_mode(mode)
    device = str(runtime_device or "cpu").strip().lower() or "cpu"
    free_vram = max(0.0, float(effective_free_vram_gb))
    has_accelerator = device != "cpu"
    motion_device = device if has_accelerator else "cpu"

    # Apple Silicon/MPS cannot reliably load the dynamic INT8 bundles produced
    # on x86 CPUs (for example, AMD/FBGEMM). Always use the portable FP16
    # encoder route on Metal; it may still run on CPU when the memory budget or
    # kernel probe does not allow MPS execution.
    if resolved_mode == TEXT_ENCODER_MODE_HIGH_PRECISION or device == "mps":
        use_accelerator = (
            has_accelerator
            and fp16_accelerator_available
            and free_vram >= FP16_ENCODER_MIN_FREE_GB
        )
        return TextEncoderRuntimeDecision(
            mode=resolved_mode,
            motion_device=motion_device,
            encoder_route=ENCODER_ROUTE_FP16,
            encoder_device=device if use_accelerator else "cpu",
            reason=(
                "fp16_accelerator"
                if use_accelerator
                else "fp16_cpu_insufficient_vram_or_capability"
            ),
            effective_free_vram_gb=free_vram,
        )

    if has_accelerator and nf4_available and free_vram >= NF4_ENCODER_MIN_FREE_GB:
        return TextEncoderRuntimeDecision(
            mode=resolved_mode,
            motion_device=motion_device,
            encoder_route=ENCODER_ROUTE_NF4,
            encoder_device=device,
            reason="nf4_accelerator",
            effective_free_vram_gb=free_vram,
        )

    use_int8_accelerator = (
        has_accelerator
        and int8_accelerator_available
        and free_vram >= INT8_ENCODER_MIN_FREE_GB
    )
    return TextEncoderRuntimeDecision(
        mode=resolved_mode,
        motion_device=motion_device,
        encoder_route=ENCODER_ROUTE_INT8,
        encoder_device=device if use_int8_accelerator else "cpu",
        reason=(
            "int8_accelerator"
            if use_int8_accelerator
            else "int8_cpu_insufficient_vram_or_capability"
        ),
        effective_free_vram_gb=free_vram,
    )


def force_text_encoder_cpu(
    decision: TextEncoderRuntimeDecision,
    reason: str = "accelerator_oom_cpu_fallback",
) -> TextEncoderRuntimeDecision:
    return TextEncoderRuntimeDecision(
        mode=decision.mode,
        motion_device=decision.motion_device,
        # NF4 has no CPU layout, so preserve the existing NF4 -> INT8 fallback.
        # MPS deliberately selects FP16; keep that route instead of switching
        # back to an x86-generated INT8 bundle.
        encoder_route=(
            ENCODER_ROUTE_INT8
            if decision.encoder_route == ENCODER_ROUTE_NF4
            else decision.encoder_route
        ),
        encoder_device="cpu",
        reason=reason,
        effective_free_vram_gb=decision.effective_free_vram_gb,
    )


def motion_model_min_free_vram_gb(model_name: str | None) -> float:
    """Conservative load-time budget shared by the current Kimodo/ARDY checkpoints."""
    _ = model_name
    return MOTION_MODEL_MIN_FREE_GB


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


MAIN_MODELS: tuple[ModelSpec, ...] = (
    ModelSpec(
        model_name="Kimodo-SOMA-RP-v1",
        modelscope_repo="nv-community/Kimodo-SOMA-RP-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-RP-v1.1",
        aliases=("soma", "soma-rp", "kimodo-soma-rp"),
    ),
    ModelSpec(
        model_name="Kimodo-SOMA-RP-v1.1",
        modelscope_repo="nv-community/Kimodo-SOMA-RP-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-RP-v1.1",
    ),
    ModelSpec(
        model_name="Kimodo-SMPLX-RP-v1",
        modelscope_repo="nv-community/Kimodo-SMPLX-RP-v1",
        huggingface_repo="nvidia/Kimodo-SMPLX-RP-v1",
        rig_profile="smplx22",
        joint_count=22,
        aliases=("smplx", "smplx-rp", "kimodo-smplx-rp"),
    ),
    ModelSpec(
        model_name="Kimodo-G1-RP-v1",
        modelscope_repo="nv-community/Kimodo-G1-RP-v1",
        huggingface_repo="nvidia/Kimodo-G1-RP-v1",
        rig_profile="g1skel34",
        joint_count=34,
        aliases=("g1", "g1-rp", "kimodo-g1-rp"),
    ),
    ModelSpec(
        model_name="Kimodo-SOMA-SEED-v1",
        modelscope_repo="nv-community/Kimodo-SOMA-SEED-v1",
        huggingface_repo="nvidia/Kimodo-SOMA-SEED-v1",
        aliases=("soma-seed", "kimodo-soma-seed"),
    ),
    ModelSpec(
        model_name="Kimodo-SOMA-SEED-v1.1",
        modelscope_repo="nv-community/Kimodo-SOMA-SEED-v1.1",
        huggingface_repo="nvidia/Kimodo-SOMA-SEED-v1.1",
    ),
    ModelSpec(
        model_name="Kimodo-G1-SEED-v1",
        modelscope_repo="nv-community/Kimodo-G1-SEED-v1",
        huggingface_repo="nvidia/Kimodo-G1-SEED-v1",
        rig_profile="g1skel34",
        joint_count=34,
        aliases=("g1-seed", "kimodo-g1-seed"),
    ),
)


def _build_main_model_registry() -> dict[str, ModelSpec]:
    registry: dict[str, ModelSpec] = {}
    for spec in MAIN_MODELS:
        for key in (spec.local_name, *spec.aliases):
            registry[str(key).lower()] = spec
    return registry


MAIN_MODEL_REGISTRY = _build_main_model_registry()

ARDY_CORE_PROFILE = ModelSpec(
    model_name="ARDY-Core-RP-20FPS-Horizon40",
    modelscope_repo="nv-community/ARDY-Core-RP-20FPS-Horizon40",
    huggingface_repo="nvidia/ARDY-Core-RP-20FPS-Horizon40",
    backend="ardy",
    source_fps=20.0,
    horizon_frames=40,
    frames_per_token=4,
    max_context_frames=200,
    rig_profile="cskel27",
    joint_count=27,
    max_diffusion_steps=10,
    default_diffusion_steps=10,
    cfg_text_weight=2.0,
    cfg_constraint_weight=2.0,
    motion_rep_fingerprint="ardy-core-rp-20fps-h40:nfpt4:motionrep-v1",
    postprocess=True,
    supports_streaming=True,
    aliases=("ardy-core", "ardy-core40"),
)
ARDY_CORE8_PROFILE = ModelSpec(
    model_name="ARDY-Core-RP-20FPS-Horizon8",
    modelscope_repo="nv-community/ARDY-Core-RP-20FPS-Horizon8",
    huggingface_repo="nvidia/ARDY-Core-RP-20FPS-Horizon8",
    backend="ardy",
    source_fps=20.0,
    horizon_frames=8,
    frames_per_token=4,
    max_context_frames=200,
    rig_profile="cskel27",
    joint_count=27,
    max_diffusion_steps=10,
    default_diffusion_steps=10,
    cfg_text_weight=2.0,
    cfg_constraint_weight=2.0,
    motion_rep_fingerprint="ardy-core-rp-20fps-h8:nfpt4:motionrep-v1",
    postprocess=True,
    supports_streaming=True,
    aliases=("ardy-core8",),
)
ARDY_G1_PROFILE = ModelSpec(
    model_name="ARDY-G1-RP-25FPS-Horizon52",
    modelscope_repo="nv-community/ARDY-G1-RP-25FPS-Horizon52",
    huggingface_repo="nvidia/ARDY-G1-RP-25FPS-Horizon52",
    backend="ardy",
    source_fps=25.0,
    horizon_frames=52,
    frames_per_token=4,
    max_context_frames=248,
    rig_profile="g1skel34",
    joint_count=34,
    max_diffusion_steps=10,
    default_diffusion_steps=10,
    cfg_text_weight=2.0,
    cfg_constraint_weight=2.0,
    motion_rep_fingerprint="ardy-g1-rp-25fps-h52:nfpt4:motionrep-v1",
    postprocess=False,
    supports_streaming=True,
    aliases=("ardy-g1", "ardy-g152"),
)
ARDY_G18_PROFILE = ModelSpec(
    model_name="ARDY-G1-RP-25FPS-Horizon8",
    modelscope_repo="nv-community/ARDY-G1-RP-25FPS-Horizon8",
    huggingface_repo="nvidia/ARDY-G1-RP-25FPS-Horizon8",
    backend="ardy",
    source_fps=25.0,
    horizon_frames=8,
    frames_per_token=4,
    max_context_frames=248,
    rig_profile="g1skel34",
    joint_count=34,
    max_diffusion_steps=10,
    default_diffusion_steps=10,
    cfg_text_weight=2.0,
    cfg_constraint_weight=2.0,
    motion_rep_fingerprint="ardy-g1-rp-25fps-h8:nfpt4:motionrep-v1",
    postprocess=False,
    supports_streaming=True,
    aliases=("ardy-g18",),
)
MOTION_MODEL_PROFILES: tuple[ModelSpec, ...] = (
    ARDY_CORE_PROFILE,
    ARDY_CORE8_PROFILE,
    ARDY_G1_PROFILE,
    ARDY_G18_PROFILE,
)
ALL_MODEL_SPECS = MAIN_MODELS + MOTION_MODEL_PROFILES
MODEL_SPEC_REGISTRY = {
    key.lower(): profile
    for profile in ALL_MODEL_SPECS
    for key in (profile.model_name, *profile.aliases)
}
MOTION_MODEL_PROFILE_REGISTRY = {
    key: profile for key, profile in MODEL_SPEC_REGISTRY.items() if profile.backend == "ardy"
}

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
)
INT8_GPU_LAYOUT = TextEncoderLayoutSpec(
    layout_id="int8_gpu_from_fp16",
    route=ENCODER_ROUTE_INT8,
    label="INT8 accelerator layout loaded from the FP16 bundle",
    primary_local_dir_name=FP16_LOCAL_DIR,
    download_assets=(FP16_ASSET,),
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
    INT8_GPU_LAYOUT,
    NF4_LAYOUT,
    LEGACY_FP16_LAYOUT,
    FP16_SINGLE_LAYOUT,
)
TEXT_ENCODER_LAYOUT_REGISTRY: dict[str, TextEncoderLayoutSpec] = {
    layout.layout_id: layout for layout in TEXT_ENCODER_LAYOUTS
}
TEXT_ENCODER_LAYOUTS_BY_ROUTE: dict[str, tuple[TextEncoderLayoutSpec, ...]] = {
    ENCODER_ROUTE_INT8: (INT8_LAYOUT, INT8_GPU_LAYOUT),
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


def resolve_model_spec(requested_name: str | None) -> ModelSpec | None:
    return MODEL_SPEC_REGISTRY.get(str(requested_name or "").strip().lower())


def resolve_motion_model_profile(requested_name: str | None) -> ModelSpec | None:
    return MOTION_MODEL_PROFILE_REGISTRY.get(str(requested_name or "").strip().lower())


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
    encoder_device: str | None = None,
) -> TextEncoderLayoutSpec:
    layouts = TEXT_ENCODER_LAYOUTS_BY_ROUTE.get(str(route or "").strip())
    if not layouts:
        raise ValueError(f"Unsupported text encoder route: {route}")
    if str(route or "").strip() == ENCODER_ROUTE_INT8:
        use_cpu = str(encoder_device or "cpu").strip().lower() == "cpu"
        return INT8_LAYOUT if use_cpu else INT8_GPU_LAYOUT
    for layout in layouts:
        if layout.preferred_if_ready and text_encoder_layout_ready(layout, models_root):
            return layout
    for layout in layouts:
        if not layout.preferred_if_ready:
            return layout
    return layouts[0]


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


def _make_logged_progress_callback(
    logger: LoggerLike,
    label: str,
    cancel_event: threading.Event | None = None,
):
    log_lock = threading.Lock()

    class _LoggedProgressCallback:
        def __init__(self, filename: str, file_size: int):
            raise_if_download_cancelled(cancel_event)
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
            elapsed = max(0.001, time.monotonic() - self._started_at)
            speed = self.downloaded / elapsed
            speed_text = f" speed={_format_bytes(int(speed))}/s"
            if self.file_size > 0:
                downloaded = min(self.downloaded, self.file_size)
                percent = min(100, int(downloaded * 100 / self.file_size))
                remaining = max(0.0, self.file_size - downloaded) / speed if speed > 0 else None
                eta_text = f" eta={remaining:.1f}s" if remaining is not None else ""
                return f"{_format_bytes(downloaded)}/{_format_bytes(self.file_size)} ({percent}%){speed_text}{eta_text}"
            return f"{_format_bytes(self.downloaded)} downloaded{speed_text}"

        def _maybe_log(self, final: bool = False) -> None:
            if self._finished and not final:
                return
            now = time.monotonic()
            if not final and now - self._last_logged_at < 5.0:
                return
            self._last_logged_at = now
            self._log(f"[DOWNLOAD] {label}: {self.filename} {self._status_text()}")

        def update(self, size: int):
            raise_if_download_cancelled(cancel_event)
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


def download_via_modelscope(
    asset: AssetSpec,
    target_dir: Path,
    logger: LoggerLike,
    cancel_event: threading.Event | None = None,
) -> None:
    raise_if_download_cancelled(cancel_event)
    repo_id = _download_site_repo_id(asset, DownloadSite.MODELSCOPE)
    if not repo_id:
        raise RuntimeError(f"Missing ModelScope repo id for {asset.label}.")

    from modelscope import snapshot_download as ms_snapshot_download

    try:
        progress_callbacks = [_make_logged_progress_callback(logger, asset.label, cancel_event)]
        with _suppress_modelscope_tqdm():
            ms_snapshot_download(
                model_id=repo_id,
                local_dir=str(target_dir),
                progress_callbacks=progress_callbacks,
            )
        raise_if_download_cancelled(cancel_event)
    except DownloadCancelledError:
        raise
    except Exception as exc:
        raise RuntimeError(f"Failed to download {asset.label} via ModelScope: {exc}") from exc


def _download_huggingface_worker(repo_id: str, target_dir: str, errors) -> None:
    try:
        from huggingface_hub import snapshot_download as hf_snapshot_download

        with _suppress_huggingface_progress():
            hf_snapshot_download(repo_id=repo_id, local_dir=target_dir)
    except BaseException as exc:
        errors.put(f"{type(exc).__name__}: {exc}")


def download_via_huggingface(
    asset: AssetSpec,
    target_dir: Path,
    cancel_event: threading.Event | None = None,
) -> None:
    raise_if_download_cancelled(cancel_event)
    repo_id = _download_site_repo_id(asset, DownloadSite.HUGGINGFACE)
    if not repo_id:
        raise RuntimeError(f"Missing Hugging Face repo id for {asset.label}.")

    if cancel_event is None:
        from huggingface_hub import snapshot_download as hf_snapshot_download

        try:
            with _suppress_huggingface_progress():
                hf_snapshot_download(repo_id=repo_id, local_dir=str(target_dir))
        except Exception as exc:
            raise RuntimeError(f"Failed to download {asset.label} via HuggingFace: {exc}") from exc
        return

    context = multiprocessing.get_context("spawn")
    errors = context.Queue()
    process = context.Process(target=_download_huggingface_worker, args=(repo_id, str(target_dir), errors))
    try:
        process.start()
        while process.is_alive():
            if cancel_event.wait(0.1):
                process.terminate()
                process.join(timeout=5)
                raise DownloadCancelledError("Download canceled.")
        process.join()
        try:
            error = errors.get(timeout=1.0)
        except Empty:
            error = ""
        if error or process.exitcode:
            error = error or f"exit code {process.exitcode}"
            raise RuntimeError(f"Failed to download {asset.label} via HuggingFace: {error}")
        raise_if_download_cancelled(cancel_event)
    finally:
        if process.is_alive():
            process.terminate()
            process.join(timeout=5)
        errors.close()
        errors.join_thread()


def ensure_asset_present(
    asset: AssetSpec,
    target_dir: Path,
    logger: LoggerLike,
    recovery_flag_dir: Path,
    download_counter: list[int],
    *,
    force_site: DownloadSite | None = None,
    allow_download: bool = True,
    cancel_event: threading.Event | None = None,
) -> None:
    raise_if_download_cancelled(cancel_event)
    if asset_is_ready(asset, target_dir):
        logger.log(f"[OK] {asset.label} already present: {target_dir}")
        return

    if not allow_download:
        raise RuntimeError(f"Missing required {asset.label}: {target_dir}")

    if should_inject_once(recovery_flag_dir, "download_net_bad", "KIMODO_TEST_INJECT_DOWNLOAD_NET_BAD_ONCE"):
        raise RuntimeError("Injected download network failure once.")

    logger.log(f"[STEP] Downloading {asset.label}: {asset.local_dir_name}")
    resuming_incomplete_download = target_dir.exists()
    if resuming_incomplete_download:
        logger.log(
            f"[INFO] {asset.label}: incomplete local download detected; checking download sites before resuming."
        )
    target_dir.mkdir(parents=True, exist_ok=True)
    candidate_sites: tuple[DownloadSite, ...]

    if force_site is not None:
        if not _download_site_repo_id(asset, force_site):
            raise RuntimeError(
                f"Forced download site '{force_site.value}' is unavailable for {asset.label}: missing repo id."
            )
        logger.log(
            f"[INFO] {asset.label}: forced download site={force_site.value} "
            f"repo={_download_site_repo_id(asset, force_site)}"
        )
        candidate_sites = (force_site,)
    else:
        logger.log(
            f"[INFO] {asset.label}: probing download sites before transfer "
            f"(timeout={DOWNLOAD_PROBE_TIMEOUT_SECONDS:.1f}s)"
        )
        huggingface_result = probe_download_site(asset, DownloadSite.HUGGINGFACE, DOWNLOAD_PROBE_TIMEOUT_SECONDS)
        raise_if_download_cancelled(cancel_event)
        modelscope_result = probe_download_site(asset, DownloadSite.MODELSCOPE, DOWNLOAD_PROBE_TIMEOUT_SECONDS)
        raise_if_download_cancelled(cancel_event)
        logger.log(f"[PROBE] {asset.label}: {_probe_summary(huggingface_result)}")
        logger.log(f"[PROBE] {asset.label}: {_probe_summary(modelscope_result)}")
        probe_results = (huggingface_result, modelscope_result)
        candidate_sites = tuple(
            result.site
            for result in sorted(probe_results, key=lambda result: (not result.ok, result.elapsed_ms))
            if result.repo_id
        )
        if not candidate_sites:
            raise RuntimeError(f"No download site is configured for {asset.label}.")
        if not any(result.ok for result in probe_results):
            logger.log(
                f"[WARN] {asset.label}: no site passed the short network probe; "
                "trying both sources because a resumable transfer may still succeed."
            )
        logger.log(
            f"[INFO] {asset.label}: download candidates="
            + " -> ".join(f"{site.value} ({_download_site_repo_id(asset, site)})" for site in candidate_sites)
        )

    download_errors: list[str] = []
    selected_site: DownloadSite | None = None
    selected_repo_id = ""
    for index, site in enumerate(candidate_sites):
        repo_id = _download_site_repo_id(asset, site)
        logger.log(f"[INFO] {asset.label}: attempting download site={site.value} repo={repo_id}")
        try:
            if site == DownloadSite.HUGGINGFACE:
                download_via_huggingface(asset, target_dir, cancel_event)
            else:
                download_via_modelscope(asset, target_dir, logger, cancel_event)
            raise_if_download_cancelled(cancel_event)
            if not asset_is_ready(asset, target_dir):
                raise RuntimeError(f"Downloaded asset is incomplete: {target_dir}")
            selected_site = site
            selected_repo_id = repo_id
            break
        except DownloadCancelledError:
            raise
        except Exception as exc:
            download_errors.append(f"{site.value}={type(exc).__name__}: {exc}")
            if index + 1 < len(candidate_sites):
                logger.log(
                    f"[WARN] {asset.label}: download via {site.value} failed; "
                    f"switching to {candidate_sites[index + 1].value}."
                )

    if selected_site is None:
        raise RuntimeError(
            f"Failed to download {asset.label} via all candidate sites: " + "; ".join(download_errors)
        )

    logger.log(f"[OK] {asset.label} ready via {selected_site.value}: {selected_repo_id}")
    download_counter[0] += 1
    if should_inject_once(recovery_flag_dir, "download_abort", "KIMODO_TEST_INJECT_DOWNLOAD_ABORT_ONCE"):
        raise RuntimeError("Injected download interrupt once.")


def build_runtime_env(
    root_dir: str | os.PathLike[str],
    source_root: str | os.PathLike[str],
    models_root: str | os.PathLike[str],
    text_encoder_mode: str,
    encoder_device: str,
    encoder_route: str | None = None,
    encoder_layout_id: str | None = None,
) -> dict[str, str]:
    assert_no_legacy_gguf_env()
    assert_no_removed_quickserver_env()
    root = Path(root_dir).resolve()
    models_path = Path(models_root).resolve()
    selected_encoder_route = str(encoder_route or "").strip()
    if not selected_encoder_route:
        raise ValueError("encoder_route is required.")
    selected_layout = (
        resolve_text_encoder_layout(encoder_layout_id)
        if encoder_layout_id
        else select_text_encoder_layout_for_route(selected_encoder_route, models_path, encoder_device)
    )
    if selected_layout.route != selected_encoder_route:
        raise ValueError(
            "Encoder route/layout mismatch: "
            f"route={selected_encoder_route} layout={selected_layout.layout_id}"
        )
    primary_dir, peft_dir = resolve_text_encoder_layout_paths(selected_layout, models_path)

    env: dict[str, str] = {
        "PYTHONPATH": os.pathsep.join(
            str(path)
            for path in (
                root,
                Path(source_root).resolve(),
                root / "ardy",
            )
            if path.is_dir()
        ),
        "KIMODO_ROOT_PATH": str(root),
        "KIMODO_MODELS_ROOT": str(models_path),
        "KIMODO_TEXT_ENCODER_MODE": normalize_text_encoder_mode(text_encoder_mode),
        "KIMODO_TEXT_ENCODER_ROUTE": selected_encoder_route,
        "LOCAL_CACHE": "true",
        "TEXT_ENCODER": "llm2vec_int8" if selected_layout.layout_id == INT8_LAYOUT.layout_id else "llm2vec",
        "TEXT_ENCODER_MODE": "local",
    }
    env["TEXT_ENCODER_DEVICE"] = str(encoder_device or "cpu").strip().lower() or "cpu"
    env["KIMODO_LLM2VEC_DIR"] = str(primary_dir)
    if peft_dir is not None:
        env["TEXT_ENCODERS_DIR"] = str(models_path)
        env["KIMODO_LLM2VEC_PEFT_DIR"] = str(peft_dir)
    else:
        env["TEXT_ENCODERS_DIR"] = ""
        env["KIMODO_LLM2VEC_PEFT_DIR"] = ""
    return env


def build_runtime_cache_env(root_dir: str | os.PathLike[str]) -> dict[str, str]:
    root = Path(root_dir).resolve()
    hf_home = root / "hf_cache"
    return {
        "HF_HOME": str(hf_home),
        "TRANSFORMERS_CACHE": str(hf_home / "transformers"),
        "HF_HUB_CACHE": str(hf_home / "hub"),
        "HUGGINGFACE_HUB_CACHE": str(hf_home / "hub"),
        "PYTHONUNBUFFERED": "1",
    }
