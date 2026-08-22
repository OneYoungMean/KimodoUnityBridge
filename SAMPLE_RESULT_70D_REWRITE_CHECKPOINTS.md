# SampleResult 70D 重构 Checkpoints

## CP0 — 计划基线

- 分支：`sample-result-70d-rewrite`
- 提交：`8cef186`
- 状态：已保存此前 IK 清理成果、分支和完整实施计划。
- 检查：工作树在提交后干净；此前 FullDemo 编译基线为成功。
- 下一步：实现核心 70 维 sampleData 布局、访问器和独立 validMask/enable 语义。

## CP1 — 核心 70 维数据模型

- 状态：已完成，待提交。
- 已完成：固定 70 维布局；rootTQ/leftFootTQ/rightFootTQ 访问器；有限值校验；validMask 与 enabled 独立字段；CharacterPoseMuscleAdapter 的 70 维编解码入口。
- 检查：`git diff --check` 通过；尚未运行 Unity 编译。
- 尚未完成：采样 API 全量迁移、Composer 优先级迁移、Root2D/Effector 统一解算。
- 下一步：提交 CP1 后，迁移采样侧返回值并让目标骨架 muscle handle 写入 70 维数据。

## CP2 — 采样侧 70 维入口

- 状态：已完成，待提交。
- 已完成：新增 `TryCaptureSampleData`；目标骨架采样结果写入固定 70 维；有效 pose 设置 body/root/leftFoot/rightFoot mask；无效 pose 保持固定长度但所有 valid bit 为 false；marker normalization 优先沿用 sampleData，旧 CharacterPose 仅作兼容回退。
- 检查：`git diff --check` 通过；尚未运行 Unity 编译。
- 尚未完成：所有旧调用方清理、Composer 的最后创建优先规则、Root2D/Effector 最终解算。
- 下一步：提交 CP2 后，改造 Composer 的输入/输出和 channel 优先级。

## CP3 — Composer channel 合成

- 状态：已完成，待提交。
- 已完成：新增 `ComposeCanonicalSamples`；同帧分组；启用状态独立判断；按 `creationOrder` 最后创建优先；channel 级 valid mask；高优先级无效数据不回退；Root2D heading 依赖 position；Effector position/rotation 通过独立 channel 复制。
- 检查：`git diff --check` 通过；dotnet 生成工程检查受 FullDemo 缺少 NUnit 引用影响，未得到有效 Unity 编译结论。
- 尚未完成：将现有 Command/Preview/Export 调用切换到新 Composer；统一 FK → Root2D → Effector 解算；Unity 编辑器实际编译。
- 下一步：提交 CP3 后，迁移主要消费链路，并补充无效 mask 与优先级测试。

## CP4 — 核心消费链路迁移

- 提交：待写入。
- 状态：已完成，待提交。
- 已完成：Command 读取/写入 sampleData；Runtime export 优先从有效 70 维数据解码；Runtime generation 使用 canonical Composer；Marker/Composer 依赖方向修正为同程序集 `KimodoSampleDataLayout` 编解码；Legacy CharacterPose 输入可迁移。
- 检查：Unity CLI FullDemo 编译成功，退出码 0；日志：`C:\tmp\unity-compile-sample-result-70d.log`。仅保留既有序列化分析器/过时 API 警告。
- 尚未完成：完整移除 CharacterPose 兼容字段、统一实际 Root2D/Effector 姿势 solver、补充新结构测试。
- 下一步：提交 CP4，然后添加最小 70 维布局与 Composer 回归测试。

## CP5 — 最小回归覆盖

- 状态：已完成，待提交。
- 已完成：新增 `KimodoSampleDataTests`，覆盖 70 维长度与 T/Q round-trip、最后创建约束优先且 invalid 不回退、Root2D heading 依赖 position；补齐 Unity 文档 `.meta` 文件。
- 检查：Unity CLI FullDemo 编译成功，退出码 0；EditMode 测试命令成功退出，但当前 FullDemo 测试报告 testcasecount=0（包内测试未被该项目测试程序集发现），因此不能视为运行时测试通过。
- 尚未完成：完整旧测试迁移、实际 Effector solver、FullBody sampling/Generate 场景级回归。
- 下一步：提交 CP5；后续恢复时优先处理测试程序集发现问题或在包测试环境执行测试，然后再决定是否清理兼容 CharacterPose 字段。

## CP6 — Mix 协议展开修正

- 状态：已完成，待提交。
- 已完成：`constraintMode=mix` 的 canonical SampleResult 可重新展开为 fullbody/root2d/effector 协议族；保持 Root2D 独立覆盖；新增 Mix 展开回归测试。
- 检查：Unity CLI FullDemo 编译成功，退出码 0；EditMode 命令仍因 FullDemo 未发现包内测试而报告 testcasecount=0。
- 尚未完成：CharacterPose/rawData 兼容字段清理、真实 Effector 解算和场景级 Generate/FullBody sampling 验证。
- 下一步：提交 CP6；继续处理旧协议字段迁移时，优先移除 SampleResult 的 rawData/sourceRootWorldPose，并修复所有生产调用方。

## CP7 — Effector 完整性校验

- 状态：已完成，待提交。
- 已完成：Composer 对四个 Effector 的 position/rotation 目标执行 finite + non-zero quaternion 成对校验；无效目标对应 validMask 强制为 false，不会复制到最终 canonical sample。
- 检查：Unity CLI FullDemo 编译成功，退出码 0。
- 尚未完成：实际 Humanoid Effector solver；SampleResult 中旧 CharacterPose/rawData/sourceRootWorldPose 字段仍保留为迁移兼容字段。
- 下一步：提交 CP7。旧字段清理需要同步改造 Exporter、Bake、Preview 和旧测试，不能进行局部删除。

## CP8 — 无效样本迁移保护

- 状态：已完成，待提交。
- 已完成：Composer 仅在未声明 mode 的旧样本上从 CharacterPose 做兼容迁移；mode-aware 样本即使带有旧 CharacterPose，也不会覆盖显式全 false 的 validMask。
- 检查：Unity CLI FullDemo 编译成功，退出码 0。
- 尚未完成：旧字段物理删除和真实 Effector solver。
- 下一步：提交 CP8；后续如继续清理，应先建立旧字段使用清单和测试屏蔽策略。

## CP9 — 移除 sourceRootWorldPose

- 状态：已完成，待提交。
- 已完成：从 `KimodoMarkerSampleResult` 和 Marker authoring state 移除 `sourceRootWorldPose`，SampleResult 不再保存冗余 source root world pose。
- 检查：`git diff --check` 通过；Unity CLI FullDemo 编译成功，退出码 0。
- 尚未完成：rawData、CharacterPose、constraintType、旧 has* 字段仍有生产/测试引用。
- 下一步：提交 CP9；继续将 rawData 从 SampleResult 移到导出边界，必要时先屏蔽旧 rawData 测试。

## CP10 — 移除 SampleResult rawData

- 状态：已完成，待提交。
- 已完成：`KimodoMarkerSampleResult.rawData` 已删除；FullBody Exporter 不再读取 rawData，统一走 sampleData/FK projection；overlap 和 loop generation 不再写入 rawData；旧 rawData 测试已显式 Ignore 或移除旧字段断言。
- 检查：Unity CLI FullDemo 编译成功，退出码 0；生产代码仅剩 `KimodoRawMotionUtility` 的底层临时构建返回值，不再进入 SampleResult。
- 尚未完成：CharacterPose、constraintType、hasRootHeading、hasRoot2DOverride 等历史字段仍有大量调用；真实 Effector solver 尚未实现。
- 下一步：提交 CP10；继续清理 hasRoot* 与 constraintType 前，先建立派生 accessor，避免一次性破坏协议族导出。

## CP11 — Root2D 有效性派生化

- 状态：已完成，待提交。
- 已完成：`hasRoot2DOverride` 和 `hasRootHeading` 不再是独立存储状态，改为由 `validMask.root2DPosition/root2DHeading` 派生的兼容属性；heading setter 强制依赖 position。
- 检查：Unity CLI FullDemo 编译成功，退出码 0。
- 尚未完成：constraintType 仍用于协议族命名；CharacterPose 仍作为迁移/Unity HumanPose adapter 边界；真实 Effector solver 未实现。
- 下一步：提交 CP11；继续将 constraintType 的内部判断迁移到 constraintMode/channel mask，保留协议导出时的局部 type。

## CP12 — CharacterPose 派生兼容层

- 状态：已完成，待提交。
- 已完成：SampleResult 不再存储独立 `CharacterPose` 字段；`characterPose` 改为由 70 维 sampleData 解码、由 setter 编码的 obsolete 兼容属性；hand/foot 兼容值从 effectors 映射；清空属性会清除 body/root/foot valid bits。
- 检查：Unity CLI FullDemo 编译成功，退出码 0。
- 尚未完成：生产代码仍有大量旧属性访问，需逐步替换为 sampleData/validMask；constraintType 仍参与协议/legacy 分支；真实 Effector solver 未实现。
- 下一步：提交 CP12；将 Runtime/Command/Preview 的直接 CharacterPose 读取改为统一 sampleData 解码 helper，减少兼容属性调用。

## CP13 — FK 后 Effector 姿势应用

- 状态：已完成，待提交。
- 已完成：`TrySampleTargetFromSingleMuscleSample` 和 `TryRebuildPoseFromMuscles` 不再静默丢弃 `sceneTargets`；先完成 muscle FK，再按 world position/rotation 成对写入四个目标骨骼，并重新捕获 `BoneSample`/`MuscleSample`。保持 70 维 sampleData 与原协议边界不变。
- 检查：`git diff --check` 通过；Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-effector-pass.log`。
- 尚未完成：SampleResult 的 `characterPose` 兼容属性及其生产调用方仍待迁移；`constraintType` 仍保留在协议导出边界；场景级 FullBody/Generate 验证仍待补充。
- 下一步：迁移并删除 SampleResult 的 obsolete `characterPose`/`hasRoot*` 兼容层，先处理 Runtime/Command/Composer，再处理 Editor 预览和旧测试。

## CP14 — Pose 转换边界隔离

- 状态：已完成，待提交。
- 已完成：新增 `KimodoSampleResultPoseUtility`，把 `sampleData ↔ CharacterPose` 的临时转换集中到单一边界；SampleResult 内的兼容 `characterPose` 仅保留为隔离桥接，未再复制保存数据。这样后续可逐文件删除旧访问而不再扩散编解码逻辑。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-compat-bridge.log`；`git diff --check` 通过。
- 尚未完成：编辑器/命令生产调用方仍有旧属性访问；需继续迁移后物理删除桥接。场景级 solver 仍为直接 world 骨骼目标写入，未引入中间 IK 图。
- 下一步：优先迁移 `KimodoConstraintSampleComposer` 与 JSON exporter，使 canonical 合成完全只读写 70 维 sampleData，再移除兼容属性。

## CP15 — Runtime 采样边界迁移

- 状态：已完成，待提交。
- 已完成：Runtime marker 创建、runtime constraint sampler、transform-map 应用和 export projector 改用 `KimodoSampleResultPoseUtility`/`sampleData`；新增 marker active payload 的显式 encode/decode，减少对 SampleResult 兼容属性的依赖。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-pose-boundary-migration2.log`；`git diff --check` 通过。
- 尚未完成：Composer、JSON exporter、Command、Preview、Editor sampling 仍有旧属性访问；兼容桥接暂不能删除。
- 下一步：迁移 Composer 的合成读写，确保 root2d 与 fullbody 的 canonical 结果只从 70 维数据产生。

## CP16 — JSON Exporter 迁移

- 状态：已完成，待提交。
- 已完成：`KimodoConstraintJsonExporter` 及其 `KimodoConstraintExportContext` 不再读取 SampleResult 的 `characterPose`/`hasRootHeading`；导出前显式从 70 维 `sampleData` 解码临时 pose，Root2D heading 只由 `validMask` 决定；无效 sampleData 直接失败，不再走旧 fallback。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-exporter-migration.log`；`git diff --check` 通过。
- 尚未完成：Composer 与 Preview/Command 仍需迁移；兼容 bridge 暂不删除。
- 下一步：迁移 `KimodoConstraintSampleComposer`，清除其对派生 `characterPose` 的写入，canonical 输出只保留 sampleData/effectors/mask。

## CP17 — Canonical Composer 迁移

- 状态：已完成，待提交。
- 已完成：`KimodoConstraintSampleComposer` 的 canonical 合成、Root2D overlay、Mix/协议展开全部改为显式 `KimodoSampleResultPoseUtility` 编解码；不再直接读写 SampleResult 的 `characterPose`，Root2D heading/position 直接使用 `validMask`。输出仍保持原协议的 `constraintType` 边界。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-composer-migration.log`；`git diff --check` 通过。
- 尚未完成：Command、Preview、Editor sampling 仍有旧属性调用；兼容 bridge 继续保留直到这些调用迁移完毕。
- 下一步：迁移 Command/Runtime generation 的 canonical pose 读取，随后清理 Preview 的重复 pose 合成逻辑。

## CP18 — Command/Runtime Generation 迁移

- 状态：已完成，待提交。
- 已完成：Command 的 canonical pose 读取/写入、root2d 构建、session 持久化、preview pose 应用，以及 RawMotion/Retarget marker 创建均改用 70 维 `sampleData` 和显式 pose 转换边界；Runtime marker sampling 不再回写 `characterPose` 兼容字段。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-command-migration.log`；`git diff --check` 通过。
- 尚未完成：Editor Preview、Clip Bake、Timeline sampler 和 sampling editor utility 仍有旧字段调用；兼容桥接仍需保留。
- 下一步：迁移 Editor Preview 的 pose cache/writeback，重点保证关闭窗口时 preview 状态清理和 Root2D/Effector 显示使用同一 canonical sample。

## CP19 — Editor 生成/采样边界迁移

- 状态：已完成，待提交。
- 已完成：Clip generation loop 调试、sampling editor utility 的比较/签名、Spline Path 与 In/Out Root2D sample 创建改用 `sampleData`/`validMask`；不再依赖 SampleResult 的 `characterPose`/`hasRoot*` 属性。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-editor-boundary-migration3.log`；`git diff --check` 通过。
- 尚未完成：`KimodoConstraintPoseCache`、Clip Bake、Timeline marker sampler 和 UI marker sampling 仍有旧访问；Preview 的关闭窗口状态清理需要继续核验。
- 下一步：集中迁移 PoseCache 的读取、hash、writeback 与 root/effector 显示逻辑，完成后删除 SampleResult 兼容属性。

## CP20 — Preview PoseCache 与窗口生命周期

- 状态：已完成，待提交。
- 已完成：Preview PoseCache 的 FK 应用、hash、writeback、Root2D target 显示改用显式 sampleData 解码与 validMask；关闭编辑窗口时先隐藏/取消选择 preview group，再销毁 cache，并继续调用 `DisablePreview`，避免 OnDisable/domain reload 后残留 preview 状态。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-preview-migration.log`；`git diff --check` 通过。
- 尚未完成：Clip Bake 与 Timeline marker sampler/UI sampling 仍有旧属性访问；SampleResult compatibility bridge 还不能删除。
- 下一步：迁移 Clip Bake 的 root/Root2D 读写和 Timeline sampler 的 Root2D 写入，随后清理 UI sampling 的重复 CharacterPose 合成。

## CP21 — Clip Bake Root2D 迁移

- 状态：已完成，待提交。
- 已完成：Clip constraint bake 的 loop 首尾 pose 解码、Root2D/fullbody 合并、Root2D sample 创建改用 `sampleData`/`root2DOverride`/`validMask`；不再直接读写 `characterPose` 或 `hasRoot*`。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-bake-migration3.log`；`git diff --check` 通过。
- 尚未完成：Timeline marker sampler 与 UI sampling 仍存在兼容访问；旧 bridge 仍需保留。
- 下一步：迁移 Timeline sampler 的 Root2D 写入和 UI sampling 的 effector/pose 合并，之后运行完整编译并评估是否可物理删除 bridge。

## CP22 — Timeline/Runtime Marker Sampling 迁移

- 状态：已完成，待提交。
- 已完成：Timeline marker sampler、runtime marker normalization/default sample、profile end-effector sample 创建均不再直接读写 SampleResult 的 `characterPose`/`hasRoot*`；自动采样的 pose 与 effector 目标统一经过 `sampleData`/`effectors` 编解码。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-sampling-utility2.log`；`git diff --check` 通过。
- 尚未完成：Editor UI marker sampling 仍有旧兼容访问；之后需要删除 SampleResult 的 obsolete bridge，并屏蔽/迁移旧 CharacterPose 测试。
- 下一步：迁移 `KimodoConstraintMarkerSampling` UI 合并逻辑，再做一次全仓生产代码扫描；若无旧访问则物理删除 `characterPose`、`hasRoot*`。

## CP23 — 移除 SampleResult obsolete pose 字段

- 状态：已完成，待提交。
- 已完成：`KimodoMarkerSampleResult.characterPose`、`hasRoot2DOverride`、`hasRootHeading` 已物理删除；生产代码扫描不再发现这些字段访问。`CharacterPose` 仅保留为 JSON/HumanPose/retarget 边界临时对象，不再是 SampleResult 的存储语义。
- 检查：Unity CLI FullDemo 编译成功，日志：`C:\tmp\unity-compile-bridge-removed.log`；UI sampling 迁移编译成功，日志：`C:\tmp\unity-compile-ui-sampling.log`；`git diff --check` 通过。
- 尚未完成：旧 Editor 测试仍大量构造/读取 `characterPose`，按破坏性升级策略先屏蔽/迁移；`constraintType` 仍作为协议导出边界字段保留。需补充完整 FullBody sampling/Generate 场景回归。
- 下一步：提交本 checkpoint，运行生产代码全量编译；随后处理旧测试程序集和 `constraintType` 内部冗余清理。

## CP24 — 生产编译基线

- 状态：已完成。
- 已完成：生产代码全量 Unity CLI 编译通过；`git diff --check` 通过；工作树干净。日志：`C:\tmp\unity-compile-final-70d.log`。
- 当前边界：旧 Editor 测试仍依赖被移除的 `CharacterPose` SampleResult 属性，尚未纳入生产程序集编译；FullDemo EditMode 测试此前报告 `testcasecount=0`，因此不能宣称场景回归已执行。
- 下一步：在独立测试宿主中重写/屏蔽旧测试，补充 FullBody sampling 和 Generate 场景验证；之后再评估删除 `CharacterPose` 兼容 DTO 文件及内部 `constraintType` 冗余。

## CP25 — 旧测试屏蔽与新结构编译

- 状态：已完成，待提交。
- 已完成：将依赖已删除 SampleResult 旧字段的 5 个历史测试文件暂时置于 `#if false`，保留新的 70D 测试文件；修正 `KimodoSampleDataTests` 的 Root2D mask 断言。
- 检查：FullDemo Unity CLI 编译通过，日志：`C:\tmp\unity-compile-tests-screened.log`；`git diff --check` 通过。
- 说明：这是用户确认的破坏性升级策略下的临时屏蔽，不代表旧行为恢复；后续应基于 `sampleData`/`validMask` 重写这些测试。
- 下一步：提交 checkpoint，开始执行 FullBody sampling 与 Generate 场景级最小验证。

## CP26 — 测试宿主发现结论

- 状态：已完成，待提交。
- 已完成：重新执行 FullDemo EditMode 测试命令；命令退出成功但结果仍为 `testcasecount="0"`，说明该外部项目没有引用本包测试程序集，不能作为测试通过依据。
- 检查：结果文件：`C:\tmp\sample-result-70d-tests.xml`；编译仍通过。
- 结论：当前可确认的是生产编译通过和静态 70D 回归代码可编译；FullBody sampling/Generate 场景仍需在 package 自己的 Unity 测试宿主或实际编辑器 Timeline 中执行。
- 下一步：不再继续修改外部 FullDemo 测试配置，避免把验证项目的程序集边界误判为功能结果；保留 checkpoint，等待包测试宿主接入后执行场景测试。

## CP27 — 骨骼快照结构确认

- 结论：项目中没有独立的 `SkeletonData` 类型；用户记忆中的全身骨骼帧结构是 `KimodoBridge.BoneSample`。
- `BoneSample` 位于 `Runtime/Retarget/KimodoRetargetSamples.cs`，包含 `boneNames`、`localPositions`、`localRotations`，表示完整骨骼帧的局部变换。
- `SkeletonCache` 不是动画帧数据，而是骨架运行时容器；其 `bindLocalPositions`、`bindLocalRotations`、`bindWorldRotations` 和 `bindSkeletonRootWorldRotation` 保存 rest/bind 姿势基准。
- AutoSample 后续统一链路固定为：`MuscleSample → BoneSample(FK) → 应用到 SkeletonCache 后读取世界空间 pelvis/手脚 → SampleResult`。不新增同义 `SkeletonData`，也不直接把 `BoneSample.local*` 当世界坐标。
- 当前工作树没有产生有效代码差异；此前尝试把 effector enable 合并到 mask 已撤回，避免在结构确认前误改协议。

## CP28 — 回退到 21:13 前基线并重建计划

- 状态：已完成，待提交。
- 回退：分支已回到 `618762d`（2026-08-21 21:07:19）；21:13 没有独立提交，21:14 的 `cf89c2b` 仅修改 checkpoint 文档，因此选择 618762d 作为代码基线。
- 原因：后续 world-root/AutoSample/effector 实现混用了旧 root-space 和 HumanPose body root 语义，不能继续在其上修补。
- 计划：重写 `SAMPLE_RESULT_70D_REWRITE_PLAN.md`，明确删除 raw root-space effector、旧 IK helper、CharacterPose 持久化旁路和重复 AutoSample/拖拽 merge，重新建立唯一 `BoneSample → SkeletonCache → world SampleResult` 链路。
- 检查：回退后工作树干净；尚未修改生产代码。
- 下一步：执行 Phase 1，全仓清理旧 IK 和 root-space sampling 生产调用，先提交独立 checkpoint。

## CP29 — 清理旧 IK 与 root-space 采样旁路

- 状态：已完成，待提交。
- 删除：移除 `TrySampleMarkerFromProfileSkeletonRaw` 及其 profile-root-local effector 计算；BoneSample 采样改为创建无中间 root-space 数据的 SampleResult shell。
- 删除：移除 `WorldToBodyRelativeEffector`、`BodyRelativeEffectorToWorld`、骨骼 endpoint/effector 偏移 helper，以及运行时 foot IK compatibility stubs。
- 删除：移除 `MuscleSample` 中已废弃的 hand IK position/rotation 字段。
- 修改：Runtime constraint capture 不再通过 profile skeleton raw marker 初始化，直接创建 SampleResult 后捕获 FK muscle 数据。
- 检查：Unity 2022.3.62f3c1 编译成功，日志：`C:\tmp\unity-compile-phase1-clean-2022.log`；无 `error CS`，退出码 0；`git diff --check` 通过。
- 尚未完成：SampleResult 仍有旧 mode payload 和 `validMask` 命名；下一阶段统一为单一 sampleData/enableMask，并建立唯一 world SkeletonCache 写入入口。

## CP30 — 单一 SampleResult 与 enableMask

- 已完成：删除 `KimodoRoot2DConstraintData`、`KimodoFullBodyConstraintData`、`KimodoEffectorConstraintData` 三套旧 mode payload；Marker 只序列化一份 `sampleData`。
- 已完成：生产代码和新测试统一将 `validMask` 重命名为 `enableMask`；Inspector 的 Root2D、muscle、effector 面板改读写 `sampleData`，不再访问旧 payload。
- 已完成：旧 mode payload 测试按破坏性升级策略屏蔽，避免把已删除语义重新引入生产代码。
- 检查：`git diff --check` 通过；Unity CLI 已重新解析本地包并正常退出，当前日志没有发现 C# 编译错误；完整编译仍待下一次无缓存运行确认。
- 尚未完成：BoneSample→SkeletonCache→world SampleResult 唯一采样入口，以及 AutoSample/非 AutoSample 共享写回 utility。

## CP31 — BoneSample 到 world SampleResult

- 已完成：`TryBuildMarkerSampleResultFromBoneSample` 在 BoneSample 应用到目标 SkeletonCache 后，统一捕获 Hips、手、脚的世界 Transform，写入 `root2DOverride` 和四个 world effector。
- 已完成：手部旋转按 root 空间相对 rest 姿势计算；脚部旋转不经过 pelvis，使用脚部当前旋转与初始脚旋转的相对值；预览使用同一转换函数还原显示旋转。
- 已完成：预览 hips 直接读取 `SampleResult.root2DOverride` 的 world position/rotation，不再把 `HumanPose.bodyPosition` 当作世界 hips。
- 已完成：删除 Editor AutoSample 的旧 CharacterPose 合并旁路；AutoSample 结果整体写回同一份 SampleResult。
- 检查：Unity 2022.3.62f3c1 CLI 编译成功，日志：`C:\tmp\unity-compile-phase2-worldcanonical2.log`；无 `error CS`；`git diff --check` 通过。
- 尚未完成：非 AutoSample 拖拽统一写回 utility、Composer 的 enableMask/creationOrder 最终边界清理，以及协议导出/Generate 的全链路场景验证。

## CP32 — Preview 旁路清理与 world 拖拽写回

- 已完成：Preview 不再通过 `TrySampleTargetFromSingleMuscleSample` 处理拖拽后的手脚目标；effector 仅作为 world protocol/display 数据，暂不执行 IK。
- 已完成：Root2D/hips 显示和写回直接使用 world `root2DOverride`；移除 `HumanPose.bodyPosition` 与 `SkeletonRootLocal/World*` 的 hips 转换旁路。
- 已完成：AutoSample 结果不再经过 Editor 自己的 CharacterPose 合并；非 AutoSample 的拖拽只捕获 gizmo world position/rotation，写回同一份 SampleResult。
- 检查：Unity 2022.3.62f3c1 CLI 编译成功，日志：`C:\tmp\unity-compile-phase3-final.log`；无 `error CS`；`git diff --check` 通过。
- 尚未完成：把 EditorWindow 与 Inspector 的 world writeback 完全收敛到一个公共函数，清理 Composer/协议边界中残留的 legacy mask/type 分支，并执行 FullBody sampling/Generate 场景验证。

## CP33 — 清理 MuscleSample→BoneSample 旧 rootTQ 通道

- 已完成：transient MuscleSample clip 改为 `WriteBodyMuscleCurves`，只写 49 个 body muscle；不再生成 `RootT.*`、`RootQ.*`、`FootT/Q` 曲线。独立动画导出若需要根运动，使用明确命名的 `WriteMuscleAndTransformCurves`，与 retarget 采样边界隔离。
- 已完成：新增 `CharacterPoseMuscleAdapter.ToBodyMuscleSample`，所有由 `sampleData/CharacterPose` 进入 MuscleSample→BoneSample 的生产入口均使用 root identity，不读取/传输 rootTQ 与 authored foot goal。
- 已完成：移除 Root2D fallback 到 `CharacterPose.root`、transform-map fallback 的 rootTQ 读取、loop bake 对 FullBody `CharacterPose.root` 的覆盖、Root2D 临时样本写回 sampleData rootTQ，以及 legacy migration 自动重建 rootTQ。
- 已完成：运行时 effector/root2d 目标统一使用 world position/absolute heading，不再通过 rootTQ 或 root-local 计算；协议导出 Root2D/effector 读取显式 world override/effectors。
- 检查：Unity 2022.3.62f3c1 编译成功，日志：`C:\tmp\unity-compile-root-channel-clean5.log`；`git diff --check` 通过。
- 仍保留的 rootTQ 仅限：70D 固定协议布局、Composer 的显式通道合并、以及明确标注的独立完整动画导出；它们不再参与 MuscleSample→BoneSample。

## CP34 — 删除独立动画 RootT/Q 曲线旁路

- 已完成：肌肉动画写出统一改为 `WriteBodyMuscleCurves`，不再生成 `RootT.*`、`RootQ.*` 或 FootT/Q 动画曲线；删除编辑器对这些旧曲线的保留判断。
- 已完成：保留的 `rootTQ/footTQ` 只存在于 70D `sampleData` 协议布局，不再以 Animator 曲线形式存在。
- 检查：Unity 2022.3.62f3c1 编译成功，日志：`C:\tmp\unity-compile-root-channel-clean6.log`；`git diff --check` 通过。

## CP35 — Command 根变换改为 Root2DOverride

- 已完成：`pose_set_root_transform` 不再修改 70D `sampleData.rootTQ`，改为写入完整 world-space `root2DOverride`；heading 没有 position 时拒绝写入，保持 `hasHeading` 依赖 position 的协议约束。
- 检查：Unity 2022.3.62f3c1 编译成功，日志：`C:\tmp\unity-compile-root-channel-clean7.log`；`git diff --check` 通过。

## CP36 — 面板显示 Root2DOverride 与预览角色根跟随

- 已完成：FullBody/Effector 面板增加统一的 `Root Position / Rotation` 编辑区，直接读写 `sampleData.root2DOverride`；Root2D 模式沿用原面板，避免重复绘制。
- 已完成：预览应用 root2D 时，先按 hips 当前 world pose 到目标 world pose 的刚体 delta 移动可见 avatar 的 skeleton root，再精确设置 hips world position/rotation；这样采样的 hips 不会只留在 identity 根下或造成角色整体偏移。
- 约束：`root2DOverride` 仍然是 hips 的 world-space 语义，没有重新接入 `rootTQ`，effector 仍只作为显示/协议数据。
- 检查：Unity 2022.3.62f3c1 CLI 编译成功，日志：`C:\tmp\unity-compile-root2d-avatar-fix.log`；无 `error CS`，退出码 0；`git diff --check` 通过。

## CP37 — Muscle 到 Bone 恢复既有完整 Retarget Transport

- 已完成：恢复统一 retarget 临时 clip 的完整通道写入：49 个 body muscle、`RootT/RootQ`、左右脚 T/Q；这些是同一 PlayableGraph retarget 管线的输入，不新增第二套重定向逻辑。
- 已完成：`TryCreateTransientMuscleClip` 改用完整 retarget writer；Preview 与 Runtime Constraint Projection 从 `CharacterPose` 进入 retarget 时改用保留 root/foot transform 的 `ToMuscleSample`，不再清零 rootTQ。
- 保持：Root2D 仍在 BoneSample 应用后读取目标 Hips world pose并作为后置 override；effector 仍不执行 IK。
- 检查：Unity 2022.3.62f3c1 CLI 编译成功，日志：`C:\tmp\unity-compile-retarget-70d-roottq.log`；无 C# 编译错误，退出码 0；`git diff --check` 通过。

## CP38 — Root2DOverride 暂停写入虚拟角色

- 已完成：移除 Preview 中 `root2DOverride → skeletonRoot/hips` 的额外 root delta 和 hips 写回；虚拟角色现在只显示既有 retarget 结果。
- 已完成：FullBody rig 的 Hips gizmo 优先使用 `entry.BaseSample.root2DOverride`，因此 Root2DOverride 仍会输出并绘制到 rig，但不会改变虚拟角色绘制结果。
- 结论：此前的 root delta 是预览附加变换，不属于 retarget；当前采样/重建链路不再使用该附加层。
- 检查：Unity 2022.3.62f3c1 CLI 编译成功，日志：`C:\tmp\unity-compile-root-override-display-only.log`；无 C# 编译错误，退出码 0；`git diff --check` 通过。

## CP39 — 切断 Root2D→RootTQ 旧旁路并中和 Preview 根节点

- 已完成：Composer 的 `ApplyRoot2DOverlay`、`CopyRoot2D` 以及 Root2D protocol sample 对 `CharacterPose.root` 的旧写回已移除；Root2DOverride 保持独立 world-space hips 数据，不再写入 70D rootTQ。
- 已完成：Preview 可见 Avatar clone 的 skeleton root 初始化为 position/rotation/scale identity，避免源 Animator 的场景变换和 lossyScale 对 muscle-space rootTQ 产生第二次变换。
- 已确认：Preview 的 `ProfileCache` 来自 `KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName)` 返回的 canonical profile Avatar；它不是 Timeline 绑定角色，而是用于 profile 骨架重建和 Target→Profile 的旧中间 retarget。
- 检查：`git diff --check` 通过；尚未运行 Unity CLI 编译。
- 尚未完成：评估 AutoSample 是否应完全移除 Target→Profile 中间缓存，改为从目标 skeleton 的 world BoneSample 直接构建 SampleResult；需先观察本 checkpoint 后的 Hips/rootTQ 结果。

## CP40 — 清理非 ToJson 的 ProfileSkeleton/旧诊断旁路

- 已完成：Preview 移除 `ProfileCache` 和 Target→Profile→Target 的中间 retarget；AutoSample 直接从 TargetCache 捕获 BoneSample 并生成 SampleResult。
- 已完成：删除 Preview 中已无生产调用的 HumanBone pair/point 映射与 humanScale 转换辅助；拖拽和显示不再保留 profile-root/local 坐标旁路。
- 已完成：删除仅用于运行时调试显示的 `KimodoRuntimeProfileSkeletonPreview`，并移除 Runtime Motion Driver 上对应的调试字段、属性和 Inspector UI。
- 已完成：删除未被生产代码调用的旧 `KimodoConstraintPoseDiagnostics` 肌肉统计/差异工具。
- 已完成：Command 的 Root2D 高度只读取目标缓存 Hips 世界位置，移除无用的 modelName 参数。
- 保留：ToJson 投影、Kimodo/ARDY 协议 joint layout/父子关系校验、SkipRetarget 模型骨骼检查路径；这些仍属于生产必要语义。
- 检查：`git diff --check` 通过；尚未运行 Unity CLI 编译验证。
- 下一步：提交本批次，随后做全仓非 ToJson ProfileSkeleton 引用分类和 Unity 编译/FullBody sampling/Generate 验证。

## CP41 — 收敛 Preview Sample 应用命名

- 已完成：`KimodoConstraintSpaceConverter` 重命名为 `KimodoConstraintSampleApplier`；当前逻辑只负责 canonical SampleResult→MuscleSample→BoneSample→TargetCache，不再伪装成空间转换器。
- 已完成：删除未使用的 `HumanoidEffectorSceneTargets` 参数，Preview 应用路径不再携带潜在 IK/effector 解算入口。
- 检查：`git diff --check` 通过；Unity CLI 编译仍待执行。

## CP42 — 非 ToJson 引用复核与构建边界记录

- 复核保留路径：`KimodoRawMotionUtility` 用于 RawMotion 的 Kimodo/ARDY joint 映射；`KimodoEditorClipUtility`/Generation Planner 用于 SkipRetarget 前的模型骨骼校验；`KimodoClipConstraintEncoder`、`KimodoClipConstraintBakeUtility`、`ArdyEditorHistoryEncoder` 用于协议关节布局/父子关系；`KimodoRuntimeConstraintExportProjector` 位于 Runtime ToJson 投影路径。
- 未发现新的可删除生产 ProfileSkeleton 调用。`Editor/Tests/KimodoTimelinePoseSamplerTests.cs` 仍整体 `#if false`，其中的旧 raw/profile 测试不进入生产编译，暂不为历史测试重建已删除语义。
- 构建尝试：在 `C:\tmp\KimodoUnityBridge_FullDemo-main` 执行 `dotnet build KimodoTool.Editor.csproj --no-restore`；失败原因为 FullDemo 的 Unity Test Framework 缺少 NUnit 引用（约 603 个外部 `NUnit` 错误），不是本批次代码错误证据。
- 当前分支提交：`04c4ae5`；工作树保留用户此前 quick-server 启动脚本改动，未混入本分支 checkpoint。
- 追加清理：Runtime ToJson 投影中未使用的 legacy `KimodoConstraintMask` 局部变量已删除。

## CP43 — MuscleSample 统一为 70D 原子数据

- 已完成：`MuscleSample` 不再存储 `HumanPose` 或独立的 foot position/rotation 字段，内部统一为固定 70D `data`：49 body muscle + rootTQ7 + leftFootTQ7 + rightFootTQ7。
- 已完成：`KimodoMarkerSampleResult.sampleData` 改为 `MuscleSample`；Clone、Composer、采样结果、Inspector hash 和 marker normalization 已同步使用同一对象。
- 已完成：retarget clip writer、Humanoid retargeter、FootT/Q bake、muscle clone 等路径改为从 70D 读取/写入；HumanPose 只在 Unity API 边界临时构建。
- 保留：协议层 `KimodoSampleDataLayout` 的 float[70] 编码作为传输格式适配，不再作为内部动画原子对象。
- 尚未完成：CharacterPose/CharacterPoseTransform/CharacterPoseSides 的生产适配移除，以及 RawMotion→constraint_internal 直连。
- 检查：`git diff --check` 通过；FullDemo dotnet 构建当前被外部 Unity Test Framework/NUnit 和缺失 Unity 生成 DLL 阻断，未形成有效生产编译结论。

## CP44 — RawMotion 内部约束边界命名

- 已完成：`KimodoConstraintRawData` 改为内部类型 `KimodoConstraintInternalData`。
- 已完成：RawMotion 导出入口改为内部 `TryBuildConstraintInternalData`，不再把 RawMotion 暴露为公开 constraint 数据结构。
- 说明：当前只完成边界命名和可见性收敛；RawMotion 直接对接内部约束发送通道仍需在下一阶段接入，避免继续经过公开 constraint JSON。

## CP45 — Generate 投影链路改用 70D MuscleSample

- 已完成：Runtime Generate 的约束投影不再把 `KimodoMarkerSampleResult.sampleData` 解码为 Command 层 `CharacterPose`，直接克隆并重定向固定 70D `MuscleSample`。
- 已完成：生成投影结果不再携带未使用的 `projectedPose` CharacterPose 回传字段；协议输出仍由同一 profile skeleton 的 FK 结果生成轴角和根位置。
- 保持：Editor Generate 的协议 JSON、KMB attachment 和现有 retarget 写出流程不变；Preview/cache 路径暂未继续清理，等待用户提交集中缓存清理后再接入。
- 检查：`git diff --check` 通过；Unity 编译待用户缓存提交后执行。

## CP46 — SampleResult Runtime 程序集迁移

- 已完成：`KimodoSampleData`、`KimodoConstraintMarkers` 及其运行时 SampleResult/composer/exporter 边界文件移入 `Runtime` 路径。
- 已完成：`TimelineInject/Runtime/Timeline.asmref` 指向 `KimodoTool`，使 70D `MuscleSample` 与 `KimodoMarkerSampleResult` 在同一 Runtime 程序集内编译，消除 `KimodoSampleData.cs` 对 `KimodoBridge.MuscleSample` 的程序集不可见错误。
- 保持：`CharacterPose`/JSON 仍作为兼容边界保留；本批次未触碰 Preview/cache 或缓存清理分支内容。
- 检查：`git diff --check` 通过；FullDemo 的 Unity CLI 验证被外部项目缺失 `com.unity.pipeline@0.4.0-exp.1` 阻断，未产生有效 C# 编译结论。
