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

## Semantic recognition and candidate selection

When recognition or pairwise quality selection is requested, convert the text request into observable acceptance criteria before judging a clip: action, phase (loop/start/stop/transition), direction or turn, root displacement, contacts, timing/seam, and style qualifiers.

- Analyze both candidates in one `animation_analyze` call and preserve their original order. Verify the returned clip/analysis handles before mapping evidence back to A/B.
- Judge semantics before generic quality: first confirm the requested action and phase, then direction/root trajectory, contacts/timing, seam continuity, and style. Saliency, keyframe count, displacement magnitude, or contact count alone are not semantic proof.
- Interpret direction relative to the character's forward axis and observed pose/root motion. Never infer quality from filenames, `_a`/`_b` suffixes, candidate order, or world-axis assumptions.
- For loops inspect first/last pose and root velocity; for starts/stops inspect motion ramp and settling; for turns inspect heading change and path curvature. Use the opened composite PNG together with structured metrics.
- Record a concise per-candidate comparison and return `match`, `not_match`, or `insufficient_evidence`. If the requested attribute cannot be established from the returned structured data and inspected image, report insufficient evidence rather than guessing.

Humanoid workflows provide body/contact semantics. Renderable Mesh objects are also analyzable through the Mesh-only path, but do not provide Humanoid foot/contact evidence. Completed Session Clips are immutable; corrections and derived outputs append new Clips. If a public command cannot perform a requested edit, report the boundary instead of claiming completion.

The Chinese entry point is [SKILL-zh.md](SKILL-zh.md).
