---
name: kimodo-unity-bridge
description: Discover, generate, analyze, compare, and refine character animation through maintained KimodoUnityBridge commands, with Humanoid workflows and Mesh-only analysis.
---

# KimodoUnityBridge

Use the public Editor entry point:

```csharp
using KimodoUnityBridge.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

The live schema, `kimodo_help`, returned IDs/names/paths, and error envelopes are authoritative. Read [TOOLS.md](TOOLS.md) for the shared execution contract, then route to:

- [Recognition](skills/recognition.md): decide whether rendered motion evidence matches a text request.
- [Generation](skills/generation.md): turn motion semantics into a new appended Session Clip.
- [Optimization](skills/optimization.md): diagnose an existing Clip and append a corrected variant.

Non-negotiable guardrails:

1. After Unity finishes compiling/importing, install or refresh the QuickServer once with `kimodo_install_server({})` before runtime-dependent commands.
2. Query the schema/help, create or select a Session, and add scene/project content explicitly.
3. Preserve opaque Session, Clip, pose, path, request, and picture handles exactly as returned.
4. Poll asynchronous generation to `completed`, `failed`, or `canceled`.
5. Open `pictures.image_path` from `animation_analyze` before reporting visual `passed`.

Humanoid workflows provide body/contact semantics. Renderable Mesh objects are also analyzable through the Mesh-only path, but do not provide Humanoid foot/contact evidence. Completed Session Clips are immutable; corrections and derived outputs append new Clips. If a public command cannot perform a requested edit, report the boundary instead of claiming completion.

The Chinese entry point is [SKILL-zh.md](SKILL-zh.md).
