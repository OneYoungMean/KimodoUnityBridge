# 开发备忘录 / Development memo

> Temporary development snapshot; use [tools/common.md](../tools/common.md) and the command catalog for execution details.
>
> 当前文件只是临时开发快照。日常任务的执行规则位于 [tools/common.md](../tools/common.md)，命令定义位于 [Command/help.json](../Command/help.json)。

## Current command surface (主要命令面)

The maintained public commands are:

- Startup and discovery: `server.install`, `kimodo.help` (`server.install` starts an asynchronous project-local installation task and returns `install:<guid>`; poll it with `generation.get`)
- Scene inputs: commands resolve the active scene and return explicit asset/evidence references
- Async tasks: `animation.generate` returns a generation `request_id`; `generation.get` polls either task type; `generation.cancel` cancels generation only
- Analysis/evidence: `animation.analysis`, `pose.analysis`, `pose.fromvideo`, `pose.frompicture`
- Pose editing: `pose.get`, `pose.set`
- Asset output: `animation.retarget`

## Current boundaries

- Commands resolve a scene Humanoid Animator or renderable Mesh directly. A Mesh-only character rejects Humanoid clips, while a Humanoid target retargets a generic clip before appending it.
- Completed Clips are immutable. Corrections, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation.analysis` accepts one explicit scene Clip and returns structured motion analysis plus a fixed 20-tile SceneView image. Its `camera` object is shared with synchronous `pose.analysis`; both use the active SceneView camera and return image data inline with no request ID. `pose.analysis` previews the same point constraints accepted by `animation.generate`, separately by default or together as opaque poses with `combine=true`.
- Point constraints use explicit `root`, `pose`, and `effector` inputs through `animation.generate.constraints`; the command infers Root2D, FullBody, or Effector after sampling. Pose plus effector is exported as IK-solved FullBody. See [the current constraint contract](plan.md#当前-constraint-契约). Humanoid analysis stores and returns a reusable Root Path consumed through `root_path`; Path Override can regenerate with the same seed from absolute Unity-yaw begin/end angles, preserve the baseline path length, and coexist with loop generation. An absolute heading override runs last at 30-frame intervals, replacing Root2D headings without changing the composed Root XZ positions; when Path Override is active, it rewrites that Root2D record instead of appending a duplicate. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose.get` creates a new External Pose marker for a Humanoid character. Use its `{track,index}` reference inside a point constraint, preview that constraint with `pose.analysis`, then edit the marker with `pose.set` if needed.

## Active documentation work

- Keep `../Command/help.json` aligned with the dispatcher, and keep concrete command details out of `../SKILL.md` and the capability tools under `../tools/`.
- Keep raw object lookup details confined to `../Command/help.json` and the single entry sentence in `../SKILL.md`.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Scene changes and Editor reloads may cancel active generations. Keep Foot IK/Raycast or other unexposed capabilities documented as boundaries unless a public command and project asset prove them.

## Root / trajectory staged redesign (approved 2026-10-28)

This is the accepted development design for the root-motion and trajectory cleanup. It is a development plan, not yet the active execution contract. The previous implementation changes remain stashed in Git before this redesign.

### Terminology and common contract

- A **stage** is one complete generation request. Every stage has one generation instruction, one source motion (when applicable), one target time mapping, and one resulting motion.
- A **pass** is a constraint-building channel inside a stage. A stage may contain multiple passes. A pass may add, replace, or modify constraints, but it does not start generation by itself.
- Stage results carry the generated motion, frame rate, frame count, duration, and the composed constraint set forward to the next stage.
- Constraint composition must use explicit channel rules. A later Root2D pass replaces or edits the relevant Root2D channel instead of silently appending conflicting Root2D rows.

### Stage 1: baseline generation

Stage 1 always runs first. Its passes are:

- the constraints already attached to the Clip;
- non-`post_apply` In/Out constraints from the Clip configuration.

`post_apply` constraints are deferred out of Stage 1. They are materialized as a Stage 2 boundary pass before path and heading passes, so Stage 2 and Stage 3 inherit their result. Stage 1 produces the baseline motion and the root samples used by later stages.

### Stage 2: path and heading overrides

Stage 2 runs when a path-angle, path-distance, or heading override requires a second generation. It inherits the Stage 1 motion as its source and uses all Stage 1 constraints as its starting constraint set.

- If `distance override` is present, use that explicit distance.
- Otherwise, compute the distance as the sum of the per-frame root displacement deltas across the entire Stage 1 animation. For Root2D this is the planar XZ delta sum.
- Build the path-angle spline from that distance; when Relative Offset is enabled, use its World-space position as the spline origin.
- Sample Stage 1 root headings at the corresponding Stage 2 times and interpolate them across generated Root2D frames; the spline supplies positions.
- `override heading` changes Root2D headings only; it does not rotate the Root2D positions or the spline.
- If heading override is requested without an existing start/end Root2D constraint, sample Root2D anchors from the original animation at those endpoints, then apply the requested heading.

Stage 2 is the source for Stage 3. Stage 3 inherits the complete Stage 2 constraint set, including all baseline, path, distance, and heading results.

### Stage 3: loop generation

Stage 3 runs only when loop generation is enabled. It inherits every Stage 2 constraint and adds the Root2D heading and loop-window passes before one final generation.

The loop keyframe is selected from the first foot-contact exchange:

1. Find a transition frame where one foot's contact channels change to all `0`, meaning that foot has fully lifted from the ground.
2. Find the following transition frame where the other foot's contact channels change to all `1`, meaning that foot has fully landed.
3. Use the midpoint frame between those two events as the loop keyframe.
4. If the exchange cannot be identified, use frame `0` and its pose as the fallback anchor.

The loop In/Out window uses the configured total window frame count and sample count:

- The window frame count describes the complete temporal width of the window.
- The sample count describes the number of constraints sampled inside that window.
- The Inspector exposes these Stage 3 values as `Loop Window Frames` and `Loop Window Samples`, defaulting to `30` and `2`; they are independent from the legacy In/Out boundary sampling fields.
- The usable window span is limited to `floor(animationFrameCount * (sampleCount - 1) / sampleCount)`. If the requested window exceeds that limit, clamp it and emit a warning; the minimum effective span is one frame.
- Use the start window's first sample as the loop anchor. Copy the start window to the tail without overlap, beginning at the frame immediately after the original final frame.
- At the start and tail endpoints, set Root2D headings from the Stage 2 motion; if an endpoint has no Root2D constraint, sample its position and heading from Stage 2. Apply the existing per-axis Loop Lock Pos/Rot options at the copied tail endpoint: locked position axes and root-rotation components match the first window sample, while unlocked components keep the sampled tail values.
- Extend the target duration so the generated animation covers the original content and the appended window.
- Preserve the window's frame spacing and sample count after the duration extension.

The old three-second loop trajectory sampling rule is not part of this design and must not determine the final animation duration.

### Implementation phases

1. **Contract and time mapping**: introduce the common Stage/Pass/result model, explicit stage source motion, and one frame-time mapping per stage.
2. **Stage 1 migration**: move Clip constraints and non-`post_apply` In/Out composition into the baseline stage.
3. **Stage 2 migration**: move path angle, path distance, relative offset, and heading handling into composable passes using Stage 1 root samples.
4. **Stage 3 migration**: select the foot-contact exchange midpoint, inherit Stage 2 constraints, adjust only Root2D endpoint headings, and implement non-overlapping In/Out window extension.
5. **Trajectory cleanup**: remove trajectory-only branching and duplicate duration logic after all callers use the staged contract. Obsolete files must follow the repository archive rule.
6. **Verification**: add focused tests for stage inheritance, delta-sum distance, heading-only fallback anchors, contact-exchange fallback, window clamping warnings, non-overlap, and final duration/frame ranges; then run the Unity Editor checks listed above.

### Long-clip window controls (2026-10-28)

- Advanced Inspector settings `Window Frames` and `Sample Count` configure the overlap retained when a long animation is split into multiple generation requests. Their serialized defaults are `30` model-rate frames and `2` evenly spaced FullBody samples.
- The overlap frame count is normalized to an even value so adjacent requests keep symmetric padding. Values that cannot fit the model request limit or the overlap are clamped with a warning.
- The configured sample count is used to select evenly spaced FullBody frames from the previous window and map them to the current window's overlap. Existing callers of the helper retain the two-endpoint default.
