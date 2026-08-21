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
