# VISTA History and Skill Refresh Decisions

This record is the running design summary for the VISTA-inspired version/history and animation-engineer Skill update.

## Decisions

1. Reuse Kimodo's existing Session identity, persisted `session_revision`, schema version, generation request records, analysis records, Timeline animation IDs, and asset references. Do not add a Python state service, CLI persistence layer, duplicate `SemanticContext`, or copied scene/analysis payload.
2. Expose a compact version identity and read-only artifact history through the existing `scene.context` response. Treat the revision as the persisted Session revision counter, not a Git branch, immutable snapshot, or undo point.
3. Preserve evidence as references and compact metadata: animation IDs, generation request IDs/status, analysis IDs/signatures/timestamps, and paths to existing files. Do not embed dense motion or full analysis JSON in the history index.
4. Keep semantic choices in the Skill: intent, action phases, duration plausibility, whether displacement/Pose/path evidence is needed, interpretation of analysis, and whether to revise. Keep deterministic input checks, execution state, and any reliable mechanical checks in C# Commands.
5. Organize the Skill around the four user-facing phases: semantic recognition, constraint construction, animation generation, analysis/evaluation. Existing recognition and comparison capabilities are supporting procedures inside analysis/evaluation, not top-level phases.
6. Move superseded manuals to `archive/` with their `.meta` files instead of deleting them; keep them out of active instructions and links.
7. Do not add a command-schema hash or parent-Clip field in this pass. `schema_version` is the persisted Session format version, while callers already query the live command schema; parentage cannot be inferred reliably for every retarget/pose/analysis path. Link only relations the runtime already knows: generation `request_id` to output `animation_id`, and analysis ID to animation/session IDs.

## Verified current behavior

- Session metadata already has `sessionId`, `sessionRevision`, `schemaVersion`; `session.json` persists `session_id`, `session_revision`, and `schema_version` under `Library/KimodoData/Sessions`.
- Generation state is persisted under each Session's `Generations/generation_<request_id>.json` and summarized in Session metadata.
- Analysis files contain `analysis_id`, `session_id`, `input_signature`, `created_at_utc`, animation references, and picture references; Session JSON indexes them without duplicating dense motion.
- The persisted analysis records and generation job records remain. `session.json` also contains a navigation index; its analysis entries currently repeat sparse keyframes, foot contacts, and picture metadata, but not the dense KMB motion or image bytes.
- `scene.context` is already a public command; the active phase documents use it as the scene and Session entry point.
- `animation.generate.analysis_option` was removed because its command result had no consumer. Internal analysis-only requests still use protocol-level `analysis_option` plus KMB ClipConstraints; they do not replace the public `animation.analysis` command, whose input remains `clips: [{ character, clip }]`.
- Full `animation.analyze` is not automatic inside generation; the previous Skill described it as an AI follow-up step. `PersistPictureSummary` already stores picture metadata/paths with the analysis record.

## Implementation notes

- `scene.context` now exposes Session identity/revision/schema version by default; the compact animation, generation, and analysis index is returned only when `include_history=true`. Generation status includes the output `animation_id`; analysis results expose `analysis_id` and `session_id`. Analysis picture paths stay references to files already persisted by the existing renderer.
- Rewrote the root Skill as a quick applicability and usage gate. The four phase documents describe the animation-engineer workflow; phase 1 now derives its procedure from concrete performance examples, and image recognition/comparison remain nested under phase 4. Superseded manuals were moved to `archive/skill-legacy-20261013/` rather than deleted.
- Updated `Command/help.json`, the live output schema, command contract coverage, and an Editor integration assertion for Session/history linkage.
- The first implementation batch was committed locally as `fff7897` (`Add session artifact history and four-phase skill`); it was not pushed.
- The active documentation now has one shared `common.md` and four phase guides. Superseded guides, including the preserved image-recognition, comparison, and generation source manuals, were moved with their Unity `.meta` files into `archive/skill-legacy-20261013/`.
- Refined the phase handoff: phase 1 records acceptance criteria and allowed semantic alternatives; phase 2 owns 60 FPS duration conversion and confirms path/target references; phase 4 routes evidence-backed revisions through phases 2 and 3. Replaced the isolated numeric travel example with a general distance/duration feasibility rule.
- Added interactive acceptance cases in `development/animation-workflow-test-cases.md`; they cover prompt-only generation, analyzed Root Path reuse, sampled Pose constraints, Clip continuation, looping, evidence-backed revision, Session history comparison, and consequential timing ambiguity.
- Static validation: `Command/help.json` parses; active Markdown links and `kimodo_*` command references resolve; the active tools directory contains exactly four phase docs plus `common.md`; `git diff --check` passes. Editor assertions cover the default history omission and explicit history response, but those assertions have not been run in Unity.
- Build/test boundary: the running Unity Editor log shows a successful script compilation before the final schema/test assertion edit, so it is not current evidence for the complete working tree. A standalone `dotnet build` of Unity-generated projects is not a valid substitute here: project references fail on the Unity Test Framework's missing NUnit dependency / framework mismatch. No Unity Test Runner interface or live `kimodo.help` invocation is available in this session; Editor tests and final live-schema comparison remain unrun.
