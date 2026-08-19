---
name: character-animation-cli-unity-zh
description: 通过维护中的 Character Animation CLI Unity 命令发现、生成、分析、比较并迭代人形动画。
---

# Character Animation CLI Unity

使用公开 Editor 入口：

```csharp
using CharacterAnimationCli.Unity.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

以实时 schema、`kimodo_help`、返回的 ID/名称/路径和错误 envelope 为准。先阅读 [TOOLS.md](TOOLS.md) 的共享规则，再按任务选择：

- [识别](skills/recognition.md)：理解动画图像并与要求的语义比较。
- [优化](skills/optimization.md)：分析、检查、修改并重新验证不可变 Session Clip。
- [生成](skills/generation.md)：将名称改写为 Prompt，规划约束并生成追加 Clip。

始终创建/选择 Session，显式加入角色和内容，保存返回的 opaque handle，将生成轮询到 `completed`、`failed` 或 `canceled`，并实际检查 `animation_analyze` 返回的 PNG 后再声称视觉通过。

英文入口：[SKILL.md](SKILL.md)。
