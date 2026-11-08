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

- [x] 已完成：生成后的完整分析走共享 Editor 分析服务，独立结果仍由 `animation.analysis` 提供；生成请求中无读取者的轻量 `analysis_option` 已移除，Inspector 的独立旧入口已归档。
- [ ] 让 `animation_analyze.picture.environment.mode` 真正控制画面环境，并验证 `tiles`、`output` 与返回的图片文件一致；当前这几个选项会被解析和回显，但实际渲染分支没有完整应用它们。
- [x] 已确认 `BuildEffectiveAnalysisOptions` 和上述旧输入字段处理已不在当前实现中；`animation.analysis` 顶层及 Clip 输入都由严格 schema 拒绝未声明字段。
- [ ] 区分并保留仍有实际用途的 Timeline segment `role`、Marker `sourceRole` 与 analysis 输入字段，禁止全局关键字替换。
- [ ] 完成图片环境、tiles 和 output 语义调整后，重新验证拾取苹果生成流程；generation 不再依赖已移除的 `analysis_option`。

## 暂缓 TODO：Command 层收敛

- [x] **Session Context 来源与 JSON 边界（3.0）。** `session.context` 每次从当前场景和 Timeline 构建轻量投影，不读取或写入 `session.json`，也不保留静态 Session 快照；`details` 组按需生成。Session 根只包含直接挂载的 `PlayableDirector` 和 Awake 运行时停用脚本，Play Mode 退出不执行恢复逻辑。
- [x] **Session 历史与 Context 分离（3.0）。** `session.history` 使用每个 Timeline GUID 对应的 `history.jsonl`，只追加动画生成、Retarget 和分析命令及异步终态；Context 不保存历史快照，旁路生成/分析产物仍按 Session 目录保留。`session.clear` 保留 Timeline/动画资产，后续命令按需创建新的场景根。
- [x] **过期恢复设计已关闭。** 不新增 `scene.context` 别名、`session.rebind`、静态注册表或 JSON 选择/恢复路径；`session.context` 的 `timeline_path` 只负责用户明确请求的 Timeline 打开/替换。
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
- `root` 与 `effector` 数组中的每个 `{joint, position, rotation}` 项接收 World `position:[x,y,z]` 和欧拉角度 `rotation:[x,y,z]`；允许只给其中一个，省略通道保留采样值，空 transform 非法。完整 root 映射到 UI 数据，由底层决定各类型的求解/导出行为。
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

- Command 清理收尾（2026-11-02，Unity 2022.3.62f3c1）：Command Editor 与 Editor.Tests 编译通过；7 个不同的定向 EditMode 用例最终均通过，包含 schema/help、loop window、anchor、Root2D helper、keyframe 和 Request lifecycle。`AnimationAnalysis_ArcWalkFixture_WritesCompositePng` 在 `-nographics` 下因无法创建 SceneView 首次失败，随后在图形模式 1/1 通过。`git diff --check` 与活动代码旧符号检查通过；未执行完整回归或生成后播放。
- 2026-11-01 FullDemo 定向回归（Unity 2022.3.62f3c1）：Command/InOut 单独组 37/37 通过；扩展的 Window、Provider、Generation 与 Command 组 114/117 通过。3 项失败均为 `KimodoConstraintNormalizationUtilityTests.ConstraintInitialization_UsesTimelinePoseForPreviewAndExport` 的 Auto Sample pose 对比；它们不在本次 Window/Transport/Command 改动路径中，且本次未对基线复跑确认。
- 同一 FullDemo 的 `dotnet build KimodoTool.Command.Editor.csproj --no-restore` 通过，0 警告、0 错误。
- 本批 Command 收尾验证（2026-11-01，Unity 2022.3.62f3c1）：FullDemo `KimodoTool.Command.Editor` 编译通过（0 警告、0 错误）；Command API 合同、In/Out 和 Session 生命周期 EditMode 定向组 53/53 通过。
- Command 信息与接口冗余收敛（2026-11-01，Unity 2022.3.62f3c1）：`GenerationCommands_ShareRequestIdLifecycleContract`、`OtherCommands_AreDiscoverableAsIndependentContracts`、`PackagedHelpJson_MatchesLiveOutputSchemas`、`AnalysisKeyframes_UseOnlyAbsoluteTimelineFrameAndNormalizeIdempotently` 定向 EditMode 4/4 通过；`Command/help.json` 已由实时 schema 导出，PowerShell JSON 解析和 `git diff --check` 通过。未跑全量回归或视觉审阅。
- generation 状态结构收敛（2026-11-01，Unity 2022.3.62f3c1）：FullDemo `KimodoTool.Command.Editor` 编译通过（0 警告、0 错误）；`AsyncResponseNormalization_RemovesEchoAndUsesOneLifecycleShape` 定向 EditMode 1/1 通过，覆盖旧扁平持久化状态转换及规范 `clip` 对象直通；`git diff --check` 通过。未执行生成后播放验证。
- 历史 Context/session.json 索引验证（2026-11-01）：该验证针对 3.0 之前的实现，仅保留作为历史记录；3.0 不再生成或读取 `session.json`。
- Context/Session 清理回归（2026-11-01，Unity 2022.3.62f3c1）：Command Editor `dotnet build --no-restore` 通过（0 警告、0 错误）；Editor.Tests 编译通过（0 警告、0 错误）；`CommandSessionLifecycleTests`、`CommandApiGroupContractTests` 与 `CommandInOutTests` 定向 EditMode 53/53 通过，新增覆盖 Timeline 投影刷新；`AnimationAnalysisCommandIntegrationTests` 6/6 通过（含 ArcWalk SceneView/合成图）。`Command/help.json` JSON 解析与 `git diff --check` 通过。未执行全量回归、生成后播放或人工视觉审阅。
- Session Context 3.0 实现验证（2026-11-07）：FullDemo `KimodoTool.Command.Editor` 与 `KimodoTool.Editor.Tests` 构建均 0 警告、0 错误；Unity EditMode batchmode 已启动但未产出 TestRunner XML，因此不能视为 EditMode 通过。`Command/help.json` JSON 解析和包仓库 `git diff --check` 通过。
- Unity 6000.5.7f1 导入/编译通过；schema、六类合法输入、部分 transform 覆盖、旧输入拒绝、IK FullBody、marker 序列化和协议折叠均已做定向断言。
- 更早的全范围定向断言记录了两项旧 CaptureWorldTargets validity 断言和一项 PoseGet fixture Timeline Animator 绑定失败；这些记录与上面的本轮结果分开看待。
- 尚未执行本次升级的后端生成或播放验证；不要引用归档中的旧项目/旧版本测试数量作为本次通过证据。
- Inspector 专用分析按钮、预计算图片桥接及反射服务已移入 `archive/inspector-analysis-legacy~`；活动分析入口为 Command 层的 `animation.analysis` 与 `pose.analysis`。

## 后续 TODO（暂缓至项目稳定后）

- 审查 `KimodoBridgeService.EnsureConnectedAsync` 的异步线程边界：它在 `ConfigureAwait(false)` 后可能继续执行 Editor 注入的 RuntimeRoot/Settings 委托。确认并发等待时是否会离开 Unity 主线程，再决定如何将 Unity Editor API 调用切回主线程。
- 线程边界检查属于 Editor 主线程敏感改动，项目稳定前只记录，不在当前阶段改实现。

## 代码清理待办（暂缓，由后续处理）

- [x] 已将不可达的非 ARDY ClipConstraint mask-bake 分支及其专属合并/分析代码归档到 `archive/non-ardy-clip-constraint-bake-20261031/removed-code.txt`。保留 ARDY In 边界 `BuildBegin`、通用 ClipConstraint 协议与共享约束 JSON 工具；generation 轻量分析已移除，因此合并运动分析没有其他活动调用者。
- [ ] **Runtime Effector 约束尚未接通。** 四个 `Set*Constraint` Runtime API 继续作为空兼容壳；若恢复 Effector 能力，先定义同帧 pose、root 与 effectors 的规范样本，再只执行一次 Avatar retarget，并复用现有 `KimodoConstraintPosePipeline` 和 `KimodoConstraintJsonExporter` 进入 generation request。禁止各 API 各自拼协议、重复 retarget，或恢复旧的分叉求解实现。
- [ ] 检查 `KimodoHandEffectorGoalTests` 中 AnimationHumanStream goal 与 Avatar post rotation 的 180° 偏差；确认是测试公式、Unity 版本差异还是运行时手部约束方向错误。
- [ ] 将 FullDemo `5.RuntimeArdyDemo.unity` 中仍序列化到 `targetHumanoidAnimator` 的 8 个目标迁入 `targetHumanoidAnimators`，然后验证 ARDY 场景能启动；当前代码只读取新列表。
- [ ] 为运行时生成失败定义退避或阻塞策略，避免 QuickServer 不可用或请求持续失败时每帧重试并重复写日志。
- [ ] 调整 `SegmentReady` 通知与生成状态提交顺序，避免订阅者异常导致已接受的段播放、但段号和约束 revision 未提交。
- [ ] 收敛 Runtime Motion Driver 的 Stop/Reset 状态重置，并确保 `ResetMotionAsync` 在异常路径也解除 `GenerationBlocked`。
- [x] 已核对当前分支不存在 `AnalysisPictureSpacingTests` 文件或其旧 helper 引用，无需再执行该清理。
- [x] 已将通用 Window JSON 合并、帧对齐字段裁剪与数组长度校验收敛到 `KimodoConstraintWindow`；PostApply 保留 SOMA 平面变换，错位数组由共享裁剪器拒绝。
- [x] 已明确 `KimodoConstraintWindow.Combine` 的帧数契约：合并结果取两者较大值，因为 Stage 3 的 Loop/替换窗口会有意扩大目标跨度；局部帧移位仍由调用方在同一 generation window 内完成。
- [x] 已收窄 transport 注入边界：生成执行只依赖 `IKimodoGenerationTransport`，模型配置查询使用独立 `IKimodoModelConfigurationTransport`；status、Mocap 与服务器生命周期仍由 `KimodoBridgeService` 提供，不让 generation 测试替身实现无关方法。
- [x] 已归档 `command_inout.cs` 中不再可达的直接 FullBody In/Out sampler，以及重复的 `RequireInOutFields` 手工 allow-list；活动 handler 统一依赖实时 schema 的 `additionalProperties: false` 校验和共享 Editor pipeline。
- [x] 已将 `CommandInOutTests` 从归档的直接采样反射入口迁移到 `ConfigureCommandInOutClip` 与共享 generation window；schema 契约测试覆盖嵌套 In/Out 旧字段拒绝。
- [x] 已评估 In/Out 的两次规划；按本次 P1 取舍保留现状，暂不增加计划缓存，避免为小幅优化引入额外状态。
- [x] 已将场景 SessionRoot 扫描统一到 `command_context.FindLoadedSessionRoots`，保留单场景和全局过滤，供场景级查询与 Runtime Guard 共用。
- [x] 已将轨迹线、analysis 轨迹线和证据线的对象创建收敛到共享绘制核心，同时保留名称、偏移、宽度与颜色差异。

## Command 信息与接口冗余收敛（2026-11-01）

本轮审查当前 HEAD（2026-09-01 至 2026-11-01 共 223 个可达提交，其中 120 个触及 Command）。按以下顺序逐项收敛；每项先归档旧片段或旧数据形状，再改活动实现并验证，避免一次性混改多个响应契约。

- [x] 已将无调用者的 `ExecutePlayableClipGenerationAsync` 归档到 `archive/command-api-convergence-20261101/` 并从活动 Command 路径停用；FullDemo Command Editor 编译通过，0 警告、0 错误。
- [x] 已从 `generation.get` 输出 schema 移除没有运行时生产者的顶层 `analysis_schema_version`；`animation.analysis` 的顶层版本字段保持不变。`generation.get.post_analysis` 仅返回状态与 `analysis_path`，完整版本字段保留在该路径指向的 JSON 文件中。
- [x] 3.0 已将 `scene.context` 改为 `session.context`：Clip 计数直接来自当前角色动画轨道的 Timeline Clip 实例，详情组按请求构建。
- [x] 3.0 不生成、不读取或维护 `session.json`；生成/分析旁路产物继续按 Timeline GUID 的 Session 目录保存。
- [x] 已让新写入的 generation 状态直接使用规范 `clip` 对象；Session generation 索引读取该对象。归一化器仍把旧持久化状态的 `path/animation/character/start/duration` 转为 `clip`，使已有状态文件可继续查询。
- [x] 3.0 保留 `KimodoSessionContextBinding` 仅用于旧场景反序列化；新 Session 根不添加或读取该组件，Play Mode 只在 Awake 停用且退出不恢复。
- [x] 已更新本轮验证记录，分别报告静态检查、Editor 编译和定向 EditMode 结果；不把 schema/输出契约调整宣称为视觉回归通过。

保留边界：生成过程的 `analysis` 与生成后完整 `post_analysis` 表示不同阶段的数据，不合并；`analysis_option.analysis_only` 仍是 Command analysis 使用的内部 Runtime 协议，不恢复为公开生成字段；In/Out 二次规划按 P1 决策保留现状；`Command/help.json` 继续由活动 schema 定义生成并做一致性校验。

## 后续定向清理候选（2026-11-02）

仅处理下列有具体重复或不可达证据的路径；不做全体 Command 重写。

- [x] **`animation.generate.loop` 移除旧布尔格式。** 破坏性协议升级只接受对象格式 `{enabled, window_frames, sample_count, lock_pos, lock_rot}`；schema、handler、示例、帮助和契约测试已同步，旧布尔值直接由 schema 拒绝，不保留专门兼容入口。
- [x] **简化 `animation.analysis` 的空 options 路径。** 公开命令不再传递恒为空的 `analysis_option`，缓存签名已升至 `animation_analysis_picture_v8_force_line_command_60`；旧签名只会失配并重算。Runtime 的 `analysis_only` 内部请求字段仍保留。
- [x] **合并生成参数重复的值域检查。** `duration_frames`、loop 窗口/采样数和 `path.distance` 的标量范围统一由实时 schema 处理；handler 仅保留有限数值检查、跨字段约束和业务安全检查。
- [x] **归档过期 loop 生成支路。** 生产只走 `BuildLoopWindowConstraintJson` 与第 0 帧 anchor 策略；旧 `BuildLoopConstraintJson`、foot-contact anchor 备用链、速度估算器和仅覆盖旧支路的测试已移出活动代码并保留在本轮归档快照中。新 window 流程与当前仍被调用的 anchor helper 保留。
- [x] **整理残留约束 JSON helper 命名。** 通用 append/root 替换逻辑已并入 `KimodoConstraintWindow`；旧 `KimodoClipConstraintBakeUtility` 文件与 meta 已按仓库规则移出活动路径并保留归档文本快照。
- [x] **拆分历史 API（3.0）。** `session.history` 按 Session 追加 `animation.generate`、`animation.retarget` 和 `animation.analysis` 的完整输入、返回及异步终态；`session.context` 只返回当前场景与 Timeline 投影，不接受历史参数。
- [x] **复核约束面板 Auto Sample 文案。** `autoSample=true` 当前确实以标记时间的 Timeline pose 覆盖手动 effectors/motion，因此现有提醒与行为一致；此项不列为清理任务，也不重命名序列化字段。
- [x] **收敛 Window 与分析帧语义。** In/Out 的 60 FPS 秒数与模型帧换算继续由 `KimodoFrameTimeUtility`/`KimodoConstraintWindowSpec` 负责；散落的 60 常量改用共享命令帧率。公开分析的 phase、足部接触和 Root trajectory 时长/位置均明确使用 `_60` 字段；生成完成时不再向内部分析缓存补写模型帧 `duration_frames`。

审查中未发现需要普遍重构 `generation.get`、`pose.get/set`、调试 Timeline 输出或统一长 Clip 的 Root2D/FullBody 采样算法的证据。它们继续保留各自职责和已经统一的 Window 执行入口。
