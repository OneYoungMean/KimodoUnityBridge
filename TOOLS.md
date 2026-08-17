# Kimodo AI Animation Tools

QuickServer version: `2.2.7`; capability: motion generation, sparse representative keyframes, dense KMB foot-contact analysis, and an experimental engine-independent KMB reference-pose retarget core with target-arm A-to-T calibration.

## Public entry and response contract

The Editor entry point is `command_dispatcher`. Discover the live schema with `GetCommandDefinitionsJson()` and invoke every command with a JSON object. The live schema and `kimodo_help` are the parameter authority.

Every successful result contains `{"ok":true}`. Every failure has `{"ok":false,"error":{"code":"...","message":"..."}}`. `session_id` is optional only while a current Session exists; otherwise the result uses `session_required`. Session names are stable and case-insensitive. A supplied existing name selects that Session.

All public time values use 60 FPS integer frames. Animation ranges are half-open: `[start_frame,end_frame)`; an animation Pose locator uses an animation-local frame.

## Command groups

| Goal | Commands |
| --- | --- |
| Discover schema, constraints, and models | `kimodo_help` |
| Repair the project-local runtime when explicitly requested | `kimodo_install_server` |
| Create/select, add content to, or close a Session | `session_get_or_create`, `session_add`, `session_close` |
| Generate and poll/cancel work | `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation` |
| Analyze, render visual evidence, and compare motion | `animation_analyze`, `animation_compare` |
| Read, align, and edit cached poses | `pose_get`, `pose_contract`, `pose_set_root_transform`, `pose_set_muscle` |
| Record and retarget assets | `kimodo_record_range`, `kimodo_retarget_animation` |

## Working sequence

1. Call `kimodo_help({})`, then `session_get_or_create({"name":"<stable name>"})`.
2. Add the explicit scene humanoid with `session_add({"kind":"character","character":"<scene name or hierarchy path>"})`. Add project clips or a controller with the corresponding `clip` or `animator` form. Animator import materializes same-Layer State-to-State transitions as Timeline-composed `transition_clip` records; it does not bake a transition AnimationClip. If the projected transition count exceeds 128, inspect the warning and opt in with `ignore_warning:true` only when the full set is required. Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped.
3. Generate with `kimodo_generate_animation`; retain its `request_id` and poll `kimodo_get_generation` to `completed`, `failed`, or `canceled`.
4. Call `animation_analyze` with one or two explicit `{character,clip,role?}` items. `level` defaults to `middle`; use `low` for a compact ghost/trajectory pair or `high` for additional key and foot-contact tiles. `-test` is a temporary renderer-validation layout: each character contributes a 512×512 orthographic ghost-3D tile and a 512×512 orthographic pelvis-trajectory tile, so one/two characters produce 1024×512/1024×1024 PNGs. The same immutable Session clip with the same effective level returns its existing analysis and picture instead of recomputing it.
5. `animation_analyze` returns one composite PNG through `pictures.image_path` and a self-describing `pictures.images` tile list. The image contains all visual evidence; there is no separate picture command or public `analysis_id`. The descriptors are also indexed in `session.json` without embedding image bytes.
6. `keyframes` are ordered by descending `saliency`; inspect them in that order. `foot_contacts` is ordered by ascending `duration_frames`, so transient left/right support changes are inspected first. Check that the ghost, key-pose, foot-contact, and pelvis-trajectory tiles express the requested action, phase, direction, contact, and final state. Use the analysis result, `pose_get`, `pose_contract`, and sparse generation constraints to revise the next attempt.
7. For a loop, compare first and last key poses, root position/heading, contact phase, and velocity visually. Treat a visible discontinuity as a reason to revise the endpoint pose or root constraint and regenerate. There is no automatic loop-seam score in this version.

## Pose and constraint use

`pose_get` reads an animation pose and creates or reuses a Pose Cache marker. Preserve its cache locator (`session_id`, `track`, `frame`, `marker_id`) for `pose_set_root_transform` and `pose_set_muscle`. `full_data:true` returns all 49 muscles plus root, hand, and foot T/Q channels; the default is compact.

Use `pose_contract` to align the target pose root to one or more source end effectors. `align_target_root` fits a direct root delta; `least_squares_root_fit` reports a residual for multiple effectors. Generation constraints remain sparse per-frame objects and may combine full-body, root2d, hand, and foot information.

## Session state and current boundaries

Each Session-changing operation updates `Assets/KimodoGeneratedClips/Sessions/<safe-session-name>/session.json` using temporary-file write and atomic replacement. Every completed Clip added to a Session is immutable: commands never overwrite, retime, or replace it; generation, record, retarget, and later corrections append a new Clip. The JSON is a bounded AI-readable index of Session revision, tracks, animations, constraints, Pose Cache markers, analysis records, visual tile descriptors, and generation history. Dense motion remains in its per-analysis cache file; do not load it unless that specific analysis is required. It is not a runtime query API.

The experimental retarget core is engine-independent and consumes KMB motion plus two explicit fullbody reference-pose payloads (source and target). A reference may be a bind, neutral, A-pose, or canonical pose; it does not require a T-pose. The regular generation fullbody locator is not sufficient unless it includes global joint positions, global joint rotations, joint names, and parent indices. Unmapped target joints inherit their animated parent by default; an explicit `freeze_global` fallback is available only when that behavior is desired. Missing intermediate target joints share the mapped endpoints' relative rotation. An optional target-arm calibration builds a virtual T-pose from explicitly named upper/lower arm pairs, retargets through it, then rebases the result to the target's real reference pose; it does not modify the character asset or calibrate non-arm joints.

This version imports Animator state clips and BlendTree candidate clips only. Transition materialization and authored trajectory commands are deferred. `animation_analyze` renders visual evidence without modifying the animation.

---

# Kimodo AI 动画工具

QuickServer 版本：`2.2.7`；能力：动作生成、稀疏代表性关键帧、稠密 KMB 脚接触分析，以及带 target 手臂 A→T 校准的实验性引擎无关 KMB 参考姿势 Retarget 核心。

## 公开入口与返回契约

Editor 入口为 `command_dispatcher`。通过 `GetCommandDefinitionsJson()` 发现实时 Schema，并使用 JSON 对象调用每个命令。参数以实时 Schema 和 `kimodo_help` 为准。

每个成功结果均包含 `{"ok":true}`。每个失败结果均为 `{"ok":false,"error":{"code":"...","message":"..."}}`。只有已存在 current Session 时才能省略 `session_id`；否则返回 `session_required`。Session 名称稳定且大小写不敏感；传入同名 Session 会选中该 Session。

所有公开时间值均为 60 FPS 整数帧。动画区间是半开区间 `[start_frame,end_frame)`；动画 Pose locator 中的帧为动画局部帧。

## 命令分组

| 目标 | 命令 |
| --- | --- |
| 发现 Schema、约束和模型 | `kimodo_help` |
| 用户明确要求时修复项目级运行时 | `kimodo_install_server` |
| 创建/选择、添加内容、关闭 Session | `session_get_or_create`、`session_add`、`session_close` |
| 生成、轮询与取消 | `kimodo_generate_animation`、`kimodo_get_generation`、`kimodo_cancel_generation` |
| 分析、渲染视觉证据与比较动作 | `animation_analyze`、`animation_compare` |
| 读取、对齐、编辑缓存 Pose | `pose_get`、`pose_contract`、`pose_set_root_transform`、`pose_set_muscle` |
| 录制和重定向资产 | `kimodo_record_range`、`kimodo_retarget_animation` |

## 工作顺序

1. 调用 `kimodo_help({})`，然后调用 `session_get_or_create({"name":"<稳定名称>"})`。
2. 使用 `session_add({"kind":"character","character":"<场景名称或层级路径>"})` 加入明确的场景 Humanoid。通过对应的 `clip` 或 `animator` 形式加入项目 Clip 或 Controller。Animator 导入会把同 Layer 的 State→State 过渡作为 Timeline 组合的 `transition_clip` 记录，不会 Bake 新的过渡 AnimationClip。预计过渡数量超过 128 时先查看 warning；只有确实需要全量结果时才使用 `ignore_warning:true`。Any State、Entry、Exit、StateMachine 和 OverrideController 过渡会被报告为跳过。
3. 调用 `kimodo_generate_animation` 生成；保存 `request_id`，并轮询 `kimodo_get_generation` 直到 `completed`、`failed` 或 `canceled`。
4. 使用一个或两个显式的 `{character,clip,role?}` 项调用 `animation_analyze`。`level` 默认是 `middle`；紧凑的 ghost/trajectory 对使用 `low`，需要额外关键帧和脚接触子图时使用 `high`。`-test` 是临时渲染验证布局：每个角色输出一张 512×512 的正交 ghost-3D 图和一张 512×512 的正交骨盆轨迹图，因此单/双角色分别输出 1024×512/1024×1024 PNG。同一个不可变 Session Clip 使用相同有效 level 时，命令直接返回既有分析和图片而不重复计算。
5. `animation_analyze` 通过 `pictures.image_path` 返回一张组合 PNG，并通过自描述的 `pictures.images` 返回子图列表。该图包含全部视觉证据；没有独立图片命令或公开的 `analysis_id`。描述符也会写入 `session.json` 索引，但不会嵌入图片字节。
6. `keyframes` 按 `saliency` 降序排列；按此顺序检查。`foot_contacts` 按 `duration_frames` 升序排列，因此短暂的左右脚支撑切换优先检查。检查 ghost、关键姿势、脚接触和骨盆轨迹子图是否表达了请求动作、阶段、方向、接触和结束状态。使用分析结果、`pose_get`、`pose_contract` 和稀疏生成约束修正下一次生成。
7. 对循环动画，视觉比较首末关键姿势、根位置/朝向、接触相位和速度。发现可见接缝时，修改端点姿势或根约束后重新生成。本版本不提供自动 loop-seam 分数。

## Pose 与约束

`pose_get` 读取动画 Pose，并创建或复用 Pose Cache marker。保存返回的 cache locator（`session_id`、`track`、`frame`、`marker_id`），供 `pose_set_root_transform` 和 `pose_set_muscle` 使用。`full_data:true` 返回全部 49 个 muscle 以及 root、hand、foot T/Q 通道；默认返回紧凑数据。

`pose_contract` 将目标 Pose 的 root 对齐到一个或多个来源末端。`align_target_root` 计算直接 root delta；`least_squares_root_fit` 对多个末端返回 residual。生成约束保持按帧稀疏对象，可组合 full-body、root2d、手和脚信息。

## Session 状态与当前边界

每次 Session 变更都通过临时文件写入和原子替换，更新 `Assets/KimodoGeneratedClips/Sessions/<safe-session-name>/session.json`。每个写入 Session 且已完成的 Clip 都不可变：command 不得覆盖、重定时或替换它；生成、Record、Retarget 与后续修正都必须追加新 Clip。该 JSON 是有界的 AI 可读索引，只记录 Session revision、轨道、动画、约束、Pose Cache marker、analysis 记录、视觉子图描述符与 generation history。稠密 motion 位于每个 analysis 的缓存文件；只在确实需要某个分析时才读取。它不是运行时查询 API。

实验性的 Retarget 核心与引擎无关，使用 KMB 动作以及两份显式 fullbody 参考姿势数据（source 和 target）。参考姿势可以是 bind、neutral、A-Pose 或 canonical pose，不要求 T-Pose。普通生成 fullbody locator 只有在同时包含全局关节位置、全局关节旋转、关节名称和父索引时，才足以作为 Retarget 参考。未映射的 target 关节默认继承已动画化父关节；只有显式指定 `freeze_global` 时才使用冻结全局姿势。缺失的 target 中间关节会共同分配已映射两端关节之间的相对旋转。可选的 target 手臂校准通过显式 upper/lower arm 对构造虚拟 T-Pose，经由该姿势 Retarget 后再 rebase 回 target 的真实参考姿势；它不修改角色资产，也不校准非手臂关节。

此版本只导入 Animator State Clip 和 BlendTree 候选 Clip。Transition materialization 与可创作 trajectory 命令仍待后续版本实现。`animation_analyze` 只渲染运动证据，不修改动画。
