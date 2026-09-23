---
name: kimodo-scene-context
description: Resolve and reuse the hidden automatic context shared by Kimodo commands.
---

# Scene context / 场景上下文

Kimodo commands no longer expose Session creation, switching, raw lookup, or closing. Each command resolves the active Unity scene and reuses one hidden automatic context for Timeline evaluation, generation, analysis, and pose sampling.

- Scene characters are resolved from the active scene and the selected/open `Animator` when available.
- Existing clips remain immutable; generation, analysis, retargeting, and corrections append derived assets.
- Returned character and animation names, `{track,index}` pose references, and project-relative paths are the only handles passed between commands.
- Do not invoke removed lifecycle or raw lookup commands; the command surface resolves scene context automatically.
- Scene changes, Editor reloads, and Play Mode transitions can cancel active generation requests and must be reported.

The hidden context may persist internal metadata for reliable polling and analysis caching. That metadata is an implementation detail and is not a public query API.
