# Runtime Motion Driver

> 适用版本：KimodoUnityBridge v2.0.1

## 概述

`KimodoRuntimeMotionDriver` 是 2.0.1 的现行运行时入口。它在组件启用时自动启动生成会话，持续取得动作片段并实时重定向到目标 Humanoid Animator；组件禁用时会停止会话。旧版 `KimodoInfiniteMotionDemo` 已移入 `Archive~`，不应再作为新项目的 API 依据。

## 快速使用

1. 给角色添加 **Kimodo → Runtime Motion Driver** 组件。
2. 将角色的 Humanoid Animator 指定到 **Target Animator**。
3. 选择模型、Text Encoder Mode、Prompt、采样步数和随机种子。
4. 进入 Play Mode。组件会自动启动 QuickServer、生成并播放动作。
5. Play Mode 中修改参数后点击 **Apply**；点击 **Reset Motion** 可清空队列并用当前设置重新开始。

发布前执行 **Kimodo → Install Kimodo Runtime To StreamingAssets**。该命令会把运行环境复制到 `Assets/StreamingAssets/NvlabKimodoQuickServer~`；发布版会从此目录启动 QuickServer。

## Inspector 主要设置

| 设置 | 说明 |
| --- | --- |
| **Target Animator** | 被实时驱动的 Humanoid Animator，必填。 |
| **Base Model** | 选择 Kimodo 或 ARDY 模型系列。 |
| **Model** | 选择该系列中实际使用的模型包。 |
| **Models Root** | 可选外部模型目录；留空使用 QuickServer 默认目录。 |
| **Text Encoder Mode** | High Performance 使用 NF4/INT8；High Precision 使用 FP16。设备位置自动决定。 |
| **Force CPU** | 强制动作模型和文本编码器走 CPU。 |
| **Prompt** | 连续生成使用的提示词。 |
| **Duration (s)** | 普通 Kimodo 每段时长，范围 1–10 秒。ARDY 使用 Profile 的流式时间窗，不显示此项。 |
| **Playback Reserve** | ARDY 剩余可播放时间到此阈值时请求后续动作，默认 1 秒。 |
| **Adaptive Playback Reserve** | 让 ARDY 根据实测响应耗时自动调整储备。 |
| **History Crop** | ARDY 历史窗口裁剪秒数；0 会根据下一个 Root2D/Full-Body 目标动态计算。未来窗口使用 Profile 上限，不在 Runtime Driver 中暴露。 |
| **Diffusion Steps** | Runtime Driver 的 ARDY 请求范围为 1–10；普通 Kimodo 范围为 1–1000。 |
| **Text Weight** | 提示词权重，范围 0–4。 |
| **Random / Seed** | 随机或固定种子。 |
| **Foot IK** | 驱动脚部目标并启用运行时双骨骼腿部 IK 修正。 |

## 脚本控制

```csharp
using KimodoBridge;

public sealed class RuntimeMotionExample : UnityEngine.MonoBehaviour
{
    public KimodoRuntimeMotionDriver driver;

    public void RunForward()
    {
        driver.SetPrompt("a person runs forward");
        driver.SetAnimationDurationSeconds(4f);
        driver.ApplyGenerationSettings();
    }

    public void MoveToWorldTarget()
    {
        driver.SetRoot2D(2f, 0f, 4f);
        driver.ApplyStagedConstraints();
    }

    public void ResetMotion()
    {
        _ = driver.ResetMotionAsync();
    }
}
```

常用接口：

- `SetPrompt` / `SetAnimationPrompt`：更新提示词。
- `GetCurrentPrompt` / `GetAnimationPrompt`：读取当前提示词。
- `SetAnimationDurationSeconds`：设置普通 Kimodo 分段时长；合法范围会被限制为 1–10 秒。
- `SetLeftHandConstraint`、`SetRightHandConstraint`、`SetLeftFootConstraint`、`SetRightFootConstraint`：暂存 Unity 世界坐标中的末端位置约束。
- `SetRoot2D`：暂存 Unity 世界坐标中的 Root2D 位置及可选世界朝向；朝向参数是世界 X/Z 平面方向向量。
- `SetRoot2DTarget`：世界坐标自动导航目标。ARDY 会跨 horizon 按速度/加速度持续重规划；普通 Kimodo 会据此估算本段时长并生成 Root2D 终点约束。
- `QueuePromptedRoot2D`：用世界坐标目标一次设置 Prompt、时长并提交 Root2D。
- `ApplyStagedConstraints` / `ClearConstraints`：应用或清除暂存约束。
- `GetPosition`：读取角色当前位置。
- `SegmentReady`、`SegmentStarted`、`SegmentCompleted`：监听分段生命周期。

## ARDY 注意事项

- ARDY 在一个 Horizon 内不可中断；新更新会等待当前 Horizon 完成，并只保留最新的待处理更新。
- 所有公开位置与 Root2D 朝向参数都使用 Unity 世界坐标；不再提供生成坐标或角色局部坐标入口。
- Core Horizon40 的 token 粒度是 4 帧、交付 Horizon 是 40 帧、有效 history 上限是 160 帧；这三个值彼此独立。
- ARDY 的提示词、约束或 seek 更新可能返回与旧帧重叠的绝对区间；Driver 会从响应的 `start_frame` 替换未播放时间线。

## 排查

- 编辑器运行目录：项目根目录下 `NvlabKimodoQuickServer~`。
- 发布版运行目录：`StreamingAssets/NvlabKimodoQuickServer~`。
- 日志：对应运行目录下的 `log/setup.log` 与 `log/bridge_server.log`。
- Play Mode 中 Inspector 改动只会暂存，必须点击 **Apply** 才会生效；Target、模型、模型目录、编码器模式或 Force CPU 改动会重启该 Driver 的生成会话。
