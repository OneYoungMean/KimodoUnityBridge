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

## AI Agent 使用手册

将 [SKILL.md](SKILL.md) 作为安装与任务入口；它负责协作调度 `tools/` 下的能力工具。`kimodo_install_server` 与 `kimodo_generate_animation` 均为异步命令，都会返回 `request_id`，统一通过 `kimodo_get_generation` 轮询。命令定义位于 [Command/help.json](Command/help.json)。

## 开发者与维护记录

仓库维护规则和开发记录集中在 [`development/`](development/README.md)。根目录 [`AGENTS.md`](AGENTS.md) 只是仓库工具入口，详细维护规则位于 development 中。

文档职责如下：

- `README.md` / [`README.zh-CN.md`](README.zh-CN.md)：面向使用者的包说明。
- `SKILL.md`：安装与任务入口。
- `tools/*.md`：协作能力工具和任务流程。
- `Command/help.json`：命令定义与参数 schema。
- [`development/`](development/README.md)：开发维护、兼容性、计划和交接说明。

## 链接

- [演示视频](https://www.bilibili.com/video/BV1HG7361Env)
- [FullDemo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)
- [开发文档](development/README.md)
- [Apache License 2.0](LICENSE)
