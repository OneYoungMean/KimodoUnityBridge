# Reusable Animation Loop: Practical Test Cases

These are interactive acceptance tests for the Skill workflow, not Unity unit tests. Run them in a Unity project with KimodoUnityBridge installed, a ready local runtime, an active Timeline Session, and at least one valid humanoid character. Re-check `kimodo.help` and the live schema before each run. Do not treat a test prompt as permission to alter scene assets outside the requested generated output.

## Shared pass criteria

- The plan states intent, action beats, space, timing, evidence needed, acceptance criteria, and consequential unknowns.
- The plan selects a task route, distinguishes current commands, inspected APIs, planned capabilities, and unavailable routes, and keeps capability separate from criterion results. Check gates may be re-entered; unrelated passing work is not regenerated.
- The request adds only supported structured constraints, validates time bases and references, and stops when an exact input cannot be resolved.
- When a command is asynchronous, the loop saves its exact `request_id`, checks warnings, polls to a terminal state, and does not duplicate an in-flight request. Timeout preserves the pending ID for resumption.
- Evaluated Humanoid Clips use `animation.analysis`; prop/integration checks inspect the evaluated scene. Returned image evidence is opened before visual claims, and unsupported conclusions remain unknown.
- A correction produces a distinct derived Clip and repeats only the affected loop steps; earlier Clips and their history remain available.
- Candidate decisions cite the current scene/session, Clip, Timeline range, and matching evidence. Old images or another candidate's analysis are not reused as proof.
- The loop records a finite budget and its stop reason. The local default is one initial candidate and two targeted revisions per failing criterion; task-specific limits take precedence. Two consecutive attempts without meaningful improvement stop that revision loop.

## Cases

### 1. Prompt-only performance

**Request:** “Have the character stand, wave toward the camera, then relax the arm.”

**Setup:** Any valid humanoid character in the current scene.

**Exercise:** Run the reusable loop without adding path, Pose, or loop constraints unless the live scene or user requirement establishes a need. Generate, poll, run `animation.analysis` on one Clip, and inspect its returned image.

**Pass when:** the plan preserves the planted greeting and relaxed ending; no unsupported spatial constraints are submitted; the report distinguishes visible evidence from unverified playback quality.

### 2. Reuse an analyzed Root Path

**Request:** “Use the analyzed path from the selected source Clip as a movement reference for this new action.”

**Setup:** A valid humanoid and a source Clip whose `animation.analysis` result contains `clip.root_trajectory.path`.

**Exercise:** Resolve the source Clip from current scene context, confirm its analysis result and path `{track,index}`, then construct `root_path` using that returned reference. Inspect warnings and analyze the generated result.

**Pass when:** the exact returned reference is used and history IDs identify source and output. The report states that `root_path` supplies start/end constraints only; it makes no claim that the generated intermediate trajectory exactly follows the source path.

### 3. Pose constraint from a source animation

**Request:** “Use the confirmed crouch from the source animation as the landing pose for a jump, then recover to standing.”

**Setup:** A valid humanoid, a source Clip with a candidate crouch, and a visible or otherwise confirmed landing context.

**Exercise:** Analyze the source Clip, select a candidate Timeline-global frame, call `pose.get` with `source.timeline_frame_60`, inspect the returned Pose, and only then pass its returned `{track,index}` as the generation constraint. Analyze the result and inspect relevant pose tiles.

**Pass when:** the Pose was sampled and confirmed for the landing role, the Timeline-global source frame is not confused with a generated-Clip-relative constraint frame, and the report does not treat one Pose as proof of the whole jump or obstacle clearance.

### 4. Continue an existing Clip

**Request:** “Continue the selected walk with the same stride and facing, then slow down and stop beside the chair.”

**Setup:** A valid humanoid, a completed walk Clip, and a chair or user-confirmed endpoint in the active scene.

**Exercise:** Inspect the source Clip's actual ending and resolve any endpoint reference. Use only the current schema-supported same-character `inout` source context and any confirmed root constraints. Generate, poll, then compare source-end and output-start evidence where available.

**Pass when:** the plan is based on the source's actual ending rather than its name; only supported references are submitted; continuity claims are limited to the returned evidence, with playback continuity left unknown unless playback is observed.

### 5. Looping dance phrase

**Request:** “Create a short dance phrase that can repeat, with an ending intended to connect to its beginning.”

**Setup:** A valid humanoid character.

**Exercise:** Treat repeatable choreography and seam continuity as separate criteria. Set the structured loop field only if the live schema supports it, generate and analyze the Clip, then inspect the returned loop-related fields and images. Observe playback if the test requires a seam-quality verdict.

**Pass when:** the loop requirement is represented structurally; the analysis report distinguishes loop candidacy from verified playback continuity.

### 6. Evidence-based targeted revision

**Request:** “Check whether the raised-arm greeting has a readable pause before the wave. If it does not, revise only that beat and keep the rest.”

**Setup:** A completed greeting Clip and a valid humanoid.

**Exercise:** Analyze and inspect the original Clip. If evidence supports the defect, revise only the affected constraint, generate and poll a derived Clip, then evaluate it against the same acceptance criterion. Revisit intent planning only if the intent, beats, timing, or criterion changes.

**Pass when:** the revision is triggered by cited evidence, targets the identified beat, and leaves the original available. If the pause cannot be judged from available evidence, no speculative retry occurs.

### 7. Session history and candidate comparison

**Request:** “Compare the two completed candidates from this Session against the goal ‘approach slowly, then stop’.”

**Setup:** A Session with two completed candidates and their analysis records.

**Exercise:** Read `scene.context`, then query `session.history` separately. Match generation and analysis entries by their IDs and evidence paths. Analyze each candidate under the same criteria and rendering conditions; open both images.

**Pass when:** the default scene response omits history, explicit history returns the compact index, IDs remain distinct, and comparison reports only criteria supported by the evidence. `session_revision` is not described as a restore point.

### 8. Consequential timing ambiguity

**Request:** “Keep a relaxed walking pace, cover the marked long route, and finish before the very short deadline.”

**Setup:** A valid humanoid and a clearly identified route/endpoint, with the time/action combination intentionally left ambiguous.

**Exercise:** The planning step identifies the conflict between pace, distance, and duration and asks which requirement should take priority. Do not submit generation until the user resolves it. After clarification, convert an approximate duration to 60 FPS frames or preserve an explicit frame count. If the user requires an exact duration that cannot be represented at 60 FPS, ask whether the nearest frame duration is acceptable.

**Pass when:** the system does not silently change the action, route, or duration and does not invent coordinates; no generation request is submitted before the consequential ambiguity is resolved.

### 9. Inspect without generating or editing

**Request:** “Check this walk for endpoint mismatch and possible foot sliding. Do not change it.”

**Setup:** One selected source Clip; scene and Session information can resolve its target. Playback may or may not be available.

**Exercise:** Resolve the source through available context, inspect endpoint evidence, and obtain playback or a relevant temporal measurement if available. Do not create a replacement animation merely because the generic loop contains generation steps.

**Pass when:** no generation or source edit occurs, no redundant clarification blocks read-only discovery, and unsupported foot-sliding claims remain unknown. Any analysis-time Timeline append is distinguished from modifying the source asset.

### 10. Prop integration and stale evidence

**Request:** “Complete the pickup so the apple remains on the table until contact, then follows the hand and stays held.”

**Setup:** A character Clip, a simple apple prop, and a Timeline. Retain evidence from an earlier candidate, then change the character Clip, prop offset, or Timeline binding. Prop curve authoring may require an inspected Unity API rather than a Kimodo command.

**Exercise:** Resolve actual prop transforms, contact events, attachment/curve behavior, and Timeline ranges. Sample the assembled scene before contact, at contact, during lifting, and at the end; observe playback when claiming temporal quality. Reuse only unaffected evidence.

**Pass when:** earlier evidence does not certify the changed integration, the actual prop follows the selected character candidate without an unsupported future command, and a passing character Clip alone does not pass the interaction. Remaining evidence gaps are explicit.

### 11. Bounded revision and failure attribution

**Request:** “Improve this walk until the declared stop position and continuity criteria pass; preserve the original.”

**Setup:** A source Clip and a finite attempt budget. Use recorded outcomes containing a pre-generation protocol failure, comparable candidates with no improvement, and a candidate that improves one criterion while breaking another required constraint.

**Exercise:** Attribute the protocol failure before changing motion constraints. For completed candidates, compare the declared criteria, retain the best acceptable output, count actual attempts, and stop at the budget or no-improvement condition. This case may be reviewed from existing traces without spending additional generations.

**Pass when:** a protocol failure is not labeled poor motion quality, regressions are not silently accepted, the loop does not restart its budget, and the final report distinguishes failed, unknown, tooling repair, and animator work.

## Record for each run

Record the Unity project/scene/session and relevant tool version, character/prop references, request and interpretation, chosen routes, assumptions, criteria, submitted settings, warnings, returned IDs when available, API-created asset/track references, Timeline range and frame origin, evidence paths, observed playback or temporal measurements, criterion results, revision budget/attempts, failure attribution, selected output, and stop reason. Keep this in the test log; do not add it to the public Command payload or persistent CLI state.
