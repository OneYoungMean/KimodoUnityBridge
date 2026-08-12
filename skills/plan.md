# Animation-agent evaluation plan

## Goal

Measure whether the Kimodo AI instructions lead an independent execution agent to plan, generate, inspect, and revise humanoid animation correctly. Improve `SKILL.md` and `TOOLS.md` only when repeated evidence shows an instruction gap.

## Fixed roles

### Execution agent

- Read only `SKILL.md`, `TOOLS.md`, the live command schema/help, and the assigned case.
- Complete the case in a Unity project using the public `command_dispatcher` workflow.
- Save commands, returned JSON, analysis output, generated assets, contact sheets, playback evidence when available, and a final acceptance report.
- Do not read the rubric, golden answer, or prior evaluation reports.

### Evaluation agent

- Receive the case, rubric, golden answer, and execution evidence.
- Score the run without performing the animation task.
- Identify missing evidence, incorrect decisions, and instructions that were absent, ambiguous, contradictory, or impractical.
- Recommend the smallest change to `SKILL.md` or `TOOLS.md`; do not change product code or case goals.

### Documentation maintainer

- Apply only evidence-backed documentation changes.
- Keep `SKILL.md` as a short entry point and retain operational rules in `TOOLS.md`.
- Preserve equivalent English and Chinese sections in `TOOLS.md`.
- Re-run the same cases after each accepted revision and compare scores.

## Test cases

### Case 1: locomotion loop

Request a forward running loop.

Required evidence:

1. Plan Root2D motion before generation.
2. Use compatible start/end full-body running poses at the same gait phase.
3. Generate with `loop:true`.
4. Analyze the generated clip and inspect rendered key-pose images.
5. Inspect relative frames `0`, `1`, `duration_frames-2`, and `duration_frames-1`.
6. Treat this as locomotion: preserve cycle displacement rather than forcing world-root coincidence.
7. Revise the path or endpoint constraints if the seam fails.

### Case 2: overhead-bar swing

Request a character to jump, grasp an overhead bar, swing forward, release, and land.

Required evidence:

1. Identify world-space bar/grip targets.
2. Add left/right hand position and rotation constraints during the grasp and swing phase.
3. Add key poses for approach, grasp, lowest swing, release, and landing.
4. Use Root2D only where ground motion is applicable; do not use it as a substitute for hand contact.
5. Analyze and inspect images for hand attachment, limb plausibility, swing arc, release, and landing.
6. Revise failed poses or contacts and regenerate.

### Case 3: state transition

Request a transition from the end of a running clip to the start of a jumping clip.

Required evidence:

1. Sample/analyze the source end pose and destination start pose.
2. Compare root position, heading, body pose, support foot, and contact state.
3. Decide whether direct blending is safe; if not, create a short transition clip constrained at both endpoints.
4. Analyze the transition and inspect both boundaries plus playback when available.
5. Report whether the transition passed, needs revision, or remains unverified.

## Golden-answer boundaries

Golden answers specify required decisions and evidence, not an exact prompt, seed, frame number, or single correct animation. The execution agent may use any supported command sequence that satisfies the case requirements.

## Rubric (100 points)

| Area | Points | Evidence of success |
| --- | ---: | --- |
| Live-schema and Session workflow | 20 | Uses current command help/schema, preserves returned identifiers, polls generation to terminal state. |
| Motion and constraints | 20 | Plans Root2D where appropriate and uses full-body/contact constraints for key action states. |
| Analysis and semantic review | 20 | Runs analysis, opens contact-sheet images, checks requested action semantics, and records findings. |
| Loop or transition review | 20 | Applies the case-specific seam or boundary checks and uses the correct in-place/locomotion distinction. |
| Evidence-driven iteration | 10 | Revises path, pose, or constraints in response to identified evidence and repeats validation. |
| Honest final report | 10 | Uses `passed`, `needs_revision`, or `not_verified`; does not claim unobserved visual or temporal qualities. |

## Hard failures

Assign zero to the affected category and flag the run when it:

- reports generation complete before the job is terminal;
- reports visual acceptance without opening the returned image;
- treats a locomotion loop as an in-place loop by forcing world-root coincidence;
- substitutes Root2D for overhead hand-contact constraints;
- identifies an unsafe state boundary but neither justifies blending nor creates a constrained transition;
- presents unavailable playback or temporal evidence as verified.

## Evidence layout

Store one immutable run directory per case:

```text
Verification~/animation-agent-evals/
  cases/<case-id>.md
  golden/<case-id>.md
  rubric.md
  runs/<timestamp>-<case-id>/
    execution-log.md
    commands.jsonl
    analysis/
    images/
    assets/
    evaluation.md
```

Keep golden answers and rubric unavailable to the execution agent. Preserve raw evidence across revisions so that documentation changes can be compared against the same case.

## Iteration protocol

1. Run all three cases against the current documentation.
2. Score each run and record failures by rubric category.
3. Change only the smallest instruction needed to address an observed, repeatable failure.
4. Re-run the unchanged cases with a fresh execution agent.
5. Accept a documentation change only when aggregate score improves without creating a new hard failure.
6. Add a new case only after the initial three cases are stable.