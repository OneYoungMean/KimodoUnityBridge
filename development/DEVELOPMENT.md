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
- Point constraints use explicit `root`, `pose`, and `effector` inputs through `animation.generate.constraints`; the command infers Root2D, FullBody, or Effector after sampling. Pose plus effector is exported as IK-solved FullBody. See [the current constraint contract](plan.md#当前-constraint-契约). Humanoid analysis stores and returns a reusable Root Path consumed through `root_path`; Path Override uses absolute Unity-yaw begin/end angles, preserves measured walking distance unless overridden, and coexists with Loop. The backend applies heading to the composed trajectory and the final reference root tracks while preserving Root XZ positions. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose.get` creates a new External Pose marker for a Humanoid character. Use its `{track,index}` reference inside a point constraint, preview that constraint with `pose.analysis`, then edit the marker with `pose.set` if needed.

## Active documentation work

- Keep `../Command/help.json` aligned with the dispatcher, and keep concrete command details out of `../SKILL.md` and the capability tools under `../tools/`.
- Keep raw object lookup details confined to `../Command/help.json` and the single entry sentence in `../SKILL.md`.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Scene changes and Editor reloads may cancel active generations. Keep Foot IK/Raycast or other unexposed capabilities documented as boundaries unless a public command and project asset prove them.

## Backend-owned generation (2026-11-09)

Unity samples Timeline and original AnimationClip references, retargets them to the model skeleton, and submits one generation request. QuickServer selects and executes stages, long-motion windows, anchor transforms, and the final output interval. Editor and Command use the same request builder. Session/History behavior is outside this change.

### Request and reference contract

- `stage_plan` carries target/output frame ranges, trajectory overrides, Loop locks/window, and Post Apply settings. Stage selection is determined by the backend.
- `window_constraints.window_frames` extends native `num_transition_frames`; `sample_count` controls sparse conditioning density. Neither changes the final output duration. Inspector window values remain 60-FPS command-frame durations converted once to model frames.
- ClipConstraints carry continuous retargeted KMB and their target frame mapping. The backend selects `source_frame_indices` for conditioning while retaining the original contiguous motion for velocity calculation. Samples are unique and retain their original temporal spacing.
- Outside uses In/Out windows and an optional Post Apply flag. Inside is independent of In/Out and samples the original AnimationClip, including its original time and pose. Its keyframe modes are uniform, foot-exchange midpoints, and mixed; `sample_count` controls the uniform subset. Missing Inside foot events fall back to uniform sampling with a warning.
- Inside length mismatches warn and constrain only the overlapping interval. Additional trajectory settings require Inside Post Apply. Inside remains active when Loop is enabled; a real Outside Out reference takes priority over Loop.
- AutoBegin and Relative Offset have been retired from the active request, Inspector, Command schema, and generation code. Requests contain only authored constraints and references.

### Stages and Post Apply

1. Stage 1 measures the spatial walking distance in the requested visible interval. It runs only when the Spline/path-angle calculation needs that measurement and Override Distance is disabled. It does not infer animation duration.
2. Stage 2 generates the trajectory and Loop reference poses. Explicit distance skips Stage 1; path-angle and distance composition retain the existing Bezier/path semantics and extend into Outside context.
3. Before Stage 3, a single constraint preparation step handles Post Apply. Outside receives one planar anchor transform across its complete window. Inside and trajectory FullBody/Effector use Stage 2 root XZ and Yaw while retaining authored height, Pitch/Roll, and body poses. Stage 3 generates the final motion from an independent constraint set.

Every model call assembles its complete real constraints, normalizes them to an anchor, generates, and restores output using the same anchor. No constraints means no anchor normalization. Cropping follows restoration; no extra root transform is applied after Stage 3.

### Loop and long motions

Loop always takes the Stage 2 opening body window from frame 0 of the requested output range. The window length determines the full tail reference range. Its first frame anchors one position/rotation transform for the entire tail window, subject to the configured locks. Foot-exchange midpoints remain available for Inside keyframe selection.

The Loop window is limited to `max(1, floor(output_frames * (sample_count - 1) / sample_count))`, with a warning when shortened. The tail extends the internal generation range by `window_frames - 1`; final output is cropped to the requested frame count and shared seam mapping. Long motions use the native model window/multiprompt path and the configured transition sample density.

### Results and verification

Each actual model call stores a motion artifact in a plain list. Normal mode returns only the final cropped animation. Debug mode also returns one concatenated KMB, segment frame ranges, and resolved constraint records at segment-local times; Unity splits these into its existing Debug Timeline layout. Normal generation writes one final animation asset and sets its Loop settings in place.

Fixed-duration ARDY uses the same stage/window plan through its own model executor; streaming ARDY remains independent. Dense KMB is converted to motion tensor before sparse conditioning frames are selected. A genuine one-frame reference temporarily receives a duplicate for velocity conversion, while conditioning keeps only the original frame.

Focused checks cover stage selection, distance/frame mapping, Post Apply XZ/Yaw preservation, Inside/Loop coexistence, anchor restoration, window sampling, output length, and Debug mapping. Real-model verification runs in the diagnostic copy using installed weights without downloading models. Short-reference generation extension and pose-window search remain TODO; pose matching will ignore root XZ and Yaw.

Verification completed on 2026-11-09:

- Python: 147 focused tests passed (`core.test_quickserver_sessions`, `kimodo.test_constraints`, `core.test_animation_analysis`, `core.test_retargeting`).
- Unity: 47 focused Edit Mode tests passed, including static/live Command help, Kimodo/ARDY reference sampling, final output ranges, and Debug segment-local constraint times. Result: `C:\nvlab\KimodoUnityBridge_FullDemo\Logs\backend-v3-complete.xml`.
- Real inference: 14 cases passed in `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_20261109_141042\results.json` and `backend_v3_20261109_142726\results.json`. Cases include long multiprompt motion, Loop, measured distance, Inside, Post Apply, FullBody/Effector, and Unity-sampled Outside/history KMB. The Unity inputs came from test fixtures, not a byte-for-byte replay of the original reported failure.
- TCP: two real ARDY Outside/Post Apply requests passed through the diagnostic supervisor on port 58723. Normal mode returned only 60 final frames; Debug returned the same 60-frame final output plus four complete generation segments. Result: `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_tcp_20261109_144526\results.json`. The diagnostic supervisor was stopped afterward.
- Loop clarification: the reference window and alignment anchor stay at output-local frame 0. After this change, all 147 Python tests passed, including frame-zero selection with/without foot events and preceding context. Real Kimodo v1.1 Loop and ARDY Inside+Loop+Heading checks both returned 60 final frames, with Debug confirming frame-zero reference poses and the full tail-window mapping. Result: `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_20261109_161408\results.json`.
- The tested production Python files match the repository sources. FullDemo's installed QuickServer copy was not replaced; Editor/Command changes require the matching new backend.
- Installed Kimodo v1.1 and ARDY SOMA30 weights were used. Kimodo v1.2 weights were unavailable, and no weights were downloaded. Visual playback has not been accepted; the focused results do not imply a clean full-project test suite.

### AnimationClip Inspector API (2026-11-02)

- `Editor/Clip/AnimationClipInspector.cs` is the single typed fact source for AnimationClip settings, curve metadata, root-motion displacement/speed, rig type, and static AnimatorController consumers.
- `AnimationClipInspector.Read`, `ReadSettings`, `ReadCurves`, `ReadRootMotion`, `ReadConsumers`, and `WriteSettings` form the reusable editor API. `InspectJson` and `SetSettingsJson` are the command-facing mapping methods.
- The Unity panel, `clip.inspect`, `clip.settings.set`, and `generation.get.clip_settings` all use this API. `KimodoClipInspector` remains only as a narrow compatibility adapter and contains no fact calculation.
