# Repository instructions

## Stable rules

- The maintained package is **KimodoUnityBridge**. The public Editor entry point is `Command/command_dispatcher.cs`.
- Treat `GetCommandDefinitionsJson()`, `kimodo_help`, and returned errors as the command and parameter authority. Runtime behavior outranks prose.
- Preserve unrelated worktree changes. Modify only files required by the requested task. Do not commit or push unless the user explicitly asks.

## Documentation ownership

- `README.md`: human package entry.
- `SKILL.md` / `SKILL-zh.md`: short AI router.
- `TOOLS.md`: bilingual shared execution contract.
- `DEVELOPMENT.md`: temporary development memo, not an execution contract.

## Verification

- When command behavior changes, check the live schema and the smallest relevant Unity/editor test.
- Report static/build evidence separately from live Editor, image, scene, and playback evidence.
- QuickServer implementation is under `NvlabKimodoQuickServer~`; preserve pinned compatibility code and attribution.
