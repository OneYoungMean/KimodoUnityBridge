# Kimodo Unity Bridge 使用手册

> 适用版本：KimodoUnityBridge v2.0.0

开箱即用、完全运行在本地的 AI 人形动画生成系统。需输入提示词、放置约束、点击生成，就能在 Unity 里得到想要的角色动画。

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
- **[Animator Tool（暂不可用）](Animator%20Tool%20说明书.md)** — 2.0.0 保留实现但禁用了菜单入口，等待工作流复核后再开放。
- **[Constraint Tool](Constraint%20Tool%20说明书.md)** — 用约束 Marker 精确控制某一帧的姿势、手脚位置和移动轨迹。

### 配置与运行时

- **[Kimodo Server Manager](Kimodo%20Server%20Manager%20说明书.md)** — 本地服务器、模型管理与全局选项的控制台（位于 Project Settings）。
- **[Runtime Motion Driver](Runtime%20Motion%20Driver%20说明书.md)** — 在 Play Mode 或发布版连续生成、实时重定向、更新提示词和运行时约束。
- 旧版 `KimodoInfiniteMotionDemo` 与对应 API 文档已移入仓库 `Archive~`，不再代表 2.0.0 的现行接口。

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

下表仅概括 AMD/ROCm 厂商侧的可尝试范围，不代表这些型号已通过 Kimodo 2.0.0 的完整验证；Kimodo 的 AMD 路线整体仍为实验性支持。

| 显卡型号 | 平台 | Kimodo 2.0.0 状态 | 备注 |
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
[KimodoBridge] Generate JSON: {"cmd":"generate","task_id":"d3bf65fea5af4b9b81470b4e477c3e01","time_as_double":0.0,"output_format":"kmb_v1","duration":5.0,"diffusion_steps":100,"text_weight":1.0,"seed":42,"transition_duration":0.0,"model":"Kimodo-SOMA-RP-v1","text_encoder_mode":"high_performance","models_root":"","force_hf_download":false,"owner_pid":39980,"prompt":"a man walk and say hello","constraints_json":"[\r\n  {\r\n    \"type\": \"root2d\",\r\n    \"frame_indices\": [\r\n      0\r\n    ],\r\n    \"smooth_root_2d\": [\r\n      [\r\n        1.45250833,\r\n        -1.27318E-07\r\n      ]\r\n    ],\r\n    \"global_root_heading\": [\r\n      [\r\n        1.0,\r\n        0.0\r\n      ]\r\n    ]\r\n  },\r\n  {\r\n    \"type\": \"fullbody\",\r\n    \"frame_indices\": [\r\n      96\r\n    ],\r\n    \"smooth_root_2d\": [\r\n      [\r\n        5.82245,\r\n        2.39648128\r\n      ]\r\n    ],\r\n    \"local_joints_rot\": [\r\n      [\r\n        [\r\n          -0.156771049,\r\n          -0.051211752,\r\n          0.133836731\r\n        ],\r\n        [\r\n          0.055792924,\r\n          0.008796106,\r\n          -0.06377604\r\n        ],\r\n        [\r\n          -0.0586637,\r\n          0.0603800565,\r\n          -0.0167797934\r\n        ],\r\n        [\r\n          0.167620629,\r\n          0.00551502826,\r\n          -0.08953347\r\n        ],\r\n        [\r\n          0.210506111,\r\n          0.10025575,\r\n          -0.119488522\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.263056666,\r\n          0.0288843624,\r\n          -0.00397691\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.179515421,\r\n          3.25732818E-09,\r\n          3.464863E-08\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.07777788,\r\n          0.0246161334,\r\n          -0.217670768\r\n        ],\r\n        [\r\n          0.418507427,\r\n          0.39918378,\r\n          -1.09501755\r\n        ],\r\n        [\r\n          -0.141499922,\r\n          -0.356744677,\r\n          -0.022965055\r\n        ],\r\n        [\r\n          -0.135467276,\r\n          0.139959216,\r\n          -0.07695397\r\n        ],\r\n        [\r\n          -0.4353713,\r\n          -0.753778,\r\n          -0.07911318\r\n        ],\r\n        [\r\n          0.0562501028,\r\n          0.0541740954,\r\n          -0.274701566\r\n        ],\r\n        [\r\n          0.000259215682,\r\n          0.385920227,\r\n          0.08321952\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.0505003445,\r\n          -0.273383439,\r\n          -0.549585462\r\n        ],\r\n        [\r\n          -0.09070826,\r\n          0.002054178,\r\n          -0.6310054\r\n        ],\r\n        [\r\n          -0.09070813,\r\n          0.002054005,\r\n          -0.631005049\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.108476967,\r\n          -0.386722118,\r\n          -0.497579\r\n        ],\r\n        [\r\n          -0.187682122,\r\n          0.08829855,\r\n          -0.631148\r\n        ],\r\n        [\r\n          -0.161730349,\r\n          0.00531825749,\r\n          -0.616615\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.127161145,\r\n          -0.30246377,\r\n          -0.456888348\r\n        ],\r\n        [\r\n          -0.210727736,\r\n          0.166402072,\r\n          -0.6587042\r\n        ],\r\n        [\r\n          -0.161592022,\r\n          0.007537249,\r\n          -0.6166285\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.177148923,\r\n          -0.3206913,\r\n          -0.3034145\r\n        ],\r\n        [\r\n          -0.244521841,\r\n          0.305587053,\r\n          -0.797039032\r\n        ],\r\n        [\r\n          -0.1606151,\r\n          0.0199728776,\r\n          -0.616606355\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.0153026031,\r\n          0.231667146,\r\n          0.0430364832\r\n        ],\r\n        [\r\n          0.4456375,\r\n          -0.05527256,\r\n          1.12859225\r\n        ],\r\n        [\r\n          0.0274326485,\r\n          0.7127498,\r\n          0.08195149\r\n        ],\r\n        [\r\n          -0.100106381,\r\n          -0.00392572442,\r\n          0.012430666\r\n        ],\r\n        [\r\n          -0.4365909,\r\n          0.7537042,\r\n          0.07806952\r\n        ],\r\n        [\r\n          0.056602627,\r\n          -0.0541779548,\r\n          0.274732918\r\n        ],\r\n        [\r\n          0.000602107437,\r\n          -0.385936916,\r\n          -0.0831370056\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.0523539223,\r\n          0.273297876,\r\n          0.5487439\r\n        ],\r\n        [\r\n          -0.0926392451,\r\n          -0.00216121972,\r\n          0.6307241\r\n        ],\r\n        [\r\n          -0.09263939,\r\n          -0.00216115825,\r\n          0.6307241\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.110088728,\r\n          0.386593729,\r\n          0.4961301\r\n        ],\r\n        [\r\n          -0.189516827,\r\n          -0.08844186,\r\n          0.630571663\r\n        ],\r\n        [\r\n          -0.163586155,\r\n          -0.00544200744,\r\n          0.6161246\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.128560826,\r\n          0.3023406,\r\n          0.455315381\r\n        ],\r\n        [\r\n          -0.212539732,\r\n          -0.166548818,\r\n          0.6580558\r\n        ],\r\n        [\r\n          -0.163446337,\r\n          -0.007636136,\r\n          0.6161384\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.178298935,\r\n          0.320569754,\r\n          0.3016425\r\n        ],\r\n        [\r\n          -0.24631615,\r\n          -0.305802733,\r\n          0.7962387\r\n        ],\r\n        [\r\n          -0.162495211,\r\n          -0.0200098455,\r\n          0.616112351\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          -0.0537720248,\r\n          -0.006740221,\r\n          -0.189833179\r\n        ],\r\n        [\r\n          0.149451733,\r\n          0.181626067,\r\n          0.0201095734\r\n        ],\r\n        [\r\n          0.04576752,\r\n          0.1476712,\r\n          -0.0530463532\r\n        ],\r\n        [\r\n          -0.0375188552,\r\n          -4.606655E-08,\r\n          2.224187E-09\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ],\r\n        [\r\n          0.159601957,\r\n          -0.126513839,\r\n          -0.3307873\r\n        ],\r\n        [\r\n          0.8806431,\r\n          -0.129935384,\r\n          -0.03024671\r\n        ],\r\n        [\r\n          0.352532029,\r\n          -0.10189,\r\n          0.07391455\r\n        ],\r\n        [\r\n          -0.135035187,\r\n          -7.08352843E-08,\r\n          7.70365247E-08\r\n        ],\r\n        [\r\n          0.0,\r\n          0.0,\r\n          0.0\r\n        ]\r\n      ]\r\n    ],\r\n    \"root_positions\": [\r\n      [\r\n        5.82245,\r\n        0.98216933,\r\n        2.39648128\r\n      ]\r\n    ]\r\n  }\r\n]"}
UnityEngine.Debug:Log (object)
KimodoBridge.BridgeProtocolClient:GenerateAsync (string,int,KimodoBridge.KimodoGenerationRequestDto,System.Action`1<string>,System.Threading.CancellationToken) (at C:/nvlab/KimodoUnityBridge/Runtime/Bridge/BridgeProtocolClient.cs:256)
KimodoBridge.KimodoBridgeService:SendGenerateRequestAsync (KimodoBridge.KimodoGenerationRequestDto,System.Action`1<string>,System.Threading.CancellationToken) (at C:/nvlab/KimodoUnityBridge/Runtime/Bridge/KimodoBridgeService.cs:448)
KimodoBridge.KimodoBridgeService/<GenerateAsync>d__37:MoveNext () (at C:/nvlab/KimodoUnityBridge/Runtime/Bridge/KimodoBridgeService.cs:165)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.KimodoBridgeGenerationResult>:Start<KimodoBridge.KimodoBridgeService/<GenerateAsync>d__37> (KimodoBridge.KimodoBridgeService/<GenerateAsync>d__37&)
KimodoBridge.KimodoBridgeService:GenerateAsync (KimodoBridge.KimodoGenerationRequestDto,System.Action`1<string>,System.Threading.CancellationToken)
KimodoBridge.KimodoBridgeCommand/<ExecuteBridgeAsync>d__1:MoveNext () (at C:/nvlab/KimodoUnityBridge/Runtime/Generation/Pipeline/KimodoBridgeCommand.cs:70)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.KimodoGenerationResultDto>:Start<KimodoBridge.KimodoBridgeCommand/<ExecuteBridgeAsync>d__1> (KimodoBridge.KimodoBridgeCommand/<ExecuteBridgeAsync>d__1&)
KimodoBridge.KimodoBridgeCommand:ExecuteBridgeAsync (KimodoBridge.KimodoBridgeCommandRequest,System.Action`2<KimodoBridge.KimodoBridgeCommandStage, string>,System.Threading.CancellationToken)
KimodoBridge.KimodoBridgeCommand/<ExecuteAsync>d__0:MoveNext () (at C:/nvlab/KimodoUnityBridge/Runtime/Generation/Pipeline/KimodoBridgeCommand.cs:27)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.KimodoBridgeCommandResult>:Start<KimodoBridge.KimodoBridgeCommand/<ExecuteAsync>d__0> (KimodoBridge.KimodoBridgeCommand/<ExecuteAsync>d__0&)
KimodoBridge.KimodoBridgeCommand:ExecuteAsync (KimodoBridge.KimodoBridgeCommandRequest,System.Action`2<KimodoBridge.KimodoBridgeCommandStage, string>,System.Threading.CancellationToken)
KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteKimodoRuntimePipelineAsync>d__4:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/GenerationPipeline/KimodoEditorGeneratePipeline.cs:196)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.KimodoBridgeCommandResult>:Start<KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteKimodoRuntimePipelineAsync>d__4> (KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteKimodoRuntimePipelineAsync>d__4&)
KimodoBridge.Editor.KimodoEditorGeneratePipeline:ExecuteKimodoRuntimePipelineAsync (KimodoBridge.Editor.KimodoEditorGenerateRequest,string,string)
KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteRuntimePipelineAsync>d__3:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/GenerationPipeline/KimodoEditorGeneratePipeline.cs:185)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.KimodoBridgeCommandResult>:Start<KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteRuntimePipelineAsync>d__3> (KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteRuntimePipelineAsync>d__3&)
KimodoBridge.Editor.KimodoEditorGeneratePipeline:ExecuteRuntimePipelineAsync (KimodoBridge.Editor.KimodoEditorGenerateRequest,string,string)
KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteAsync>d__1:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/GenerationPipeline/KimodoEditorGeneratePipeline.cs:32)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.Editor.KimodoEditorGenerateResult>:Start<KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteAsync>d__1> (KimodoBridge.Editor.KimodoEditorGeneratePipeline/<ExecuteAsync>d__1&)
KimodoBridge.Editor.KimodoEditorGeneratePipeline:ExecuteAsync (KimodoBridge.Editor.KimodoEditorGenerateRequest)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<GenerateAndFinalizeAsync>d__19:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipGenerationExecutionService.cs:646)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.Editor.KimodoEditorGenerateResult>:Start<KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<GenerateAndFinalizeAsync>d__19> (KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<GenerateAndFinalizeAsync>d__19&)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService:GenerateAndFinalizeAsync (KimodoBridge.KimodoPlayableClip,KimodoBridge.Editor.KimodoExternalConstraintRequest,System.Action`2<KimodoBridge.KimodoBridgeCommandStage, string>,System.Threading.CancellationToken)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<>c__DisplayClass4_0/<<StartSingle>b__0>d:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipGenerationExecutionService.cs:146)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<KimodoBridge.Editor.IKimodoEditorCommandResult>:Start<KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<>c__DisplayClass4_0/<<StartSingle>b__0>d> (KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<>c__DisplayClass4_0/<<StartSingle>b__0>d&)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService/<>c__DisplayClass4_0:<StartSingle>b__0 (KimodoBridge.Editor.EditorGenerateSession,System.Threading.CancellationToken)
KimodoBridge.Editor.EditorGenerateSessionRunner/<ExecuteAsync>d__14:MoveNext () (at C:/nvlab/KimodoUnityBridge/Editor/Core/Manager/EditorGenerateSessionRunner.cs:296)
System.Runtime.CompilerServices.AsyncTaskMethodBuilder:Start<KimodoBridge.Editor.EditorGenerateSessionRunner/<ExecuteAsync>d__14> (KimodoBridge.Editor.EditorGenerateSessionRunner/<ExecuteAsync>d__14&)
KimodoBridge.Editor.EditorGenerateSessionRunner:ExecuteAsync (KimodoBridge.Editor.EditorGenerateSessionRunner/RunningSessionState,System.Func`3<KimodoBridge.Editor.EditorGenerateSession, System.Threading.CancellationToken, System.Threading.Tasks.Task`1<KimodoBridge.Editor.IKimodoEditorCommandResult>>)
KimodoBridge.Editor.EditorGenerateSessionRunner:Start (UnityEngine.Object,string,KimodoBridge.Editor.KimodoEditorCommandKind,System.Func`3<KimodoBridge.Editor.EditorGenerateSession, System.Threading.CancellationToken, System.Threading.Tasks.Task`1<KimodoBridge.Editor.IKimodoEditorCommandResult>>,KimodoBridge.Editor.EditorGenerateSession&,string&) (at C:/nvlab/KimodoUnityBridge/Editor/Core/Manager/EditorGenerateSessionRunner.cs:99)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService:StartSingle (KimodoBridge.KimodoPlayableClip,KimodoBridge.Editor.EditorGenerateSession&,string&) (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipGenerationExecutionService.cs:142)
KimodoBridge.Editor.KimodoPlayableClipGenerationExecutionService:TryStartGenerate (KimodoBridge.KimodoPlayableClip,KimodoBridge.Editor.EditorGenerateSession&,string&) (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipGenerationExecutionService.cs:42)
KimodoBridge.Editor.KimodoPlayableClipEditor:DrawGenerationSection () (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipEditor.cs:241)
KimodoBridge.Editor.KimodoPlayableClipEditor:OnInspectorGUI () (at C:/nvlab/KimodoUnityBridge/Editor/Core/KimodoPlayableClipEditor.cs:130)
UnityEngine.GUIUtility:ProcessEvent (int,intptr,bool&)
