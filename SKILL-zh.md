---
name: kimodo-unity-bridge-zh
description: 通过维护中的 KimodoUnityBridge 命令发现、生成、分析、比较并迭代角色动画，兼顾 Humanoid 与 Mesh-only 分析。
---

# KimodoUnityBridge

使用公开 Editor 入口：

```csharp
using KimodoUnityBridge.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

以实时 schema、`kimodo_help`、返回的 ID/名称/路径和错误 envelope 为准。先阅读 [TOOLS.md](TOOLS.md) 的共享执行契约，再按任务选择：

- [识别](skills/recognition.md)：判断渲染出的动作证据是否符合文字要求。
- [生成](skills/generation.md)：把动作语义转换为新的追加 Session Clip。
- [优化](skills/optimization.md)：诊断已有 Clip，并追加修正版。

不可省略的护栏：

1. Unity 编译/导入完成后，先调用一次 `kimodo_install_server({})`，再执行依赖运行时的命令。
2. 查询 schema/help，创建或选择 Session，并显式加入场景/项目内容。
3. 原样保存 Session、Clip、Pose、Path、Request 和图片路径等 opaque handle。
4. 将异步生成轮询到 `completed`、`failed` 或 `canceled`。
5. 实际打开 `animation_analyze` 返回的 `pictures.image_path` 后，才能报告视觉 `passed`。

Humanoid 工作流提供身体/接触语义；可渲染 Mesh 也能走 Mesh-only 分析路径，但不能据此声称 Humanoid 脚接触证据。已完成的 Session Clip 不可变；修正和派生结果必须追加新 Clip。公开命令无法执行的修改要报告边界，不能声称完成。

英文入口：[SKILL.md](SKILL.md)。
