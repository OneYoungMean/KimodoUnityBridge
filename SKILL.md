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

## Intent preflight

Before selecting commands, answer these fixed questions for the request:

- Does the requested animation need to loop?
- Does it need retargeting to another character?
- Does it need cropping or splicing?
- Does it need pose/keyframe constraints?

Record each answer as `yes`, `no`, or `unspecified`, with one short reason. If one or two answers are ambiguous and would change the output, ask the user before taking that branch. For a large batch with many ambiguities, use and report these safe defaults: no loop unless the request explicitly says loop/continuous cycle; no retargeting; no cropping or splicing; and no pose constraints unless the request specifies contacts, endpoints, or a concrete pose requirement. Never silently invent a range, target character, or keyframe.

Map positive answers to commands: `loop=yes` means `kimodo_generate_animation(loop:true)`; `retarget=yes` means `kimodo_retarget_animation`; and `crop_or_splice=yes` means `kimodo_record_range` with an explicit half-open range. `kimodo_record_range` is not a looping, repetition, or generic post-generation export mechanism. If `loop:true` is accepted without a fallback warning, rely on the generator's loop contract and do not add a separate seam-check or recording pass. If the request needs a generated result materialized but the public schema has no distinct materialization command, report that boundary instead of using `record_range` as a workaround.

## Closed-loop generation workflow

For generation or correction tasks, use this sequence after intent preflight:

1. If a source Clip or current candidate exists, analyze it with `animation_analyze` before generating or editing. For pure text-to-motion with no source or candidate, state that the initial analysis is unavailable; do not invent one.
2. Use the intent answers and analysis to decide whether Pose/keyframe constraints are needed. When `pose_constraints=yes`, call `pose_get` on relevant local frames before generation and pass the returned Pose references as sparse constraints. Analyzer keyframes are evidence, not constraints.
3. Generate with `kimodo_generate_animation`; use `loop:true` when `loop=yes`.
4. Analyze the generated output with `animation_analyze`. For an accepted `loop:true` request, this checks the requested motion and output identity, not a separate loop-seam contract.
5. If a requested property fails and the public workflow can express a correction, return to step 2 and append a new Clip. Do not add a recording pass unless `crop_or_splice=yes`.

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
- For generated loops accepted with `loop:true`, rely on the generator's loop contract rather than adding a separate first/last-pose seam check. For existing or imported loops without that contract, inspect first/last pose and root velocity; for starts/stops inspect motion ramp and settling; for turns inspect heading change and path curvature. Use the opened composite PNG together with structured metrics.
- Record a concise per-candidate comparison and return `match`, `not_match`, or `insufficient_evidence`. If the requested attribute cannot be established from the returned structured data and inspected image, report insufficient evidence rather than guessing.

Humanoid workflows provide body/contact semantics. Renderable Mesh objects are also analyzable through the Mesh-only path, but do not provide Humanoid foot/contact evidence. Completed Session Clips are immutable; corrections and derived outputs append new Clips. If a public command cannot perform a requested edit, report the boundary instead of claiming completion.

Chinese guidance is included in the bilingual generation and optimization references; no separate `SKILL-zh.md` file is currently shipped.
