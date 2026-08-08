# MCP Tools API 说明书

> 适用范围：KimodoUnityBridge v2.0.1，Unity Editor / Edit Mode

Kimodo 提供 10 个面向自动化调用的 Tool，可查询可用模型、维护 QuickServer，并根据角色和提示词生成独立 `AnimationClip` 资产，或直接在 Timeline 中创建并生成动画。

当前实现是**框架无关的 Unity Editor Tool 层**：`KimodoMcpTools` 负责 Tool 定义、JSON 参数解析和实际调用，但项目不会自动把这些 Tool 注册到某个 MCP Server。使用 Unity MCP 插件或自建 MCP Server 时，适配层需要把 Tool 名和 JSON 参数转发给本页介绍的统一入口。



## 快速接入

实现类位于：

```text
Editor/Core/Manager/KimodoMcpTools.cs
```

获取全部 Tool 的 JSON Schema：

```csharp
string definitionsJson = KimodoBridge.Editor.KimodoMcpTools.GetToolDefinitionsJson();
```

调用 Tool：

```csharp
string responseJson = KimodoBridge.Editor.KimodoMcpTools.Invoke(
    "kimodo_list_characters",
    "{\"include_project_assets\":false}");
```

最小 MCP 适配层只需完成两件事：

1. 用 `GetToolDefinitionsJson()` 返回的定义注册 Tool。
2. 收到调用后，把 Tool 名和 JSON 参数原样传给 `Invoke(toolName, argumentsJson)`，再把返回的 JSON 交还客户端。

例如：

```csharp
public static string ForwardKimodoTool(string toolName, string argumentsJson)
{
    return KimodoBridge.Editor.KimodoMcpTools.Invoke(toolName, argumentsJson);
}
```

这段代码只是转发入口，不包含特定 MCP 框架的注册写法。请按实际使用的 MCP Server 或 Unity MCP 插件补上对应的 Attribute、Handler 或消息通道。



## 通用约定

### 调用环境

- 生成接口仅允许在 Unity **Edit Mode** 中使用。
- Unity 正在编译、导入资源或准备进入 Play Mode 时，生成请求会被拒绝。
- 开始生成前，应先通过 Project Settings 中的 Kimodo Server Manager 配好服务器和模型。
- 编译、Assembly Reload、进入 Play Mode、切换 Edit Mode 活动场景或退出 Editor 时，正在运行的 Editor 生成任务会被取消。

### 响应格式

成功响应总是包含：

```json
{
  "ok": true
}
```

失败响应为：

```json
{
  "ok": false,
  "error": "错误原因"
}
```

参数解析、对象查找和生成启动错误都会转换成失败 JSON，不会要求 MCP 适配层处理 Unity 异常。

### 异步生成

两个生成 Tool 都会立即返回，不会等待动画完成。调用方必须保存 `request_id`，并轮询 `kimodo_get_generation`，直到状态变为 `completed`、`failed` 或 `canceled`。

```text
generate -> request_id -> get_generation -> running -> completed / failed / canceled
                                      \-> cancel_generation
```

任务记录只保存在当前 Unity Editor Domain 的内存中，最多保留最近 128 条。重新编译、Domain Reload 或重启 Editor 后，旧的 `request_id` 应视为过期。

### 对象引用

场景对象使用 Unity `GlobalObjectId` 字符串，例如：

```json
"GlobalObjectId_V1-2-..."
```

请使用 `kimodo_list_characters` 返回的 `character_ref`，不要自行拼接示例中的占位值。

Project 中的角色资产使用 `Assets/...` 路径，例如：

```json
"Assets/Characters/Hero.prefab"
```

只有 `kimodo_generate_animation_asset` 接受 Project 角色资产；Timeline 生成所用的角色和 `PlayableDirector` 必须是当前场景对象。
`pose_refs` 也必须是场景对象的 `GlobalObjectId`，每项可指向 Humanoid GameObject 或其 Animator。



## Tool 一览

| Tool | 用途 |
| --- | --- |
| `kimodo_list_characters` | 枚举可用于生成的 Humanoid 角色 |
| `kimodo_list_models` | 枚举当前服务器上所有可行的动作模型与文本编码器组合 |
| `kimodo_help` | 获取 QuickServer 内置协议说明 |
| `kimodo_reinstall_server` | 停止并重装 QuickServer，然后用高性能文本编码器激活默认模型 |
| `kimodo_open_timeline_session` | 创建并加载一个临时 TimelineAsset，供同一角色的生成结果按顺序写回 |
| `kimodo_close_timeline_session` | 关闭会话、清空 Timeline Window 选中并删除临时 TimelineAsset |
| `kimodo_generate_animation_asset` | 根据角色和提示词生成独立 AnimationClip 资产 |
| `kimodo_generate_timeline_animation` | 在指定 Director 的 Timeline 中创建并生成 Kimodo Clip |
| `kimodo_get_generation` | 查询异步生成进度和结果 |
| `kimodo_cancel_generation` | 取消仍在运行的生成任务 |



## kimodo_list_models

该接口会连接 QuickServer（必要时启动服务器），但不下载或加载动作模型。它从服务器返回扁平的 `configs`：每项都包含可直接传给生成接口的 `model` 与 `text_encoder_model`，以及当前设备下解析出的 `runtime_device`、`text_encoder_route` 和 `text_encoder_device`。`high_performance` 会根据设备与显存选择 INT8/NF4 路线；`high_precision` 选择 FP16 路线或 CPU 回退。

`available: true` 表示该组合受当前服务器支持并可请求；缺失权重仍会在首次激活或生成时下载。生成时应把同一项中的 `model` 和 `text_encoder_model` 原样传入。


## kimodo_help

该接口转发 QuickServer 的内置 `help` 协议，返回所有 TCP 命令及其简短说明，包括 `runtime.list_models`、`runtime.activate`、`generate`、`cancel` 和会话命令。它不加载模型，可用于 Agent 在运行时确认协议能力。


## kimodo_reinstall_server

该接口会停止当前 Bridge、重装打包的 `NvlabKimodoQuickServer~` 运行目录，并保留已有 `models` 缓存；重装完成后自动用 Project Settings 的默认动作模型和 `high_performance` 文本编码配置激活运行时。激活阶段可能下载缺失模型并等待加载完成，返回 `ok: true` 才表示可生成。运行中的生成会先被停止，建议在批量生成前调用。


## kimodo_open_timeline_session / kimodo_close_timeline_session

`kimodo_open_timeline_session` 会在 `Assets/KimodoGeneratedClips/Timelines` 创建唯一的临时 `.playable` TimelineAsset，创建并绑定一个新的 AnimationTrack，然后把指定场景 Director 加载到此资产并打开 Timeline Window。同一 Session 下的资产生成会在提交时依次预留时段。

打开会话不会修改原 TimelineAsset；但 Director 会在会话期间指向该临时资产。关闭时，若 Director 仍指向会话资产，则会恢复其打开会话前的 Timeline 和播放时间；Timeline Window 会清空该会话的 Clip 选中和检查对象，临时 `.playable` 会被删除。已生成的独立 `.anim` 不会删除。运行中的生成任务必须先取消或结束，才能关闭 Session。

| 字段 | 类型 | 必需 | 说明 |
| --- | --- | --- | --- |
| `director_ref` | string | 打开时是 | 场景 `PlayableDirector` 或其 GameObject 的 `GlobalObjectId` |
| `character_ref` | string | 打开时是 | 场景 Humanoid 角色的 `GlobalObjectId` |
| `start_seconds` | number | 否 | 首段起始秒；默认 `0` |
| `timeline_session_id` | string | 关闭时是 | 打开接口返回的 Session id |



## kimodo_list_characters

枚举场景中的 Humanoid 角色；也可以选择扫描 `Assets` 下的 Prefab 或模型资产。只有能够解析出有效 Humanoid Avatar 的 Animator 才会返回。

### 参数

| 字段 | 类型 | 必需 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `include_project_assets` | boolean | 否 | `false` | 是否额外扫描 `Assets` 下的 GameObject 资产 |
| `max_results` | integer | 否 | `100` | 最大返回数量，实际限制在 `1..1000` |

### 示例

```json
{
  "include_project_assets": false,
  "max_results": 100
}
```

### 返回

```json
{
  "characters": [
    {
      "character_ref": "GlobalObjectId_V1-2-...",
      "name": "Hero",
      "source": "scene",
      "avatar": "HeroAvatar",
      "asset_path": "",
      "scene_path": "Assets/Scenes/Demo.unity",
      "active": true
    }
  ],
  "count": 1,
  "ok": true
}
```

字段说明：

- `source` 为 `scene` 或 `project`。
- 场景角色的 `character_ref` 是 `GlobalObjectId`；Project 角色的引用是 `Assets/...` 路径。
- `active` 表示场景对象当前是否处于激活层级；Project 资产通常为 `false`。



## kimodo_generate_animation_asset

根据提示词生成独立的 `.anim` 资产。这个接口不读取 Timeline Marker Constraint，但可以使用 `pose_refs` 提交显式姿态约束，适合资产批量生产、Agent 自动化和离线动画库构建。

### 参数

| 字段 | 类型 | 必需 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `character_ref` | string | 是 | — | 场景角色的 `GlobalObjectId`，或 Project 角色的 `Assets/...` 路径 |
| `prompt` | string | 是 | — | 动作提示词 |
| `duration_seconds` | number | 否 | `5` | 动画时长，必须为有限正数 |
| `model` | string | 否 | Project Settings 默认模型 | Kimodo 模型名 |
| `text_encoder_model` | string | 否 | Project Settings 默认值 | `high_performance` 或 `high_precision`；高性能配置会根据显存自动选择 INT8/NF4 路线 |
| `seed` | integer | 否 | 随机 | 固定后可用于复现同配置生成 |
| `diffusion_steps` | integer | 否 | 模型默认值 | 普通 Kimodo 省略时为 100；ARDY 省略时使用模型步数 |
| `text_weight` | number | 否 | `1` | 提示词权重，限制在 `0..4` |
| `output_mode` | string | 否 | `humanoid_muscle` | 输出格式，见下表 |
| `output_folder` | string | 否 | `Assets/KimodoGeneratedClips` | 输出目录，必须位于 `Assets` 下 |
| `asset_name` | string | 否 | 角色名加时间戳 | 资产名，不需要 `.anim` 扩展名 |
| `timeline_session_id` | string | 否 | — | 来自 `kimodo_open_timeline_session`；生成成功后将 AnimationClip 写入该 Timeline |
| `analysis_options` | object | 否 | — | 可选分析配置；见下方关键帧分析 |
| `pose_refs` | string[] | 否 | 空 | 场景 Humanoid GameObject 或 Animator 的 `GlobalObjectId`，作为姿态约束 |
| `times` | number[] | 否 | 首尾等间隔 | 每个姿态在生成动画内的秒数；提供时数量必须与 `pose_refs` 相等 |
| `constraint_types` | string[] | 否 | 每项 `fullbody` | 每个姿态的约束类型，只接受 `fullbody` 或 `root2d`，数量必须与 `pose_refs` 相等 |

省略 `times` 时：1 个姿态位于首帧；2 个位于首帧和尾帧；更多姿态在首尾帧之间等间隔分布。接口只读取每个对象调用时的当前 Humanoid 姿态，不会修改其 Animator、Avatar 或 Transform。

### Timeline Session 与关键帧分析

先打开 Session，再把返回的 `timeline_session_id` 传给资产生成请求。每次请求在**提交时**预留其 `duration_seconds`，所以并行任务也按提交顺序落在同一 AnimationTrack 上，而不是按完成顺序错位。

```json
{
  "director_ref": "GlobalObjectId_V1-2-...",
  "character_ref": "GlobalObjectId_V1-2-...",
  "start_seconds": 0
}
```

调用 `kimodo_open_timeline_session` 会返回 `timeline_session_id`、新建的 `timeline_asset_ref` / `timeline_asset_path`、`track_ref` 和下一段开始时间。生成任务完成后，`kimodo_get_generation` 会返回 Timeline Clip 资产引用；若开启关键帧分析，还会返回 `analysis_track_ref`。

```json
"analysis_options": {
  "keyframes": { "enabled": true, "max_count": 8 }
}
```

关键帧分析是 Python 侧的确定性后处理：保留首尾帧，并结合根加速度、转向、全身姿态变化、脚接触切换和时间覆盖选帧。结果随同生成完成响应的 `analysis.keyframes` 返回；每项的 `saliency` 是连续 `0..1` 值，`0` 表示平稳或普通，`1` 表示该生成片段内最显著的运动事件。首尾帧和时间覆盖仍可能被离散选中，因此“是否列为关键帧”不等同于显著性为二值。Session 存在时会额外写入 `Kimodo Analysis` MarkerTrack，供 Agent 按标记时间截图。它不会改变推理、KMB 载荷或生成出的 AnimationClip。

### 输出格式

| `output_mode` | 结果 |
| --- | --- |
| `humanoid_muscle` | 默认。重定向为 Humanoid Muscle Clip，适合在其他兼容 Humanoid 角色上使用 |
| `character_bone` | 重定向并写成目标角色骨架的 Bone Clip |
| `model_bone` | 保留模型原始骨架 Bone Clip，主要用于检查原始生成结果 |

`output_folder` 只接受 `Assets` 或其子目录。外部绝对路径以及包含 `.`、`..` 或空路径段的目录会被拒绝。若同名资产已存在，Unity 会生成唯一文件名，不会覆盖现有资产。

### 生成 Muscle Clip

```json
{
  "character_ref": "GlobalObjectId_V1-2-...",
  "prompt": "walk forward confidently and look around",
  "duration_seconds": 5,
  "output_mode": "humanoid_muscle",
  "seed": 12345
}
```

### 生成 Character Bone Clip 到指定目录

```json
{
  "character_ref": "Assets/Characters/Hero.prefab",
  "prompt": "turn left and wave with the right hand",
  "duration_seconds": 4,
  "output_mode": "character_bone",
  "output_folder": "Assets/Animations/Kimodo",
  "asset_name": "Hero_TurnAndWave"
}
```

### 启动返回

```json
{
  "character": "Hero",
  "output_mode": "humanoid_muscle",
  "seed": 12345,
  "request_id": "00000000-0000-0000-0000-000000000000",
  "status": "running",
  "ok": true
}
```

示例中的 GUID 仅表示字段格式，不是可查询的真实任务。



## kimodo_generate_timeline_animation

在指定 `PlayableDirector` 使用的 `TimelineAsset` 中创建 `KimodoPlayableClip`，设置提示词、起始时间和时长，然后启动现有 Timeline 生成流程。

### 参数

| 字段 | 类型 | 必需 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `director_ref` | string | 是 | — | 场景 `PlayableDirector` 或含该组件 GameObject 的 `GlobalObjectId` |
| `character_ref` | string | 是 | — | 场景 Humanoid 角色的 `GlobalObjectId` |
| `prompt` | string | 是 | — | 动作提示词 |
| `track_ref` | string | 否 | 自动选择 | 指定当前 Timeline 上的 AnimationTrack |
| `start_seconds` | number | 否 | `0` | Timeline 起始时间，必须为有限非负数 |
| `duration_seconds` | number | 否 | `5` | Clip 时长，必须为有限正数 |
| `model` | string | 否 | Project Settings 默认模型 | Kimodo 模型名 |
| `text_encoder_model` | string | 否 | Project Settings 默认值 | `high_performance` 或 `high_precision`；高性能配置会根据显存自动选择 INT8/NF4 路线 |
| `seed` | integer | 否 | 随机 | 本次生成使用的固定种子 |
| `diffusion_steps` | integer | 否 | 模型默认值 | 扩散步数 |
| `text_weight` | number | 否 | `1` | 提示词权重，限制在 `0..4` |
| `use_constraints` | boolean | 否 | `true` | 是否走现有 Timeline Constraint / Auto Begin 流程 |
| `analysis_options` | object | 否 | — | 可选关键帧分析配置；完成后随 `kimodo_get_generation` 返回 |
| `pose_refs` | string[] | 否 | 空 | 场景 Humanoid GameObject 或 Animator 的 `GlobalObjectId`，作为姿态约束 |
| `times` | number[] | 否 | 首尾等间隔 | 每个姿态相对生成 Clip 开头的秒数；提供时数量必须与 `pose_refs` 相等 |
| `constraint_types` | string[] | 否 | 每项 `fullbody` | 每个姿态的约束类型，只接受 `fullbody` 或 `root2d`，数量必须与 `pose_refs` 相等 |

### Track 选择规则

- 指定 `track_ref` 时，它必须是 `director_ref` 所用 Timeline 中的 `AnimationTrack`。
- Track 未绑定时，接口会把它绑定到 `character_ref` 的 Animator。
- Track 已绑定到其他角色时，请求会失败，不会改写原绑定。
- 省略 `track_ref` 时，接口优先复用已经绑定到该 Animator 的 AnimationTrack。
- 找不到可复用 Track 时，会新建 `Kimodo MCP - 角色名` Track 并完成绑定。

### Constraint 行为

- `use_constraints: true`：使用当前 Timeline 上启用的 Constraint，并沿用现有 Auto Begin 锚点流程。
- `use_constraints: false`：显式提交空外部 Constraint，不采集 Timeline Constraint。
- `pose_refs` 始终作为显式姿态约束；`use_constraints: true` 时会与 Timeline Constraint 合并，`false` 时只使用这些显式姿态。
- `pose_refs` 中的对象只读取调用时的当前 Humanoid 姿态，不会修改其 Animator、Avatar 或 Transform。
- 省略 `times` 时：1 个姿态位于首帧；2 个位于首帧和尾帧；更多姿态在首尾帧之间等间隔分布。

### 示例

```json
{
  "director_ref": "GlobalObjectId_V1-2-...",
  "character_ref": "GlobalObjectId_V1-2-...",
  "prompt": "run to the marked point and stop",
  "start_seconds": 2,
  "duration_seconds": 6,
  "use_constraints": true,
  "seed": 24680
}
```

### 启动返回

```json
{
  "timeline_clip_ref": "GlobalObjectId_V1-3-...",
  "track_ref": "GlobalObjectId_V1-3-...",
  "seed": 24680,
  "request_id": "00000000-0000-0000-0000-000000000000",
  "status": "running",
  "ok": true
}
```

Timeline 必须已经引用 `TimelineAsset`。接口会立即保存新建的 Track/Clip；若后续生成失败或被取消，该 Timeline Clip 仍可能保留，便于检查、重试或手动删除。



## kimodo_get_generation

查询生成任务的最新状态。

### 参数

```json
{
  "request_id": "从生成接口获得的 GUID"
}
```

### 返回

```json
{
  "request_id": "00000000-0000-0000-0000-000000000000",
  "status": "completed",
  "stage": "Completed",
  "message": "Generation completed.",
  "error": "",
  "started_at_utc": "2026-07-31T08:00:00.0000000Z",
  "target_alive": true,
  "asset_path": "Assets/KimodoGeneratedClips/Kimodo_Hero.anim",
  "raw_bone_asset_path": "",
  "seed": 12345,
  "prompt": "walk forward",
  "ok": true
}
```

字段说明：

- `status`：`running`、`completed`、`failed` 或 `canceled`。
- `stage`：当前管线阶段，例如 `Validate`、`Constraint`、`InvokeBackend`、`AssetWrite`、`Bake`、`Retarget`、`Finalize`、`Completed`。
- `message`：当前进度或完成消息。
- `error`：失败原因；没有错误时为空字符串。
- `target_alive`：发起任务时对应的 Unity 对象是否仍然存在。
- `asset_path`、`raw_bone_asset_path`、`seed`、`prompt`：生成结果建立后返回；没有对应资产时路径为空字符串。

查询未知、过期或 Domain Reload 前的 ID 会返回 `ok: false`。



## kimodo_cancel_generation

取消仍在运行的生成任务。取消信号是协作式的，接口返回后应继续查询任务，直到状态稳定为 `canceled`、`failed` 或 `completed`。

### 参数

```json
{
  "request_id": "从生成接口获得的 GUID",
  "reason": "用户取消批量生成"
}
```

`reason` 可省略；默认原因为 `Generation canceled by MCP.`。

### 返回

```json
{
  "request_id": "00000000-0000-0000-0000-000000000000",
  "status": "running",
  "stage": "InvokeBackend",
  "message": "Generating Kimodo motion...",
  "error": "",
  "started_at_utc": "2026-07-31T08:00:00.0000000Z",
  "canceled": true,
  "ok": true
}
```

`canceled: true` 表示已向一个仍在运行的任务发出取消请求；任务已经结束时通常返回 `false`。该响应是发出请求当下的状态快照，因此 `status` 可能暂时仍为 `running`。



## 推荐调用顺序

1. 调用 `kimodo_list_characters`，从返回值选择 `character_ref`。
2. 根据目标选择 `kimodo_generate_animation_asset` 或 `kimodo_generate_timeline_animation`。
3. 保存返回的 `request_id` 和实际采用的 `seed`。
4. 定期调用 `kimodo_get_generation`，不要阻塞 Unity 主线程等待结果。
5. `completed` 后读取 `asset_path`；`failed` 时记录 `error`；不再需要时调用 `kimodo_cancel_generation`。



## 当前边界

- 本页描述的是现有 `KimodoMcpTools` Unity Editor API，不是 Runtime/Player API。
- 项目当前未依赖或内置特定 Unity MCP 框架，也没有要求 `[McpTool]` 一类固定 Attribute。
- `GetToolDefinitionsJson()` 提供可注册的 JSON Schema；Tool 的传输、鉴权、连接生命周期和 MCP 客户端配置由外部适配层负责。
- 编译通过只能证明 C# 接口与项目源码兼容；在接入具体 MCP Server 后，仍需在 Unity 中实际验证 Tool 注册、主线程调度和端到端生成。
