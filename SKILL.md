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
2. `session_add({"kind":"character","character":"<scene name or hierarchy path>"})`; save the returned safe character name.
3. Generate with `kimodo_generate_animation`; save `request_id`; poll `kimodo_get_generation` to a terminal status.
4. Call `animation_analyze` for the completed animation; save `analysis_id`, then read `analysis_path` and its dense-KMB `motion_path`.
5. Call all three picture commands with that `analysis_id`: motion overlay, key poses, and 3D trajectory.
6. Compare the visual evidence with the prompt. Revise sparse constraints, endpoint poses, or the prompt and iterate.

Read the returned `session_json_path` whenever the complete Session state is required. A new Session is empty. `session_add` explicitly adds the scene humanoid, clip, or Animator content.

## Visual acceptance

- Key-pose images must show the requested action, direction, body state, contact/object relationship, and ending state.
- Inspect `keyframes` in their returned descending-saliency order. Inspect `foot_contact_changes` in their returned shortest-duration-first order, then verify the dense KMB contact track.
- Motion-overlay images must show the expected root path, displacement, orientation, and no unexplained drift.
- Trajectory images must show plausible root, pelvis, hand, and foot paths.
- Loop work requires inspecting first/last poses, root heading and position, foot-contact phase, and velocity continuity. A visible seam requires another generation/refinement pass.

## Pose work

Use `pose_get` to obtain a source pose and its writable Pose Cache marker. Keep the returned marker locator for `pose_set_root_transform` and `pose_set_muscle`. Use `full_data:true` when all muscle channels are needed. Use `pose_contract` to fit one or more hand/foot targets and keep its reported residual when judging a multi-effector fit.

Dataset animation names become concise natural-language prompts: preserve action, phase, direction/path, speed, contact/object, and ending state; remove take IDs, actor IDs, and internal variant suffixes that carry no movement meaning.
