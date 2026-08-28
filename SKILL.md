---
name: kimodo-unity-bridge
description: Install and use KimodoUnityBridge for character-animation recognition, comparison, and generation while deferring exact commands and parameters to the maintained API help.
---

# KimodoUnityBridge

## Installation

- Add this package to the target Unity project and allow Unity to finish importing and compiling it.
- On first use, initialize the project-local runtime through the installation entry documented in [Command/help.json](Command/help.json).
- Do not repeat runtime installation after an ordinary compile, import, or Editor restart. Run it again only for installation, upgrade, or recovery.
- Use the package diagnostics when installation or startup fails; do not claim the animation workflow is ready until the required project components are available.

## Workflow

Read only the sub-skill that matches the request:

- [Recognize](skills/recognize.md): determine whether animation evidence expresses a requested motion.
- [Compare](skills/compare.md): compare the relative quality of two animations.
- [Generate](skills/generate.md): create a new animation or a corrected derived result.

When a request combines these intents, apply the relevant sub-skills in outcome order—for example, recognize or compare existing evidence before generating a correction.

## Boundaries

- Do not infer motion meaning or quality from filenames, internal labels, candidate order, or undocumented conventions.
- Preserve completed animation results; create a derived result when generation or correction is required.
- Do not invent a source, target character, frame range, pose, path, or constraint that the request and project state do not establish.
- Treat visual and structured analysis as evidence, not certainty. Static evidence alone cannot prove sliding, popping, acceleration, or velocity continuity.
- Report unsupported operations and unverified properties explicitly instead of inventing a workflow or claiming success.
- A visual result passes only after its returned visual evidence has actually been inspected.

## API help

Before invoking an API, read [Command/help.json](Command/help.json). It defines the maintained command names, permitted fields, required fields, enums, defaults, nested schemas, and command descriptions. Current runtime results and errors outrank prose guidance when they differ.

## 中文

### 安装

- 将本 Package 加入目标 Unity 项目，并等待 Unity 完成导入与编译。
- 首次使用时，按照 [Command/help.json](Command/help.json) 中的安装入口初始化项目内运行环境。
- 普通编译、导入或 Editor 重启后不要重复安装；只有安装、升级或恢复时才再次运行。
- 安装或启动失败时检查 Package 诊断信息；必要的项目组件未就绪前，不能声称动画工作流已经可用。

### 使用流程

只读取与请求匹配的子 Skill：

- [识别](skills/recognize.md)：判断动画证据是否表达指定动作。
- [比较](skills/compare.md)：比较两个动画的相对质量。
- [生成](skills/generate.md)：创建新动画或修正后的派生结果。

请求同时包含多个意图时，按结果依赖顺序组合相关子 Skill，例如先识别或比较现有证据，再生成修正版。

### 边界约束

- 不能根据文件名、内部标签、候选顺序或未记录的惯例推断动作含义或质量。
- 保留已经完成的动画结果；生成或修正时创建派生结果。
- 不能臆造请求和项目状态未确定的源、目标角色、帧范围、姿势、路径或约束。
- 视觉与结构化分析只是证据，不等于确定事实；静态证据无法单独证明滑步、跳变、加速度或速度连续性。
- 明确报告不支持的操作和无法验证的属性，不能虚构流程或声称成功。
- 只有实际检查返回的视觉证据后，才能报告视觉通过。

### API Help

调用 API 前读取 [Command/help.json](Command/help.json)。它定义当前维护的命令名称、允许字段、必选字段、枚举、默认值、嵌套结构和命令说明。运行时返回和错误与文字说明不一致时，以当前运行时结果为准。
