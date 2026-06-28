# Test Guide

## Directory Rules

- `test\`: top-level menus, runners, stress scripts, diagnostics, and matrix entrypoints.
- `test\cases\`: concrete reusable scenario scripts.
- `archive\deprecated_tests\`: retired top-level test entrypoints and compatibility wrappers.
- `archive\deprecated_cases\`: retired case scripts.

## Recommended Entrypoints

- `cases\case_cpu_prepared_models.bat`: reuse ready assets under `C:\nvlab\models~`; best daily CPU smoke test.
- `cases\case_cpu_from_scratch.bat`: run in an isolated copied workspace without shared `models-root`; best cold-start verification.
- `test_stress_10_generates_menu.bat`: repeated CPU/CUDA generate stability and performance check.
- `test_recovery_matrix_serial.bat`: serial recovery matrix covering download, setup, crash, manual-stop, and model-variant scenarios.

## Supporting Diagnostics

- `test_run_server_multi_start.bat`: repeated-start and idempotency behavior.
- `test_run_server_watchdog_params.bat`: watchdog parameter propagation and cleanup behavior.
- `test_setup_network_probe.bat`: uv/pip setup and download latency probe for environment debugging.

## Important Cases

- `cases\case_runner.bat`: shared scenario runner used by many cases.
- `cases\case_cpu_prepared_models.bat`: recommended prepared-model CPU entry case.
- `cases\case_cpu_from_scratch.bat`: recommended cold-start CPU entry case.
- `cases\case_cpu_setup_and_run.bat`: prepared-model CPU end-to-end path.
- `cases\case_cpu_cold_setup_and_run.bat`: isolated cold-start CPU end-to-end path.
- `cases\case_cpu_crash_once.bat`: injected crash then recovery.
- `cases\case_cpu_network_bad_once.bat`: injected download/network failure then recovery.
- `cases\case_cpu_manual_stop_once.bat`: manual stop then recovery.
- `cases\case_setup_interrupt_once.bat`: generic setup interruption recovery.
- `cases\case_setup_network_bad_once.bat`: generic setup network failure recovery.
- `cases\case_download_interrupt_once.bat`: download interruption recovery.
- `cases\case_download_network_bad_once.bat`: download network failure recovery.
- `cases\case_download_then_model_missing_once.bat`: downloaded asset disappears once, then recovery.
- `cases\case_highvram_soma.bat`: high-VRAM route regression.
- `cases\case_model_variant_g1_rp.bat`: G1 model variant coverage.
- `cases\case_model_variant_smplx_rp.bat`: SMPLX model variant coverage.
- `cases\case_model_variant_soma_seed.bat`: SOMA SEED model variant coverage.
- `cases\case_local_tools_uv_git.bat`: bundled `uv`/`git`/`git-lfs` availability check.
- `cases\case_uv_no_cache.bat`: no-cache `uv` environment/setup diagnostic.

## Archived

- `archive\deprecated_tests\test_cpu_local_llama_route.bat`: retired GGUF/llama CPU route.
- `archive\deprecated_tests\test_cpu_local_llama_route.ps1`: retired GGUF/llama CPU route.
- `archive\deprecated_tests\test_cpu_prepared_models.bat`: retired top-level wrapper; use `cases\case_cpu_prepared_models.bat`.
- `archive\deprecated_tests\test_cpu_from_scratch.bat`: retired top-level wrapper; use `cases\case_cpu_from_scratch.bat`.
- `archive\deprecated_tests\test_recovery_matrix_parallel.bat`: retired wrapper; use `test_recovery_matrix_serial.bat`.
- `archive\deprecated_cases\case_setup_not_started.bat`: older cold-start variant, superseded by `cases\case_cpu_from_scratch.bat`.
