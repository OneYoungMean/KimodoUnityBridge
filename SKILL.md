---
name: kimodo-unity-animation
description: Discover and run Kimodo humanoid animation generation, pose, constraint, analysis, semantic key-pose review, loop-seam inspection, recording, and retargeting commands in the current Unity Editor.
---

# Kimodo Unity Animation Commands

Use the public Editor entry point:

```csharp
using KimodoUnityBridge.Command;

string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

Expose `schema.tools` through `Invoke`. Treat the live schema and returned errors as authoritative.

## Workflow

1. Read [TOOLS.md](TOOLS.md) for the stable workflow, execution rules, and animation quality gate.
2. Call `kimodo_help({})`, then request the live help for each non-trivial command.
3. Follow the returned workflow and preserve returned safe names, locators, `analysis_id`, and `request_id`.
4. Poll generation to a terminal status, confirm the animation was appended, and apply the quality gate before reporting completion.

Use `kimodo_help({"section":"constraints"})` for current constraint details and `kimodo_help({"section":"models"})` when selecting a non-default model or text encoder.

The Chinese counterpart is [SKILL-zh.md](SKILL-zh.md).