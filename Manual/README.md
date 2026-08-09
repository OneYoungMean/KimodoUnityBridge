# Kimodo Unity Bridge 使用手册

> 适用版本：KimodoUnityBridge v2.0.1

开箱即用、完全运行在本地的 AI 人形动画生成系统。你只需输入提示词、放置约束、点击生成，就能在 Unity 里得到想要的角色动画。

这份手册是所有说明文档的入口。下面按你的使用场景，分别指向对应的分册。

<!-- 这里可以放一张插件整体界面的截图 -->



## 从这里开始

如果你是第一次接触本插件，建议按这个顺序读：

1. 先看 [Kimodo Server Manager](Kimodo%20Server%20Manager%20说明书.md)，把本地运行环境和模型准备好。
2. 再看 [Timeline Tool](Timeline%20Tool%20说明书.md)，跑通"写提示词 → 生成 → 播放"的最基础流程。
3. 之后按需深入约束、状态机等进阶用法；运行时请使用 `KimodoRuntimeMotionDriver`。

遇到报错随时翻 [常见问题与报错处理](常见问题与报错处理.md)。



## 分册目录

### 生成工具

- **[Timeline Tool](Timeline%20Tool%20说明书.md)** — 在时间轴上生成动画的基础玩法，包含长动画、循环、过渡的组合思路。
- **[Constraint Tool](Constraint%20Tool%20说明书.md)** — 用约束 Marker 精确控制某一帧的姿势、手脚位置和移动轨迹。

### 配置与运行时

- **[Kimodo Server Manager](Kimodo%20Server%20Manager%20说明书.md)** — 本地服务器、模型管理与全局选项的控制台（位于 Project Settings）。
- **[QuickServer 启动与协议](QuickServer%20启动与协议说明书.md)** — 命令行启动、Windows/Linux 差异、TCP 协议、JSON/BVH/KMB 输出和外部客户端示例。
- **[Runtime Motion Driver](Runtime%20Motion%20Driver%20说明书.md)** — 在 Play Mode 或发布版连续生成、实时重定向、更新提示词和运行时约束。
- 旧版 `KimodoInfiniteMotionDemo` 与对应 API 文档已移入仓库 `Archive~`，不再代表 2.0.1 的现行接口。

### API 与自动化

- **[Kimodo Animation Skill](../SKILL.md)** — 平台无关的最小生成自检和精确 API 契约：当前 Timeline Session、查询、采样、TryAdd/TryRemove、分析、Bake/Retarget 与异步生成。
- **[中文对照](../SKILL-zh.md)** — 与英文 Skill 逐项对应的中文 API 契约。
- **[自动化使用入口](Command%20API%20说明书.md)** — Unity MCP、Unity CLI、Unity Cowork 如何读取并遵循该 Skill。

### 排查问题

- **[常见问题与报错处理（QA）](常见问题与报错处理.md)** — 按场景分组的常见报错与解决方案。



## 环境要求

- Unity 2022.3 及以上，支持 Windows、macOS、Linux 平台。
- 内存 ≥ 8G，硬盘可用空间 ≥ 16G。
- NVIDIA 显卡显存 ≥ 6G 时可运行 CUDA 版本（不做强制限制，CPU 也能跑，只是更慢）。

### uv（环境配置工具）

Windows、macOS 和 Linux 的启动脚本都会检测 `uv`；缺少时会询问并尝试把本地副本下载到 QuickServer 的 `program/exe/uv`，通常无需提前安装。若自动安装失败或超时，可手动安装后再回到 Unity 继续生成：

```bash
# macOS / Linux
curl -LsSf https://astral.sh/uv/install.sh | sh
```

```powershell
# Windows PowerShell
powershell -ExecutionPolicy Bypass -c "irm https://astral.sh/uv/install.ps1 | iex"
```

## 支持的平台与硬件

### 系统平台

- **Windows**：当前最完整、最推荐的使用平台。
- **macOS**：支持本地运行，GPU 路线走 Apple `MPS`；`motion_correction` 默认可缺省。
- **Linux**：支持本地运行，适合 CUDA 机器和自定义部署环境。

### 硬件后端

- **NVIDIA CUDA**：当前支持最完整，也是主要推荐路线。High Performance 优先走 NF4/INT8，High Precision 走 FP16。
- **Apple Silicon / MPS**：支持，走 `FP16` 路线。
- **CPU**：始终可用，但速度会明显慢于 GPU。
- **Intel XPU**：当前已开始接入识别与分流，属于实验性支持；现阶段不要默认视为完整优化平台。
- **AMD / ROCm / 其他 GPU**：当前按通用加速器兼容路线处理，属于实验性支持；运行时自检通过后会使用可用的 FP16 路线，否则回退 CPU。

### 当前建议

- 想要最稳定、最快的体验：优先使用 **Windows + NVIDIA CUDA**。
- 使用 Mac：优先准备 **Apple Silicon + MPS** 预期，默认按 `FP16` 使用。
- 使用非 NVIDIA GPU：建议先按“可兼容运行”理解，而不是按“完整官方支持”理解。

### AMD 显卡参考

下表仅概括 AMD/ROCm 厂商侧的可尝试范围，不代表这些型号已通过 Kimodo 2.0.1 的完整验证；Kimodo 的 AMD 路线整体仍为实验性支持。

| 显卡型号 | 平台 | Kimodo 2.0.1 状态 | 备注 |
| --- | --- | --- | --- |
| Radeon RX 7000 / RX 9000 系列（AMD Windows 兼容表中的型号） | Windows | 实验性，可尝试 | 通常需要先安装 [HIP SDK / ROCm for Windows](https://rocm.docs.amd.com/projects/install-on-windows/en/latest/index.html)。 |
| Radeon PRO W7000 / W9000 部分型号 | Windows | 实验性，可尝试 | 仅按 AMD 的兼容范围判断，不能视为 Kimodo 完整验证。 |
| Ryzen AI Max 部分型号 | Windows | 实验性，可尝试 | 仅按 AMD 的兼容范围判断，不能视为 Kimodo 完整验证。 |
| Radeon RX 6000 系列 | Windows | 不建议 | 不应按开箱即用或正式支持预期。 |
| AMD 官方 ROCm 兼容表中的 GPU | Linux | 实验性，可尝试 | 一般需要先安装 [ROCm](https://rocm.docs.amd.com/en/latest/) 并准备匹配的 PyTorch 环境。 |



## macOS 上缺少 MotionCorrection解决方案（可选）

macOS 上缺少 `motion_correction` 通常不会影响 Kimodo 的主生成流程；它主要影响官方的后处理步骤。

当前版本在 macOS 上 **会在 setup 阶段自动尝试编译并安装 `motion_correction`**。QuickServer 会自动补 `cmake`，但如果系统里缺少底层构建依赖，编译仍然可能失败。

如果你在 macOS 上遇到 `motion_correction` 相关编译失败，建议先安装这些依赖：

- `brew install simde pybind11 eigen`

装完后重新生成；QuickServer 会在启动时再次检查 setup，并尝试自动构建它。

如果只是先跑通生成流程，可以先忽略这一步；等确认 mac 端确实需要官方后处理时，再处理 `motion_correction` 相关依赖即可。



## 提交反馈

如果遇到本手册没有覆盖的问题，欢迎提交日志帮助改进，具体方式见 [常见问题与报错处理](常见问题与报错处理.md) 的"提交 Bug"一节。
