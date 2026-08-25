# Kimodo command boundary test plan

The public contract is the live result of `command_dispatcher.GetCommandDefinitionsJson()`, `kimodo_help`, and the returned error envelope. Every case below should assert:

```json
{"ok":false,"error":{"code":"...","message":"..."}}
```

## 1. Dispatcher and protocol envelope (P0)

| ID | Input | Expected |
|---|---|---|
| D-01 | null, empty, whitespace command | `unknown_command`; no exception escapes the entry point |
| D-02 | leading/trailing whitespace around a valid command | same result as the trimmed command |
| D-03 | unknown command, including removed legacy names | `unknown_command` |
| D-04 | `{`, `[]`, `null`, scalar JSON, duplicate keys | `invalid_argument` for non-object/invalid JSON; duplicate-key policy is documented and stable |
| D-05 | every failure | `ok=false`, non-empty `error.code` and `error.message`; no stack trace/path leakage |
| D-06 | definitions | unique names, closed object schemas, required fields declared in `properties` |
| D-07 | an undeclared argument on each command | Decide and lock the policy: the published schemas say reject (`additionalProperties=false`), while the raw C# dispatcher currently ignores unknown keys unless a caller validates first |

Schema/runtime parity is a separate assertion: enum and numeric limits advertised by `GetCommandDefinitionsJson()` must match the runtime validators, or the schema must be amended.

## 2. Help and discovery (P0)

Test `section=commands/models/constraints`, case and whitespace normalization, omitted section default, unknown section, known command manual, unknown command manual, and model listing when the server is disconnected or returns an empty configuration list.

## 3. Session lifecycle (P0)

Use a temporary Unity scene and always close the created session in teardown.

- `session_get_or_create`: omitted name, blank name, new name, reopen existing name with different casing, switch between sessions, repeated call idempotency.
- Names: empty/whitespace, very long name, invalid/path-like name, same name after close.
- `session_close`: no current session (`session_required`/no-current error), omitted id, valid id, malformed GUID, unknown GUID, closing an already closed session.
- `session_add`: invalid `kind`, missing conditional `character`/`clip`/`animator`, missing/ambiguous scene object, duplicate character, generic clip on humanoid, humanoid clip on mesh-only object, `ignore_warning` false vs true, and animator transition count at 128/129.
- Verify revision increments, persisted `session.json` remains parseable, and closing a session cancels active generations.

## 4. Analyze and compare (P0)

`animation_analyze`:

- `clips` missing, empty, three items, scalar item, missing character/clip, duplicate roles, invalid role, one implicit source role, two explicit source/target roles.
- `level`: omitted/default, `low`, `middle`, `high`, case/whitespace, unknown value (including removed `-test`).
- `resolution`: omitted (512), exactly 64/4096, 63/4097, zero, negative, float, string, null.
- Verify completed clips are unchanged, cache reuse is stable, and mesh-only output omits humanoid contact claims.

`animation_compare`:

- missing/non-object `origin` or `target`; missing animation/range; range not `[start,end)`; non-integers; negative start; `start == end`; end beyond duration; exact `[0,duration)`.
- Verify output deltas are finite and no source clip/timeline mutation occurs.

## 5. Recording and retargeting (P0)

`kimodo_record_range`: missing character/start/end, negative frames, `start == end`, reversed range, exact one-frame range, `speed` omitted/1, zero/negative/NaN/Infinity, root-motion flag, blank/custom name, default/custom output folder, and cleanup after a write failure.

`kimodo_retarget_animation`: missing source/animation/target, unknown or ambiguous references, same source and target, invalid Avatars, blank/custom name/folder, and cleanup after retarget failure.

## 6. Generation and asynchronous state (P0)

`kimodo_generate_animation`:

- missing/blank prompt and character; duration 1/0/-1; omitted/default 300; very large integer; loop false/true at 300 and 301 (fallback warning); output modes all enum values plus unknown; text encoders in both spellings plus unknown; model name, path-like model, unknown/unavailable model; seed min/max; diffusion steps below/at/above clamp limits; output name/folder.
- Constraints: missing/non-array; non-object item; frame 0 and `duration-1`; frame `duration`; negative frame; point constraint with no field; root2d position+heading together; missing pair; zero heading; root_path alone; root_path mixed with point fields; invalid pose reference; zero-length path; overlapping paths; explicit root2d overriding root_path.
- Verify accepted response has a GUID `request_id`; the reserved range is reported; overlapping edits return `generation_range_locked` with complete `details`.

`kimodo_get_generation` / `kimodo_cancel_generation`:

- missing/blank/malformed GUID; unknown GUID; valid GUID in current session; GUID from another session; repeated cancel; cancel reason omitted/blank/custom; persisted completed/failed/canceled status remains queryable.

## 7. Pose commands (P1)

- `pose_get`: source object/fields, negative/out-of-range frame, `full_data` omitted/false/true, invalid clip/character, locked frame.
- `pose_create_path`: all presets, `length` zero/negative/non-finite, bezier with fewer than two knots, malformed vectors, non-bezier knots rejection, inverse flag.
- `pose_set_root_transform`: invalid/missing reference, root missing/empty, position length/type/non-finite, rotation length/type/non-finite, rotation without existing position, position-only and position+rotation.
- `pose_set_muscle`: invalid/missing reference, empty map, unknown channel, non-finite value, min/max muscle values, multiple channels; verify atomicity on one invalid channel.
- `pose_contract`: invalid references, mode, empty/duplicate/unknown end effectors/components, position-only/rotation-only/both, and residual output finiteness.

## 8. Operational and concurrency checks (P1)

Run commands while compiling/importing, while entering Play Mode, with QuickServer stopped, with a delayed server response, and concurrently from two callers. Assert bounded timeouts, no deadlocks, no leaked temporary assets, and deterministic error envelopes.
