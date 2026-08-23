# KimodoUnityBridge

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

In Unity Package Manager, choose **Add package from disk** and select this repository's `package.json`.
For a local manifest dependency, use:

```json
"com.unity.kimodo_unity_motion_tools": "file:C:/nvlab/KimodoUnityBridge"
```

## Minimal start

1. Import the package's **Light Sample** from Unity Package Manager, or open the [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo).
2. Open its Timeline and select a Kimodo clip bound to a character.
3. Enter a motion prompt and choose **Generate & Bake**.
4. Wait for the project-local runtime and model to become ready, then play the Timeline.

Runtime diagnostics are written to `NvlabKimodoQuickServer~/log/setup.log` and `NvlabKimodoQuickServer~/log/bridge_server.log` in the Unity project.

## AI agents

Install the package into the target project, then read the English section of [TOOLS.md](TOOLS.md). Discover current commands through `KimodoUnityBridge.Command.command_dispatcher` and `kimodo_help` before execution.

## Links

- [Demo video](https://www.bilibili.com/video/BV1HG7361Env)
- [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)
- [Development and CLI coverage](DEVELOPMENT.md)
- [Apache License 2.0](LICENSE)
