# KimodoUnityBridge

[English](README.md)

Kimodo 为现有 Unity 项目提供本地 AI 人形动画生成能力，支持基于提示词的动作生成、姿态与末端执行器约束、分析、烘焙、重定向、Animator 内容、Timeline 编排以及运行时动作播放。

- 使用项目自有的本地运行时。
- 支持 Windows、macOS 和 Linux；主要加速路径为 CUDA，也提供 CPU 回退。
- 动画生成和资产管理均纳入 Unity 项目工作流。

## 环境要求

- Unity 2022.3 或更高版本
- 一个现有的 Unity 项目
- 人形工作流需要带有效 Humanoid Avatar 的角色
- 至少 8 GB 内存，并为所选模型准备足够磁盘空间

## 安装

在 Unity Package Manager 中选择 **Add package from git URL**，然后输入：

```text
https://github.com/OneYoungMean/KimodoUnityBridge.git
```

也可以在 manifest 中使用同一个 Git 地址：

```json
"com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
```

## 最小使用流程

1. 从 Unity Package Manager 导入 **Light Sample**，或打开 [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)。
2. 打开其中的 Timeline，选择绑定到角色的 Kimodo Clip。
3. 输入动作提示词并选择 **Generate & Bake**。
4. 等待项目本地运行时和模型准备就绪，然后播放 Timeline。

运行时诊断日志位于 Unity 项目中的 `NvlabKimodoQuickServer~/log/setup.log` 和 `NvlabKimodoQuickServer~/log/bridge_server.log`。

## 命令 API 快速参考

命令使用 JSON 输入和输出。完整且实时的 schema 见 [`Command/help.json`](Command/help.json)，也可以通过 `kimodo_help` 查询。

### 生成动画并获取路径

生成是异步的。保存 `kimodo_generate_animation` 返回的 `request_id`，然后调用 `kimodo_get_generation` 轮询，直到状态结束：

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

完成结果中的 `path` 是生成的 `AnimationClip` 在项目中的相对路径。任务尚未生成资产或失败时该字段为空；后续 Session 命令应使用安全的 `animation` 名称。

### 解析 Session 中的原始对象

当外部工具需要真实 Unity 对象引用时，使用 `session_get_raw`。`kind` 支持 `character`、`track`、`clip`、`constraint`；可选的 `character` 用于在同名对象之间消歧：

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

名称必须与 Session 对象精确匹配（匹配时不区分大小写）。如果存在多个匹配对象，请传入 `character`，否则命令会返回 `invalid_argument` 错误，并在消息中说明名称有歧义。`guid` 是 Unity `GlobalObjectId`，可用于定位对象；对象具有资产路径时，`asset_guid` 是对应的 `AssetDatabase` GUID。场景对象以及部分 Timeline track/marker 可能没有 `asset_guid` 或 `path`。

## 使用者

将包安装到目标 Unity 项目后，按上面的最小使用流程操作。运行行为和命令结果以已安装项目及其实时 schema 为准。

## 开发者与 AI Agent

先阅读 [SKILL.md](SKILL.md) 了解动画 Agent 执行规则，再阅读 [Command/help.json](Command/help.json) 获取当前命令和参数。实时 schema 与运行时返回值优先于本 README。

仓库维护规则和开发记录集中在 [`development/`](development/README.md)。根目录 [`AGENTS.md`](AGENTS.md) 只是仓库工具入口，详细维护规则位于 development 中。

文档职责如下：

- `README.md` / [`README.zh-CN.md`](README.zh-CN.md)：面向使用者的包说明。
- `SKILL.md` 与 `skills/*.md`：Agent 执行规则和任务流程。
- `Command/help.json`：实时命令与参数 schema。
- [`development/`](development/README.md)：开发维护、兼容性、计划和交接说明。

## 链接

- [演示视频](https://www.bilibili.com/video/BV1HG7361Env)
- [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)
- [开发文档](development/README.md)
- [Apache License 2.0](LICENSE)
