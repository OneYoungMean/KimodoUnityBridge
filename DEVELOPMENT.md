# 开发备忘录 / Development memo

> Temporary development snapshot. This is not an AI execution contract and does not override the live schema, `kimodo_help`, or user instructions.
>
> 当前文件只是临时开发快照，不是 AI 执行契约，也不覆盖实时 schema、`kimodo_help` 或用户指令。日常执行以 [TOOLS.md](TOOLS.md) 为准。

## Current command surface

The maintained public commands are:

- Startup and discovery: `kimodo_install_server`, `kimodo_help` (`kimodo_install_server` runs once after Unity is ready, before runtime-dependent commands)
- Session/content: `session_get_or_create`, `session_add`, `session_close`
- Generation jobs: `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation`
- Analysis/evidence: `animation_analyze`, `animation_compare`
- Pose/path editing: `pose_get`, `pose_create_path`, `pose_contract`, `pose_set_root_transform`, `pose_set_muscle`
- Asset output: `kimodo_record_range`, `kimodo_retarget_animation`

## Current boundaries

- A new Session is empty. Add a scene Humanoid Animator or renderable Mesh explicitly with `session_add(kind:"character")`; add clips or Animator content explicitly. A Mesh-only character rejects Humanoid clips, while a Humanoid target retargets a generic clip before appending it.
- Completed Session Clips are immutable. Corrections, recordings, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation_analyze` accepts one or two explicit Session clips and returns numeric analysis plus one composite PNG at `pictures.image_path` with a self-describing `pictures.images` tile list. `level` is `low`, `middle`, `high`, or `-test`; `resolution` is 64–4096 pixels and defaults to 512. Mesh-only targets do not provide Humanoid contact semantics.
- `animation_compare` is Humanoid-only and compares two ranges without modifying the Session; it reports root/yaw, mean-muscle, and end-effector differences, not compatible foot-contact evidence or semantic quality.
- `session_add(kind:"animator")` imports supported state Clip candidates and materializes supported same-Layer State-to-State transitions as logical `transition_clip` records. It does not bake transition AnimationClip assets; unsupported Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped.
- Root2D, fullbody, and pose-based hand/foot constraints are supplied through `kimodo_generate_animation.constraints`; `pose_create_path` creates a reusable Path and generation consumes it via `root_path`. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose_get` creates a new External Pose marker for a Humanoid character. Edit it with `pose_set_root_transform` or `pose_set_muscle`; use `pose_contract` for end-effector alignment.

## Active documentation work

- Keep `TOOLS.md` and both SKILL entry points aligned with this command surface. Remove references to retired command names instead of preserving them as examples.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Closing or switching a Session cancels its active generations. Keep Foot IK/Raycast or other evaluator-only capabilities documented as boundaries unless a public command and fixture prove them.
