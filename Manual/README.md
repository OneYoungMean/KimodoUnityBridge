# Kimodo Unity Bridge 使用手册

> 适用版本：KimodoUnityBridge v1.1.39

开箱即用、完全运行在本地的 AI 人形动画生成系统。你只需输入提示词、放置约束、点击生成，就能在 Unity 里得到想要的角色动画。

这份手册是所有说明文档的入口。下面按你的使用场景，分别指向对应的分册。

<!-- 这里可以放一张插件整体界面的截图 -->



## 从这里开始

如果你是第一次接触本插件，建议按这个顺序读：

1. 先看 [Kimodo Server Manager](Kimodo%20Server%20Manager%20说明书.md)，把本地运行环境和模型准备好。
2. 再看 [Timeline Tool](Timeline%20Tool%20说明书.md)，跑通"写提示词 → 生成 → 播放"的最基础流程。
3. 之后按需深入约束、状态机、运行时等进阶用法。

遇到报错随时翻 [常见问题与报错处理](常见问题与报错处理.md)。



## 分册目录

### 生成工具

- **[Timeline Tool](Timeline%20Tool%20说明书.md)** — 在时间轴上生成动画的基础玩法，包含长动画、循环、过渡的组合思路。
- **[Animator Tool](Animator%20Tool%20说明书.md)** — 直接在状态机里替换某个状态的动作，或为两个状态之间插入衔接动画。
- **[Constraint Tool](Constraint%20Tool%20说明书.md)** — 用约束 Marker 精确控制某一帧的姿势、手脚位置和移动轨迹。

### 配置与运行时

- **[Kimodo Server Manager](Kimodo%20Server%20Manager%20说明书.md)** — 本地服务器、模型管理与全局选项的控制台（位于 Project Settings）。
- **[Runtime 配置与 API](Runtime%20配置与%20API%20说明书.md)** — 让发布版游戏在运行时实时生成动画，含 InfiniteMotionDemo 配置与代码接口。

### 排查问题

- **[常见问题与报错处理（QA）](常见问题与报错处理.md)** — 按场景分组的常见报错与解决方案。



## 环境要求

- Unity 2021 及以上，支持 Windows、macOS、Linux 平台。
- 内存 ≥ 8G，硬盘可用空间 ≥ 10G。
- NVIDIA 显卡显存 ≥ 6G 时可运行 CUDA 版本（不做强制限制，CPU 也能跑，只是更慢）。



## 支持的平台与硬件

### 系统平台

- **Windows**：当前最完整、最推荐的使用平台。
- **macOS**：支持本地运行，GPU 路线走 Apple `MPS`；`motion_correction` 默认可缺省。
- **Linux**：支持本地运行，适合 CUDA 机器和自定义部署环境。

### 硬件后端

- **NVIDIA CUDA**：当前支持最完整，也是主要推荐路线。`Low` 模式优先走 `NF4`，`High` 模式走 `FP16`。
- **Apple Silicon / MPS**：支持，走 `FP16` 路线。
- **CPU**：始终可用，但速度会明显慢于 GPU。
- **Intel XPU**：当前已开始接入识别与分流，属于实验性支持；现阶段不要默认视为完整优化平台。
- **AMD / ROCm / 其他 GPU**：当前按通用 GPU 兼容路线处理，属于实验性支持。若运行时自检通过，会使用 `Int8` 路线继续运行。

### 当前建议

- 想要最稳定、最快的体验：优先使用 **Windows + NVIDIA CUDA**。
- 使用 Mac：优先准备 **Apple Silicon + MPS** 预期，默认按 `FP16` 使用。
- 使用非 NVIDIA GPU：建议先按“可兼容运行”理解，而不是按“完整官方支持”理解。



## macOS 上的 MotionCorrection

macOS 上缺少 `motion_correction` 通常不会影响 Kimodo 的主生成流程；它主要影响官方的后处理步骤。

如果你希望在 macOS 上手动启用 `motion_correction`，可参考作者提供的安装思路：

1. 先安装依赖：`brew install cmake simde pybind11 eigen`
2. 进入 QuickServer 内的 `kimodo` 源码目录
3. 按作者脚本说明完成 `motion_correction` 的构建安装

如果只是先跑通生成流程，可以先忽略这一步；等确认 mac 端确实需要官方后处理时，再补装即可。



## 提交反馈

如果遇到本手册没有覆盖的问题，欢迎提交日志帮助改进，具体方式见 [常见问题与报错处理](常见问题与报错处理.md) 的"提交 Bug"一节。
