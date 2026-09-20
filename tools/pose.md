---
name: kimodo-pose
description: Create and edit External Pose slots for explicit Humanoid pose constraints.
---

# Pose tool / Pose 工具

负责 `pose_get`、`pose_set` 以及按需拆分使用的 `pose_set_root_transform` 和 `pose_set_muscle`。

- 仅对当前场景上下文中明确指定的 Humanoid Clip 采样。
- 只复用运行时返回的 `{track,index}` Pose 引用。
- 编辑命令都创建派生 Pose，不覆盖已存在的 Pose。
- Pose 引用只有在用户明确要求约束时才传入 generation；不能把分析选帧自动当作约束。

`pose_set` 可在一次调用中同时更新 root 和 muscles，至少提供其中一项。输出至少包含新的 `{track,index}`、来源角色/Clip/帧，以及未验证项目。

`pose_set` 也支持 `effector` 对象，用于更新末端执行器目标。键名为 `left_hand`、`right_hand`、`left_foot`、`right_foot`，每个值至少包含一个 `position: [x,y,z]` 或 `rotation: [x,y,z,w]`；只提交的通道会覆盖原值，并标记对应的有效性。例如：

```json
{
  "pose": { "track": "Character Pose", "index": 12 },
  "effector": {
    "left_hand": { "position": [0.2, 1.1, 0.4] },
    "right_foot": { "rotation": [0, 0, 0, 1] }
  }
}
```
