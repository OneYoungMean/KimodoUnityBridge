# 开发备忘录 / Development memo

> Temporary development snapshot. This is not an AI execution contract and does not override the live schema, `kimodo_help`, or user instructions.
>
> 当前文件只是临时开发快照，不是 AI 执行契约，也不覆盖实时 schema、API Help 或用户指令。日常任务从 [SKILL.md](SKILL.md) 路由，具体命令与参数读取 [Command/help.json](Command/help.json)。

## Current command surface

The maintained public commands are:

- Startup and discovery: `kimodo_install_server`, `kimodo_help` (`kimodo_install_server` initializes the project-local runtime on first use and is repeated only for installation, upgrade, or recovery)
- Session/content: `session_get_or_create`, `session_add`, `session_close`
- Generation jobs: `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation`
- Analysis/evidence: `animation_analyze`, `animation_compare`
- Pose editing: `pose_get`, `pose_contract`, `pose_set_root_transform`, `pose_set_muscle`
- Asset output: `kimodo_record_range`, `kimodo_retarget_animation`

## Current boundaries

- A new Session is empty. Add a scene Humanoid Animator or renderable Mesh explicitly with `session_add(kind:"character")`; add clips or Animator content explicitly. A Mesh-only character rejects Humanoid clips, while a Humanoid target retargets a generic clip before appending it.
- Completed Session Clips are immutable. Corrections, recordings, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation_analyze` accepts one or two explicit Session clips and returns numeric analysis plus one composite PNG at `pictures.image_path` with a self-describing `pictures.images` tile list. `level` is `low`, `middle`, or `high`; `low` keeps the grouped test panels, `middle` adds time-ordered keyframe and start/end poses, and `high` also adds time-ordered foot-transition poses. Mesh-only targets do not provide Humanoid contact semantics.
- `animation_compare` is Humanoid-only and compares two ranges without modifying the Session; it reports root/yaw, mean-muscle, and end-effector differences, not compatible foot-contact evidence or semantic quality.
- `session_add(kind:"animator")` imports supported state Clip candidates and materializes supported same-Layer State-to-State transitions as logical `transition_clip` records. It does not bake transition AnimationClip assets; unsupported Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped.
- Root2D, fullbody, and pose-based hand/foot constraints are supplied through `kimodo_generate_animation.constraints`. Humanoid analysis stores and returns a reusable Root Path consumed through `root_path`; Path Override can regenerate with the same seed from absolute Unity-yaw begin/end angles, preserve the baseline path length, and coexist with loop generation. An absolute heading override runs last at 30-frame intervals, replacing Root2D headings without changing the composed Root XZ positions; when Path Override is active, it rewrites that Root2D record instead of appending a duplicate. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose_get` creates a new External Pose marker for a Humanoid character. Edit it with `pose_set_root_transform` or `pose_set_muscle`; use `pose_contract` for end-effector alignment.

## Active documentation work

- Keep `Command/help.json` aligned with the dispatcher, and keep concrete command details out of `SKILL.md` and its three sub-skills.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Closing or switching a Session cancels its active generations. Keep Foot IK/Raycast or other evaluator-only capabilities documented as boundaries unless a public command and fixture prove them.
