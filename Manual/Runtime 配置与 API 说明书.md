# Runtime 运行时配置与 API

## 概述

前面几份说明书讲的都是在编辑器里生成动画。Kimodo 同样可以打包进 Runtime——也就是说，你可以让发布出去的游戏在运行时实时生成人形动画，而不必提前烤好。

要看懂运行时怎么用，最好的样板是插件自带的 **KimodoInfiniteMotionDemo**（无限动作 Demo）。它挂在一个角色对象上，运行后会不停地生成新动作、首尾相接地播放，形成永不重复的连续表演。本文以它为线索，讲清楚运行时需要配置什么、以及可以从代码里调用哪些接口。

> 实时生成对硬件有要求。GPU 越好体验越流畅，作者建议 3080 及以上的显卡用来跑实时生成。

<!-- 这里放一张 Demo 运行时画面的截图（带底部提示词栏）-->



## 运行环境从哪里来

运行时和编辑器共用同一套本地服务器，只是目录位置不同：

- 在编辑器里：运行目录是工程根目录下的 `NvlabKimodoQuickServer~`。
- 打包发布后：运行目录在 `StreamingAssets/NvlabKimodoQuickServer~` 下。

也就是说，你在编辑器里通过 Server Manager 配好、下载好的环境，需要随包带到 StreamingAssets，发布版才能直接用。Demo 会自动按当前是编辑器还是发布版来解析正确的目录。

<!-- 这里放一张 StreamingAssets 目录结构的截图 -->



## Demo 上的配置项

把 KimodoInfiniteMotionDemo 组件挂到角色上后，Inspector 里会按分组显示这些字段。

### Scene References（场景引用）

| 字段 | 说明 |
| --- | --- |
| **Profile Skeleton Root** | 驱动动作的骨架根节点，生成的动作会先落在它上面。 |
| **Humanoid Retarget Animators** | 一个或多个要被重定向驱动的人形角色。填多个时，可以在运行中切换由哪个角色来表演。 |
| **Character Switch Duration Seconds** | 切换角色时的过渡时长（秒）。 |

### Bridge Runtime（服务器运行）

| 字段 | 说明 |
| --- | --- |
| **Models Root** | 可选的外部模型目录，留空则使用运行目录下的默认位置。 |
| **Model Name** | 使用的 Kimodo 模型，默认 `Kimodo-SOMA-RP-v1`。 |
| **High Vram** | 勾选使用完整编码器（约 16G 显存），否则用量化版（约 4G）。 |
| **Force Setup** | 强制重新配置运行环境，一般不用勾。 |
| **Startup Timeout Minutes** | 等待服务器启动的超时时间（分钟）。首次启动要下载模型，可适当放宽。 |

### Generation（生成）

| 字段 | 说明 |
| --- | --- |
| **Default Prompt** | 默认提示词，运行时底部输入框为空时使用它。 |
| **Generation Frames** | 每一段生成的帧数。 |
| **Diffusion Steps** | 采样步数，越高越精细也越慢。 |
| **Random Seed / Fixed Seed** | 勾选 Random 每段都不同；取消勾选则用固定 Seed 复现。 |
| **Segment Interval Seconds** | 每一段的目标时长（秒）。 |
| **Loop Hint** | 提示后端这是连续/循环生成，有助于段与段之间更连贯。 |
| **Overlap Constraint Samples** | 段与段衔接时，从上一段尾部取多少帧姿势作为下一段的约束，范围 1–10。值越大衔接越平滑。 |
| **Allow Partial Joints** | 允许动作数据只包含部分关节。 |
| **Trim Segment Tail / Segment Tail Trim Percent** | 是否裁掉每段尾部一小段、以及裁掉的比例。裁掉容易出问题的收尾帧，能让段间衔接更干净。 |

### Foot IK Targets（脚部 IK）

| 字段 | 说明 |
| --- | --- |
| **Drive Foot IK Targets** | 是否驱动脚部 IK 目标，减少脚滑。 |
| **Left / Right Foot IK Target Name** | 左右脚 IK 目标的对象名称。 |

### Debug（调试）

| 字段 | 说明 |
| --- | --- |
| **Auto Start On Enable** | 组件启用时自动开始生成。 |
| **Verbose Logging** | 输出详细日志，排查问题时打开。 |

<!-- 这里放一张 Demo Inspector 配置项的截图 -->



## 它是怎么连续生成的

理解了下面这套节奏，你就能照着做自己的实时生成逻辑：

1. 启动时拉起本地服务器，等它就绪。
2. 生成第一段动作，解析出动作数据并排入播放队列。
3. 播放当前段的同时，调度器在后台提前生成下一段，让队列不断档。
4. 每一段会取它**尾部的姿势**作为下一段的全身约束，下一段从这个姿势接着长出来——这就是段与段之间不跳变的原因。
5. 如此循环，形成连续不断的动作流。

底部的提示词栏让你随时介入：输入新描述、点 **Random** 换一句、拖 **Segment Length** 改每段长度、点 **Reset** 用新提示词重新开始。

<!-- 这里放一张"分段生成 + 首尾约束衔接"的示意图 -->



## 代码接口（API）

### 控制 Demo

KimodoInfiniteMotionDemo 暴露了一组方法，方便你用按钮或脚本驱动它：

```csharp
demo.StartDemo();                          // 开始连续生成
demo.StopDemo();                           // 停止
demo.ResetDemo();                          // 用当前提示词重新开始
demo.SetPrompt("a person waves hello");    // 更新提示词
demo.SetAnimationDurationSeconds(4f);       // 设置每段时长（秒）
demo.SetLoop(true);                        // 开关循环衔接
demo.GetLoop();                            // 查询循环衔接是否开启
demo.SetOverlap(4);                        // 设置段间衔接取多少帧（1–10）
demo.GetOverlap();                         // 查询当前衔接帧数
demo.SwitchToNextCharacter();              // 切换到下一个角色
```

带 Async 后缀的版本（`StartDemoAsync` 等）返回 Task，需要等待完成时可以用它们。

### 自己搭一套生成流程

如果你不想用 Demo、想从零接入，核心类型是 **KimodoRuntimeGenerationService**。它接收一份运行设置，负责启动服务器、提交生成请求、停止服务器。

```csharp
// 1. 准备运行设置
var settings = new KimodoRuntimeGenerationSettings
{
    bridgeSettings = new BridgeRuntimeSettings
    {
        runtimeRoot = runtimeRootPath,   // 运行目录
        launcherPath = launcherPath,     // 启动脚本路径
        modelName = "Kimodo-SOMA-RP-v1",
        highVram = false
    }
};

// 2. 创建服务并启动服务器
var service = new KimodoRuntimeGenerationService(settings);
await service.StartAsync(KimodoBackendType.Bridge, OnProgress, token);

// 3. 提交一次生成请求
var request = new KimodoGenerationRequestDto
{
    prompt = "a person dancing",
    duration = 5f,
    steps = 100,
    seed = null,            // null 表示随机种子
    loop_hint = true,
    constraints_json = ""   // 可选：首尾约束 JSON
};
KimodoGenerationResultDto result =
    await service.GenerateAsync(request, KimodoBackendType.Bridge, OnProgress, token);

// 4. 用完释放
service.Dispose();
```

`result.motionJsonCompact` 里是生成出来的动作数据，拿到之后解析、重定向到你的角色、播放即可（Demo 里的处理流程可以直接参考）。

### 几个关键类型

| 类型 | 作用 |
| --- | --- |
| **KimodoRuntimeGenerationService** | 运行时生成的总入口，管理启动、生成、停止。 |
| **KimodoRuntimeGenerationSettings** | 生成服务的设置，包含服务器设置和 ComfyUI 选项。 |
| **BridgeRuntimeSettings** | 本地服务器的详细设置（运行目录、启动脚本、模型、显存等）。 |
| **KimodoGenerationRequestDto** | 一次生成请求：提示词、时长、步数、种子、约束等。 |
| **KimodoGenerationResultDto** | 生成结果，主要是动作数据 `motionJsonCompact`。 |
| **KimodoBackendType** | 后端类型，Bridge（本地）或 ComfyUi。 |

<!-- 这里放一张运行时调用关系示意图 -->



## 注意事项

- 发布版要能用，必须把配好的运行环境随包放进 StreamingAssets，否则运行时找不到服务器会报错。
- 启动服务器和首次下载模型耗时较长，请把 Startup Timeout 设得宽松些，并在 UI 上给用户一个等待提示。
- 实时生成吃显卡。硬件一般时，可以适当调短每段时长、降低扩散步数来换取流畅度。
- 运行时报错的处理思路和编辑器一致，可参阅 **常见问题与报错处理** 一文；发布版的日志在 StreamingAssets 目录下。
