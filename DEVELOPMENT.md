# 开发能力与命令覆盖

当前版本：Unity package `0.1.0`，QuickServer `2.2.7`。

`Command/command_dispatcher.cs` 是唯一公开 command 入口；`GetCommandDefinitionsJson()` 和 `kimodo_help` 是参数真相。

## vNext command surface

| 分组 | Commands |
| --- | --- |
| 发现与运行时维护 | `kimodo_help`、`kimodo_install_server` |
| Session | `session_get_or_create`、`session_add`、`session_close` |
| 生成任务 | `kimodo_generate_animation`、`kimodo_get_generation`、`kimodo_cancel_generation` |
| 分析 | `animation_analyze`、`animation_compare` |
| Pose | `pose_get`、`pose_contract`、`pose_set_root_transform`、`pose_set_muscle` |
| 资产 | `kimodo_record_range`、`kimodo_retarget_animation` |

合计：16 个 command。旧命令名、旧别名、旧 schema 和旧测试不保留兼容入口。

## 能力矩阵

| 能力 | Command 覆盖 | 状态 | 开发说明 |
| --- | --- | --- | --- |
| Schema、模型与约束发现 | `kimodo_help` | 完整 | 命令参数以实时 schema 为准。 |
| 项目级 QuickServer 修复 | `kimodo_install_server` | 调试 | 仅用于用户明确要求或启动/安装诊断。 |
| 稳定 Session 生命周期 | `session_get_or_create`、`session_add`、`session_close` | 完整 | 名称大小写不敏感；新 Session 为空；切换或关闭会取消本 Session 的生成任务。 |
| Session JSON | 所有 Session 变更命令 | 完整 | `Assets/KimodoGeneratedClips/Sessions/<safe name>/session.json` 原子更新；记录稀疏 analysis 索引和图片子图描述符，详细 KMB 按路径读取。每个已完成并写入 Session 的 Clip 不可变，后续结果只能追加新 Clip。 |
| 单段生成与任务查询 | `kimodo_generate_animation`、`kimodo_get_generation`、`kimodo_cancel_generation` | 完整 | 生成异步，使用 `request_id` 轮询。 |
| 动画分析与图片证据 | `animation_analyze` | 完整 | 底层同步调用 `KimodoPlayableClipGenerationExecutionService.Analysis(...)`；接受一或两个显式角色 Clip，返回按 saliency 降序的关键帧、按持续帧数升序的 `foot_contacts`，以及 low/middle/high 的一张组合 PNG 和自描述子图。额外的 `-test` 仅用于渲染验证：每角色输出正交 ghost-3D 与骨盆轨迹两张 512×512 图。渲染不修改 Clip；不返回质量、loop seam 或 trajectory 指标。 |
| 动画比较 | `animation_compare` | 部分 | 目前比较根、姿势和末端差异；详细接触与轨迹指标待后端实现。 |
| Pose Cache 与拟合 | `pose_get`、`pose_contract`、`pose_set_root_transform`、`pose_set_muscle` | 完整 | marker ID 为 Session、角色、来源和帧的 SHA-256；`full_data` 返回 49 muscles 和 T/Q。 |
| 生成约束 | `kimodo_generate_animation.constraints` | 完整 | 同帧 sparse fullbody/root2d/hand/foot 组合；内部 clip in/out 约束不属于 command 改造范围。 |
| Record 与 Retarget | `kimodo_record_range`、`kimodo_retarget_animation` | 完整 | 使用 Session 角色与返回安全动画名。 |
| 引擎无关 KMB Retarget Core | 尚无公开 command | 实验性 | 使用 source/target 两份 fullbody reference pose 和 KMB 动作；不依赖 Unity、Cocos、Godot 或 Blender。未映射 target 默认继承父关节，缺失中间链分配端点相对旋转；`freeze_global` 为显式回退。可选 target 手臂 A→虚拟 T 校准只处理上下臂及其子树，随后 rebase 回真实 A/bind reference。 |
| Animator 导入 | `session_add(kind:"animator")` | 完整（有限范围） | 导入 State/BlendTree 叶子，并将同 Layer State→State 过渡组合为 Timeline `transition_clip`；无 Exit Time 为四个源代表性离开帧变体，超过 128 默认 warning。Any State、Entry、Exit、StateMachine、OverrideController 不物化。 |
| Transition / authored trajectory | `session_add(kind:"animator")` | Transition 已实现，trajectory 未实现 | Transition 不生成新的 AnimationClip 资产；旧 TransitionClip 在重导入时保留，新导入批次追加新记录。 |

## 维护规则

1. command 的新增、删除或改名必须同一变更同步 `TOOLS.md` 的中英文区域、`SKILL.md`、`SKILL-zh.md` 和本文件。
2. QuickServer 代码、测试或文档变更每个完整变更集只增加一次 patch 版本，并同步 `TOOLS.md` 和本文件的版本行。
3. Unity 验证应区分 command 程序集结果与不相关测试程序集的既有错误；后端修改至少运行最小相关单元测试。
