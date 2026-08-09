---
name: kimodo-unity-animation
description: Operate Kimodo humanoid animation in the current Unity Editor session. Use for Timeline Session creation and querying, scene-character motion generation, pose sampling, character or clip edits, generation analysis, and Timeline range bake or retarget.
---

# Kimodo Unity Animation API

Chinese counterpart: [SKILL-zh.md](SKILL-zh.md).

Use the live tool schema and returned errors as the final authority. Send JSON objects only. Every response contains `ok`; on failure it is `{"ok":false,"error":"..."}`.

## Execution Contract

- Work against the current Unity Editor Scene in Edit Mode.
- All Timeline operations address the current Session. Never send `timeline_session_id`, `director_ref`, or a Timeline asset reference as an input.
- Use `character_ref`, `animation_id`, `request_id`, `asset_ref`, and `clip_ref` returned by prior tools. They are opaque identifiers.
- A character used by generation, sampling, retarget, or TryAdd must be a scene object with a valid Humanoid Avatar. A Project asset is never a generation character.
- Use `query_current_session` with `type`.
- Omit optional keys instead of supplying `null`.

## Mandatory One-Time Minimal Self-Check

Run this exact workflow once before substantive work. It uses the configured default model; do not call `Kimodo_list_models` unless a model change is requested.

1. `session_open_timeline({})`.
2. `query_current_session({"type":"character","pattern":"*","long":true})`; select one returned item whose `avatar` is `valid_humanoid`, and save its `character_ref`.
3. `kimodo_generate_animation_asset({"character_ref":"...","prompt":"stand still and breathe naturally","duration_seconds":1})`; save `request_id`.
4. Repeatedly call `kimodo_get_generation({"request_id":"..."})` until `status` is `completed`, `failed`, or `canceled`.
5. On `completed`, call `query_current_session({"type":"animation","character_ref":"...","pattern":"*","long":true})`; confirm the new item has `animation_id`, `global_start`, `global_end`, and `asset_path`.

Do not close the Session while the request is running. Call `session_close_timeline({})` only after no generation remains running.

## Tool Index

| Tool | Purpose | Current Session required |
| --- | --- | --- |
| `kimodo_list_characters` | List valid Humanoid characters. | No |
| `Kimodo_list_models` | List valid model/encoder combinations for an explicit model change. | No |
| `kimodo_help` | Return backend capability text. | No |
| `session_open_timeline` | Create a fresh current Session or load a named in-memory Session. | No |
| `session_close_timeline` | Close and preserve the current Session. | Yes |
| `query_current_session` | List Session, character, or animation objects with ls-style selectors. | Yes |
| `session_locate_animation` | Select a Timeline animation and evaluate its time. | Yes |
| `session_sample_pose` | Evaluate one character pose at global Session time. | Yes |
| `session_try_add` / `session_try_remove` | Add or remove a character track or clip. | Yes |
| `kimodo_generate_animation_asset` | Start asynchronous text-to-animation generation. | Yes |
| `kimodo_get_generation` / `kimodo_cancel_generation` | Poll or cancel generation. | Request id |
| `kimodo_analyze_timeline_range` | Return stored and backend range analysis. | Yes |
| `kimodo_bake_timeline_range` | Bake a global range to an AnimationClip; optionally retarget it. | Yes |

## Read APIs

### `kimodo_list_characters`

**Synopsis:** `kimodo_list_characters({ include_project_assets?, max_results? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `include_project_assets` | boolean | No | `false` | No | Include Humanoid prefab/model assets below `Assets`. These results are discovery-only; do not use them as generation characters. |
| `max_results` | integer | No | `100` | No | Clamped to `1..1000`. |

**Result:** `characters[]` and `count`. Each item includes `character_ref`, `name`, `source` (`scene` or `project`), `avatar`, `asset_path`, `scene_path`, and `active`. Use only `source: "scene"` `character_ref` values for generation.

### `Kimodo_list_models`

**Synopsis:** `Kimodo_list_models({})`

Call only if the user asks to change `model` or `text_encoder_model`.

**Parameters:** none.

**Result:** `configs[]` and `count`. Choose an item with `available: true`; pass its `model` and `text_encoder_model` unchanged to generation.

### `kimodo_help`

**Synopsis:** `kimodo_help({})`

**Parameters:** none. **Result:** backend help/capability payload. Use it only for backend-specific options not defined in this Skill.

### `query_current_session`

**Synopsis:** `query_current_session({ type?, pattern?, objects?, long?, head?, tail?, show_type?, character_ref?, character_name? })`

This is the Maya `ls`-style query API. It lists only current-Session objects. `pattern` and every item in `objects` support `*` for any sequence and `?` for one character, case-insensitively.

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `type` | enum | No | `session` | No | `session`, `character`, or `animation`. |
| `pattern` | string | No | `*` | No | Wildcard selector. Ignored when `objects` is non-empty. |
| `objects` | string[] | No | omitted | Yes | One or more name, `character_ref`, or `animation_id` wildcard selectors. A match against any item is included. |
| `long` | boolean | No | `true` | No | `true` returns full object details; `false` returns compact identifiers. |
| `head` | integer | No | omitted | No | Non-negative. Keep the first N matches. |
| `tail` | integer | No | omitted | No | Non-negative. Apply after `head`; keep the final N remaining matches. |
| `show_type` | boolean | No | `false` | No | Add `type` to each returned object. |
| `character_ref` | string | No | omitted | No | With `type: "animation"`, restrict results to this character. |
| `character_name` | string | No | omitted | No | Alternative character restriction for `type: "animation"`. |

**Selector rules:** use `character_ref` in preference to `character_name`; names can collide. For `type: "animation"`, `objects` may match `name` or `animation_id`. Supplying neither `character_ref` nor `character_name` lists animations for all current-Session characters.

**Result:** `{ session_name, type, pattern, count, objects[] }`. With `long: true`:

| Object type | Returned fields |
| --- | --- |
| `session` | `session_name`, `director_ref`, `timeline_asset_ref`, `timeline_asset_path`, `characters`, `current_time`, `current`. |
| `character` | `character_ref`, `name`, `animator_ref`, `avatar_ref`, `avatar`, `avatar_error`, `track_ref`, `animation_count`, `next_start_seconds`, `animations`. |
| `animation` | `animation_id`, `name`, `source`, `global_start`, `global_end`, `duration`, `clip_in`, `time_scale`, `asset_ref`, `asset_path`, `frame_rate`, `frame_count`, `is_human_motion`, `analysis`, plus `character`. |

## Session Lifecycle APIs

### `session_open_timeline`

**Synopsis:** `session_open_timeline({ session_name? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `session_name` | string | No | omitted | No | Omitted creates a fresh uniquely named Session. A name that is currently alive in this Unity Domain loads that Session; another name creates a named Session. |

**Effect:** creates a temporary Director and TimelineAsset, scans scene Animators, creates animation tracks, flattens discovered Animator clips, enables Timeline preview, and opens the Timeline window.

**Result:** full Session object as described by `query_current_session` with `type: "session"`.

### `session_close_timeline`

**Synopsis:** `session_close_timeline({})`

**Parameters:** none.

**Precondition:** the current Session has no running generation.

**Effect:** clear Timeline selection, save the TimelineAsset, and close its editing environment. The current Session, its Director, and its TimelineAsset remain available for a later `session_open_timeline({"session_name":"..."})`; this tool never deletes assets or scene objects.

**Result:** `session_name`, `timeline_asset_path`, `session_saved: true`, `session_retained: true`, `closed: true`.

## Timeline Selection and Sampling APIs

### Shared object-reference rules

Use these pairs wherever shown below. Each pair requires at least one value unless the tool says otherwise.

| Selector | Accepted values | Rule |
| --- | --- | --- |
| Character selector | `character_ref` or `character_name` | At least one must resolve to a current-Session character. Prefer `character_ref`; omit the name when the reference is known. |
| Animation selector | `animation_id` or `animation_name` | At least one must resolve within the selected character. Prefer `animation_id`; omit the name when the id is known. |

### `session_locate_animation`

**Synopsis:** `session_locate_animation({ character_ref|character_name, animation_id|animation_name, session_global? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | At least one | — | No | Character selector. |
| `animation_id` / `animation_name` | string | At least one | — | No | Animation selector. |
| `session_global` | number | No | selected animation `global_start` | No | Non-negative finite global Session time in seconds. |

**Result:** `session_name`, `character`, full `animation`, `session_global`, `located: true`.

### `session_sample_pose`

**Synopsis:** `session_sample_pose({ character_ref|character_name, session_global })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | At least one | — | No | Character selector. |
| `session_global` | number | Yes | — | No | Non-negative finite global Session time in seconds. |

**Result:** `pose_sample_id`, `session_name`, `character`, `session_global`, `root_position`, `root_rotation`, `body_position`, `body_rotation`, `muscles[]`, and `bones[]` (`bone`, world `position`, world `rotation`).

## Session Editing APIs

### `session_try_add`

**Synopsis:** `session_try_add({ kind, character_ref?, character_name?, clip_ref? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `kind` | enum | Yes | — | No | `character` or `clip`. |
| `character_ref` | string | Conditional | — | No | Required for `kind: "character"`; must resolve to a scene GameObject or Animator. Required as the character selector for `kind: "clip"` unless `character_name` is supplied. |
| `character_name` | string | Conditional | — | No | Allowed only as the current-Session character selector for `kind: "clip"`. It cannot add a new character. |
| `clip_ref` | string | Conditional | — | No | Required for `kind: "clip"`; an AnimationClip GlobalObjectId or `Assets/...` path. |

**Rules:** adding a character tries to resolve or generate a Humanoid Avatar. Failure returns an `avatar_required` error and does not retain a partial track. Adding a clip always appends it at the character track end; it never fills a gap.

**Result:** character addition returns `{ added: true, kind: "character", character }`; clip addition returns `{ added: true, kind: "clip", animation }`.

### `session_try_remove`

**Synopsis:** `session_try_remove({ kind, character_ref|character_name, animation_id|animation_name? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `kind` | enum | Yes | — | No | `character` or `clip`. |
| `character_ref` / `character_name` | string | At least one | — | No | Current-Session character selector. |
| `animation_id` / `animation_name` | string | Conditional | — | No | Required only for `kind: "clip"`; animation selector. |

**Rules:** removing a clip does not move remaining clips, compress virtual global time, rebuild the track, or reuse the removed global address.

**Result:** clip removal returns `removed: true`, `kind: "clip"`, `animation_id`; character removal returns `removed: true`, `kind: "character"`, `character_ref`.

## Generation APIs

### `kimodo_generate_animation_asset`

**Synopsis:** `kimodo_generate_animation_asset({ character_ref, prompt, duration_seconds?, model?, text_encoder_model?, seed?, diffusion_steps?, output_mode?, output_folder?, asset_name?, loop?, analysis_option?, pose_refs?, times?, constraint_types? })`

**Precondition:** a current Session exists. The `character_ref` must resolve to a scene Humanoid character. If it is not yet in the Session, it is appended before generation.

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` | string | Yes | — | No | Scene GameObject or Animator GlobalObjectId. Project asset paths are rejected. |
| `prompt` | string | Yes | — | No | Non-empty motion prompt. |
| `duration_seconds` | number | No | `5` | No | Positive finite seconds. |
| `model` | string | No | Project Settings default | No | Registered model/configuration id, never a path. Use `Kimodo_list_models` before explicitly changing it. |
| `text_encoder_model` | enum | No | Project Settings default | No | `high_performance` or `high_precision`. If it or `model` is supplied, their pair must be currently available. |
| `seed` | integer | No | random non-negative integer | No | Reproducibility seed. |
| `diffusion_steps` | integer | No | model default | No | Standard Kimodo: clamped to `1..1000`, default `100`; ARDY: clamped to the selected profile range, default `0`. |
| `output_mode` | enum | No | `humanoid_muscle` | No | `humanoid_muscle`, `character_bone`, or `model_bone`. The first two require a valid target Humanoid Avatar. |
| `output_folder` | string | No | `Assets/KimodoGeneratedClips` | No | Must be `Assets` or beneath it; no `.` or `..` segments. |
| `asset_name` | string | No | timestamped character name | No | Asset base name without extension. |
| `loop` | boolean | No | `false` | No | Kimodo models only. The server first generates a seed motion, then regenerates with its frame-0 body pose at frames `0` and `frame_count - 1`. The provisional motion is not saved; final Clip `loopTime` is enabled. Root translation remains unconstrained. |
| `analysis_option` | object | No | omitted | No | Backend analysis object. Pass only fields supported by `kimodo_help`; `keyframes.enabled: true` requests screenshot keyframes when supported. |
| `pose_refs` | string[] | No | omitted | Yes | Scene GameObject or Animator GlobalObjectIds used as pose constraints. |
| `times` | number[] | Conditional | evenly distributed from first to final generated frame | Yes | Allowed only with `pose_refs`; exactly the same count; every value finite and in `[0, duration_seconds]`. |
| `constraint_types` | enum[] | Conditional | `fullbody` for each pose | Yes | Allowed only with `pose_refs`; exactly the same count; each item is `fullbody` or `root2d`. |

**Multi-value rules:** `pose_refs`, `times`, and `constraint_types` are index-aligned. Supplying `times` or `constraint_types` without `pose_refs` fails.

**Immediate result:** `request_id`, `status: "running"`, `character`, `output_mode`, `model`, `text_encoder_model`, `seed`, `session_name`, `timeline_start_seconds`, and `timeline_duration_seconds`. The Timeline slot is reserved immediately; do not treat it as a completed asset.

### `kimodo_get_generation`

**Synopsis:** `kimodo_get_generation({ request_id })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `request_id` | UUID string | Yes | — | No | Identifier returned by `kimodo_generate_animation_asset`. |

**Result:** `request_id`, `status`, `stage`, `message`, `error`, `started_at_utc`, `target_alive`; after a result exists, also `asset_path`, `raw_bone_asset_path`, `seed`, `prompt`, optional `analysis`; Session writeback adds `session_name`, `timeline_start_seconds`, `timeline_duration_seconds`, `timeline_clip_asset_ref`, `animation_id`, and optional `analysis_track_ref`. Stop polling only at `completed`, `failed`, or `canceled`.

### `kimodo_cancel_generation`

**Synopsis:** `kimodo_cancel_generation({ request_id, reason? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `request_id` | UUID string | Yes | — | No | Identifier returned by generation. |
| `reason` | string | No | `Generation canceled by command.` | No | Cancellation reason. |

**Result:** the normal generation status payload plus `canceled` indicating whether cancellation was applied. Poll again to observe the terminal status.

## Analysis and Bake APIs

### `kimodo_analyze_timeline_range`

**Synopsis:** `kimodo_analyze_timeline_range({ character_ref|character_name, start_global, end_global, analysis_option? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | At least one | — | No | Current-Session character selector. |
| `start_global` | number | Yes | — | No | Finite inclusive global time; range requires `0 <= start_global < end_global`. |
| `end_global` | number | Yes | — | No | Finite exclusive global time. |
| `analysis_option` | object | No | `{}` | No | Backend analysis options. The API forces `analysis_only: true`. |

**Result:** `session_name`, `character`, `start_global`, `end_global`, `analyses[]`, and `analysis`. Stored analyses are returned for every overlapping generated animation. Backend analysis runs only when overlapping clips retain KMB data; otherwise `analysis` is derived from stored generation results and may have no issues or keyframes.

### `kimodo_bake_timeline_range`

**Synopsis:** `kimodo_bake_timeline_range({ character_ref|character_name, start_global, end_global, retarget_character_ref?, asset_name?, output_folder? })`

| Parameter | Type | Required | Default | Multiple | Meaning and constraints |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | At least one | — | No | Source current-Session character selector. |
| `start_global` | number | Yes | — | No | Finite inclusive global time; range requires `0 <= start_global < end_global`. |
| `end_global` | number | Yes | — | No | Finite exclusive global time. |
| `retarget_character_ref` | string | No | source character | No | Target scene character reference or current-Session target name. If no track exists, TryAdd is attempted. The target must have a valid Humanoid Avatar. |
| `asset_name` | string | No | timestamped source name | No | Output AnimationClip base name without extension. |
| `output_folder` | string | No | `Assets/KimodoGeneratedClips` | No | Same `Assets`-only rule as generation. |

**Result:** `baked: true`, `asset_ref`, `asset_path`, `character`, `source_character`, `start_global`, `end_global`, and full appended `animation`. Without retarget, output contains source bone curves. With retarget, output is a Humanoid muscle clip appended to the target track.
