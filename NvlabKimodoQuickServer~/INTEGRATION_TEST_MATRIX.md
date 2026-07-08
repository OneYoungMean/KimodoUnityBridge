# Kimodo QuickServer Integration Test Matrix

## Resource Policy Legend

| Value | Meaning |
| --- | --- |
| `env_policy=reuse-existing-env` | Reuse `NvlabKimodoQuickServer~/Env` or `Env~`; env bootstrap is not part of the test target. |
| `env_policy=isolated-env-setup` | Build/use a per-test local env inside the isolated workspace copy; setup behavior is part of the test target. |
| `model_policy=reuse-existing-model-cache` | Reuse `NvlabKimodoQuickServer~/models` only when model presence should not affect the semantic result. |
| `model_policy=isolated-models` | Use the isolated workspace copy's local `models`; download/missing-model behavior is part of the test target. |
| `model_policy=probe-output-only` | Download probe writes into the test run's own probe directory instead of any reusable model root. |
| `uv_policy=reuse-available-uv` | Allow bundled/local/PATH uv resolution. |
| `uv_policy=force-download-uv` | Skip local/PATH uv and force the download path. |
| `uv_policy=probe-only` | Only probe/download health, not normal startup reuse semantics. |

All test runs are created under `NvlabKimodoQuickServer~/test_runs/<timestamp>_<case>`.

## Full Case Table

| ID | Name | Resources | Notes / Success Focus |
| --- | --- | --- | --- |
| `T01` | Basic T-Pose Generate | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Basic `launch -> generate -> quit` succeeds. |
| `T02` | Double Start Same Params | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Two launchers with same params do not break bootstrap/serve flow. |
| `T03` | Double Start Different Params | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Concurrent launcher startup with different runtime request still converges cleanly. |
| `T04` | Queue Order | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Two queued generate requests both complete. |
| `T05` | Stop Idle | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Explicit quit from idle state works and recovery generate still works. |
| `T06` | Stop Generating | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Quit during active generate shuts down and recovery generate still works. |
| `T07` | Cancel NonCurrent Boot | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Design placeholder, currently skipped. |
| `T08` | Cancel NonCurrent CLI | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel queued task without killing active task flow. |
| `T09` | Cancel Current Boot | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel launcher in boot window and recover. |
| `T10` | Cancel Current SettingUpEnv Immediate | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel setup immediately and recover. |
| `T11` | Cancel Current SettingUpEnv 1s | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel setup after 1s and recover. |
| `T12` | Cancel Current SettingUpEnv 61s | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Long setup cancel window. |
| `T13` | Cancel Current SettingUpEnv 301s | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Longer setup cancel window. |
| `T14` | Cancel Current SettingUpEnv 601s | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Longest setup cancel window. |
| `T15` | Cancel Current Download Immediate | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Cancel download immediately and recover. |
| `T16` | Cancel Current Download 1s | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Cancel download after 1s and recover. |
| `T17` | Cancel Current Download 61s | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Long download cancel window. |
| `T18` | Cancel Current Download 301s | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Longer download cancel window. |
| `T19` | Cancel Current Download 601s | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Longest download cancel window. |
| `T20` | Cancel Current LoadingRuntime | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel during runtime load and recover. |
| `T21` | Cancel Current Generating | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Cancel active generate by task id. |
| `T22` | Cancel Empty Task Id | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Invalid cancel request returns non-fatal error path. |
| `T23` | Cancel Unknown Task Id | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Unknown task cancel path remains healthy. |
| `T24` | Cancel Finished Task Id | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Finished task cancel path remains healthy. |
| `T25` | Kill Owner Boot | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Owner death during boot releases server and recovery works. |
| `T26` | Kill Owner SettingUpEnv | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Owner death during setup releases server and recovery works. |
| `T27` | Kill Owner Download | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Owner death during download releases server and recovery works. |
| `T28` | Kill Owner LoadingRuntime | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Owner death during runtime load releases server and recovery works. |
| `T29` | Kill Owner Generating | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Owner death during generate releases server and recovery works. |
| `T30` | Owner Kill Recovery | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Recovery after owner-kill-on-generate remains healthy. |
| `T31` | Kill CLI Boot | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Killing CLI in boot stage releases server and recovery works. |
| `T32` | Kill CLI SettingUpEnv | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Killing CLI in setup stage releases server and recovery works. |
| `T33` | Kill CLI Download | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Killing CLI in download stage releases server and recovery works. |
| `T34` | Kill CLI LoadingRuntime | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Killing CLI in runtime load releases server and recovery works. |
| `T35` | Kill CLI Generating | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Killing CLI in generate stage releases server and recovery works. |
| `T36` | No Cached Models | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Startup succeeds without reusable model cache. |
| `T37` | No Cached UV | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Startup works without pre-pointing to a uv binary. |
| `T38` | High VRAM | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | High-VRAM runtime path succeeds. |
| `T39` | Simulate VRAM 1G | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Simulated 1G VRAM path succeeds. |
| `T40` | Simulate VRAM 4G | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Simulated 4G VRAM path succeeds. |
| `T41` | Simulate VRAM 6G | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Simulated 6G VRAM path succeeds. |
| `T42` | Force HuggingFace Download | `env_policy=reuse-existing-env, model_policy=isolated-models, uv_policy=reuse-available-uv` | Forced HF download path succeeds. |
| `T43` | No Existing Env | `env_policy=isolated-env-setup, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Explicit local env path succeeds. |
| `T44` | Reuse Existing Env | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Explicit reusable env path succeeds. |
| `T45` | Reuse Existing Models | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Explicit reusable model path succeeds. |
| `T46` | Download Source Health Probe | `env_policy=reuse-existing-env, model_policy=probe-output-only, uv_policy=probe-only` | Manual network probe: uv / torch / model routes can at least transfer bytes without immediate error. |
| `T47` | Force Downloaded UV | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=force-download-uv` | Manual network test for forced uv download path. |
| `T48` | Short Idle Timeout Override | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Small `KIMODO_IDLE_TIMEOUT_SEC` actually shuts server down, then recovery generate succeeds. |
| `T49` | Example Default T-Pose Batch | `env_policy=isolated-env-setup, model_policy=isolated-models, uv_policy=reuse-available-uv` | Runs `example\example_run_server_tpose.bat` with no extra parameters in the isolated workspace copy and requires a full minimal generate to succeed. |
| `T50` | Example Default Startup Batch | `env_policy=isolated-env-setup, model_policy=isolated-models, uv_policy=reuse-available-uv` | Runs `example\example_run_server_startup.bat` with no extra parameters in the isolated workspace copy and requires startup-ready plus quit to succeed. |
| `T51` | Reject Legacy Start Command | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Verifies TCP `start` is no longer accepted after protocol收束. |
| `T52` | Reject Legacy Stop Command | `env_policy=reuse-existing-env, model_policy=reuse-existing-model-cache, uv_policy=reuse-available-uv` | Verifies TCP `stop` is no longer accepted after protocol收束. |
| `T53` | Basic T-Pose Generate Cold Start | `env_policy=isolated-env-setup, model_policy=isolated-models, uv_policy=reuse-available-uv` | Direct non-example cold-start minimal generate, used as the strict counterpart to `T01`. |
| `T54` | Cancel Current Generating Cold Start | `env_policy=isolated-env-setup, model_policy=isolated-models, uv_policy=reuse-available-uv` | Cold-start generate enters active run, then cancel must succeed and recovery must remain healthy. |
| `T55` | Short Idle Timeout Override Cold Start | `env_policy=isolated-env-setup, model_policy=isolated-models, uv_policy=reuse-available-uv` | Cold-start generate followed by short idle timeout shutdown, then recovery generate must still succeed. |

## Default Full Sweep

`--full` currently excludes any case tagged `hf`, `probe`, or `manual`.
