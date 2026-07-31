# NvlabKimodoQuickServer

> Ships with Kimodo Unity Motion Tools 2.0.0; the QuickServer package version is also 2.0.0.

## Language
- Chinese: `README_ZH.md`
- English: `README.md`

## Features
- Build runtime environment with `uv` pipeline.
- Start the QuickServer TCP supervisor and let it queue bridge generate tasks.
- Reuse a single TCP connection for Session, Generate, Cancel, and direct KMB results.
- Return task-scoped `queued / loading / progress / cancelling / cancelled / done / error` messages.

## Requirements
- Windows 10/11 x64, macOS, or Linux. Use `run_server.bat` on Windows and `run_server.sh` on macOS/Linux.
- CUDA is the most complete accelerator path. Apple MPS, AMD/ROCm, and Intel XPU are experimental and fall back to CPU when runtime validation fails.
- Models are downloaded into the local `models\` directory by default. For a test or shared cache, override it through Unity's **Local Models Path** or the `KIMODO_MODELS_ROOT` environment variable.
- `uv` is required. `run_server.bat` / `run_server.sh` can download an unmanaged local `uv` binary into `program\exe\uv\` on first launch if missing. Its package cache still uses uv's normal global cache location.

## Start
```bat
cd /d C:\path\to\NvlabKimodoQuickServer~
run_server.bat
```

macOS / Linux:
```bash
cd /path/to/NvlabKimodoQuickServer~
./run_server.sh
```

The launcher checks and completes setup automatically before starting the TCP supervisor; do not append a `setup` subcommand. Unity normally supplies the model, text-encoder mode, and models directory with each generation request. The Windows batch launcher handles lifecycle arguments only and does not forward advanced runtime arguments such as `--model`, `--models-root`, or `--output`; see `PARAMETERS.md`.

`text_encoder_mode=high_precision|high_performance` selects the precision preference; QuickServer then places the encoder from current free VRAM and backend capabilities. It first reserves about 2 GB for the motion model and treats the remaining free VRAM as the encoder budget; NF4/INT8/FP16 require 6/8/16 GB respectively. Explicit `simulate_free_vram_gb=0` moves the entire runtime to CPU.

## TCP protocol notes
- Every request may carry `request_id`; every response for that request echoes it, so one persistent TCP connection can multiplex commands safely.
- `session.open` binds the current TCP connection to a new explicit Session. Without it, commands use `session:default`.
- Every Session owns a FIFO Generate command queue with a limit of 32. Each Generate produces exactly one final result.
- `session.close` closes only an explicit Session. Closing `session:default` shuts down QuickServer. Legacy `quit` has the same server-wide effect.
- `generate` uses `text_encoder_mode`; `highvram` and `force_cpu` are removed. The Force CPU UI sends `simulate_free_vram_gb=0`.
- `generate` accepts optional `task_id`. If omitted, QuickServer assigns a stable task id before queueing.
- Once a task id is assigned, every response for that task carries the same `task_id`.
- A task can emit intermediate statuses such as `queued`, `loading`, `progress`, or `cancelling`, and always ends in `done`, `error`, or `cancelled`.
- `cancel` accepts an optional `task_id`. If omitted, QuickServer cancels the first cancellable queued task and returns the resolved task id.
- ARDY generation is non-interruptible inside a Horizon. Cancel stops the waiting Generate response at the next Horizon boundary but keeps the Session timeline until `session.close`.
- A newer ARDY Generate does not cancel the active Horizon. The active request finishes first, and only the newest queued ARDY update is retained.

## Direct KMB transport

`generate` uses `output_format=kmb_v1`. A successful ARDY response is one JSON line with `byte_length`, immediately followed by a non-empty KMB1 range that extends beyond the current Playback Reserve.

An ARDY Generate with a positive `duration` is a fixed-length request: it starts a fresh logical generation, may initialize from an explicit History clip constraint, internally runs as many Horizons as needed, returns one exact-length KMB result, and then releases that logical timeline. An ARDY Generate without `duration` is streaming: clients send session-relative `time_as_double`, QuickServer keeps the per-Session RNG/history/timeline, and later Generate calls update it until `session.close`. `duration: 0` is invalid rather than a streaming alias.

For streaming ARDY, QuickServer converts seconds with the selected model FPS, keeps only the profile-sized accelerator history, and caches the CPU timeline for seek. For Core Horizon40, token granularity is 4 frames, one delivered Horizon is 40 frames, and retained history is capped at 160 frames; these values are independent. `ardy_playback_reserve_seconds` defaults to 1 second; `ardy_adaptive_playback_reserve` defaults to true and adjusts the effective reserve from measured server response time. Missing `prompt` or `constraints_json` keeps the current value; `[]` clears the complete constraint snapshot. Prompt or constraint changes keep motion through `time_as_double + Playback Reserve`, then regenerate and return the affected absolute KMB range.

A decreasing `time_as_double` is a seek. Normal responses append from the previously delivered tail; seek and replan responses may overlap previously returned frames, so clients replace their timeline from `start_frame`.

Optional History/Future KMB inputs use a JSON `kmb_attachments` manifest with contiguous offsets and lengths, followed by concatenated KMB1 blobs. Clip constraints reference `format=kmb_attachment_v1` and a zero-based `attachment` index. The KMB1 FlatBuffer schema itself is unchanged. `ardy_file_v1` remains debug-only behind `KIMODO_ARDY_ALLOW_TEST_FILES`.

### ARDY clip constraints

A clip without `is_history` is treated as complete History; History clips cannot carry a mask. A Future clip sets `is_history=false`. Its flat boolean mask has `4 + (joint_count - 1) * 3` entries ordered as `Root.x, Root.y, Root.z, RootHeading`, followed by non-Root joint XYZ channels in KMB/ARDY profile order.

Python reconstructs ARDY features from KMB root positions and local quaternions. Diffusion steps remain in the checkpoint-native range 1–10.

## Parameters
- See `PARAMETERS.md`
