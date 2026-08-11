---
name: kimodo-unity-animation
description: Discover and run Kimodo humanoid animation commands in the current Unity Editor.
---

# Kimodo Unity Animation Commands

Chinese counterpart: [SKILL-zh.md](SKILL-zh.md).

Use this package's public Editor entry point:

```csharp
using KimodoUnityBridge.Command;

string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

Expose every item in `schema.tools` as an AI tool whose implementation calls `Invoke` with the tool name and a JSON object. The live schema and returned errors are authoritative. Every result has `ok`; a failure is `{"ok":false,"error":"..."}`. Omit optional keys instead of sending `null`.

## Discover and verify the workflow

1. Call `kimodo_help({})`. Follow its returned `workflow` array; do not guess command order or arguments.
2. Call `kimodo_help({"command":"<name>"})` before using an unfamiliar command.
3. Call `kimodo_help({"section":"models"})` only when selecting a non-default model or text encoder.

The minimal generation check returned by help is:

```text
session_open({})
query_current_session({"query":"characters"})
kimodo_generate_animation({"character":"<returned name>","prompt":"stand still and breathe naturally","duration_frames":60})
kimodo_get_generation({"request_id":"<returned request_id>"}) until terminal
session_close({})
```

Do not close the Session while generation is running. A terminal status is `completed`, `failed`, or `canceled`. On completion, query `character_animations` to confirm the generated animation was appended:

```json
{"query":"character_animations","character":"<returned name>"}
```

## Current contract

- Commands run in the current Unity Editor in Edit Mode.
- Public time values are integer frames at 60 FPS. Ranges are `[start_frame,end_frame)`.
- Characters are safe scene/Session names. If duplicate scene names exist, use `session_try_add` with the hierarchy path before other character commands.
- `session_open` creates or reopens the current retained Session; `session_close` saves and closes its editing environment without deleting generated animation assets.
- `kimodo_generate_animation` may create and close a retained `__KimodoAuto__` Session when no current Session exists, but explicit Session use is preferred for multi-step AI work.
- Never send `timeline_session_id`, external model paths, or parameters absent from the live schema.

## Tool groups

- Discovery: `kimodo_help`.
- Session: `session_open`, `session_close`, `query_current_session`, `session_try_add`, `session_try_remove`.
- Pose and constraints: `pose_create`, `pose_get`, `pose_set`, `pose_copy`, `kimodo_build_root2d_path`.
- Animation: `kimodo_generate_animation`, `kimodo_get_generation`, `kimodo_cancel_generation`, `kimodo_analyze`, `query_picture`, `kimodo_bake_range`.
- Debug maintenance: `kimodo_debug_install_server`; use only when explicitly diagnosing the local QuickServer installation.

For pose/constraint examples, see [Manual/AI workflow and Constraint API](Manual/AI%20工作流示例与%20Constraint%20API%20设计.md). Always check the command's live schema before copying an example.
