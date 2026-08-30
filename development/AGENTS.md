# Repository instructions

## Scope

These rules apply to repository maintenance. Animation task execution is defined by [SKILL.md](../SKILL.md) and its linked tools; command details are defined by [Command/help.json](../Command/help.json).

## Repository rules

- Preserve unrelated worktree changes.
- Modify only files required by the requested task.
- Do not commit or push unless the user explicitly asks.
- Keep runtime compatibility code and attribution intact unless removal is explicitly requested.

## Documentation ownership

- `../README.md` / `../README.zh-CN.md`: human-facing package entry and installation overview.
- `../SKILL.md`: installation gate, task entry, capability-tool orchestration, and shared execution rules.
- `../tools/*.md`: capability-tool procedures for Session, generation, recognition, comparison, Pose, and derived outputs.
- `../Command/help.json`: generated command and parameter schema; keep it aligned with the dispatcher.
- `DEVELOPMENT.md`: temporary development snapshot, not an execution contract.
- `README.md`: development and handoff navigation page.
- `plan.md`: documentation and compatibility maintenance plan.

## Maintenance checks

- For command changes, verify the live schema, `kimodo_help`, and the smallest relevant Unity/editor check.
- For documentation changes, run a static terminology/command audit and `git diff --check`.
- Report static, build, runtime, image, scene, and playback evidence separately.
