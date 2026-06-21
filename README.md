# KimodoUnityBridge

This repository packages Unity-side Kimodo integration plus a new offline bridge runtime template (`NvlabKimodoQuickServer~`) for local motion generation.

## 1) Repository Structure

It contains two coupled parts:

1. Unity integration (`Runtime/`, `Editor/`, `TimelineInject/`)
- Timeline clip/editor workflow (`KimodoPlayableClip`).
- Constraint marker tooling under Timeline.
- Bridge generation backend that launches and talks to a local Kimodo server.
- Project settings panel: `Project/Kimodo Server Manager`.

2. Runtime template (`NvlabKimodoQuickServer~`)
- Runtime bootstrap and launcher scripts.
- Internal setup/model preparation managed by the runtime entrypoint.
- Bridge startup scripts.
- End-to-end TCP example scripts.

## 2) Active Runtime Pipeline (Unified)

Only the new pipeline is supported:

- Start server: `run_server.bat` on Windows or `run_server.sh` on Linux
- Example test: `example\example_run_server_tpose.bat`
- Setup and model preparation are handled internally by the runtime entrypoint; Unity bridge does not call standalone setup/download scripts directly.

Legacy offline scripts are not part of the supported startup path.

## 3) Quick Start

Runtime template path:
- `C:\nvlab\KimodoUnityBridge\NvlabKimodoQuickServer~`

Typical sequence:

```bat
cd /d C:\nvlab\KimodoUnityBridge\NvlabKimodoQuickServer~
run_server.bat --model Kimodo-SOMA-RP-v1 --output console
```

The first launch will bootstrap setup automatically if needed.

Or run the end-to-end example:

```bat
cd /d C:\nvlab\KimodoUnityBridge\NvlabKimodoQuickServer~
example\example_run_server_tpose.bat
```

## 4) Unity Startup Path

Unity bridge runtime behavior:

1. Runtime root is `NvlabKimodoQuickServer` in Unity project root.
2. If missing, it is bootstrapped from package template `NvlabKimodoQuickServer~`.
3. Start script resolution uses new pipeline only:
- `run_server.bat`
- `run_server.sh`
4. If a legacy offline script is detected in runtime root, code throws an exception.

## 5) Server Protocol

Bridge server module:
- `kimodo.bridge.bridge_server`

Transport:
- TCP socket
- newline-delimited JSON request/response

Commands:
- `{"cmd":"ping"}` -> `pong` / `loading` / `error`
- `{"cmd":"generate", ...}` -> `done` with `motion_json_compact` on success
- `{"cmd":"quit"}` -> `bye`

## 6) Parameters

Unity clip fields (Bridge mode):
- `bridgeModelName`
- `bridgeVramMode` (`Low` / `High`)

Runtime start parameters (new pipeline):
- `--model <MODEL_NAME>`
- `--highvram`
- `--output console|file`
- `--log <path>`
- `--force-setup` (force internal setup rebuild before launch)

Model aliases accepted by runtime scripts include `soma`, `g1`, `smplx`, `soma-seed`.

## 7) Runtime Artifacts to Monitor

Under runtime root (`NvlabKimodoQuickServer` or template root while testing):
- `.setup.complete`
- `.setup.lock`
- `serverport`
- `log\setup.log`
- `log\run_server.log`
- `log\bridge_server.log`
- `log\watchdog.log`

## 8) Automation Notes

For automation agents:

1. Prefer invoking:
- `run_server.bat`
- `example\example_run_server_tpose.bat`
2. Treat `serverport` as source-of-truth endpoint.
3. Protocol flow:
- ping until ready/loading resolves -> generate -> quit.


