# SampleResult 70D 重构实施计划

## 目标

在 `sample-result-70d-rewrite` 分支完成一次破坏性升级：以 `KimodoMarkerSampleResult` 作为采样、Command、Preview、导出和显示之间唯一的原子数据结构，移除采样侧 `muscle + transformData` 双轨返回，统一为固定 70 维 `float` 数据，并保持既定 Root2D/Effector 解算顺序。

## 已冻结的协议与语义

### 70 维 sampleData 布局

```text
[0..48]   body muscle 49
[49..55]  rootTQ 7
[56..62]  leftFootTQ 7
[63..69]  rightFootTQ 7
```

rootTQ 和 footTQ 的具体 T/Q 排列、四元数顺序、单位及坐标空间必须由常量和访问器统一定义，业务代码禁止裸索引。

### 有效性与启用状态

- `validMask` 只表示采样数据是否存在且有效。
- `enable` 表示约束是否启用，不能由 `validMask` 推导，也不能反向推导。
- 无效 pose 必须将对应 mask 置为 false，不能用零值伪造有效数据。
- Effector position/rotation 必须成对有效；缺一项则该 Effector 无效。

### 解算顺序

```text
sampleData 基础 FK
→ Root2D world position / absolute yaw 覆盖
→ Effector world position / rotation 解算
→ final pose / display / export
```

Root2D 不写入 rootTQ；只覆盖基础姿势的世界位置 X/Z 和 Y 旋转，基础 Y、pitch、roll 默认保留。

### Composer 优先级

`KimodoTimelineConstraintMarkerSampler` 负责单个 Timeline marker 的采样；`KimodoConstraintSampleComposer` 负责 Command 层同帧约束合并。

- 只合并启用的约束。
- 约束按创建顺序排序。
- 最后创建的约束优先级最高。
- 高优先级约束的 channel 若无效，最终 channel 保持无效，不回退到旧约束。
- Composer 不负责 FK、Effector 解算或协议 JSON 生成。

## 实施阶段与提交点

### Phase 0：基线与计划

- 在新分支保存当前 IK 清理成果。
- 提交本计划和 checkpoint 日志。
- 记录 Unity/FullDemo 当前编译基线。

提交：`checkpoint: sample-result rewrite baseline and plan`

### Phase 1：核心数据模型

- 新建或重构 `KimodoMarkerSampleResult`。
- 固定 `SampleDataLength = 70` 及四段 offset/count 常量。
- 增加安全访问器/编解码器，禁止调用方裸索引。
- 建立 `validMask` 的语义粒度。
- 将 enable 保持为独立状态，避免与 mask 混淆。
- 保留 `sampleTime` 和 `constraintMode`。
- 移除 SampleResult 中的 rawData、sourceRootWorldPose、冗余 CharacterPose/root 字段。

提交：`checkpoint: add canonical 70d sample result`

### Phase 2：采样与 Adapter

- 修改所有采样 API，使其返回 70 维 `sampleData` 和 valid mask。
- 扩展 `CharacterPoseMuscleAdapter`：
  - 49 维 body muscle ↔ Unity HumanPose；
  - 目标骨架 muscle handle ↔ rootTQ/footTQ；
  - FK 重建结果重新编码为 70 维。
- 删除采样侧 transformData 旁路。
- 对无效 pose 做显式 mask 处理。

提交：`checkpoint: migrate sampling to 70d sample data`

### Phase 3：Composer 与约束合成

- 实现同帧 SampleResult 分组。
- 按创建顺序实现最后创建优先。
- 明确 channel 级 mask 合并和 invalid 不回退规则。
- 支持 FullBody、Root2D、Effector、Mix。
- Composer 返回新对象，不原地修改输入。

提交：`checkpoint: implement last-created constraint composition`

### Phase 4：解算、Preview、Generate、Export

- 统一基础 FK → Root2D → Effector 的姿势链路。
- 保证 Preview、FullBody sampling、Generate、底层 export 使用同一套顺序。
- Root2D/Effector 使用 world space。
- 保持协议字段完整性，禁止输出半缺失 Effector。
- 关闭窗口时继续正确清理 Preview 状态。

提交：`checkpoint: apply canonical pose solve pipeline`

### Phase 5：测试与验证

- 对因破坏性升级暂时失效的旧测试做显式 Ignore/条件屏蔽，不删除。
- 增加 70 维布局、mask/enable、rootTQ/footTQ、Composer 优先级和解算顺序测试。
- 验证 FullBody sampling 无异常。
- 验证 Generate 正常。
- 使用 Unity FullDemo 编译验证。

提交：`checkpoint: restore regression coverage and build verification`

## 每个 checkpoint 必须记录

写入 `SAMPLE_RESULT_70D_REWRITE_CHECKPOINTS.md`：

- 提交 hash 和日期；
- 当前阶段；
- 已完成内容；
- 尚未完成内容；
- 已执行的检查命令及结果；
- 下一次恢复时的第一步。

## 恢复规则

如果中途掉线，先读取本文件和 checkpoint 日志，再运行：

```powershell
git status --short --branch
git log --oneline -8
```

不得跳过未完成阶段，也不得把多个阶段的未验证修改合并成一个无法回滚的提交。

## 暂不做的事情

- 不恢复旧 CharacterPose 作为核心原子类型。
- 不对 70 维数组直接做通用插值。
- 不重新引入旧 Playable IK 中间链路。
- 不修改 `C:\tmp\KimodoUnityBridge_FullDemo-main` 项目，只用于编译验证。
