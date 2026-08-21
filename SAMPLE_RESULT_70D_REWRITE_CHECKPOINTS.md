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
