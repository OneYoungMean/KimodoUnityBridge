# KimodoUnityBridge 简要交接说明

## 这是什么

KimodoUnityBridge 是 Unity Editor 中的公开动画工作流入口，用于发现、生成、分析、比较和修正角色动画。命令入口是：

```csharp
using KimodoUnityBridge.Command;
command_dispatcher.GetCommandDefinitionsJson();
command_dispatcher.Invoke(commandName, argumentsJson);
```

## 阅读顺序

1. `SKILL.md`：总流程、意图判断和语义验收。
2. `TOOLS.md`：命令边界、证据要求和不可变性规则。
3. `skills/recognition.md`：候选动画识别与 A/B 选择。
4. `skills/generation.md`：文本到动画的生成流程。
5. `skills/optimization.md`：已有 Clip 的诊断与追加修正。

实时 schema、`kimodo_help`、命令返回的 ID/名称/路径和错误信息优先于文档中的假设。

## 标准生成闭环

```text
意图预判
  → 有源 Clip/候选时分析
  → 需要姿势约束时按需 pose_get
  → 生成
  → 分析生成结果
  → 失败且公开命令支持时追加新 Clip 并迭代
```

意图预判固定确认四件事：

- 是否需要循环；
- 是否需要重定向到其他角色；
- 是否需要裁剪或拼接；
- 是否需要姿势/关键帧约束。

关键规则：

- `loop:true` 是生成循环的机制，不用 `record_range`、Timeline 或再次 Record 创建循环。
- 已接受且无回退警告的 `loop:true` 依赖生成器循环契约，不额外做首尾接缝检查。
- `record_range` 只用于意图明确的裁剪、拼接或提取指定 Session 区间。
- `animation_analyze` 返回的 keyframes 是分析证据，不是生成约束；需要约束时使用 `pose_get`。
- 已完成 Clip 不可变，修正和派生结果都追加为新 Clip。

## 证据要求

报告视觉通过前必须实际打开 `animation_analyze` 返回的 PNG。静态图片不能证明完整时序、滑步、跳变或加速度；没有播放或密集采样时，这些属性应报告为 `not_verified`。

## 维护提示

本文件是公开工作流的简要交接说明，不包含题目答案、私有评分或评测结果。修改操作规则时，应同步检查 `SKILL.md`、`TOOLS.md` 和相关 `skills/*.md`，并运行 Skill 校验与 `git diff --check`。
