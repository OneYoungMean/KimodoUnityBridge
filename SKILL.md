---
name: kimodo-unity-animation
description: Discover, generate, inspect, and refine humanoid animation through Kimodo commands in the current Unity Editor.
---

# Kimodo Unity Animation

Use the public Editor entry point:

```csharp
using CharacterAnimationCli.Unity.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

Expose exactly the live `schema.tools` entries as tools. Results always use the vNext `ok` envelope. Treat command definitions, returned IDs, names, paths, and cache locators as authoritative opaque handles.

## Standard workflow

1. `kimodo_help({})`, then `session_get_or_create({"name":"<stable name>"})`.
2. `session_add({"kind":"character","character":"<scene name or hierarchy path>"})`; save the returned safe character name. Add a project clip or Animator explicitly with `kind:"clip"` or `kind:"animator"`; Animator import creates Timeline-composed `transition_clip` records only for same-Layer State-to-State transitions. Inspect the 128-clip warning and use `ignore_warning:true` only when full expansion is required.
3. Generate with `kimodo_generate_animation`; save `request_id`; poll `kimodo_get_generation` to a terminal status.
4. Call `animation_analyze` for the completed animation. A matching immutable Clip and effective analysis options return the existing result; save `analysis_id` and read its `analysis_path` only when that sparse detail is needed. Its `motion_path` remains the dense-KMB attachment.
5. Call all three picture commands with that `analysis_id`: motion overlay, key poses, and 3D trajectory.
6. Compare the visual evidence with the prompt. Revise sparse constraints, endpoint poses, or the prompt and iterate.

Read the returned `session_json_path` as a compact Session index, then read a referenced analysis path only when necessary. Every completed Clip appended to a Session is immutable: never overwrite, retime, or replace it; create a new appended Clip for a generated, recorded, retargeted, or corrected result. A new Session is empty. `session_add` explicitly adds the scene humanoid, clip, or Animator content.
A transition is a logical composite over Timeline segments, not a baked AnimationClip asset; unsupported Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped.

## Visual acceptance

- Key-pose images must show the requested action, direction, body state, contact/object relationship, and ending state.
- Inspect `keyframes` in their returned descending-saliency order. Inspect `foot_contact_changes` in their returned shortest-duration-first order, then verify the dense KMB contact track.
- Motion-overlay images must show the expected root path, displacement, orientation, and no unexplained drift.
- Trajectory images must show plausible root, pelvis, hand, and foot paths.
- Loop work requires inspecting first/last poses, root heading and position, foot-contact phase, and velocity continuity. A visible seam requires another generation/refinement pass.

## Pose work

Use `pose_get` to obtain a source pose and its writable Pose Cache marker. Keep the returned marker locator for `pose_set_root_transform` and `pose_set_muscle`. Use `full_data:true` when all muscle channels are needed. Use `pose_contract` to fit one or more hand/foot targets and keep its reported residual when judging a multi-effector fit.

Dataset animation names become concise natural-language prompts: preserve action, phase, direction/path, speed, contact/object, and ending state; remove take IDs, actor IDs, and internal variant suffixes that carry no movement meaning.
