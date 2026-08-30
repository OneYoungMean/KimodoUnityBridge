---
name: kimodo-pose
description: Create and edit External Pose slots for explicit Humanoid pose constraints.
---

# Pose tool / Pose 工具

负责 `pose_get`、`pose_contract`、`pose_set_root_transform` 和 `pose_set_muscle`。

- 仅对当前 Session 中明确指定的 Humanoid Clip 采样。
- 只复用运行时返回的 `{track,index}` Pose 引用。
- 编辑命令都创建派生 Pose，不覆盖已存在的 Pose。
- Pose 引用只有在用户明确要求约束时才传入 generation；不能把分析选帧自动当作约束。

输出至少包含新的 `{track,index}`、来源角色/Clip/帧，以及未验证项目。
