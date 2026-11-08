---
name: kimodo-unity-bridge
description: Generate, inspect, compare, retarget, and improve Humanoid animation and simple prop interactions in Unity projects with KimodoUnityBridge installed. Runs a three-stage Prepare → Execute → Verify workflow with gated evidence and per-criterion reporting.
---

# KimodoUnityBridge

Use this Skill to create, continue, retarget, inspect, compare, or improve Humanoid animation and simple prop interactions in Unity with KimodoUnityBridge.

## Three-Stage Workflow

Run every task as three gated stages. Do not start a later stage until the earlier stage's exit evidence exists, and re-enter an earlier stage when one of its inputs changes.

| Stage | Goal | Exit evidence |
|---|---|---|
| 1. Prepare | Resolve context, intent, and acceptance criteria | Confirmed character / Clip / Timeline / Session references, criteria with check methods, dependency plan |
| 2. Execute | Produce or edit the artifact | Terminal request status or API result, plus asset path, `{track,index}`, or Timeline binding |
| 3. Verify | Prove the criteria and report | Opened images, measured fields, per-criterion `verified` / `failed` / `unknown` |

Preferred entry points for in-process Unity callers:

```csharp
string result = KimodoUnityBridge.Api.Animation.Generate(argumentsJson);
string analysis = KimodoUnityBridge.Api.Animation.Analysis(argumentsJson);
string status = KimodoUnityBridge.Api.Generation.Get(argumentsJson);
string inspection = KimodoUnityBridge.Api.Clip.Inspect(argumentsJson);
string settings = KimodoUnityBridge.Api.Clip.SetSettings(argumentsJson);
```

These `Api.*` methods execute the operation directly and return the same JSON contract as the corresponding command.

The JSON dispatcher remains the external host adapter:

```csharp
string schema = KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
string result = KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

Use public command names (never internal C# handlers). In hosts that expose the bridge through an editor-script tool, call the dispatcher through that tool. Read the live schema first; fall back to [`Command/help.json`](Command/help.json) only when the connected Editor is unavailable.

### Stage 1 — Prepare

1. State the result that should be visible in Unity: action beats, ordering, contacts, duration, end state, and the assumptions filling any gap. Ask only about choices that would materially change that result.
2. Read `session.context` as the authoritative source for characters, Clips, Timeline ranges, and object references. Check `session.history` before creating new work so existing candidates and analyses are reused.
3. Resolve every reference you intend to use: scene character name or `object_ref`, loaded Clip name or `Assets/` `Packages/` path, Pose slot as `{track,index}`, Timeline asset path. Never invent handles.
4. Split the action into independently checkable beats and record dependencies between character Clips, prop motion, and Timeline bindings. Split into multiple Clips only when local revision or continuation genuinely needs it.
5. Write acceptance criteria before generating: separate necessary constraints from quality preferences, and give each an observable check method and tolerance. Provide semantic alternatives where action meaning must be distinguished.
6. Collect only the evidence the chosen path needs: `pose.get` for a specific frame or scene/rest pose, `pose.analysis` for previewed constraints, existing `animation.analysis` results, or confirmed object Transforms. When a dedicated action start pose is required and no source exists, generate an initial Clip and materialize the frame with `pose.get`.
7. Fix the frame and space basis explicitly: commands run at 60 FPS; point-constraint `frame` is Clip-relative; analysis and `pose.get` temporal frames are Timeline-global. Positions and targets are World space; rotations are Euler degrees.

Keep this loop state current, in the format from [tools/common.md](tools/common.md): intent, references, planned Clips, subtask-to-path plan, criteria, collected Pose/root/effector evidence, candidate records (`request_id`, seed, prompt, constraints, `analysis_id`), per-criterion results, and revision history.

**Gate:** if the target character, source Clip, target Timeline, or the necessary criteria are still unknown, do not generate or edit.

### Stage 2 — Execute

1. Skip generation and source-Clip editing entirely when the request is inspection or comparison only.
2. Choose an implementation path per subtask — Kimodo command, Unity/Timeline API, `AnimationClip`/`AnimationCurve` API, or project-local tooling — and record `subtask → path → artifact → check`. Verify the API exists before relying on it.
3. For `animation.generate`:
   - Write one short comma-free semantic sentence (25 words or fewer) covering action, stage, direction/path, speed, body state, contacts, and expected end state. Put exact numbers in structured constraints, not prose. Be explicit about stride and arm swing for locomotion; loop prompts that only say "walk loop" tend to produce near-static motion.
   - `constraints` point form: `frame` (Clip-relative) with `pose` `{track,index}` (FullBody, optional root/effector), `root` alone (Root2D), or `root` + `effector` (Effector). `effector` alone is invalid; empty transforms are invalid; omitted Transform channels inherit sampled values.
   - `root_path` comes from `animation.analysis` → `root_trajectory.path`, and constrains only start and end; add point constraints for intermediate route locations.
   - `inout` boundaries must reference a completed source Clip. Increase `window_frames` (and `sample_count` when useful) for short transitions; inspect `inout_sampling` afterwards to judge whether context was sufficient.
   - For looped output, set `loop` and confirm the resulting Clip's loop setting with `clip.inspect`; the copied tail starts on the core content's terminal frame and makes the Clip asset longer than the requested Timeline duration.
   - Ensure `output.folder` exists before submitting (create it with `AssetDatabase.CreateFolder`); a missing folder can relocate the generated asset tree.
   - `wait_seconds` lets the Editor wait cooperatively for completion; when the task is still running it returns the same `request_id` for polling.
4. Submit once, then save the full `request_id` along with seed, prompt, and constraints. Poll with `generation.get`: one interruptible query per turn, with an overall timeout. `accepted`/`running` means keep waiting; `completed`/`failed`/`canceled` are terminal. A backend response being ready is not completion — only the Unity-side terminal status counts. A poll timeout means still pending, not failed; keep the ID. Use `generation.cancel` only when the task is no longer needed.
5. Generate dependent Clips in order. Prefer `inout` boundaries from a completed source Clip, and anchor chained boundaries to poses sampled from the same origin Clip rather than to a previous random generation. Record whether each Clip was initialized from an explicit Pose or the Avatar rest pose.
6. For edits via `pose.set` or the `AnimationClip`/`AnimationCurve` API: keep the original, create a derivative, and change only keyframes that conflict with a declared criterion or are evidenced as redundant. Record the changed frames/curves and the evidence behind each change.
7. For props and Timeline integration, use the appropriate Unity API for Transform curves, tracks, and parenting, and keep prop contact frames aligned with the character candidate. Extend existing Timelines rather than rebuilding them.

**Gate:** no terminal artifact, no success claim. Report the failure or missing evidence in Stage 3 instead.

### Stage 3 — Verify

1. Call `animation.analysis` once per Clip (`clips` carries exactly one character and one Clip). Open every returned image before drawing any visual conclusion, and confirm each image matches the intended candidate, Timeline, and camera framing.
2. Report evidence separately: **visual** (what is actually visible in the opened images), **structured** (measured fields returned by commands), and **playback** (timing and smoothness observed in playback or supported temporal measurements). Static images do not prove smoothness; motion measurements do not prove visual semantics.
3. Judge necessary criteria first, then structure, scene interaction, and playback quality. Mark a criterion `unknown` when evidence is missing or unreadable — `unknown` is not a failure. Multi-frame conclusions need multi-frame measurements.
4. When comparing candidates, analyze and render each under identical settings and report `candidate_1` / `candidate_2` / `tie` / `unknown` per criterion with brief evidence.
5. Revise only against a failed criterion, using this table: `failed criterion → supporting evidence → changed input → new candidate → result`.
   - Budget: if the user gave no limit or stop condition, use one initial candidate plus at most two targeted revisions per failing criterion. Stop when necessary criteria pass, or when two consecutive comparable attempts show no substantive improvement. A new seed or a `completed` status is not improvement.
   - Keep the best candidate that satisfies the necessary requirements; reject revisions that regress a previously passing requirement.
6. Use `clip.inspect` to read the resulting Clip's frame rate, length, curve count, loop flag, root-motion settings, and consumers; use `clip.settings.set` (`loop_time`, `keep_original_position_xz`, `root_motion_enabled`) when a supported setting must change, then re-inspect to confirm the read-back values. Compare the Clip asset length with its Timeline Clip `duration`, and report any difference instead of assuming the asset and the Timeline segment match.
7. After integration, re-check dependents: changed character Clip → re-sample prop follow; changed binding/offset/prop state → re-check the affected interaction evidence. Confirm Timeline bindings and Clip ranges in the target Timeline.
8. Report the chosen Clip/asset and target Timeline. List each necessary criterion as `verified` / `failed` / `unknown` with its evidence and limits. Distinguish request accepted, task terminal, character-Clip acceptance, and full interaction acceptance. State the attempts made, the best result, failure categories, the actual stop reason, and concrete next steps.

**Gate:** while any necessary criterion is `unknown`, do not claim full verification.

### Stage Re-entry

| Trigger | Return to |
|---|---|
| Character, source Clip, Timeline, or criteria changed | Stage 1 |
| A new candidate or derivative edit is needed | Stage 2 |
| Evidence is missing, stale, or conflicts | Stage 3 |
| A necessary criterion is `failed` with a known cause | Stage 2 targeted revision |
| Tooling is unavailable after checking package and schema paths | Stop the dependent work and report the limitation |

For observed issues and workarounds, see [tools/known-issues.md](tools/known-issues.md). The loop conventions in [tools/common.md](tools/common.md) remain authoritative for evidence, identification, comparison, and revision details.

## Installation

Requires Unity 2022.3 or newer; Humanoid animation workflows need a character with a valid Humanoid Animator. In Unity Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/OneYoungMean/KimodoUnityBridge.git
```

See [README.md](README.md) or [README.zh-CN.md](README.zh-CN.md) for full requirements, samples, and a quick start.

## Documentation

- [tools/common.md](tools/common.md): read before executing animation tasks; contains scope, task routing, tool use, verification, revision, and result reporting.
- [Command/help.json](Command/help.json): checked-in command and parameter reference.
- [tools/known-issues.md](tools/known-issues.md): observed issues and optional workarounds.
