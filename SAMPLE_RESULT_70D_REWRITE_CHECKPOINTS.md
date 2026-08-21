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
