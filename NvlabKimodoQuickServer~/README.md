# NvlabKimodoQuickServer1

## Language
- Chinese: `README_ZH.md`
- English: `README.md`

## Features
- Build runtime environment with `uv` pipeline.
- Start the QuickServer TCP supervisor and let it queue bridge generate tasks.
- Reuse a single TCP connection for Session, Generate, Cancel, and animation Handle commands.
- Return task-scoped `queued / loading / progress / cancelling / cancelled / done / error` messages.

## Requirements
- Windows 10/11 x64
- Models are downloaded into the local `models\` directory by default. Use `--models-root` only when a test or shared cache needs an override.
- `uv` is required. `run_server.bat` / `run_server.sh` can download an unmanaged local `uv` binary into `program\exe\uv\` on first launch if missing. Its package cache still uses uv's normal global cache location.

## Install
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat setup --output console
```

If you already have a baked FP16 text encoder at `C:\nvlab\LLMVec-GGUF\KIMODO-Meta3_llm2vec_FP16`, you can build the local CPU INT8 asset first:
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
program\exe\uv\uv.exe run --python 3.12 --no-project python tools\build_llm2vec_int8.py --verify
```

Linux:
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh setup --output console
```

## Example
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat --model Kimodo-SOMA-RP-v1 --output console
```

Linux:
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh --model Kimodo-SOMA-RP-v1 --output console
```

`text_encoder_mode=high_precision|high_performance` selects the precision preference; QuickServer then places the encoder from current free VRAM and backend capabilities. It first requires about 2 GB for the motion model, rechecks free VRAM after loading it, and uses GPU budgets of 6/8/16 GB for NF4/INT8/FP16. Explicit `simulate_free_vram_gb=0` moves the entire runtime to CPU.

TCP smoke test:
```bat
example\example_run_server_tpose.bat
```

Live console variant:
```bat
example\example_run_server_tpose_console_live.bat
```

## TCP protocol notes
- Every request may carry `request_id`; every response for that request echoes it, so one persistent TCP connection can multiplex commands safely.
- `session.open` binds the current TCP connection to a new explicit Session. Without it, commands use `session:default`.
- Every Session owns a FIFO Generate queue with a limit of 32. Kimodo runs atomically; persistent ARDY streams advance one complete Horizon per fair scheduler turn.
- `session.close` closes only an explicit Session. Closing `session:default` shuts down QuickServer. Legacy `quit` has the same server-wide effect.
- `generate` uses `text_encoder_mode`; `highvram` and `force_cpu` are removed. The Force CPU UI sends `simulate_free_vram_gb=0`.
- `generate` accepts optional `task_id`. If omitted, QuickServer assigns a stable task id before queueing.
- Once a task id is assigned, every response for that task carries the same `task_id`.
- A task can emit intermediate statuses such as `queued`, `loading`, `progress`, or `cancelling`, and always ends in `done`, `error`, or `cancelled`.
- `cancel` accepts an optional `task_id`. If omitted, QuickServer cancels the first cancellable queued task and returns the resolved task id.
- ARDY Cancel first returns `cancelling`. After the non-interruptible Horizon finishes and cleanup completes, the same TCP receives an asynchronous `{"status":"event","event":"task.closed",...}` message without a `request_id`.

## Animation handles

QuickServer keeps uploaded/generated KMB animations in a process-local, quota-limited store. Requests use a JSON line followed by `byte_length` raw bytes when a binary body is present.

- `animation.upload`: send `format=flatbuf_motion_v1`, `byte_length`, optional `description`, then KMB bytes; returns `handle_info`.
- `animation.info`: send `handle`; returns `handle_info`.
- `animation.download`: send `handle`; returns a JSON header followed by KMB bytes.
- `animation.release`: send `handle`; releases immediately or after an active task unpins it.
- `generate` with `output_format=kmb_handle_v1` returns `handle_info` without inline KMB bytes. `flatbuf_motion_v1` remains the legacy inline-binary mode.

`animation.upload` has a three-second end-to-end limit. Static Handles are server-instance-global, immutable, repeat-downloadable, and use capacity-based LRU fallback. ARDY `kmb_handle_v1` results are Session-bound streams backed by two fixed buffers: Generate returns the Handle before the first Horizon is ready, download destructively swaps and returns the currently valid frames, and Cancel closes the whole stream after any in-flight Horizon finishes. Buffer capacity is `duration × FPS` rounded up to a Horizon multiple.

Handles expire when QuickServer restarts. Capacity-based LRU cleanup is a fallback for clients that fail to release; handles do not expire by age. Production clip constraints use `format=kmb_handle_v1`; `ardy_file_v1` is available only behind the explicit test-file flag.

### ARDY clip constraints

A clip without `is_history` remains a complete history clip for compatibility, and history clips cannot carry a mask. A future clip uses an uploaded static KMB Handle and sets `is_history=false`. Its required flat boolean mask has `4 + (joint_count - 1) * 3` entries ordered as `Root.x, Root.y, Root.z, RootHeading`, followed by non-Root joint XYZ channels in strict KMB/ARDY Profile order. `RootHeading` controls both internal cos/sin channels. `true` constrains a channel and `false` leaves it free. Later clips overwrite earlier clips at the same frame/channel; a later `false` also clears an earlier constraint.

Python runs FK from KMB root positions and local quaternions into ARDY root-relative joint positions. Any future body-position mask automatically runs a free Root pass followed by a Body pass with generated Root XYZ and heading locked. ARDY postprocess is skipped while a future clip is active, with no external output overwrite. Diffusion steps remain in the checkpoint-native range 1–10. Run `Env~\Scripts\python.exe tools\test_ardy_clip_constraint_tpose.py --models-root C:\nvlab\models~ --model ardy-core --steps 10` for the real-checkpoint upper-body T-pose smoke test.
- FlatBuffer responses still use `byte_length` followed immediately by the binary payload for that same task.

## Parameters
- See `PARAMETERS.md`
