---
name: kimodo-animation-loop-common
description: Shared execution, evidence, recognition, comparison, revision, and handoff contract for reusable Kimodo animation tasks.
---

# Common execution contract

[简体中文](common_zh.md)

This file is the active shared contract for the root Skill. It replaces the former four phase manuals with one loop and keeps the useful evidence rules from the historical recognition and comparison procedures. It coordinates Kimodo commands and Unity/project APIs as equal implementation routes for one animation task.

## Scope and task routing

This workflow focuses on Humanoid Clips, generation Poses, and simple Transform-driven props such as doors, balls, and weapons. For Animator Controllers, state machines, gameplay scripting, complex rigs, quadrupeds, vehicles, or physics, inspect the available Unity and project APIs and complete the supported parts through the best route; specialized frame-by-frame acting polish can be handed to an animator when needed.

Choose a primary route from the requested outcome; combine routes for dependent subtasks:

| Request | Route |
|---|---|
| Inspect or compare existing animation | Resolve and evaluate the existing Clips; skip generation and source edits. |
| Generate a new animation | Define criteria, generate candidates, then evaluate. |
| Improve, continue, or retarget an existing Clip | Inspect the source, choose an edit or generation route, and compare a derived output against the source. |
| Animate character/prop interaction | Coordinate character motion, prop motion, contact events, and Timeline integration; verify the assembled result. |
| Ambiguous intent | Inspect available context first; ask only about unresolved choices that materially change the result. |

## Command entry and state

The public Editor entry is:

```csharp
string schema = KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
string result = KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

Plan the complete visible Unity result before choosing tools. Kimodo commands, Unity Editor APIs, Timeline APIs, AnimationClip/AnimationCurve APIs, runtime APIs, and project-local tools are peer routes in the same execution loop. Honor a user-specified command or tool route and invoke it. When the user has not specified a route, choose the route that directly produces the requested result. Read the live schema or `kimodo.help` when available; when the connected Editor is unavailable, use the checked-in help as the starting reference and adapt to structured errors from the call. Use protocol command names, not internal C# handler names. With Unity CLI, execute the dispatcher in the connected Editor. Command frames use 60 FPS; use the documented Clip-relative or Timeline-global origin for each field. For each subtask, record `subtask → route → output → acceptance check`. If an operation is better represented by an allowed Unity or project API, use that API directly and record the route.

[`Command/help.json`](../Command/help.json) is the checked-in command reference; compare it with the connected Editor's live schema whenever that schema is available.

Resolve character, Clip, Timeline, and Session references through `scene.context` and the relevant command results. Use returned handles for scene objects, `{track,index}` for Pose slots, and asset paths for fields that accept paths.

The active Unity scene, Timeline, and Kimodo Session are an editable working pipeline. The agent may switch context and extend an existing Timeline by inserting tracks, clips, bindings, or synchronized prop motion. Preserve existing tracks and clips by default; prefer adding a track or making a derived Timeline over rewriting or rebuilding existing content. Modify existing content only when required by the task, and preserve a recoverable original first. After editing, verify the active scene/session, track bindings, clip ranges, and synchronization in the intended Timeline.

When removal is requested, move the affected files and associated Unity `.meta` files into the nearest archive directory and check affected references.

Coordinate-space contract:

- Public command positions and targets (`root`, `effector`, and `pose.set`) are expressed in Unity World space.
- Build point constraints using the supported Root2D, Effector, and FullBody compositions described by the live input schema. `root.rotation` and `effector.*.rotation` use Euler degrees `[x,y,z]`; omitted transform channels retain sampled values. The schema and structured command errors describe invalid transforms, missing root context, and duplicate frames.
- `animation.analysis.root_trajectory` is returned in World space. The analyzed Clip begins at frame zero, so its trajectory's zero origin is the World reference used by the command. When `animation.generate` consumes its `root_path`, generation resolves that World path once against the target character's World root position and planar heading.
- Local joint rotations and camera-specific fields keep their documented spaces; the World-space rule applies to position and target fields.

At the start of a task, keep a compact loop state:

```text
task route and current check
intent, explicit requirements, assumptions, and unknowns
project/scene/session, character/object references, and relevant tool versions
planned Clips and dependencies
subtask-to-route plan and produced assets/tracks
acceptance criteria and allowed semantic alternatives
confirmed Pose, root, effector, path, and object evidence
candidates: request_id, animation_id, seed, prompt, constraints, analysis_id
criterion results: verified, failed, or unknown
revision history, best candidate, attempt budget, and no-improvement count
capability evidence and unresolved choices per subtask
```

Navigate candidates through persisted Session history and returned IDs, keeping the public state and scene/analysis payloads in their existing sources.

## Route and check contract

Use the task routing above. Read-only discovery, including schema lookup and scene inspection, may precede route confirmation. Before generation or edits, name the target object or Clip, expected output, relevant criteria, and dependencies. Default to preserving the original and creating a derived output; apply existing user authorization without asking again.

For each subtask, distinguish the implementation route from its capability evidence:

| Capability | Required basis |
|---|---|
| Current Kimodo command | Present in the live schema; check current scene and runtime prerequisites. |
| Unity or project API route | Inspect the relevant API and integration, then verify the produced result in the current project. |
| Proposed capability | Check the package and live schema to establish its implementation state before relying on it. |
| Unavailable route | Record the command/API routes checked, then use another available route or report the remaining limitation. |

Schema presence proves an interface exists, not that its output meets the task. Keep criterion results (`verified`, `failed`, `unknown`) and an animator handoff separate from capability availability.

Use re-enterable checks, skipping work irrelevant to the route:

| Check | Evidence needed before dependent work |
|---|---|
| Intent and scope | Requested result, explicit limits, consequential choices, and harmless assumptions. |
| References and scene state | Intended project/scene, character and Avatar where required, source Clip, Timeline/session, and relevant prop transforms, pivots, and bounds. |
| Constraints and criteria | Confirmed coordinate/frame bases, interaction events, applicable tolerances, and a feasible check for each required result. |
| Execution | Submitted inputs and IDs, terminal result or API-produced assets, and the actual evaluated Timeline bindings/ranges. Inspection-only tasks reuse existing Clips. |
| Verification | Candidate-matched measurements/images and playback when needed; changes trigger checks on affected dependencies. |

Resolve missing facts through available context first. Ask when unresolved alternatives would materially change the requested result; hold dependent generation/edits until answered and continue independent work. Keep each criterion `unknown` until matching evidence establishes its result. Entering a check does not require extra confirmation.

## Response and asynchronous rules

- Check top-level `ok` before reading domain fields.
- Command errors use `ok=false` and `error={code,message,details?}`.
- A successful poll can still have task `status` `failed` or `canceled`; report that terminal state and its error.
- Save the exact `request_id`; poll at a fixed interval until a terminal state or bounded timeout.
- Apply this to any asynchronous command returning a pending `request_id`, including `server.install` and `animation.generate`. A polling timeout leaves the task pending, not failed or canceled; retain the ID for resumption.
- Retain and poll a running request by its ID; cancel it when the task should stop.
- Treat warnings and non-blocking checks as evidence to inspect.
- Preserve completed Clips and use derived outputs for revisions.

## Intent-to-criteria loop

Apply only the work required by the selected route; inspection/comparison does not generate or edit source animation:

1. Resolve participating characters and objects from the current scene. Identify the visible result, action order, contact points, duration, and ending state. State assumptions.
2. Identify independently testable action beats. Split into Clips when local revision and continuation needs justify it. Record dependencies between character Clips, prop motion, and Timeline bindings.
3. Select relevant criteria, distinguishing required constraints from quality preferences. Criteria may be semantic, spatial, temporal, contact-related, structural, visual, or playback-related. State their observable checks and tolerances where needed, and judge each criterion against its own evidence.
4. Gather the smallest evidence needed: `scene.context`, `pose.get`, `pose.analysis`, an existing Clip analysis, or a confirmed object Transform. When a point-constraint preview will help assess the requested pose, pass the same constraints to `pose.analysis` and inspect its image, measured world-space joints, target distances, and warnings. Set `combine=true` to render multiple previews together as opaque poses in one SceneView image. Its `camera` object is shared with `animation.analysis`; SceneView lighting, skybox, gizmos, and grid keep their current settings unless explicitly supplied. For measurements of the generated pose, sample the completed Clip with `pose.get` and analyze that sample.
5. When a task calls for an action-specific starting Pose or Clip continuation, resolve a suitable source with `pose.get` and use its returned `{track,index}` reference. If such a Pose is required and no suitable source exists, select or generate a preliminary Clip, materialize the intended frame, and inspect it with `pose.analysis`. When no explicit action Pose is needed, generation can use the Avatar rest-pose initialization described by the command schema.
6. For generation, construct a short semantic prompt and supported structured constraints from the user's explicit requirements and confirmed scene evidence. Map explicit root position, heading, path, Pose, hand/foot target, loop, duration, and continuation requirements to supported Kimodo fields; use confirmed evidence for unspecified details and leave other optional fields at their schema defaults. Use Unity or project APIs for object curves, Timeline tracks, bindings, and other asset edits. Inspect a `pose.get` result and use its returned reference for a continuation source. Generate dependent Clips in order from their evaluated predecessor endings. Report whether a constraint used an explicit Pose or the command's Avatar rest-pose initialization.
7. Generate or edit candidates only when required, poll asynchronous requests, and retain the IDs and submitted settings. For API outputs without Kimodo IDs, retain the actual asset/track references. Inspection routes use the existing output as their subject.
8. Evaluate relevant hard requirements, then structure, scene interaction, and playback/quality criteria as applicable. Use `animation.analysis` for Humanoid Clip evaluation; use evaluated scene measurements and appropriate APIs for prop/Timeline checks. Open returned images before visual claims. Validate the evidence-to-output mapping below before accepting any criterion.
9. When there are multiple candidates or the user requests a comparison, use identical conditions and decide each criterion independently.
10. When evidence shows a criterion failed, target the relevant input and create a derived candidate. When evidence is incomplete, gather the corresponding measurement or report `unknown`.
11. Apply the stopping and handoff rules below. Submit a revised request when new evidence or a changed input gives it a distinct purpose.

## Keyframe-led generation and revision

For a task that depends on keyframe-level motion:

1. Inspect the available Clip and analysis first. When usable keyframe data is absent, generate an initial candidate from the task's established semantic prompt and supported constraints, then obtain its full `animation.analysis` result. Reuse `generation.get`'s `post_analysis` when it completed successfully and contains the full usable keyframe data; when it is missing, failed, or incomplete, run `animation.analysis` on the completed Clip.
2. Use analysis keyframes as observations and frame-selection hints. Inspect relevant frames with `pose.get` and the available analysis evidence, then identify and edit the actual curves through the Unity AnimationClip/AnimationCurve API or another verified project API. Preserve the original Clip and make a derived candidate.
3. Revise only keys that conflict with the declared action, phase, trajectory, contact, or endpoint criteria, or are demonstrably redundant. Keep keys needed to preserve intended phase changes, contacts, and transitions. Record which frames/curves changed and the evidence that motivated each change.
4. After each generation or keyframe edit, evaluate the candidate with the same criteria and comparable analysis settings, then compare it with the immediately previous candidate and the current best. Keep the best candidate that satisfies hard requirements. If a new candidate does not improve on the immediately previous candidate under the declared target criterion, reject that branch, retain the best candidate, and stop that revision loop.

## Clip transition constraints

Represent every transition between adjacent animation Clips with `animation.generate`'s `inout` constraint and completed source Clips for the relevant In and/or Out boundaries. Inspect the generated `inout_sampling` and evaluate the assembled Timeline seam.

When a transition is short, increase the In/Out `window_frames` and, where useful, `sample_count` to provide a wider boundary context. The window is capped by available source-Clip frames and generated duration, and the requested sample count must fit distinct model frames. For `outside`, context is padded and cropped while preserving the requested output duration; for `inside`, the wider windows constrain the returned Clip's own start/end. Use the available source range and returned `inout_sampling` to determine whether the transition has enough context for verification.

## Prompt wording

Write a concise natural-language action prompt from: action, phase, trajectory/direction, speed, body state, object/contact, and intended ending. Dataset-style labels often encode these as tokens such as `walk_ff_225_stop`, `jog_arc_cw_loop`, or `two_hands_walk_ff_start`.

## Recognition contract

Use recognition to test the task's stated intent against evidence. When action semantics must be distinguished, use alternatives supplied by the caller or derive relevant alternatives from the prompt and acceptance criteria. Treat filenames, Clip names, candidate order, IDs, and saliency as provenance or navigation data, not semantic evidence.

Use only relevant tasks from this set:

```text
visual: trajectory_shape, action_semantics, visual_motion_quality, contact_pattern
structured: loop_endpoint, keyframe_heading, trajectory_turn,
            trajectory_length, phase_track, endpoint_pose, motion_profile,
            force_line
```

Allowed visual values remain deliberately small:

```text
trajectory_shape: closed_loop | open_path | near_static | unclear
visual_motion_quality: smooth_appearance | visible_break | unclear
contact_pattern: alternating | left_dominant | right_dominant | unclear
```

For every task, return a value, confidence, and evidence. Use structured fields, readable images, and playback or temporal measurements according to the criterion. Mark a result `unknown` or `unclear` when the available evidence is absent, unreadable, or conflicting. Use `smooth_appearance` for the static image and temporal evidence for playback smoothness. Read structured trajectory values from named returned fields.

For `force_line`, read `clip.motion_profile.force_line`. Compare the left and right shoulder-to-hand sagittal-angle delta sums over the full Clip and matching `phase_track` intervals. `imbalance_ratio` is `abs(left-right) / max(left,right)`; treat `balanced` as true only when the result status is `ok` and the ratio is at most `0.15`. Mark the criterion unknown when status is `insufficient_evidence`. This is a sampled arm-swing symmetry measure, not a physical force estimate.

When comparing action semantics, use the stated or prompt-derived alternatives and phase/pose/contact evidence. Check phase coverage across relevant frames so a single contact or anchor frame is interpreted in context. If the available evidence cannot distinguish alternatives, report `unknown`.

## Comparison contract

Analyze and render each candidate separately with the same picture request, resolution, and evidence procedure. Validate the candidate-to-image mapping before comparing. Compare only criteria declared for the task, such as semantic correctness, trajectory coherence, foot contacts, required phase coverage, required Pose coverage, expression strength, endpoint continuity, or a declared neighboring-Clip transition.

For each criterion, report `candidate_1`, `candidate_2`, `tie`, or `unknown`, with a short reason and evidence. Judge candidates criterion by criterion using the declared target and matching evidence. Prefer one candidate when the difference is decisive; otherwise report a tie or insufficient evidence.

## Evidence boundaries

For each acceptance claim, retain a compact provenance record:

```text
evaluated candidate/source asset and request/animation/analysis IDs when supplied
project, scene, Session and intended Timeline with clip/track bindings
sample frame or interval, frame origin, and analysis/render settings
relevant character/prop transforms, offsets, and source asset state
returned evidence path or measurement field, criterion result, and limitation
```

Use existing IDs and Session history to locate evidence. When the tool does not return an ID, cite its actual asset, frame, and evidence path. Before reusing evidence, verify that the evaluated source and relevant scene/Timeline state still match. Reuse evidence for unchanged dependencies and recheck prop-follow samples after character Clip changes, or interaction evidence after binding, offset, or prop-state changes. After integration, confirm that each image maps to the intended candidate, Timeline, and camera framing.

Report these separately:

- **Visual**: what the opened image visibly shows.
- **Structured**: what the command returned as a measured field.
- **Playback**: what was observed during actual playback or a supported runtime check.

Use static pictures for visible pose and composition; use supported temporal measurements or actual playback to assess velocity/acceleration smoothness, foot sliding, popping, and continuity. State the sampling limits of numerical measurements and distinguish them from full-playback observation. Mark missing evidence `unknown`, not failed. Use multi-frame measurements for interval claims; a single sampled joint distance describes that sample's relation.

Attribute failures before choosing a revision: use structured errors to correct invalid references, prerequisites, or request fields; revise generation inputs when a completed character Clip fails its criteria; inspect bindings, offsets, and synchronization for prop integration failures; and run the relevant analysis, sampling, or playback route to close evidence gaps. More than one category may apply.

## Revision and human handoff

Use a targeted revision table internally:

```text
failed criterion → supporting evidence → changed input → new candidate → result
```

Before a generation/revision loop, record the user's candidate or time budget and stopping condition when supplied. Apply a user-specified stopping condition even when no separate budget is given, and apply any user-specified budget as the attempt cap. Use the local budget of one initial candidate and up to two targeted revisions only when the user provides neither a budget nor a stopping condition. Track attempts and shared candidates, and report cost or runtime warnings while continuing within available authorization. Keep the count stable when criteria or loop labels change.

Apply the user's stopping condition when provided, stopping earlier when a hard operation is unavailable or the remaining decision requires human artistic judgment. Without a user-provided stopping condition, stop when the required criteria pass or two consecutive targeted attempts bring no meaningful improvement under comparable evidence. Apply the user's candidate or time budget as the cap; use the local default budget only when the user provides neither a budget nor a stopping condition. Define improvement against the declared criterion; changing a Seed or receiving `completed` is not evidence of improvement. Missing evidence calls for inspection before another generation attempt.

Keep the best candidate that satisfies hard requirements and reject regressions on previously passing requirements. If none satisfies them, preserve the best partial output and report its failed/unknown criteria. Limit each revision to affected outputs and recheck their dependencies. A missing command alone is not a stop condition: inspect the relevant package and API route within the user's tool restrictions before reporting a tooling limitation. If required tooling cannot be obtained, stop dependent execution, report the issue, and wait for the user's repair rather than substituting a subagent.

For simple props, use Kimodo commands where exposed and Unity APIs where needed to author Transform curves, Timeline tracks, or hand attachment. Keep them tied to the character candidate through shared contact frames, offsets, Timeline range, and evidence IDs. Verify both character pose and object motion; if an API route is genuinely unavailable, mark that criterion unknown and explain the limitation.

## Result report

Name the selected Clip/assets and intended Timeline. Report required criteria as `verified`, `failed`, or `unknown`, each with its matching evidence and relevant limitation. Distinguish request acceptance, task terminal status, character-Clip acceptance, and whole-interaction acceptance. A required unknown prevents a claim of complete verification.

Summarize targeted attempts, best result, failure category, and the actual stop reason. If work remains, identify the affected interval, body part, curve, binding, or missing evidence and the precise next action. A toolchain repair or evidence gap need not be an animator handoff; reserve that handoff for work that actually needs an animator.

When a relevant symptom appears, consult [`known-issues.md`](known-issues.md) for observations and optional workarounds. Use this common contract for the active workflow; archived manuals can provide historical context.
