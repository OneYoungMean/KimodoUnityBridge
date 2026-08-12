# 开发能力与 CLI 覆盖表

本文记录当前 package 能力与 CLI 覆盖范围，供功能开发和 API 补齐时核对。

当前版本：Unity package `3.0.0`，QuickServer `2.2.4`。

## 覆盖口径

- **完整**：`command_dispatcher` 可以完成该能力的主要工作流。
- **部分**：只覆盖能力的一部分，或仍需外围 Unity 工具完成关键步骤。
- **调试**：仅提供开发或诊断入口，不作为常规动画工作流。
- **未覆盖**：package 已有该能力，但 `command_dispatcher` 没有对应命令。
- **外部工具**：明确由项目现有的 Unity 自动化工具负责。

Editor CLI 的权威入口是 `Editor/Core/Manager/command_dispatcher.cs`。Runtime 的 `KimodoCliMotionRoutePlanner` 是组件 API，不计入 Editor command 覆盖率。

## 当前 Editor command

| 分组 | Commands |
| --- | --- |
| 发现 | `kimodo_help` |
| Runtime 诊断 | `kimodo_debug_install_server` |
| Session | `session_open`, `session_close`, `query_current_session`, `session_try_add`, `session_try_remove` |
| 生成与任务 | `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation` |
| 分析与图片 | `kimodo_analyze`, `query_picture` |
| Pose | `pose_create`, `pose_get`, `pose_set`, `pose_copy` |
| 录制、Retarget 与路径 | `kimodo_record_range`, `kimodo_retarget_animation`, `kimodo_build_root2d_path` |

合计：19 个 command。每次开发以 `GetCommandDefinitionsJson()` 和 `kimodo_help` 的实际返回为准。

## 能力覆盖矩阵

| 能力 | Package 当前能力 | CLI 入口 | 覆盖 | 开发备注 |
| --- | --- | --- | --- | --- |
| 命令与模型发现 | 返回全部 command schema、单命令 schema 和可用模型配置 | `kimodo_help` | 完整 | schema 是参数真相 |
| Package 安装 | 通过 UPM Git、embedded 或 `file:` 安装 | 分发 skill 修改 `manifest.json` | 部分 | 安装发生在 command 可用之前 |
| 项目级 QuickServer 准备 | 生成时按需准备、启动和连接项目运行时 | `kimodo_debug_install_server` | 调试 | 常规生成不要求显式安装命令 |
| Session 创建、加载与关闭 | 创建/加载可保留的 60 FPS Timeline Session | `session_open`, `session_close` | 完整 | 同时只维护一个 current Session，并复用一个不保存到场景的临时 Director |
| Session 状态查询 | 查询角色、动画、约束和 Animator transition | `query_current_session` | 完整 | 支持 7 种 query |
| 角色加入与移除 | 将场景 Humanoid Animator 加入/移出 Session | `session_try_add`, `session_try_remove` | 完整 | 使用安全名称或场景路径 |
| AnimationClip 加入与移除 | 将项目 Clip 加入角色轨道并维护虚拟时间地址 | `session_try_add`, `session_try_remove` | 完整 | 非 Muscle Clip 会尝试 Retarget |
| AnimatorController 导入 | 展开 State/Clip，并 Record 确定性 Clip-to-Clip transition | `session_try_add(kind:"animator")` | 部分 | BlendTree 分支选择和 Timeline 摆放由外部工具完成 |
| 单段动画生成 | 创建 `KimodoPlayableClip`、生成、写入资产并加入 Session | `kimodo_generate_animation` | 完整 | 支持模型、seed、steps、输出模式、约束和 analysis option |
| 生成进度、范围锁与取消 | 按 `request_id` 查询终态或取消任务；同角色 Track 的重叠采样/删除立即返回 `generation_range_locked` | `kimodo_get_generation`, `kimodo_cancel_generation` | 完整 | accepted/running 不算完成；不阻塞 Unity 主线程 |
| 多片段连接生成 | Timeline Inspector 可对同一轨道选中的多个 Clips 一次连接生成 | 无 | 未覆盖 | 没有多片段 command/schema |
| Pose 采样与编辑 | 通过 Constraint Retarget 管线读取 Timeline Pose，使用规范 Profile Root 创建/复制/更新 Root、Muscle、Foot IK | `pose_create`, `pose_get`, `pose_set`, `pose_copy` | 完整 | 可写 Pose 由 Session 管理；不使用旧 Unity Scene Root 语义 |
| FullBody Constraint | 从 Pose 构造完整人体约束 | `kimodo_generate_animation.constraints` | 完整 | 包含全身关节以及根骨骼位置与朝向；`frame` 是生成 Clip 内相对帧 |
| Root2D Constraint | 从 Pose 或直接 Position/Heading 构造根骨骼约束 | `kimodo_generate_animation.constraints` | 完整 | 只约束根骨骼地面平面位置与朝向；直接值为 `[x,z]` 与二维 heading |
| Hand/Foot Constraint | 从 Pose 或显式 Position/Rotation 构造左右手脚末端约束 | `kimodo_generate_animation.constraints` | 完整 | 显式末端位置先转换到 Profile Skeleton，再导出 `target_positions` |
| Hand/Foot IK 场景编辑 | Constraint Editor 用红立方体调整末端目标并实时更新 Pose | 无 | 未覆盖 | 当前是 Editor 交互能力 |
| 数学 Root2D 路径 | 生成 line、turn、s、circle 路径点 | `kimodo_build_root2d_path` | 完整 | 输出可直接转换为 Root2D constraints |
| Spline 与 NavMesh 路径创作 | Scene 编辑、Spline 采样、NavMesh 路点与脚步约束 | 无 | 未覆盖 | 当前由 Editor UI 和 Editor service 实现 |
| 动画分析 | 分析命名动画或半开 Session 帧区间并缓存 `analysis_id` | `kimodo_analyze` | 完整 | 支持 `analysis_option` |
| 四视图图片 | 渲染 Pose、Analysis 或 Constraints 的诊断拼图 | `query_picture` | 完整 | 返回图片结果用于视觉检查 |
| Range Record | 将 Session 半开区间录制为 AnimationClip | `kimodo_record_range` | 完整 | 支持速度、Root Motion 处理和输出目录 |
| 独立 Retarget | 将一个已加载动画转换到另一 current Session 角色 | `kimodo_retarget_animation` | 完整 | 源角色和目标角色都需要有效 Humanoid Avatar |
| 一般 Timeline 编排 | Clip 放置、重叠、Ease、Track/Binding 编辑 | 外围 Unity 自动化 | 外部工具 | Kimodo command 只维护自身 Session 工作流 |
| Runtime Motion Driver | 连续生成、提示词、Root2D、Hand/Foot Pose/Position 约束和实时播放 | 无 Editor command | 部分 | 公共 C# API 位于 `KimodoRuntimeMotionDriver`；Runtime 导出支持独立末端 `target_positions` |
| Runtime 路线调用 | 按世界坐标目标或 waypoint 队列驱动 Runtime Motion Driver | `KimodoCliMotionRoutePlanner.Animate/AnimateRoute` | 部分 | 这是组件 API，不是 `command_dispatcher` command |
| Runtime 发布安装 | 将运行时复制到 `StreamingAssets` | 无 | 未覆盖 | 当前入口为 Unity 菜单命令 |
| Server Manager | 项目运行时路径、状态、模型、缓存和维护操作 | `kimodo_help(section:"models")`、调试安装 | 部分 | 其余维护能力保留在 Project Settings UI |

## Constraint 数据结构

每个生成帧使用一个稀疏 Constraint 对象。对象至少包含生成 Clip 内的相对帧，以及一个约束字段：

```json
{
  "frame": 30,
  "fullbody": { "pose": { "source": "walk", "frame": 12 } },
  "root2d": { "position": [1.2, 0.4], "heading": [0.0, 1.0] },
  "left_hand": {
    "position": [0.3, 1.1, 0.5],
    "rotation": [0.0, 0.0, 0.0, 1.0]
  }
}
```

- `fullbody` 从 Pose locator 读取完整人体姿态，包括根骨骼位置与朝向。
- `root2d` 接受 Pose locator，或直接的 `position:[x,z]` 与 `heading:[x,z]`；它覆盖同帧 `fullbody` 的平面根骨骼。
- 左右手脚各自接受 `pose`、`position:[x,y,z]`、`rotation:[x,y,z,w]` 或其组合；显式位置/旋转覆盖自身 Pose 或同帧 `fullbody`。
- 坐标为 Unity 规范坐标：+X 向右、+Y 向上、+Z 向前；Quaternion 顺序为 `[x,y,z,w]`。
- 旧 `{frame,type,...}` union 不兼容，也不保留别名。

## 维护要求

1. 新增、删除或改名 command 时，同步更新 command 清单、矩阵和 `TOOLS.md` 中英文区。
2. 已有 package 能力获得 command 后，将状态改为“完整”或“部分”，并写明仍未覆盖的边界。
3. Runtime 公共 C# API 只有在注册到 `command_dispatcher` 后，才能记为 Editor CLI 覆盖。
4. 覆盖结论必须来自当前源码和可执行 schema；测试替身或计划不计为已覆盖。
5. Unity package 或 QuickServer 版本变化时，同步更新本文顶部的当前版本。
