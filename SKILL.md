---
name: character-animation-cli-unity
description: Discover, generate, analyze, compare, and refine humanoid animation through the maintained Character Animation CLI Unity commands.
---

# Character Animation CLI Unity

Use the public Editor entry point:

```csharp
using CharacterAnimationCli.Unity.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

Treat the live schema, `kimodo_help`, returned IDs/names/paths, and error envelopes as authoritative. Read [TOOLS.md](TOOLS.md) for shared execution rules, then route to:

- [Recognition](skills/recognition.md): interpret animation images and compare them with requested semantics.
- [Optimization](skills/optimization.md): analyze, inspect, revise, and re-validate immutable Session Clips.
- [Generation](skills/generation.md): turn names into prompts, plan constraints, and generate appended Clips.

Always create/select a Session, add the character/content explicitly, preserve opaque returned handles, poll generation to `completed`, `failed`, or `canceled`, and inspect the PNG returned by `animation_analyze` before claiming visual acceptance.

The Chinese entry point is [SKILL-zh.md](SKILL-zh.md).
