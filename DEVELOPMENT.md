# 开发能力与 CLI 覆盖表

本文记录当前 package 能力与 CLI 覆盖范围，供功能开发和 API 补齐时核对。

当前版本：Unity package `0.1.0`，QuickServer `2.2.5`。

## 覆盖口径

- **完整**：`command_dispatcher` 可以完成该能力的主要工作流。
- **部分**：只覆盖能力的一部分，或仍需外围 Unity 工具完成关键步骤。
- **调试**：仅提供开发或诊断入口，不作为常规动画工作流。
- **未覆盖**：package 已有该能力，但 `command_dispatcher` 没有对应命令。
- **外部工具**：明确由项目现有的 Unity 自动化工具负责。

Editor CLI 的权威入口是 `Editor/Core/Manager/command_dispatcher.cs`。未注册到该入口的 Runtime 或 Editor 实现不计入 CLI 能力。

## 当前 Editor command

| 分组 | Commands |
| --- | --- |
| 发现 | `kimodo_help` |
| Runtime 诊断 | `kimodo_debug_install_server` |
| Session | `session_open`, `session_close`, `query_current_session`, `session_try_add`, `session_try_remove` |
| 生成与任务 | `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation` |
| 分析与图片 | `kimodo_analyze`, `query_picture` |
| Pose | `pose_create`, `pose_get`, `pose_set`, `pose_copy` |
| Record、Bake、Retarget 与路径 | `kimodo_record_range`, `kimodo_bake_range`, `kimodo_retarget_animation`, `kimodo_build_root2d_path` |

合计：18 个 command。每次开发以 `GetCommandDefinitionsJson()` 和 `kimodo_help` 的实际返回为准。

## 能力覆盖矩阵

| 能力 | Package 当前能力 | CLI 入口 | 覆盖 | 开发备注 |
| --- | --- | --- | --- | --- |
| 命令、路由与模型发现 | 返回 command schema、按意图路由、句柄流向、约束语义和可用模型配置 | `kimodo_help` | 完整 | schema 是参数真相；新 Session 明确为空 |
| Package 安装 | 通过 UPM Git、embedded 或 `file:` 安装 | 分发 skill 修改 `manifest.json` | 部分 | 安装发生在 command 可用之前 |
| 项目级 QuickServer 准备 | 生成时按需准备、启动和连接项目运行时 | `kimodo_debug_install_server` | 调试 | 常规生成不要求显式安装命令 |
| Session 创建、加载与关闭 | 创建/加载可保留的 60 FPS Timeline Session | `session_open`, `session_close` | 完整 | 同时只维护一个 current Session |
| Session 状态查询 | 查询角色、动画、约束和 Animator transition | `query_current_session` | 完整 | 支持 7 种 query |
| 角色加入与移除 | 将场景 Humanoid Animator 加入/移出 Session | `session_try_add`, `session_try_remove` | 完整 | 使用安全名称或场景路径 |
| AnimationClip 加入与移除 | 将项目 Clip 加入角色轨道并维护虚拟时间地址 | `session_try_add`, `session_try_remove` | 完整 | 非 Muscle Clip 会尝试 Retarget |
| AnimatorController 导入 | 展开 State/Clip，并 Bake 确定性 Clip-to-Clip transition | `session_try_add(kind:"animator")` | 部分 | BlendTree 分支选择和 Timeline 摆放由外部工具完成 |
| 单段动画生成 | 创建 `KimodoPlayableClip`、生成、写入资产并加入 Session | `kimodo_generate_animation` | 完整 | 支持模型、seed、steps、输出模式、约束和 analysis option |
| 生成进度、范围锁与取消 | 按 `request_id` 查询终态或取消任务；同角色 Track 的重叠采样/删除立即返回 `generation_range_locked` | `kimodo_get_generation`, `kimodo_cancel_generation` | 完整 | accepted/running 不算完成；不阻塞 Unity 主线程 |
| 多片段连接生成 | Timeline Inspector 可对同一轨道选中的多个 Clips 一次连接生成 | 无 | 未覆盖 | 没有多片段 command/schema |
| Pose 采样与编辑 | 原生读取 MuscleClip 同义的 49 Muscle + Root/Hand/Foot TQ，并支持局部 Patch | `pose_create`, `pose_get`, `pose_set`, `pose_copy` | 完整 | 可写 Pose 由 Session 管理；四元数为 `[x,y,z,w]`，位移单位米，Muscle 不 Clamp |
| Unified Constraint（mask） | 一个 Marker 保存 CharacterPose、类型、时间、Root heading 开关和通道 mask；导出前按 Muscle→Foot IK→Hand IK→Root2D 合成 | `kimodo_generate_animation.constraints`（同帧 sparse 对象；旧 flat union 兼容解析） | 完整 | 新建只有一种 Constraint；QuickServer 仍接收原协议 DTO |
| FullBody / Root2D / Hand/Foot 协议桥接 | 从同一帧最终骨架分别投影为既有协议记录；EndEffector 保留 FK/root 上下文并发送 `target_positions` | `kimodo_generate_animation.constraints` | 完整 | 同帧记录共享 root/FK，协议不变 |
| Constraint 场景编辑 | Inspector 显示 mask 与 Root/Hand/Foot TQ；Override Edit 显示 49 Muscle 所依赖的去重 Humanoid 骨骼本地 Euler；Root2D 拖拽只回写 canonical root，避免重复根变换 | 无 | 部分 | Scene 与 Override Euler 编辑均经 transient Avatar 反算回 Muscle；旧 Marker 资产继续兼容 |
| Loop generation 预处理 | `kimodo_generate_animation(loop:true)` | 部分 | Unity 层扩展约束并生成闭环前后 Bezier Root2D；超出 600 帧回退默认流程 |
| 数学 Root2D 路径 | 生成 line、turn、s、circle 路径点 | `kimodo_build_root2d_path` | 完整 | 输出可直接转换为 Root2D constraints |
| Spline 路径创作 | Scene 编辑与 Spline 采样 | 无 | 未覆盖 | 当前仅为 Editor 交互能力；CLI 使用数学 Root2D 路径 |
| 动画分析 | 分析命名动画或半开 Session 帧区间并缓存 `analysis_id` | `kimodo_analyze` | 完整 | 支持 `analysis_option` |
| 四视图图片 | 渲染 Pose、Analysis 或 Constraints 的诊断拼图 | `query_picture` | 完整 | 返回图片结果用于视觉检查 |
| Range Record | 将 Session 半开区间录制为 AnimationClip | `kimodo_record_range` | 完整 | 支持速度、Root Motion 处理和输出目录 |
| Range Bake（兼容） | 将 Session 半开区间 Bake 为 AnimationClip | `kimodo_bake_range` | 完整 | 保留兼容入口；支持 Retarget 参数 |
| 独立 Retarget | 将已加载动画转换到另一 current Session 角色 | `kimodo_retarget_animation` | 完整 | 需要有效 Humanoid Avatar |
| Bake Retarget | Bake 时重定向到另一 current Session 角色 | `kimodo_bake_range(retarget_character)` | 完整 | 需要有效 Humanoid Avatar |
| 任意 Clip 独立 Retarget | Editor Retarget 工具可进行 Bone/Muscle 转换和写回 | 无 | 部分 | CLI 仅覆盖 Bake 内的 Retarget |
| 一般 Timeline 编排 | Clip 放置、重叠、Ease、Track/Binding 编辑 | 外围 Unity 自动化 | 外部工具 | Kimodo command 只维护自身 Session 工作流 |
| Runtime Motion Driver | 连续生成、提示词、Root2D、Hand/Foot Pose 约束和实时播放 | 无 Editor command | 部分 | 公共 C# API 位于 `KimodoRuntimeMotionDriver`；Root2D 到达阈值内不再暂存约束；末端约束导出时携带所在 canonical FK/root 与 `target_positions` |
| Runtime 发布安装 | 将运行时复制到 `StreamingAssets` | 无 | 未覆盖 | 当前入口为 Unity 菜单命令 |
| Server Manager | 项目运行时路径、状态、模型、缓存和维护操作 | `kimodo_help(section:"models")`、调试安装 | 部分 | 其余维护能力保留在 Project Settings UI |

## Constraint 数据结构

外部 command 使用同帧 sparse 对象，旧 flat union 仍兼容解析；Timeline 内部统一为一个 Constraint Marker：

```json
{
  "characterPose": "<canonical pose>",
  "constraintType": "constraint",
  "sampleTime": 0.0,
  "hasRootHeading": true,
  "mask": { "muscle": true, "rootPosition": true, "leftHand": false }
}
```

- mask 控制 Muscle、Root position/heading、左右 HandTQ 和 FootTQ 通道；同一 Constraint 可同时启用多个通道。
- 求解顺序固定为 Muscle → Foot IK → Hand IK → Root2D（最后整体移动/旋转已完成的 FK/IK 骨架）。
- 导出边界再投影成既有 `fullbody`、`root2d` 与手脚协议记录；EndEffector 单独发送 `target_positions`，但复用同一帧最终 FK/root；QuickServer 协议本身不变。
- `humanScale` 是 HumanPose 归一化坐标与协议米单位之间的内部投影元数据，不是可创作通道。

## 维护要求

1. 新增、删除或改名 command 时，同步更新 command 清单、矩阵和 `TOOLS.md` 中英文区。
2. 已有 package 能力获得 command 后，将状态改为“完整”或“部分”，并写明仍未覆盖的边界。
3. Runtime 公共 C# API 只有在注册到 `command_dispatcher` 后，才能记为 Editor CLI 覆盖。
4. 覆盖结论必须来自当前源码和可执行 schema；测试替身或计划不计为已覆盖。
5. Unity package 或 QuickServer 版本变化时，同步更新本文顶部的当前版本。
