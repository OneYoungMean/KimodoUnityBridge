# Kimodo Bridge Lifecycle Plan

## Summary
- `run_server.bat/.sh` only launches `quickserver_cli`
- `quickserver_cli` is now the only external TCP entrypoint
- `quickserver_cli` owns lifecycle, runtime reuse, generate queue, cancel, idle release, and owner-pid shutdown
- `bridge_server.py` no longer needs to be the public TCP surface; its runtime helpers are reused internally
- `serverport` is kept only for dynamic port discovery
- `cancel` is now keyed by `task_id`

## Current Architecture

### `run_server.bat/.sh`
- Owns the outermost bootstrap lock
- Handles uv detection / install
- Runs setup first
- Releases bootstrap lock after setup
- Then starts `quickserver_cli`
- No longer needs to carry model / VRAM / device business parameters into setup

### `quickserver.py`
- Pure setup entry only
- Only handles environment / venv / dependency setup
- No longer owns run / wait / watchdog / cli-launch semantics

### `quickserver_cli`
- Writes dynamic `serverport`
- Accepts TCP commands from Unity
- Supports:
  - `start`
  - `generate`
  - `cancel`
  - `stop` / `quit`
- Maintains:
  - current runtime config
  - current loaded model/runtime
  - one active generate task
  - queued generate tasks
  - owner Unity pid
- Releases runtime on idle
- Shuts itself down when owner pid exits or full idle timeout is reached

### `bridge_server.py`
- Still contains reusable helper functions for:
  - runtime self-check
  - asset provisioning
  - model loading
  - constraint parsing
  - generate result serialization
- Its old standalone TCP server path is no longer the intended runtime entry for Unity

## TCP Protocol

### `start`
Request fields:
- `cmd = "start"`
- `model`
- `highvram`
- `force_cpu`
- `models_root`
- `force_hf_download`
- `owner_pid`

Behavior:
- Ensures the requested runtime is loaded
- Reuses current runtime if signature matches
- Rebuilds runtime if signature differs
- Returns `status = "ready"` on success

### `generate`
Request fields:
- `cmd = "generate"`
- `task_id`
- existing Kimodo generate payload fields

Behavior:
- Queues the task
- Keeps one active generate at a time
- Executes tasks sequentially
- Returns the final result on the same connection when that specific task finishes

### `cancel`
Request fields:
- `cmd = "cancel"`
- `task_id`

Behavior:
- If queued: removes the queued task
- If active: signals cancellation for the active task
- If missing: returns `idle` with not-found message

### `stop` / `quit`
Behavior:
- Cancels active work
- Clears queue
- Releases runtime
- Shuts down `quickserver_cli`

## Runtime Signature
- Runtime reuse currently keys on:
  - `model`
  - `highvram`
  - `force_cpu`
  - `models_root`
  - `force_hf_download`

## `serverport`
- Still required because multiple Unity instances may run multiple cli instances
- File content remains only:
  - `host:port`
- It is used only for endpoint discovery
- It is no longer the source of:
  - health
  - loading state
  - watchdog identity

## Bootstrap Lock
- The outer bootstrap lock now belongs to `run_server.bat/.sh`
- It covers:
  - uv probing / install
  - setup invocation
- If a second launcher starts while the lock owner is still alive:
  - it waits for lock release
- If the owner pid is dead:
  - the stale lock is removed
- After setup completes, the lock is released and both launchers may continue to cli startup
- Final self-reuse / self-exit remains the responsibility of `quickserver_cli`

## Queue / Cancel Rules
- Only one generate runs at a time
- Additional generate requests are queued
- `task_id` is mandatory for `generate`
- `task_id` is mandatory for `cancel`
- Unity-side request id should map directly to `task_id`

## Owner / Idle Shutdown
- `quickserver_cli` watches the Unity owner pid passed from launcher / start
- If owner pid disappears:
  - cancel active task
  - clear queued tasks
  - release runtime
  - exit
- If idle timeout is reached:
  - `quickserver_cli` exits
- Before full exit, runtime may be released earlier during idle periods

## Unity-Side Expectations
- Unity still discovers the endpoint through `serverport`
- Unity connects to `quickserver_cli`, not the old bridge TCP server
- Unity must send `start` before relying on a specific model/runtime config
- Unity generate cancellation should use the same `task_id`

## Known Risks
- The legacy standalone TCP path in `bridge_server.py` still exists in source and should be removed later after validation
- Full end-to-end smoke verification was not completed in this pass because the checked runtime copy did not currently contain a ready `.venv`
- Runtime reconfiguration while a generate task is active is intentionally rejected as `busy`
- Large inline `constraints_json` is still sent over JSON; no payload optimization has been done yet
