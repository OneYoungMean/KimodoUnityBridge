# Repository instructions

## Stable rules

- The maintained package is **KimodoUnityBridge**. The public Editor entry point is `Command/command_dispatcher.cs`.
- Treat `GetCommandDefinitionsJson()`, `kimodo_help`, returned IDs/names/paths, and error envelopes as the command and parameter authority. Runtime behavior outranks prose.
- Preserve unrelated worktree changes. Modify only files required by the requested task. Do not commit or push unless the user explicitly asks.

## Documentation ownership

- `README.md`: human-facing package entry and installation overview.
- `SKILL.md`: package installation, task routing, product boundaries, and the API-help entry.
- `Command/help.json`: generated command and parameter schema; keep it aligned with the dispatcher.
- `skills/recognize.md`: semantic motion recognition API chain, visual prompt, evidence macros, and decision program.
- `skills/compare.md`: relative animation-quality API chain, visual prompt, criterion macros, and decision program.
- `skills/generate.md`: generation API chain, intent/evidence macros, constraints, verification, and correction program.
- `DEVELOPMENT.md`: temporary development snapshot; it is not an execution contract.
- `plan.md`: documentation/validation maintenance plan; it does not override the live schema.
- Historical rewrite notes are kept in git history, not maintained as current instructions.

## Verification

- For command behavior, inspect the live schema and `kimodo_help`, then run the smallest relevant Unity/editor check.
- For documentation-only changes, run a static command-name/term audit and one representative `AnimationEval` asset-backed smoke check when available.
- Report static/build evidence separately from live Editor, image, scene, and playback evidence.
- A visual result is only `passed` after the PNG returned by `animation_analyze` was actually opened; otherwise use `needs_revision` or `not_verified`.
- QuickServer implementation is under `NvlabKimodoQuickServer~`; preserve pinned compatibility code and attribution.
