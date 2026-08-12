# Kimodo Unity Motion Tools — AI contract / AI 工具契约

This is machine-facing operational context, not a human tutorial. The English section is authoritative; the Chinese section is a debug mirror with the same structure.

Current versions / 当前版本：

- Unity package: `3.0.0` / Unity 包：`3.0.0`
- QuickServer: `2.2.4` — project-local Kimodo and ARDY generation runtime with explicit hand/foot target positions. / QuickServer：`2.2.4`——支持显式手脚末端目标位置的项目级 Kimodo 与 ARDY 生成运行时。

## English

### 1. Boundary

Use Kimodo only when an existing Unity project needs humanoid animation generation, analysis, pose editing, constraints, recording, retargeting, Animator import, or Timeline animation work. Use the Unity automation mechanism already present in the environment. Leave project creation, unrelated scene work, rendering, and general Timeline editing to other tools. The runtime belongs to the Unity project; never install or manage it as a machine-wide service.

### 2. Install into an existing project

1. Confirm the target contains `Assets`, `Packages`, and `ProjectSettings`.
2. Read `ProjectSettings/ProjectVersion.txt`. The package requires Unity `2022.3` or newer.
3. Preserve the existing `Packages/manifest.json` and add only this dependency when absent:

   ```json
   "com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
   ```

   Use a user-supplied Git/Gitee URL or local `file:` path when requested.
4. Let the existing Unity automation refresh the project. Diagnose package resolution from Unity logs; never edit `Library/PackageCache`.

### 3. Discover commands before use

Resolve the installed package root from an embedded package, the manifest's `file:` target, or `Library/PackageCache`. The public framework-neutral C# entry point is:

```csharp
KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

Always start with:

```csharp
command_dispatcher.Invoke("kimodo_help", "{}");
command_dispatcher.Invoke("kimodo_help", "{\"command\":\"COMMAND_NAME\"}");
command_dispatcher.Invoke("kimodo_help", "{\"section\":\"models\"}");
```

The returned schema is authoritative. Use only returned command names and parameters, and use a registered model name where required. Every command returns JSON with `ok:true` on success or `ok:false,error:<message>` on failure.

### 4. Stable execution rules

- Session time is fixed at 60 FPS. Public ranges use `[start_frame,end_frame)`; generation constraint frames are relative to the generated clip.
- Preserve every returned character/animation safe name, pose locator, `analysis_id`, and `request_id` exactly.
- Generation is asynchronous. Poll `kimodo_get_generation` until `status` is `completed`, `failed`, or `canceled`; acceptance or `running` is not completion.
- While generation is running, commands that sample or remove an overlapping range on the same character track fail immediately with `code: generation_range_locked`. Non-overlapping tracks/ranges continue normally; wait for the returned `request_id` to finish or cancel it before retrying.
- Asset generation and server maintenance run in Unity Edit Mode and must wait while Unity is compiling or importing.
- A current Session is the preferred workspace. `kimodo_generate_animation` can create and retain an automatic Session when none is open.
- Sessions reuse one hidden transient Director. `session_close` detaches that Director and preserves user scene GameObjects; named Session assets remain available for reopen.
- Use only models returned by `kimodo_help` with `section:"models"`.
- Do not call the raw QuickServer TCP protocol for normal Unity animation tasks.

### 5. Minimal generation workflow

```text
session_open {}
query_current_session {"query":"characters"}
kimodo_generate_animation {"character":"<safe name>","prompt":"stand still and breathe naturally","duration_frames":60}
kimodo_get_generation {"request_id":"<request_id>"}  # repeat to terminal state
session_close {}
```

Before each non-trivial call, request that command's current help. Verify the final response, generated animation asset, Session metadata, and Unity Console. Report visual playback separately when it was not observed.

### 6. Pose and constraint semantics

A pose locator is `{"source":"<source>","frame":<integer>}`. A character source samples a read-only Timeline pose. A `<character>.Poses` source is writable. Use `pose_copy` before modifying a read-only pose, then use `pose_get`/`pose_set`. Pose `root.forwardPos` is signed ground-plane distance along canonical forward (+Z), `root.rightwardPos` is signed ground-plane distance along canonical right (+X), and `root.rotateY` is absolute world yaw in degrees around Unity Y. The internal skeleton rotations remain axis-angle data. Do not infer coordinate spaces or manufacture profile-skeleton values; preserve data returned by the pose commands.

Inline constraints use one sparse object per relative frame. The legacy `{frame,type,...}` union is rejected. Each object has `frame` plus one or more of these fields:

- `fullbody: {pose}` constrains the complete body, including root position and heading.
- `root2d: {pose}` or `{position:[x,z],heading:[x,z]}` constrains only planar root motion.
- `left_hand`, `right_hand`, `left_foot`, and `right_foot` each accept `pose`, `position:[x,y,z]`, `rotation:[x,y,z,w]`, or a combination.

Coordinates are canonical Unity coordinates: +X right, +Y up, +Z forward. `root2d` overrides the planar root inherited from same-frame `fullbody`; hand/foot position and rotation override their own pose or same-frame `fullbody`. Querying constraints returns the same per-frame structure.

Minimal pose-edit flow:

```text
kimodo_analyze -> save analysis_id/keyframes
pose_copy -> save writable pose locator
pose_get -> inspect current data
pose_set -> change only required fields
query_picture -> inspect a four-view diagnostic image
kimodo_generate_animation -> pass the locator in constraints
kimodo_get_generation -> poll to terminal state
```

### 7. Analysis, recording, retargeting, and Animator

- `kimodo_analyze` accepts either one named animation or a Session frame range. Save its `analysis_id` for `query_picture`.
- `kimodo_record_range` records a half-open Session range into a source-character AnimationClip.
- `kimodo_retarget_animation` retargets one loaded animation to another current Session character. Valid source and target Humanoid Avatars are required.
- `session_try_add` imports a humanoid character, AnimationClip, or Animator content according to its current schema. Deterministic clip-to-clip transitions may be recorded; BlendTree branch choice and unrelated Timeline placement belong to the surrounding Unity tool.
- Query current Session state before mutation and use returned safe names rather than scene guesses.

### 8. Project-owned runtime and diagnosis

Normal generation prepares and starts the project-local runtime as needed. `kimodo_debug_install_server` is debug-only incremental repair/setup; inspect its current help before use. Do not expose it as a global installer.

The default runtime root is the Unity project's `NvlabKimodoQuickServer~`, with its Python environment at root `.venv`. When Auto Sync Server is enabled and the installed runtime is older than the packaged version, major sync clears everything, minor sync keeps `models`, and patch sync keeps `models` plus root `.venv`. Relevant logs are `log/setup.log` and `log/bridge_server.log`. On failure, capture the command JSON/result, Unity version, `Editor.log`, runtime logs, Session state, and whether the failure occurred during package resolution, setup, model provisioning, generation, recording, retargeting, or playback.

### 9. Backend developer seam

For backend maintenance only, `NvlabKimodoQuickServer~/core` is the source of server routing, setup, protocol, and ARDY behavior; `NvlabKimodoQuickServer~/kimodo` contains Kimodo model/motion code. Launchers call `core.quickserver_cli`. Treat current source and tests as authoritative.

## 中文调试对照

### 1. 边界

仅在一个现有 Unity 项目需要人形动画生成、分析、Pose 编辑、约束、录制、Retarget、Animator 导入或 Timeline 动画工作时使用 Kimodo。复用环境中已经存在的 Unity 自动化机制。项目创建、无关场景工作、渲染以及一般 Timeline 编辑交给其他工具。运行时归 Unity 项目所有，禁止安装或管理成机器级全局服务。

### 2. 安装到现有项目

1. 确认目标包含 `Assets`、`Packages` 和 `ProjectSettings`。
2. 读取 `ProjectSettings/ProjectVersion.txt`；当前包要求 Unity `2022.3` 或更新版本。
3. 保留现有 `Packages/manifest.json`，缺少时只增加以下依赖：

   ```json
   "com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
   ```

   用户明确指定时改用其 Git/Gitee 地址或本地 `file:` 路径。
4. 让现有 Unity 自动化刷新项目。从 Unity 日志诊断包解析错误；禁止编辑 `Library/PackageCache`。

### 3. 使用前发现命令

依次从 embedded package、manifest 的 `file:` 目标或 `Library/PackageCache` 解析安装后的包根目录。公开且框架无关的 C# 入口是：

```csharp
KimodoUnityBridge.Command.command_dispatcher.GetCommandDefinitionsJson();
KimodoUnityBridge.Command.command_dispatcher.Invoke(commandName, argumentsJson);
```

始终从以下调用开始：

```csharp
command_dispatcher.Invoke("kimodo_help", "{}");
command_dispatcher.Invoke("kimodo_help", "{\"command\":\"COMMAND_NAME\"}");
command_dispatcher.Invoke("kimodo_help", "{\"section\":\"models\"}");
```

返回的 schema 是权威定义。只使用 schema 返回的命令名和参数，并在要求模型时使用注册模型名。所有命令均返回 JSON：成功为 `ok:true`，失败为 `ok:false,error:<message>`。

### 4. 稳定执行规则

- Session 时间固定为 60 FPS。公开区间使用 `[start_frame,end_frame)`；生成约束帧是生成 Clip 内的相对帧。
- 原样保存所有返回的角色/动画安全名称、Pose locator、`analysis_id` 和 `request_id`。
- 生成是异步任务。反复调用 `kimodo_get_generation`，直到 `status` 为 `completed`、`failed` 或 `canceled`；accepted 或 `running` 不代表完成。
- 生成运行期间，若命令要采样或删除同一角色 Track 上与生成区间重叠的范围，会立即返回 `code: generation_range_locked`。不重叠的 Track/区间仍可正常执行；应等待返回的 `request_id` 结束，或取消后重试。
- 动画资产生成与服务器维护只在 Unity Edit Mode 执行；Unity 编译或导入期间必须等待。
- 优先在当前 Session 中工作。没有 Session 时，`kimodo_generate_animation` 可以创建并保留自动 Session。
- 所有 Session 复用一个隐藏的临时 Director。`session_close` 只解绑该 Director，并保留用户场景 GameObject；命名 Session 资产仍可重新打开。
- 模型只能使用 `kimodo_help` 的 `section:"models"` 返回项。
- 普通 Unity 动画任务不得直接调用 QuickServer TCP 协议。

### 5. 最小生成工作流

```text
session_open {}
query_current_session {"query":"characters"}
kimodo_generate_animation {"character":"<安全名称>","prompt":"stand still and breathe naturally","duration_frames":60}
kimodo_get_generation {"request_id":"<request_id>"}  # 重复至终态
session_close {}
```

每个非平凡调用前先查询该命令的当前 help。验证最终响应、生成的动画资产、Session Metadata 和 Unity Console。没有实际观察播放时，将视觉验证单独标为未验证。

### 6. Pose 与 Constraint 语义

Pose locator 是 `{"source":"<source>","frame":<整数>}`。角色来源表示 Timeline 上的只读采样 Pose；`<角色>.Poses` 来源可写。修改只读 Pose 前先调用 `pose_copy`，之后使用 `pose_get`/`pose_set`。Pose 的 `root.forwardPos` 是沿角色标准前方（+Z）的有符号地面距离，`root.rightwardPos` 是沿角色标准右方（+X）的有符号地面距离，`root.rotateY` 是绕 Unity Y 轴的绝对世界 Yaw 角（度）；骨架内部旋转仍使用轴角数据。不得猜测坐标空间或伪造 Profile Skeleton 数值；应保留 Pose 命令返回的数据。

内联 Constraint 每个相对帧使用一个稀疏对象；旧 `{frame,type,...}` union 会被拒绝。每个对象包含 `frame`，以及以下一个或多个字段：

- `fullbody: {pose}`：约束完整身体，包括根骨骼位置与朝向。
- `root2d: {pose}` 或 `{position:[x,z],heading:[x,z]}`：只约束根骨骼平面运动。
- `left_hand`、`right_hand`、`left_foot`、`right_foot`：各自接受 `pose`、`position:[x,y,z]`、`rotation:[x,y,z,w]` 或其组合。

坐标统一为 Unity 规范坐标：+X 向右、+Y 向上、+Z 向前。`root2d` 覆盖同帧 `fullbody` 继承的平面根骨骼；手脚位置与旋转覆盖自身 Pose 或同帧 `fullbody`。查询 Constraint 时返回相同的逐帧结构。

最小 Pose 编辑流程：

```text
kimodo_analyze -> 保存 analysis_id/关键帧
pose_copy -> 保存可写 Pose locator
pose_get -> 检查当前数据
pose_set -> 只修改必要字段
query_picture -> 检查四视图诊断图
kimodo_generate_animation -> 在 constraints 中传入 locator
kimodo_get_generation -> 轮询到终态
```

### 7. Analysis、录制、Retarget 与 Animator

- `kimodo_analyze` 接受一个命名动画或一段 Session 帧区间；保存返回的 `analysis_id` 供 `query_picture` 使用。
- `kimodo_record_range` 将半开 Session 区间录制为源角色的 AnimationClip。
- `kimodo_retarget_animation` 将一个已加载动画 Retarget 到当前 Session 的另一角色；源角色和目标角色都必须具有有效 Humanoid Avatar。
- `session_try_add` 按当前 schema 导入 Humanoid 角色、AnimationClip 或 Animator 内容。确定性的 Clip-to-Clip 过渡可以录制；BlendTree 分支选择和无关 Timeline 摆放由外围 Unity 工具负责。
- 修改前先查询当前 Session 状态，使用返回的安全名称，不猜测场景名称。

### 8. 项目级运行时与诊断

普通生成会按需准备并启动项目级运行时。`kimodo_debug_install_server` 仅用于调试式增量修复/安装，使用前先读取当前 help；不得把它暴露成全局安装器。

默认运行根目录是 Unity 项目下的 `NvlabKimodoQuickServer~`，Python 环境位于该根目录的 `.venv`。启用 Auto Sync Server 后，运行时版本低于包内版本时会按 major/minor/patch 层级同步：major 清空全部内容，minor 保留 `models`，patch 保留 `models` 和根目录 `.venv`。相关日志为 `log/setup.log` 和 `log/bridge_server.log`。失败时保存命令 JSON/返回、Unity 版本、`Editor.log`、运行时日志、Session 状态，并标明问题发生在包解析、setup、模型准备、生成、录制、Retarget 还是播放阶段。

### 9. 后端开发边界

仅在维护后端时，`NvlabKimodoQuickServer~/core` 是服务器路由、setup、协议与 ARDY 行为的源码；`NvlabKimodoQuickServer~/kimodo` 保存 Kimodo 模型/动作代码。启动脚本调用 `core.quickserver_cli`。以当前源码和测试为准。
