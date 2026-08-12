---
name: kimodo-unity-animation-zh
description: 在当前 Unity Editor 中发现并执行 Kimodo 人形动画生成、Pose、Constraint、分析、语义关键姿势审查、循环接缝检查、录制与 Retarget 命令。
---

# Kimodo Unity 动画命令

使用公开 Editor 入口：

```csharp
using KimodoUnityBridge.Command;

string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

将 `schema.tools` 通过 `Invoke` 暴露。以实时 Schema 和返回错误为准。

## 工作流

1. 阅读 [TOOLS.md](TOOLS.md)，了解稳定工作流、执行规则和动画质量门。
2. 调用 `kimodo_help({})`，并在每个非平凡命令前查询实时 Help。
3. 按返回的工作流执行，并原样保存安全名称、locator、`analysis_id` 和 `request_id`。
4. 将生成轮询到终态，确认动画已追加，并在报告完成前执行质量门。

当前 Constraint 细节通过 `kimodo_help({"section":"constraints"})` 查询；选择非默认模型或 Text Encoder 时调用 `kimodo_help({"section":"models"})`。

英文原文：[SKILL.md](SKILL.md)。