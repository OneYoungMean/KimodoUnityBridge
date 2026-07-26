# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2026-04-24]

### Added
- Support for `TEXT_ENCODER_DEVICE` environment variable to force LLM2Vec to use the CPU instead of GPU. Setting `TEXT_ENCODER_DEVICE=cpu` reduces VRAM usage to <3 GB with a fairly small speed hit.
- `--save_example_dir` argument to `kimodo_gen` to save outputs to an example directory that can be directly loaded into `kimodo_demo`

### Fixed
- Bug in post-processing that was incorrectly making the smoothed root the target for the root in full-body constraints
- Modified how transitions are handled in multi-prompt generation to improve smoothness

### Removed
- `share_transition` and `percentage_transition_override` options from python API for multi-prompt generation

## [2026-04-13]

### Added
- Option `--bvh_standard_tpose` to use standard T-pose for BVH file saved from `generate.py`
- Option to use standard T-pose for BVH file saved or downloaded from demo
- Option to input/output BVH files that use standard T-pose with `motion_convert.py`
- Added BVH file containing the standard Kimodo T-pose to `kimodo/assets/skeletons/somaskel77/somaskel77_standard_tpose.bvh`
- Updated documentation with these new options

## [2026-04-10]

### Added
- [Kimodo-SOMA-RP-v1.1](https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.1) and [Kimodo-SOMA-SEED-v1.1](https://huggingface.co/nvidia/Kimodo-SOMA-SEED-v1.1) models and added support in the codebase. If not specified, the latest version of the models will be used automatically with the demo and CLI.
- [Kimodo Motion Generation Benchmark](https://huggingface.co/datasets/nvidia/Kimodo-Motion-Gen-Benchmark) for standardized evaluation of motion generation models training on the BONES-SEED dataset.
- Scripts to construct the full benchmark, generate motions for test cases, and compute evaluation metrics. 
- Documentation explaining the benchmark and how to use the evaluation pipeline.
- [TMR-SOMA-RP-v1](https://huggingface.co/nvidia/TMR-SOMA-RP-v1) motion-text embedding model to be used for evaluation metrics.
- Added option to load LLM2Vec text encoder in fp32 precision.

### Fixed
- Always use batch size 1 with LLM2Vec to avoid unexpected behavior of different embeddings based on batch size.
- Load LLM2Vec directly onto the GPU, if available.
- Updated documentation on constraints with more details.

## [2026-04-01]

### Fixed
- Fix unnecessary text encoder reload when switching between models in the interactive demo (if not using the text encoder server API).

## [2026-03-31]

### Added
- New `kimodo_convert` CLI tool for converting generated motions between formats (NPZ, BVH, MuJoCo CSV, AMASS NPZ).
- Support for loading and saving BVH, CSV, and NPZ motion files in the interactive demo.

## [2026-03-27]

### Fixed
- Bug fix for foot contact visualization in the interactive demo.
- Patch bug with BVH export for SOMA models.

## [2026-03-19]

### Changed
- **Breaking:** Model inputs/outputs now use the SOMA 77-joint skeleton (`somaskel77`). This affects saved motion formats and constraint files from previous versions.

### Added
- Released timeline annotations for the BONES-SEED dataset on HuggingFace.

## [2026-03-16] - Initial Release

### Added
- Open-source release of Kimodo codebase under Apache-2.0 license.
- Five model variants: Kimodo-SOMA-RP-v1, Kimodo-G1-RP-v1, Kimodo-SOMA-SEED-v1, Kimodo-G1-SEED-v1, Kimodo-SMPLX-RP-v1.
- Command-line interface (`kimodo_gen`) for motion generation with text prompts and kinematic constraints.
- Interactive web-based motion authoring demo (`kimodo_demo`) with timeline editor, constraint tracks, and 3D visualization.
- Support for multiple output formats: default NPZ, MuJoCo qpos CSV (G1), AMASS NPZ (SMPL-X).
- Documentation site with quick start guide, installation instructions, CLI reference, and API docs.
- Compatibility with downstream tools: ProtoMotions (physics-based policy training) and GMR (motion retargeting).
