# Kimodo QuickServer Timeout Inventory

This inventory separates timeout-like constraints into three buckets:

- `Active`: part of the current startup / generate / shutdown path.
- `Legacy`: still in source, but not on the main path anymore.
- `Example-only`: only affects old examples or harness utilities.

## Active Timeouts And Polling Windows

| Scope | File | Value | Status | Coverage |
| --- | --- | --- | --- | --- |
| UV download probe | `run_server.bat`, `run_server.sh` | `KIMODO_UV_PROBE_TIMEOUT_SEC`, default `1s` | Active | `T47` manual, `T46` manual |
| UV download transfer | `run_server.bat`, `run_server.sh` | `KIMODO_UV_INSTALL_TIMEOUT_SEC`, default `600s` | Active | `T47` manual |
| Bootstrap lock poll | `run_server.bat`, `run_server.sh` | `1s` sleep loop, no hard timeout | Active | `T02`, `T03` |
| Asset source probe | `quickserver_assets.py` | `DOWNLOAD_PROBE_TIMEOUT_SECONDS = 1.0` | Active | `T46` manual, indirect in `T42` / download-family cases |
| Setup URL probe | `quickserver_setup.py` | `urllib timeout=2s` | Active | No dedicated automated case yet |
| CLI idle shutdown | `quickserver_cli.py` | `KIMODO_IDLE_TIMEOUT_SEC`, default `600s` | Active | `T05`, `T48` |
| 运行时卸载启发式 | `quickserver_cli.py` | `max(30, idle_timeout/2 or 300)` | Active | No dedicated automated case yet |
| Queue wait poll | `quickserver_cli.py` | `0.5s` | Active | `T04`, `T08`, `T21` |
| Task completion wait poll | `quickserver_cli.py` | `0.5s` | Active | `T04`, `T21` |
| Integration startup timeout | `integration_test_suite.py` | `START_TIMEOUT_SEC = 20 min` | Active harness limit | All startup-bearing tests |
| Integration generate timeout | `integration_test_suite.py` | `TEST_TIMEOUT_SEC = 20 min` | Active harness limit | All generate-bearing tests |
| Integration stop wait | `integration_test_suite.py` | shutdown waits `60s`, joins `10s/30s/60s/120s` | Active harness limit | `T05`, `T06`, `T25`-`T35`, `T48` |
| Unity startup timeout | `BridgeRuntimeSettings.cs` | default `600000ms` | Active | No external Python coverage; Unity-side only |
| Unity connect timeout | `BridgeRuntimeSettings.cs`, `BridgeProtocolClient.cs` | default `3000ms` | Active | No dedicated automated case yet |
| Unity IO timeout | `BridgeRuntimeSettings.cs`, `BridgeProtocolClient.cs` | default `600000ms` | Active | No dedicated automated case yet |
| Unity model-loading timeout | `BridgeRuntimeSettings.cs`, `BridgeProtocolClient.cs` | default `3600000ms`, poll `1000ms` | Active | No dedicated automated case yet |
| Unity status connect / status IO | `BridgeRuntimeSettings.cs`, `BridgeRuntimeControl.cs`, `BridgeStartupWaiter.cs` | `1500ms` / `1200ms` | Active | No dedicated automated case yet |
| Unity log pump stop wait | `BridgeLogPump.cs` | `1500ms` | Active | No dedicated automated case yet |
| Unity log pump file wait | `BridgeLogPump.cs`, `BridgeRuntimeSettings.cs` | `20000ms` default; bridge-message override `60000ms` | Active | No dedicated automated case yet |
| Unity log pump polls | `BridgeRuntimeSettings.cs`, `BridgeLogPump.cs` | missing file `120-900ms`, idle `20-260ms` | Active | No dedicated automated case yet |
| Editor generate timeout | `KimodoEditorGeneratePipeline.cs`, `KimodoServerManagerSettingsProvider.cs` | UI-configured seconds, floor `30s`, dynamic extension for missing models | Active | No dedicated automated case yet |
| Llama API HTTP timeout | `text_encoder_llama_api.py` | default `120s` | Active on llama API path | No dedicated automated case yet |

## Legacy / Candidate For Removal

| Scope | File | Value | Why It Is Legacy |
| --- | --- | --- | --- |
| Standalone bridge idle timeout | `bridge_server.py` | default `600s` | Current public TCP surface is `quickserver_cli.py`; this standalone TCP path is no longer the main launcher target. |
| Standalone bridge idle watchdog loop | `bridge_server.py` | periodic idle check | Same reason as above; legacy standalone path. |
| Removed GGUF startup timeout var | `quickserver_assets.py` (`KIMODO_GGUF_STARTUP_TIMEOUT_SEC`) | env var name only | Kept only for legacy-env rejection / cleanup semantics, not current runtime control. |

## Example-Only / Stale Test Artifacts

| Scope | File | Value | Recommendation |
| --- | --- | --- | --- |
| Example startup wait | `example_run_server_tpose.bat` | `WAIT_TIMEOUT_SEC`, default `1800s` | Example-only. Does not represent the current integration harness. |
| Example wait hints | `example_run_server_tpose.bat` | `WAIT_HINT_INTERVAL_SEC = 10s` | Example-only. |
| Example quit grace | `example_run_server_tpose.bat` | fixed `15s` branch | Example-only. |
| Example generate wait | `example_run_server_tpose_client.ps1` | `KIMODO_TEST_GENERATE_WAIT_MINUTES` | Example-only. |

## New Coverage Added In This Round

| Case | What It Covers |
| --- | --- |
| `T48` | Verifies a short `KIMODO_IDLE_TIMEOUT_SEC` actually causes supervisor shutdown, then validates recovery generate. |
| `T47` | Manual network coverage for forced uv download path and its uv-specific probe/install timeouts. |
| `T46` | Manual network coverage for download-source probe windows across uv / torch / model sources. |

## Still Missing Automated Coverage

These are active but currently not covered by the external Python suite because they are either Unity-side or deep setup internals:

- `quickserver_setup.py` `2s` URL probe timeout
- Unity `BridgeProtocolClient` connect / IO / model-loading timeouts
- Unity `BridgeStartupWaiter` startup timeout behavior
- Unity `BridgeLogPump` wait / poll windows
- Editor-side dynamic generate timeout extension
- `text_encoder_llama_api.py` `120s` timeout on the llama HTTP embedding path

If these need coverage, the next step should be a Unity-driven test harness rather than extending the external Python suite further.
