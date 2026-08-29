# KimodoUnityBridge

[简体中文](README.zh-CN.md)

Kimodo adds local AI humanoid animation generation to an existing Unity project. It supports prompt-driven motion, pose and end-effector constraints, analysis, baking, retargeting, Animator content, Timeline authoring, and runtime motion playback.

- Runs from a project-owned local runtime.
- Supports Windows, macOS, and Linux; CUDA is the primary acceleration path and CPU fallback is available.
- Keeps animation generation and assets inside the Unity project workflow.

## Requirements

- Unity 2022.3 or newer
- An existing Unity project
- A character with a valid Humanoid Animator for humanoid workflows
- At least 8 GB memory and sufficient disk space for the selected models

## Install

In Unity Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/OneYoungMean/KimodoUnityBridge.git
```

For a manifest dependency, use the same Git URL:

```json
"com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
```

## Minimal start

1. Import the package's **Light Sample** from Unity Package Manager, or open the [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo).
2. Open its Timeline and select a Kimodo clip bound to a character.
3. Enter a motion prompt and choose **Generate & Bake**.
4. Wait for the project-local runtime and model to become ready, then play the Timeline.

Runtime diagnostics are written to `NvlabKimodoQuickServer~/log/setup.log` and `NvlabKimodoQuickServer~/log/bridge_server.log` in the Unity project.

## Command API quick reference

Commands accept and return JSON. The complete, live schema is in [`Command/help.json`](Command/help.json) and can also be queried with `kimodo_help`.

### Generate an animation and read its path

Generation is asynchronous. Save the `request_id` returned by `kimodo_generate_animation`, then poll `kimodo_get_generation` until the status is terminal:

```text
kimodo_generate_animation({
  "character": "Hero",
  "prompt": "walk forward",
  "duration_frames": 120
})
// {"ok":true,"request_id":"...","status":"accepted"}

kimodo_get_generation({"request_id":"..."})
// completed: {"ok":true,"status":"completed","animation":"WalkForward",
//             "path":"Assets/KimodoGeneratedClips/WalkForward.anim", ...}
```

The completed result's `path` is the project-relative path to the generated `AnimationClip`. It is empty while the job has not produced an asset (or when it fails); use the safe `animation` name for subsequent Session commands.

### Resolve a raw Session object

Use `session_get_raw` when an integration needs a real Unity object reference. `kind` supports `character`, `track`, `clip`, and `constraint`; `character` is optional and disambiguates objects with the same name:

```text
session_get_raw({
  "kind": "clip",
  "name": "WalkForward",
  "character": "Hero"
})
// {"ok":true,"kind":"clip","name":"WalkForward",
//  "guid":"GlobalObjectId_V1-...",
//  "asset_guid":"...",
//  "path":"Assets/KimodoGeneratedClips/WalkForward.anim",
//  "object_type":"AnimationClip","character":"Hero", ...}
```

Names must match a Session object exactly (matching is case-insensitive). If more than one object matches, pass `character`; otherwise the command returns an `invalid_argument` error whose message reports the ambiguity. `guid` is a Unity `GlobalObjectId` and is the portable object identity. `asset_guid` is the `AssetDatabase` GUID when the object has an asset path; scene objects and some Timeline tracks/markers may have an empty `asset_guid` or `path`.

## For users

Install the package into the target Unity project and follow the minimal start flow above. Runtime behavior and command results come from the installed project and its live schema.

## For developers and AI agents

Read [SKILL.md](SKILL.md) for animation-agent execution rules, then [Command/help.json](Command/help.json) for current commands and parameters. The live schema and returned values outrank this README.

Repository maintenance rules and development notes are in [`development/`](development/README.md). The root [`AGENTS.md`](AGENTS.md) is only the repository-tool entry point and links to the detailed maintenance rules.

The documentation ownership is:

- `README.md` / [`README.zh-CN.md`](README.zh-CN.md): user-facing package documentation.
- `SKILL.md` and `skills/*.md`: Agent execution rules and task procedures.
- `Command/help.json`: live command and parameter schema.
- [`development/`](development/README.md): developer maintenance, compatibility, plans, and handoff notes.

## Links

- [Demo video](https://www.bilibili.com/video/BV1HG7361Env)
- [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)
- [Development documentation](development/README.md)
- [Apache License 2.0](LICENSE)
