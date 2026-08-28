# KimodoUnityBridge 简要交接说明

## 这是什么

KimodoUnityBridge 是 Unity Editor 中的公开动画工作流入口，用于发现、生成、分析、比较和修正角色动画。命令入口是：

```csharp
using KimodoUnityBridge.Command;
command_dispatcher.GetCommandDefinitionsJson();
command_dispatcher.Invoke(commandName, argumentsJson);
```

## 阅读顺序

1. `SKILL.md`：安装、任务路由、产品边界和 API Help 入口。
2. `skills/recognize.md`：判断动画是否表达指定动作。
3. `skills/compare.md`：比较两个动画的相对质量。
4. `skills/generate.md`：创建新动画或修正后的派生结果。
5. `Command/help.json`：具体命令、参数、必选关系和结构说明。

实时 schema、API Help、命令返回值和错误信息优先于流程文档中的假设。

## 标准生成闭环

```text
理解请求结果
  → 存在源结果时先检查
  → 只选择请求所需的操作与约束
  → 生成
  → 检查生成结果
  → 证据显示可修正问题时创建派生结果并迭代
```

关键规则：

- 循环请求不能证明结果已经无缝，生成后仍要检查可用边界证据。
- 范围操作只用于请求明确的裁剪、拼接或提取，不能代替循环生成或验证。
- 分析选择的帧是证据，不会自动成为生成约束。
- 已完成动画结果应保留，修正时创建派生结果。

## 证据要求

报告视觉通过前必须实际检查返回的视觉证据。静态图片不能证明完整时序、滑步、跳变或加速度；没有播放或密集采样时，这些属性应报告为 `not_verified`。

## 维护提示

本文件是公开工作流的简要交接说明，不包含题目答案、私有评分或评测结果。修改操作规则时，应同步检查 `SKILL.md`、三个 `skills/*.md` 和 `Command/help.json` 的职责边界，并运行 Skill 校验与 `git diff --check`。
