# Generation

Use this skill when creating a new appended Session Clip.

## Name to prompt

```text
[start state] + [main action] + [phase] + [path/direction]
+ [speed/energy] + [body/contact] + [ending or loop condition]
```

Preserve preparation → main action → recovery/end. Treat dataset labels as semantic hints. Remove take/actor/mirror/internal variant metadata, keep verified action tokens and numerical headings, and do not expand unknown abbreviations.

Examples:

```text
walk_ff_225_stop
→ Walk forward, turn toward 225 degrees, decelerate, and stop in a balanced upright pose.
jog_arc_cw_loop
→ Jog continuously along a clockwise arc in a seamless locomotion loop.
```

## Current workflow

1. After Unity is ready, install/refresh the runtime once, then read `GetCommandDefinitionsJson()` and `kimodo_help({})`.
2. Select/create a Session with `session_get_or_create`; add the scene character explicitly with `session_add(kind:"character")` and save the returned safe name.
3. Materialize endpoint/key poses with `pose_get` when the request has a concrete start/end/contact requirement. Use `pose_contract` only when end-effector alignment is actually needed.
4. Prepare only the sparse constraints required by the request. Use `fullbody` for a complete Pose, `root2d` for planar root position/heading, and the hand/foot fields for individual contacts. Use `pose_create_path` for a reusable root trajectory and pass its `{track,index}` as `root_path.path`.
5. Start `kimodo_generate_animation`, preserve `request_id`, and poll `kimodo_get_generation` to a terminal state. Record model, encoder, output mode, seed, warnings, and generated safe name.
6. Hand the completed Clip to [Optimization](optimization.md): analyze it, open the PNG, and append a revised Clip when evidence fails.

## Constraint rules

- Each point-constraint item has a non-negative generated-clip `frame` and at least one of `fullbody`, `root2d`, `left_hand`, `right_hand`, `left_foot`, or `right_foot`; multiple fields may coexist in one item.
- A `root_path` item may set its first frame (default 0), cannot mix with point fields or overlap another root-path frame range, and expands over the remaining generated Clip.
- `root2d` accepts a Pose reference or direct `[x,z]` position plus `[x,z]` heading. At the same frame, explicit `root2d` overrides the path; `fullbody` supplies the base pose and hand/foot fields override matching effectors.
- Use `kimodo_help({"section":"constraints"})` for exact shape. Do not invent undocumented fields.

## Output modes and failure boundaries

- All current generation modes require the selected Session character to be a valid scene Humanoid Animator with a valid Avatar.
- `model_bone` changes the output representation; it does not make Mesh-only generation available.
- Registered `model` and `text_encoder_model` values come from `kimodo_help({"section":"models"})`.
- `loop:true` is a bounded request. When `duration_frames` exceeds 300, current implementation falls back to normal generation and returns a warning; report the warning.
- A failed or canceled request is not a generated Clip. Report the terminal status and error, and do not claim visual acceptance.

## 中文

当任务是创建新的追加 Session Clip 时使用本文件。

### 名称转 Prompt

按“起始状态 → 主动作 → 阶段 → 路径/方向 → 速度/能量 → 身体/接触 → 结束或循环条件”改写。去掉 take/演员/镜像/内部变体元数据，保留确认过的动作 token 和数值角度，不猜测未知缩写。

### 当前流程

1. Unity 就绪后先安装/刷新一次 runtime，再读取 `GetCommandDefinitionsJson()` 和 `kimodo_help({})`。
2. 用 `session_get_or_create` 选择/创建 Session，用 `session_add(kind:"character")` 显式加入场景角色，原样保存返回的安全名称。
3. 有明确起始/结束/接触要求时用 `pose_get` 实体化关键 Pose；只有确实需要末端对齐时才用 `pose_contract`。
4. 只准备请求所需的稀疏约束：`fullbody` 为完整 Pose，`root2d` 为平面 Root 位置/朝向，手脚字段为单独接触；用 `pose_create_path` 创建可复用 Root 轨迹，并将返回 `{track,index}` 作为 `root_path.path`。
5. 调用 `kimodo_generate_animation`，保存 `request_id`，轮询 `kimodo_get_generation` 到终态，并记录模型、编码器、输出模式、seed、警告和生成的安全名称。
6. 将完成的 Clip 交给 [优化](optimization.md)：分析、打开 PNG，证据失败时追加修正版。

### 约束规则

- 每个点约束项有非负的生成 Clip 局部 `frame`，并至少包含 `fullbody`、`root2d`、`left_hand`、`right_hand`、`left_foot`、`right_foot` 之一；同一项可以组合多个字段。
- `root_path` 项可指定首帧（默认 0），不能和点字段混用或和另一个 root-path 帧区间重叠，并扩展到生成 Clip 剩余部分。
- `root2d` 可用 Pose 引用，或直接使用 `[x,z]` 位置与 `[x,z]` 朝向。同帧显式 `root2d` 优先于 Path；`fullbody` 提供基础姿势，手脚字段覆盖对应末端。
- 准确结构以 `kimodo_help({"section":"constraints"})` 为准，不自行发明字段。

### 输出模式与失败边界

- 当前所有生成模式都要求所选 Session 角色是带有效 Avatar 的场景 Humanoid Animator。
- `model_bone` 只改变输出表示，不会使 Mesh-only 生成可用。
- 注册模型和 `text_encoder_model` 以 `kimodo_help({"section":"models"})` 为准。
- `loop:true` 是有边界的请求；当前 `duration_frames` 超过 300 时会回退到普通生成并返回 warning，必须报告警告。
- 失败或取消的请求不算生成了 Clip；应报告终态和错误，不能声称视觉通过。
