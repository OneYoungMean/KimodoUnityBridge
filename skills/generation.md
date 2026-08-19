# Generation

Use this when creating a new appended Session Clip.

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

## Current generation workflow

1. `kimodo_help({})`, `session_get_or_create`, then `session_add(kind:"character")` if needed.
2. Use `pose_get` to sample endpoint/key poses and create Pose Cache markers. Use `pose_contract` for end-effector alignment when appropriate.
3. Prepare sparse `fullbody`, `root2d`, and pose-based hand/foot constraints according to the live constraints help. This project has no separate Root2D path command.
4. Call `kimodo_generate_animation`, preserve `request_id`, and poll to a terminal status.
5. Pass the completed Clip to [Optimization](optimization.md): run `animation_analyze`, open the returned PNG, and append a revised Clip if needed.

## 中文

用于创建新的 Session Clip。按“起始状态 → 主动作 → 阶段 → 路径/方向 → 速度/能量 → 身体/接触 → 结束或循环条件”将名称改写为英文 Prompt；去掉 take/演员/镜像/内部变体元数据，保留确认过的动作 token 和数值角度，不猜测未知缩写。当前流程为：`kimodo_help` → `session_get_or_create` → `session_add` → 用 `pose_get` 采样并按需 `pose_contract` → 准备 sparse fullbody/root2d/手脚 Pose 约束 → `kimodo_generate_animation` → 轮询终态 → 转到优化流程分析、打开 PNG 并追加修正版。该项目没有独立 Root2D path command。
