# VISTA History and Skill Refresh Decisions

This record preserves the VISTA-inspired version/history and animation-engineer Skill decisions.

## Current Session Context 3.0 design

`session.context` builds a lightweight projection from the active Scene and Timeline each time. It has no static Session cache, `session.json` snapshot, or serialized Context Binding source of truth. The Session ID is the Timeline asset GUID. Its default response contains Session identity/status and counts plus Scene and Timeline names/paths; `details` builds only requested character, clip, timeline, and task groups. The Session root directly owns its `PlayableDirector` and Awake runtime guard. The guard disables the root during Play Mode, and no exit callback restores it.

`session.history` is separate and append-only per Timeline GUID. Each `history.jsonl` starts with Session/Timeline identity, creation time, history format, and package version. It records complete input and result events for `animation.generate`, `animation.retarget`, and `animation.analysis`; asynchronous generation adds a terminal event under the accepted operation sequence. Context and history do not depend on a `session.json` snapshot. Existing files are left untouched and are not migrated by 3.0.

The decisions below describe pre-3.0 work and are retained for historical context; where they conflict, the 3.0 design above is current.

## Decisions

1. Derive Session identity from the Timeline asset GUID and build the current projection directly from the loaded scene/Timeline. Do not add a Python state service, CLI persistence layer, duplicate `SemanticContext`, or copied scene/analysis payload.
2. Expose a compact current-state summary through `session.context`; expose append-only command history separately through `session.history`.
3. Preserve evidence as references and compact metadata: animation IDs, generation request IDs/status, analysis IDs/signatures/timestamps, and paths to existing files. Do not embed dense motion or full analysis JSON in the history index.
4. Keep semantic choices in the Skill: intent, action phases, duration plausibility, whether displacement/Pose/path evidence is needed, interpretation of analysis, and whether to revise. Keep deterministic input checks, execution state, and any reliable mechanical checks in C# Commands.
5. Organize the Skill around the four user-facing phases: semantic recognition, constraint construction, animation generation, analysis/evaluation. Existing recognition and comparison capabilities are supporting procedures inside analysis/evaluation, not top-level phases.
6. Move superseded manuals to `archive/` with their `.meta` files instead of deleting them; keep them out of active instructions and links.
7. Do not add a command-schema hash or parent-Clip field in this pass. `schema_version` is the persisted Session format version, while callers already query the live command schema; parentage cannot be inferred reliably for every retarget/pose/analysis path. Link only relations the runtime already knows: generation `request_id` to output `animation_id`, and analysis ID to animation/session IDs.

## Verified current behavior

- `session.context` has no JSON snapshot or serialized Binding source of truth. The Session ID is the Timeline asset GUID; `history.jsonl` stores Session identity and command history.
- Generation state is persisted under each bound Session's `Generations/generation_<request_id>.json` and summarized only when the independent history index is requested.
- Analysis files contain `analysis_id`, `session_id`, `input_signature`, `created_at_utc`, animation references, and picture references; `session.history` records command inputs and results independently of the live Context projection.
- The persisted analysis records and generation job records remain separate from Context. Full keyframes, foot contacts, and picture metadata remain in `analysis_*.json`, while image bytes and KMB motion remain on disk.
- `session.context` is the public current-scene and Timeline entry point; `session.history` is the separate historical command.
- `animation.generate.analysis_option` was removed because its command result had no consumer. Internal analysis-only requests still use protocol-level `analysis_option` plus KMB ClipConstraints; they do not replace the public `animation.analysis` command, whose input remains `clips: [{ character, clip }]`.
- Full `animation.analyze` is not automatic inside generation; the previous Skill described it as an AI follow-up step. `PersistPictureSummary` already stores picture metadata/paths with the analysis record.

## Implementation notes

- `session.context` exposes Session identity, status, clip/task counts, and current scene/Timeline structure, with optional tagged details. The separate `session.history` command returns grouped append-only operations. Generation status includes the output `animation_id`; analysis results expose `analysis_id` and `session_id`. Analysis picture paths stay references to files already persisted by the existing renderer.
- Rewrote the root Skill as a quick applicability and usage gate. The four phase documents describe the animation-engineer workflow; phase 1 now derives its procedure from concrete performance examples, and image recognition/comparison remain nested under phase 4. Superseded manuals were moved to `archive/skill-legacy-20261013/` rather than deleted.
- Updated `Command/help.json`, the live output schema, command contract coverage, and an Editor integration assertion for Session/history linkage.
- The first implementation batch was committed locally as `fff7897` (`Add session artifact history and four-phase skill`); it was not pushed.
- The active documentation now has one shared `common.md` and four phase guides. Superseded guides, including the preserved image-recognition, comparison, and generation source manuals, were moved with their Unity `.meta` files into `archive/skill-legacy-20261013/`.
- Refined the phase handoff: phase 1 records acceptance criteria and allowed semantic alternatives; phase 2 owns 60 FPS duration conversion and confirms path/target references; phase 4 routes evidence-backed revisions through phases 2 and 3. Replaced the isolated numeric travel example with a general distance/duration feasibility rule.
- Added interactive acceptance cases in `development/animation-workflow-test-cases.md`; they cover prompt-only generation, analyzed Root Path reuse, sampled Pose constraints, Clip continuation, looping, evidence-backed revision, Session history comparison, and consequential timing ambiguity.
- Static validation: `Command/help.json` parses; active Markdown links and `kimodo_*` command references resolve; the active tools directory contains exactly four phase docs plus `common.md`; `git diff --check` passes. Current Editor runs cover the default history omission, explicit history response, Context binding, Play Mode lifecycle, and live Timeline projection refresh; the focused Command group passed 53/53.
- Build/test boundary: the FullDemo Unity-generated Command and Editor.Tests projects build successfully with 0 errors. Focused Unity EditMode runs are current evidence for the working tree; they do not replace full regression, generation playback, or manual visual review. `AnimationAnalysisCommandIntegrationTests`, including the ArcWalk SceneView/composite-image path, passed 6/6.
