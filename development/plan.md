# KimodoUnityBridge 文档与兼容性维护计划

## 目标

让维护文档只描述可验证的产品能力、命令边界、工作流和证据限制；命令参数集中维护在命令目录中。

## 维护依据

1. `Command/command_dispatcher.cs` 暴露的入口。
2. `GetCommandDefinitionsJson()` 与 `Command/help.json` 的命令定义。
3. `kimodo.help`、运行时返回值、状态和错误 envelope。
4. 当前 Unity 项目和包内样例资产的实际结果。

文档不应复制猜测性的完整参数结构，也不应把验证脚本或临时工具作为产品依赖。

## 文档职责

| 文档 | 职责 |
|---|---|
| `development/AGENTS.md` | 仓库协作规则、文档所有权和验证要求 |
| `../README.md` | 人类用户的安装与入门说明 |
| `../README.zh-CN.md` | `README.md` 的简体中文版本 |
| `../SKILL.md` | 用途、安装和文档导航 |
| `../Command/help.json` | 维护命令、参数、必选关系和嵌套 schema |
| `../tools/common.md` | 范围、任务分流、能力编排、通用循环、命令入口、异步状态、证据、识别、比较和修正决策 |
| `../archive/skill-legacy-20261013/` | 被新阶段手册替换的历史说明，仅供追溯 |
| `DEVELOPMENT.md` | 临时开发快照，不是执行契约 |
| `README.md` | 面向维护者的文档导航页 |

## 当前产品边界

- 自动场景上下文和 `animation.analysis` 支持可渲染 Mesh 的分析路径。
- 生成和 Pose 采样仍要求有效 Humanoid Avatar。
- 已完成 Clip 不可变；修正、Retarget 和生成都会追加派生 Clip。
- 静态图片不能单独证明播放连续性、滑步、跳变、加速度或速度连续性。
- Foot IK、Raycast 或其他外部工作流只有在公开命令和项目资产实际支持时才能写成已实现能力。

## 兼容性维护

- Constraint 公共命令按下方当前契约执行破坏性升级；旧输入不提供兼容别名。
- 保留旧 Spline 对象迁移逻辑，直到真实旧资产完成迁移回归。
- 保留 QuickServer 旧 `.venv` 检测与迁移，直到升级和回滚路径验证完成。
- 新代码优先使用 canonical 字段和实时 schema；兼容别名不得继续扩散。

## 暂缓 TODO：Analysis Picture 行为修复

- [ ] **Inspector 独立 Analysis 入口。** 在 `Advanced` 增加独立的完整 `animation.analysis` 选项；生成命令不再暴露轻量 `analysis_option`。生成完成后由共享 Editor 分析服务处理 Timeline 采样、缓存、Root Path 标记和 SceneView 图片。
- [ ] 让 `animation_analyze.picture.environment.mode` 真正控制画面环境，并验证 `tiles`、`output` 与返回的图片文件一致；当前这几个选项会被解析和回显，但实际渲染分支没有完整应用它们。
- [ ] 评估并移除 `BuildEffectiveAnalysisOptions` 对 `keyframe_count`、`keyframes.max_count`、`keyframes.enabled` 的兼容清理；破坏性升级后应由 schema 直接拒绝旧字段。
- [ ] 区分并保留仍有实际用途的 Timeline segment `role`、Marker `sourceRole` 与 analysis 输入字段，禁止全局关键字替换。
- [ ] 完成图片选项与 Analysis option 协议调整后，重新验证拾取苹果生成流程；在此之前，拾取苹果任务使用当前稳定的 generation 命令链，不依赖未完成的 Analysis 协议重构。

## 暂缓 TODO：Command 层收敛

- [ ] 核对多场景切换和 Domain Reload 下 `currentTimelineSession`、`firstTimelineSession` 与持久化 `is_current` 的归属，覆盖切换、重开和恢复流程；此项暂不改 Session 持久化结构。
- [ ] **Issue 1：会话重载恢复。** 单独设计显式 `session.rebind`，按 `playableAssetPath` 或 `session_id` 校验当前场景、角色 `object_ref`、轨道和片段引用后再恢复；同时修正 `session.clear` 的 `recreated_on_next_command` 契约。不要仅放宽自动恢复门槛，以免旧 Timeline 误绑到新场景。
- [ ] **Issue 3：跨片段 RootT.y 基线。** 先在同一 Avatar、Animator、`trackOffset`、`removeStartOffset` 和 `applyRootMotion` 条件下复现，再按 root-motion 语义决定有条件的垂直 rebase；站立片段与跳跃片段必须分开验收，暂不引入固定髋高。
- [x] **Issue 6：inout 幻影速度误报。** deferred Out 样本在速度校验副本中排除未重锚的 `rootOverride`，保留姿态数据和原始生成样本；即时真实根运动仍由 7 m/s 校验拦截。
- [ ] 为 QuickServer、连接超时、取消和输入错误定义稳定的错误码映射；当前执行器仍将未分类异常映射为 `invalid_argument`。
- [x] 已从 `animation.generate` 公开 schema 和 Editor generation request path 移除无读取者的轻量 `analysis_option`；ClipConstraint bake 继续使用内部 analysis-only KMB ClipConstraints。
- [x] 已让 schema 校验与 handler 共用一次 JSON 解析结果；字符串命令入口仍保留，内部测试的错误响应行为不变。
- [x] 已在 Runtime Constraints 内抽取不带隐式 clamp 的 Bézier 求值器，供 Command RootPath 与 Runtime path-angle 共用；切线计算仍留在 Command。

## Known issues：复合动作规划与姿势/约束选择

本节的详细记录已移到 skill 随附的 [`tools/known-issues.md`](../tools/known-issues.md)。它是执行时可参考的绕行信息，不是强制流程、硬性指标或停止条件；后续随执行循环、Pose test、command schema 和 Unity/API 集成更新统一修订。

## 当前 Constraint 契约

本节描述当前实现。历史设计、旧测试数量和已废弃实现保存在 `archive/constraint-upgrade~/`，不作为实现建议或行为依据。命令参数以实时 schema、`kimodo.help` 和 `Command/help.json` 为准。

- command 创建顺序为：采样完整 pose/root → 根据显式输入断言具体类型 → 覆盖对应数据。采样得到的支撑数据不参与类型推断。
- 活跃执行类型只有 Root2D、FullBody、Effector。Mix 是非法类型；没有同帧样本 composer 或按通道降级选型。

| 点约束输入 | 最终类型 |
|---|---|
| `root` | Root2D |
| `root + pose`、`pose` | FullBody |
| `root + effector` | Effector |
| `root + pose + effector`、`pose + effector` | IK 求解后的 FullBody |

- 点约束公开字段为 `frame`、`root`、`pose`、`effector`。拒绝旧 `root2d`、`effect` 和显式 `type` 输入；空约束与单独 `effector` 非法。
- `root` 与每个 effector 接收 World `position:[x,y,z]` 和欧拉角度 `rotation:[x,y,z]`；允许只给其中一个，省略通道保留采样值，空 transform 非法。完整 root 映射到 UI 数据，由底层决定各类型的求解/导出行为。
- command 拒绝重复帧；`root_path`、`inout` 仍是独立输入形式。不在 Timeline/runtime 增加全局重复帧拒绝。
- exporter 保留各样本的具体类型。用户确认保留 `KimodoConstraintProtocolNormalizer` 在最终协议序列化时将同帧 Root2D 折入 FullBody/Effector 的行为；不要把这一协议步骤当作 command 类型推断或恢复旧 composer 的理由。
- `sampleValid` 校验整份样本；四个 effector enabled 位和 `rootHeading` 表达当前意图。旧序列化 mask 字段只用于读取旧资产，不用于推断类型或逐通道 validity fallback。
- root 编辑不 carry effector；不存在 carry 开关。Mix 拒绝测试保留为防止旧类型重新进入活动路径的回归检查。
- 旧源码、失效测试与过期计划移入 archive；归档不参与编译或当前行为验收。

## 验证要求

1. 静态检查文档中的命令名、路径和旧术语。
2. 在维护的 Unity 项目中完成最小导入/编译检查。
3. 按需验证场景上下文、分析图片、Pose/Path、生成终态和派生 Clip 不可变性。
4. 分开报告静态、运行时、图像、场景和播放证据。
5. 只有实际打开返回的视觉证据后，才能报告视觉通过；证据不足时使用 `not_verified`、`needs_revision` 或 `unsupported`。

## 完成标准

- 产品仓库不引用包外的验证夹具、评分键或监督路径。
- 文档、实时 schema 和运行时返回值对同一能力的描述一致。
- 不把兼容层或实验性功能描述成稳定产品能力。
- 每个视觉结论都能追溯到实际打开的返回证据。

## 当前验证记录

- Unity 6000.5.7f1 导入/编译通过；schema、六类合法输入、部分 transform 覆盖、旧输入拒绝、IK FullBody、marker 序列化和协议折叠均已做定向断言。
- 最近全范围定向断言仍有三项现存失败：两项旧 CaptureWorldTargets validity 断言，以及 PoseGet fixture 的 Timeline Animator 绑定。它们不作为当前 constraint 类型契约。
- 尚未执行本次升级的后端生成或播放验证；不要引用归档中的旧项目/旧版本测试数量作为本次通过证据。
- Inspector 专用分析按钮、预计算图片桥接及反射服务已移入 `archive/inspector-analysis-legacy~`；活动分析入口为 Command 层的 `animation.analysis` 与 `pose.analysis`。

## 后续 TODO（暂缓至项目稳定后）

- 审查 `KimodoBridgeService.EnsureConnectedAsync` 的异步线程边界：它在 `ConfigureAwait(false)` 后可能继续执行 Editor 注入的 RuntimeRoot/Settings 委托。确认并发等待时是否会离开 Unity 主线程，再决定如何将 Unity Editor API 调用切回主线程。
- 线程边界检查属于 Editor 主线程敏感改动，项目稳定前只记录，不在当前阶段改实现。

## 代码清理待办（暂缓，由后续处理）

- [x] 已将不可达的非 ARDY ClipConstraint mask-bake 分支及其专属合并/分析代码归档到 `archive/non-ardy-clip-constraint-bake-20261031/removed-code.txt`。保留 ARDY In 边界 `BuildBegin`、通用 ClipConstraint 协议与共享约束 JSON 工具；generation 轻量分析已移除，因此合并运动分析没有其他活动调用者。
- [ ] 检查 `KimodoHandEffectorGoalTests` 中 AnimationHumanStream goal 与 Avatar post rotation 的 180° 偏差；确认是测试公式、Unity 版本差异还是运行时手部约束方向错误。
- [ ] 将 FullDemo `5.RuntimeArdyDemo.unity` 中仍序列化到 `targetHumanoidAnimator` 的 8 个目标迁入 `targetHumanoidAnimators`，然后验证 ARDY 场景能启动；当前代码只读取新列表。
- [ ] 为运行时生成失败定义退避或阻塞策略，避免 QuickServer 不可用或请求持续失败时每帧重试并重复写日志。
- [ ] 调整 `SegmentReady` 通知与生成状态提交顺序，避免订阅者异常导致已接受的段播放、但段号和约束 revision 未提交。
- [ ] 收敛 Runtime Motion Driver 的 Stop/Reset 状态重置，并确保 `ResetMotionAsync` 在异常路径也解除 `GenerationBlocked`。
- [ ] 更新或归档 `AnalysisPictureSpacingTests` 中反射调用的旧截图间距 helper；当前测试名仍对应已不存在的私有实现。
