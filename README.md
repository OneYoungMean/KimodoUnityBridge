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

## AI Agent Manual

Read [SKILL.md](SKILL.md) as the installation and task entry point; it coordinates the capability tools in `tools/`. Command definitions are kept in [Command/help.json](Command/help.json).

## Developers and maintenance records

Repository maintenance rules and development notes are in [`development/`](development/README.md). The root [`AGENTS.md`](AGENTS.md) is only the repository-tool entry point and links to the detailed maintenance rules.

The documentation ownership is:

- `README.md` / [`README.zh-CN.md`](README.zh-CN.md): user-facing package documentation.
- `SKILL.md`: installation and task entry point.
- `tools/*.md`: cooperating capability tools and their task procedures.
- `Command/help.json`: command definitions and parameter schema.
- [`development/`](development/README.md): developer maintenance, compatibility, plans, and handoff notes.

## Links

- [Demo video](https://www.bilibili.com/video/BV1HG7361Env)
- [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)
- [Development documentation](development/README.md)
- [Apache License 2.0](LICENSE)
