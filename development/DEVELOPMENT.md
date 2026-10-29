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
- Humanoid `animation.analysis.clip.motion_profile.force_line` measures left/right arm swing balance. It converts each shoulder-to-hand vector into character-root-local sagittal angle, sums absolute frame-to-frame angle deltas over the full Clip and each `phase_track` interval, and reports `imbalance_ratio = abs(left-right) / max(left,right)`. A ratio at or below `0.15` is `balanced`; stationary or incomplete samples return `insufficient_evidence`.
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
- When Root2D and a FullBody/root-context row share a frame, the Stage composition folds Root2D into that row: Root2D supplies planar position and heading, while FullBody retains Y, body pose, and other target channels. The standalone Root2D row is then removed.

### Stage 1: baseline generation and distance source

Stage 1 always runs first. It generates the baseline motion from the Clip constraints and all non-`post_apply` In/Out constraints. It also provides the root samples used to calculate the measured path distance. The measured distance is the sum of every adjacent root XZ delta across the complete Stage 1 motion.

When no Stage 2 condition is enabled, this result is finalized directly. There is no standalone Stage 3 request.

### Stage 2: Root2D and path passes

Stage 2 runs when Loop, a `post_apply` In/Out boundary, path-angle override, path-distance override, or heading override is enabled. It inherits the complete Stage 1 constraint set and motion.

- An explicit distance override is used even when its value is `0`; otherwise use the Stage 1 delta-sum distance.
- A path-angle override replaces the effective Root2D channel with its two endpoint pass and the generated spline rows. Relative Offset supplies the origin when enabled; otherwise Auto Begin supplies the origin when it is the effective frame-0 anchor.
- A distance-only override uses the existing default Root2D pass as its starting channel and rescales its planar path to the explicit distance, including an explicit value of `0`.
- Heading override changes Root2D headings only. Existing Root2D rows are edited in place; if no endpoint row exists, endpoint rows are sampled from the Stage 1 root motion first.
- Stage 2 keeps all effective Root2D rows in its composed constraint set. Stage 3 receives that same set without rebuilding Root2D.

### Stage 3: Root/body composition and windows

Stage 3 runs only when Stage 2 produced a loop or deferred boundary window, or when the effective Stage 2 constraint set contains FullBody/Effector records. It inherits every Stage 2 constraint.

- FullBody and Effector root positions are interpolated from Stage 2 motion only when path-angle override is active. Their pose and target channels are preserved.
- When heading override is active, all already-effective Root2D, FullBody, and Effector headings are updated, including constraints inherited from Stage 2.
- Post-apply In/Out windows are anchored by the earliest constraint in each window and are placed around the core Stage 2 content. Window payloads are carried separately from ordinary deferred fields so Stage 3 can apply the requested re-anchoring policy. Loop and Out windows are mutually exclusive; Out has priority.
- The loop window begins at the selected loop keyframe, is appended after the core content without overlap, and preserves its configured frame/sample mapping. The duration is extended to cover the appended window.
- The copied tail window is planar-reanchored from the source window's first sample to the Stage 2 terminal root. Loop Lock Pos/Rot selectively overrides the corresponding position axes and root rotation components after that offset.
- When PathAngle and Loop are both active, the Loop pass computes that window anchor first, then uses the PathAngle terminal Root2D value as the target anchor and removes the terminal PathAngle Root2D sample before appending the Loop window.

The contact-exchange selector remains available for later activation, but the current approved rollout anchors Stage 3 at frame `0`:

1. Find a transition frame where one foot's contact channels change to all `0`, meaning that foot has fully lifted from the ground.
2. Find the following transition frame where the other foot's contact channels change to all `1`, meaning that foot has fully landed.
3. Use the midpoint frame between those two events as the loop keyframe.
4. Until the selector is enabled, use frame `0` and its pose as the anchor. The exchange midpoint remains covered by tests and can be restored without changing the window contract.

The loop In/Out window uses the configured total window frame count and sample count:

- The window frame count describes the complete temporal width of the window.
- The sample count describes the number of constraints sampled inside that window.
- The Inspector exposes these Stage 3 values as `Loop Window Frames (60 FPS)` and `Loop Window Samples`, defaulting to `30` and `2`; the frame value is always a 60-FPS command-frame count and is converted to model frames with ceiling before sampling. They are independent from the legacy In/Out boundary sampling fields.
- For example, `30` means `0.5` seconds: it becomes `15` model frames at 30 FPS, `13` at 25 FPS, and `10` at 20 FPS.
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
