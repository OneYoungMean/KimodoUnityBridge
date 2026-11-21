from __future__ import annotations

"""Single network entry point for FlowHMR model assets."""

import os
from pathlib import Path
from typing import Iterable


FLOWHMR_REPOSITORY = "https://github.com/flowhmr/flowhmr.git"
FLOWHMR_HF_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_HF_REPOSITORY", "fafsaf1/flowhmr-0.46B")
# Configure only verified mirrors; do not guess ModelScope namespaces.
FLOWHMR_MODELSCOPE_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_MODELSCOPE_REPOSITORY", "").strip()
YOLOX_HF_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_YOLOX_HF_REPOSITORY", "cj-mills/yolox-coco-baseline-pytorch").strip()
YOLOX_MODELSCOPE_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_YOLOX_MODELSCOPE_REPOSITORY", "").strip()
VGGT_MODELSCOPE_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY", "facebook/VGGT-Omega").strip()

FLOWHMR_BUNDLED_FILES = ("assets/body_models/smpl_neutral_J_regressor.pt",)
FLOWHMR_GATED_FILES = (
    "ckpts/vggt-omega/vggt_omega_1b_512.pt",
    "ckpts/sam-3d-body-dinov3/model.ckpt",
    "ckpts/sam-3d-body-dinov3/model_config.yaml",
    "ckpts/sam-3d-body-dinov3/assets/mhr_model.pt",
    "assets/body_models/smplh/neutral/model.npz",
)
GATED_REPOSITORIES = (
    ("ckpts/vggt-omega/vggt_omega_1b_512.pt", "ckpts/vggt-omega", "KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY", "facebook/VGGT-Omega"),
    ("ckpts/sam-3d-body-dinov3/model.ckpt", "ckpts/sam-3d-body-dinov3", "KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY", "facebook/sam-3d-body-dinov3"),
    ("ckpts/sam-3d-body-dinov3/model_config.yaml", "ckpts/sam-3d-body-dinov3", "KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY", "facebook/sam-3d-body-dinov3"),
    ("ckpts/sam-3d-body-dinov3/assets/mhr_model.pt", "ckpts/sam-3d-body-dinov3", "KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY", "facebook/sam-3d-body-dinov3"),
)


def ensure_flowhmr_source(root: Path) -> None:
    root = root.resolve()
    if (root / "run_v2m_demo_generation.py").is_file():
        return
    raise RuntimeError(
        f"FlowHMR source is missing at {root}. The KimodoBridge package should contain vendor/FlowHMR; "
        "set KIMODO_FLOWHMR_ROOT only when using an external checkout."
    )


def _download_snapshot(label: str, target_dir: Path, modelscope_repo: str, huggingface_repo: str) -> None:
    """Try ModelScope first, then HF (including when HF normally requires access)."""
    errors: list[str] = []
    target_dir.mkdir(parents=True, exist_ok=True)
    if modelscope_repo:
        try:
            from modelscope import snapshot_download

            snapshot_download(model_id=modelscope_repo, local_dir=str(target_dir))
            return
        except Exception as exc:
            errors.append(f"ModelScope {modelscope_repo}: {type(exc).__name__}: {exc}")
    else:
        errors.append("ModelScope repository is not configured")
    if huggingface_repo:
        try:
            from huggingface_hub import snapshot_download

            snapshot_download(repo_id=huggingface_repo, local_dir=str(target_dir))
            return
        except Exception as exc:
            errors.append(f"Hugging Face {huggingface_repo}: {type(exc).__name__}: {exc}")
    else:
        errors.append("Hugging Face repository is not configured")
    raise RuntimeError(f"Unable to download {label}: " + "; ".join(errors))


def _missing(root: Path, paths: Iterable[str]) -> list[str]:
    return [relative for relative in paths if not (root / relative).is_file()]


def _licensed_assets_error(root: Path, missing: list[str]) -> RuntimeError:
    return RuntimeError(
        "FlowHMR needs licensed/local assets that KimodoBridge will not redistribute or download without "
        "access: " + ", ".join(missing) + ". Request access to VGGT-Omega and SAM 3D Body at "
        "https://huggingface.co/facebook/VGGT-Omega and "
        "https://huggingface.co/facebook/sam-3d-body-dinov3; obtain the SMPL-H neutral model from "
        "https://mano.is.tue.mpg.de/. Place each file at the listed path under "
        f"{root}."
    )


def ensure_flowhmr_assets(root: Path, checkpoint: str | None = None) -> Path:
    """Prepare public snapshots and validate local/bundled licensed assets."""
    root = root.resolve()
    ensure_flowhmr_source(root)
    checkpoint_value = checkpoint or os.environ.get("KIMODO_FLOWHMR_CHECKPOINT", "").strip()
    ckpt = Path(checkpoint_value).expanduser() if checkpoint_value else Path("checkpoints/flowhmr_latest/flowhmr_latest.ckpt")
    if not ckpt.is_absolute():
        ckpt = root / ckpt
    ckpt = ckpt.resolve()
    config = ckpt.with_name("config.yml")

    if not ckpt.is_file() or not config.is_file():
        _download_snapshot(
            "FlowHMR checkpoint", root / "checkpoints" / "flowhmr_latest",
            FLOWHMR_MODELSCOPE_REPOSITORY, FLOWHMR_HF_REPOSITORY,
        )
    if not ckpt.is_file() or not config.is_file():
        raise RuntimeError(
            f"FlowHMR checkpoint/config is incomplete: {ckpt} and {config}. "
            f"Set KIMODO_FLOWHMR_CHECKPOINT or download {FLOWHMR_HF_REPOSITORY} into "
            "vendor/FlowHMR/checkpoints."
        )

    yolox = root / "ckpts/yolox/yolox_l.pth"
    if not yolox.is_file():
        _download_snapshot(
            "YOLOX checkpoint", root / "ckpts/yolox",
            YOLOX_MODELSCOPE_REPOSITORY, YOLOX_HF_REPOSITORY,
        )
    if not yolox.is_file():
        raise RuntimeError(
            f"YOLOX checkpoint is missing: {yolox}. Configure a verified mirror using "
            "KIMODO_FLOWHMR_YOLOX_MODELSCOPE_REPOSITORY or KIMODO_FLOWHMR_YOLOX_HF_REPOSITORY "
            "and ensure it contains yolox_l.pth."
        )

    bundled = _missing(root, FLOWHMR_BUNDLED_FILES)
    if bundled:
        raise RuntimeError("FlowHMR package is incomplete; bundled files are missing: " + ", ".join(bundled))
    # For gated HF assets, only try an explicitly configured ModelScope mirror.
    # Never use a user's HF token implicitly; failure becomes a manual-download
    # instruction below.
    attempted_mirrors: set[tuple[str, str]] = set()
    for relative, destination, modelscope_env, _hf_repo in GATED_REPOSITORIES:
        target = root / relative
        if target.is_file():
            continue
        if modelscope_env == "KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY":
            mirror_repo = os.environ.get(modelscope_env, VGGT_MODELSCOPE_REPOSITORY).strip()
        else:
            mirror_repo = os.environ.get(modelscope_env, "").strip()
        mirror_key = (mirror_repo, destination)
        if mirror_key in attempted_mirrors:
            continue
        attempted_mirrors.add(mirror_key)
        try:
            _download_snapshot(
                relative, root / destination, mirror_repo, "",
            )
        except RuntimeError:
            # Keep checking the complete gated set so the final exception lists
            # every missing file and the exact manual preparation locations.
            pass

    gated = _missing(root, FLOWHMR_GATED_FILES)
    if gated:
        raise _licensed_assets_error(root, gated)
    configured_dinov3 = os.environ.get("KIMODO_FLOWHMR_DINOV3_REPO", "").strip()
    torch_home = Path(os.environ.get("TORCH_HOME", Path.home() / ".cache" / "torch"))
    dinov3_root = Path(configured_dinov3).expanduser() if configured_dinov3 else torch_home / "hub" / "facebookresearch_dinov3_main"
    if not dinov3_root.is_dir():
        raise RuntimeError(
            "DINOv3 source is missing. KimodoBridge disabled implicit torch.hub downloads. "
            "Prepare a local DINOv3 source checkout and set KIMODO_FLOWHMR_DINOV3_REPO="
            f"{dinov3_root}."
        )
    return ckpt
