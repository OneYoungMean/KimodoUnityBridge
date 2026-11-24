from __future__ import annotations

"""Single network entry point for FlowHMR model assets."""

import os
from pathlib import Path
from typing import Iterable


FLOWHMR_REPOSITORY = "https://github.com/flowhmr/flowhmr.git"
FLOWHMR_HF_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_HF_REPOSITORY", "fafsaf1/flowhmr-0.46B")
# These ModelScope repositories were verified for the files used by QuickServer.
FLOWHMR_MODELSCOPE_REPOSITORY = os.environ.get(
    "KIMODO_FLOWHMR_MODELSCOPE_REPOSITORY", "oneyoungmean/flowhmr-0.46B"
).strip()
YOLOX_HF_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_YOLOX_HF_REPOSITORY", "kadirnar/yolox_l-v0.1.1").strip()
YOLOX_MODELSCOPE_REPOSITORY = os.environ.get(
    "KIMODO_FLOWHMR_YOLOX_MODELSCOPE_REPOSITORY", "oneyoungmean/yolox_l-v0.1.1"
).strip()
VGGT_MODELSCOPE_REPOSITORY = os.environ.get("KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY", "facebook/VGGT-Omega").strip()
SAM3D_MODELSCOPE_REPOSITORY = os.environ.get(
    "KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY", "facebook/sam-3d-body-dinov3"
).strip()
SMPLH_MODEL = "assets/body_models/smplh/neutral/model.npz"
SMPLH_SKELETON_MODEL = "assets/body_models/smplh/neutral/model_skeleton.npz"
FLOWHMR_CHECKPOINT_REPOSITORY_DIR = "checkpoints/flowhmr-0.46B/flowhmr_latest"

FLOWHMR_BUNDLED_FILES = ("assets/body_models/smpl_neutral_J_regressor.pt",)
FLOWHMR_GATED_FILES = (
    "ckpts/vggt-omega/vggt_omega_1b_512.pt",
    "ckpts/sam-3d-body-dinov3/model.ckpt",
    "ckpts/sam-3d-body-dinov3/model_config.yaml",
    "ckpts/sam-3d-body-dinov3/assets/mhr_model.pt",
    SMPLH_MODEL,
)
FLOWHMR_SKELETON_GATED_FILES = (
    "ckpts/vggt-omega/vggt_omega_1b_512.pt",
    "ckpts/sam-3d-body-dinov3/model.ckpt",
    "ckpts/sam-3d-body-dinov3/model_config.yaml",
    "ckpts/sam-3d-body-dinov3/assets/mhr_model.pt",
    SMPLH_SKELETON_MODEL,
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


def _flatten_downloaded_files(target_dir: Path, expected_files: Iterable[str]) -> None:
    """Flatten SDK snapshots whose repository layout adds an extra directory."""
    expected_files = tuple(expected_files)
    for filename in expected_files:
        target = target_dir / filename
        if target.is_file():
            continue
        matches = sorted(
            path for path in target_dir.rglob(filename)
            if path.is_file() and path != target
        )
        if not matches:
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        import shutil
        shutil.move(str(matches[0]), str(target))
    missing = [name for name in expected_files if not (target_dir / name).is_file()]
    if missing:
        raise RuntimeError("Downloaded snapshot is missing expected files: " + ", ".join(missing))


def _download_snapshot(label: str, target_dir: Path, modelscope_repo: str, huggingface_repo: str,
                       *, allow_patterns: Iterable[str] | None = None,
                       expected_files: Iterable[str] = ()) -> None:
    """Try ModelScope first, then HF (including when HF normally requires access)."""
    errors: list[str] = []
    allow_patterns = tuple(allow_patterns or ())
    expected_files = tuple(expected_files)
    target_dir.mkdir(parents=True, exist_ok=True)
    if modelscope_repo:
        try:
            from modelscope import snapshot_download

            kwargs = {"model_id": modelscope_repo, "local_dir": str(target_dir)}
            if allow_patterns:
                kwargs["allow_file_pattern"] = list(allow_patterns)
            snapshot_download(**kwargs)
            _flatten_downloaded_files(target_dir, expected_files)
            return
        except Exception as exc:
            errors.append(f"ModelScope {modelscope_repo}: {type(exc).__name__}: {exc}")
    else:
        errors.append("ModelScope repository is not configured")
    if huggingface_repo:
        try:
            from huggingface_hub import snapshot_download

            kwargs = {"repo_id": huggingface_repo, "local_dir": str(target_dir)}
            if allow_patterns:
                kwargs["allow_patterns"] = list(allow_patterns)
            snapshot_download(**kwargs)
            _flatten_downloaded_files(target_dir, expected_files)
            return
        except Exception as exc:
            errors.append(f"Hugging Face {huggingface_repo}: {type(exc).__name__}: {exc}")
    else:
        errors.append("Hugging Face repository is not configured")
    raise RuntimeError(f"Unable to download {label}: " + "; ".join(errors))


def _missing(root: Path, paths: Iterable[str]) -> list[str]:
    return [relative for relative in paths if not (root / relative).is_file()]


def _ensure_smplh_skeleton_asset(root: Path, source_root: Path | None = None) -> None:
    """Derive the joint-only SMPL-H data used by motion inference."""
    target = root / SMPLH_SKELETON_MODEL
    if target.is_file():
        return
    source = root / SMPLH_MODEL
    if not source.is_file() and source_root is not None:
        source = source_root / SMPLH_MODEL
    if not source.is_file():
        return

    import numpy as np

    with np.load(source, allow_pickle=False) as model:
        required = ("J_regressor", "shapedirs", "v_template", "kintree_table")
        missing = [name for name in required if name not in model]
        if missing:
            raise RuntimeError(f"SMPL-H source model is missing fields: {', '.join(missing)}")
        regressor = np.asarray(model["J_regressor"], dtype=np.float32)
        shapedirs = np.asarray(model["shapedirs"], dtype=np.float32)
        template = np.asarray(model["v_template"], dtype=np.float32)
        kintree = np.asarray(model["kintree_table"])
        if (regressor.ndim != 2 or template.ndim != 2 or template.shape[1] != 3 or
                shapedirs.ndim != 3 or shapedirs.shape[:2] != template.shape or
                regressor.shape[1] != template.shape[0] or kintree.ndim != 2):
            raise RuntimeError("SMPL-H source model has incompatible skeleton fields.")

        compact = {
            "J_template": regressor @ template,
            "J_shapedirs": np.einsum("jv,vdb->jdb", regressor, shapedirs),
            "kintree_table": kintree,
            "compact_format": np.array(1, dtype=np.uint8),
        }
        for name in ("hands_meanl", "hands_meanr"):
            if name in model:
                compact[name] = np.asarray(model[name], dtype=np.float32)

    target.parent.mkdir(parents=True, exist_ok=True)
    if not target.exists():
        np.savez_compressed(target, **compact)


def _licensed_assets_error(root: Path, missing: list[str]) -> RuntimeError:
    model_note = ""
    if SMPLH_SKELETON_MODEL in missing:
        model_note = (
            f" The joint-only file is generated locally from the licensed full SMPL-H neutral model; "
            f"place that source at {root / SMPLH_MODEL} and run setup again."
        )
    return RuntimeError(
        "FlowHMR needs licensed/local assets that KimodoBridge will not redistribute or download without "
        "access: " + ", ".join(missing) + ". Request access to VGGT-Omega and SAM 3D Body at "
        "https://huggingface.co/facebook/VGGT-Omega and "
        "https://huggingface.co/facebook/sam-3d-body-dinov3; obtain the SMPL-H neutral model from "
        "https://mano.is.tue.mpg.de/. Place each missing model at its required path under "
        f"{root}.{model_note}"
    )


def ensure_flowhmr_assets(root: Path, checkpoint: str | None = None,
                          skeleton_only: bool = False, model_root: Path | None = None) -> Path:
    """Prepare public snapshots and validate local/bundled licensed assets."""
    root = root.resolve()
    model_root = (model_root or root).resolve()
    ensure_flowhmr_source(root)
    for relative in FLOWHMR_BUNDLED_FILES + (SMPLH_SKELETON_MODEL,):
        source = root / relative
        target = model_root / relative
        if not target.is_file() and source.is_file():
            target.parent.mkdir(parents=True, exist_ok=True)
            import shutil
            shutil.copy2(source, target)
    if skeleton_only:
        _ensure_smplh_skeleton_asset(model_root, source_root=root)
    checkpoint_value = checkpoint or os.environ.get("KIMODO_FLOWHMR_CHECKPOINT", "").strip()
    ckpt = Path(checkpoint_value).expanduser() if checkpoint_value else Path("checkpoints/flowhmr_latest/flowhmr_latest.ckpt")
    if not ckpt.is_absolute():
        ckpt = model_root / ckpt
    ckpt = ckpt.resolve()
    config = ckpt.with_name("config.yml")

    if not ckpt.is_file() or not config.is_file():
        _download_snapshot(
            "FlowHMR checkpoint", model_root / "checkpoints" / "flowhmr_latest",
            FLOWHMR_MODELSCOPE_REPOSITORY, FLOWHMR_HF_REPOSITORY,
            allow_patterns=(
                f"{FLOWHMR_CHECKPOINT_REPOSITORY_DIR}/flowhmr_latest.ckpt",
                f"{FLOWHMR_CHECKPOINT_REPOSITORY_DIR}/config.yml",
                "flowhmr_latest.ckpt", "config.yml",
            ),
            expected_files=("flowhmr_latest.ckpt", "config.yml"),
        )
    if not ckpt.is_file() or not config.is_file():
        raise RuntimeError(
            f"FlowHMR checkpoint/config is incomplete: {ckpt} and {config}. "
            f"Set KIMODO_FLOWHMR_CHECKPOINT or download {FLOWHMR_HF_REPOSITORY} into "
            "models/FlowHMR/checkpoints."
        )

    yolox = model_root / "ckpts/yolox/yolox_l.pth"
    if not yolox.is_file():
        _download_snapshot(
            "YOLOX checkpoint", model_root / "ckpts/yolox",
            YOLOX_MODELSCOPE_REPOSITORY, YOLOX_HF_REPOSITORY,
            allow_patterns=("yolox_l.pth",), expected_files=("yolox_l.pth",),
        )
    if not yolox.is_file():
        raise RuntimeError(
            f"YOLOX checkpoint is missing: {yolox}. Configure a verified mirror using "
            "KIMODO_FLOWHMR_YOLOX_MODELSCOPE_REPOSITORY or KIMODO_FLOWHMR_YOLOX_HF_REPOSITORY "
            "and ensure it contains yolox_l.pth."
        )

    bundled = _missing(model_root, () if skeleton_only else FLOWHMR_BUNDLED_FILES)
    if bundled:
        raise RuntimeError("FlowHMR package is incomplete; bundled files are missing: " + ", ".join(bundled))
    # For gated HF assets, only try an explicitly configured ModelScope mirror.
    # Never use a user's HF token implicitly; failure becomes a manual-download
    # instruction below.
    attempted_mirrors: set[tuple[str, str]] = set()
    for relative, destination, modelscope_env, _hf_repo in GATED_REPOSITORIES:
        target = model_root / relative
        if target.is_file():
            continue
        if modelscope_env == "KIMODO_FLOWHMR_VGGT_MODELSCOPE_REPOSITORY":
            mirror_repo = os.environ.get(modelscope_env, VGGT_MODELSCOPE_REPOSITORY).strip()
        elif modelscope_env == "KIMODO_FLOWHMR_SAM3D_MODELSCOPE_REPOSITORY":
            mirror_repo = os.environ.get(modelscope_env, SAM3D_MODELSCOPE_REPOSITORY).strip()
        else:
            mirror_repo = os.environ.get(modelscope_env, "").strip()
        mirror_key = (mirror_repo, destination)
        if mirror_key in attempted_mirrors:
            continue
        attempted_mirrors.add(mirror_key)
        try:
            _download_snapshot(
                relative, model_root / destination, mirror_repo, "",
                allow_patterns=tuple(
                    Path(asset).relative_to(Path(destination)).as_posix()
                    for asset, asset_destination, *_ in GATED_REPOSITORIES
                    if asset_destination == destination
                ),
                expected_files=tuple(
                    Path(asset).relative_to(Path(destination)).as_posix()
                    for asset, asset_destination, *_ in GATED_REPOSITORIES
                    if asset_destination == destination
                ),
            )
        except RuntimeError:
            # Keep checking the complete gated set so the final exception lists
            # every missing file and the exact manual preparation locations.
            pass

    gated_paths = FLOWHMR_SKELETON_GATED_FILES if skeleton_only else FLOWHMR_GATED_FILES
    gated = _missing(model_root, gated_paths)
    if gated:
        raise _licensed_assets_error(model_root, gated)
    return ckpt
