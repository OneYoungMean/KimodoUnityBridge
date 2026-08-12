# Repository instructions

## Scope

- This repository is a Unity animation tool package, not a general Unity automation system.
- Keep project creation, unrelated scene editing, rendering, and machine-wide service management out of this package.
- Treat `Editor/Core/Manager/command_dispatcher.cs` and its current command schemas as the public automation truth.
- Preserve unrelated worktree changes. Stage exact paths, inspect the staged diff, commit only the current task, and never push unless explicitly requested.

## Documentation

- `README.md` is the concise human-facing repository entry. Keep it limited to the current overview, requirements, installation, one minimal start flow, AI entry link, and license.
- `TOOLS.md` is the only AI operational documentation. Its English and Chinese sections must describe the same contract in the same order.
- `DEVELOPMENT.md` is the developer-facing capability and CLI coverage matrix. Update it in the same commit whenever a package capability or command coverage changes.
- Do not add separate human tutorials, feature tours, quick-start manuals, duplicated API references, troubleshooting catalogs, or integration-specific MCP/CLI/Cowork pages.
- Keep command parameter details in `GetCommandDefinitionsJson()` and `kimodo_help`; document only stable meanings, workflow order, boundaries, and verification rules in `TOOLS.md`.
- When public behavior changes, update both language sections of `TOOLS.md` in the same commit. Keep licenses and third-party attribution files intact.

## QuickServer version rule

- The QuickServer version source is `NvlabKimodoQuickServer~/package.json`.
- Every completed change set that adds, modifies, or deletes a tracked file under `NvlabKimodoQuickServer~` must increment the patch version exactly once before commit. This includes server code, launchers, configuration, tests, and documentation. Updating `package.json` for that required bump does not trigger another bump.
- Manually update the QuickServer version and its current one-line capability summary in both language sections of `TOOLS.md`, plus the current version line in `DEVELOPMENT.md`, in the same change set. Do not defer or automate this edit.
- Multiple server files changed for one coherent commit receive one patch increment; a later server commit receives another.

## Architecture and verification

- `NvlabKimodoQuickServer~/core` owns TCP routing, sessions, setup, protocol serialization, model provisioning, and ARDY integration. `NvlabKimodoQuickServer~/kimodo` owns Kimodo model and motion code.
- The bundled LLM2Vec copies target the repository-pinned Transformers behavior. Preserve their compatibility code and attribution.
- Editor generation routes through `command_generation_runner` and `KimodoEditorGeneratePipeline`; runtime playback remains separate from Editor asset record/writeback.
- Run the smallest relevant backend tests after server changes. Validate Unity-facing changes in an appropriate Unity project and report static/build evidence separately from live Editor or visual evidence.
