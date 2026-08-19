# 开发备忘录 / Development memo

> This is a temporary record of current development work. It is not an AI execution contract and does not override the live schema or user instructions.
>
> 这是当前开发工作的临时记录，不是 AI 执行契约，也不覆盖实时 schema 或用户指令。

## Current command surface

The maintained public commands are:

- Discovery and runtime repair: `kimodo_help`, `kimodo_install_server`
- Session/content: `session_get_or_create`, `session_add`, `session_close`
- Generation jobs: `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation`
- Analysis/evidence: `animation_analyze`, `animation_compare`
- Pose cache/editing: `pose_get`, `pose_contract`, `pose_set_root_transform`, `pose_set_muscle`
- Asset output: `kimodo_record_range`, `kimodo_retarget_animation`

## Current boundaries

- A new Session is empty. Add the scene Humanoid explicitly with `session_add(kind:"character")`; add clips or Animator content explicitly.
- Completed Session Clips are immutable. Corrections, recordings, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation_analyze` accepts one or two explicit Session clips and returns numeric analysis plus one composite PNG at `pictures.image_path` with a self-describing `pictures.images` tile list. `level` is `low`, `middle`, `high`, or `-test`.
- `animation_compare` compares two ranges without modifying the Session; it is evidence for boundary/root/pose differences, not a semantic quality oracle.
- `session_add(kind:"animator")` materializes supported same-Layer State-to-State transitions as logical `transition_clip` records. It does not bake transition AnimationClip assets; unsupported Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped.
- Root2D, fullbody, and pose-based hand/foot constraints are supplied through `kimodo_generate_animation.constraints`. There is no separate Root2D path command in this command surface.
- `pose_get` creates or reuses a Pose Cache marker. Edit that cache with `pose_set_root_transform` or `pose_set_muscle`; use `pose_contract` for end-effector alignment.

## Active documentation work

- Keep `TOOLS.md` and both SKILL entry points aligned with this command surface. Remove references to retired command names instead of preserving them as examples.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, Pose Cache editing, and immutable-Clip append behavior in the maintained project.
