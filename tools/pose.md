---
name: kimodo-pose
description: Create and edit External Pose slots for explicit Humanoid pose constraints.
---

# Pose tool / Pose 工具

负责 `pose_get`、`pose_set` 以及按需拆分使用的 `pose_set_root_transform` 和 `pose_set_muscle`。

`transform_capture` 接受 `pose_get` 返回的 `pose: {track,index}`。在 `transforms`
中使用特殊 key `@pose` 表示该 Pose 的角色，`@pose/<相对骨骼路径>` 表示该角色局部；
同一数组中加入桌子、苹果等场景层级路径即可在真实世界坐标下合拍。返回四视图
`image_path`、Pose 来源和 `evaluated_joints_world`，截图不会移动原角色，渲染可见性会恢复。
该命令不接受 `frames`；要检查动画指定帧，先 `pose_get`，再用返回的 Pose 引用截图。

```json
{
  "pose": { "track": "<pose_get 返回的 track>", "index": 0 },
  "transforms": ["@pose", "Desk", "Desk/Apple"],
  "resolution": 768
}
```

- 仅对当前场景上下文中明确指定的 Humanoid Clip 采样。
- 只复用运行时返回的 `{track,index}` Pose 引用。
- 编辑命令都创建派生 Pose，不覆盖已存在的 Pose。
- Pose 引用只有在用户明确要求约束时才传入 generation；不能把分析选帧自动当作约束。

`pose_get` 的 `source.timeline_frame_60` 是 Timeline 全局坐标下的命令帧，不是 Clip 局部帧；应直接复制 `animation_analyze` 返回的 `keyframes[*].timeline_frame_60`。底层会先换算为 Timeline 秒值，再按源动画自身帧率采样。

`pose_set` 可在一次调用中同时更新 root 和 muscles，至少提供其中一项。输出至少包含新的 `{track,index}`、来源角色/Clip/全局帧，以及未验证项目。

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
