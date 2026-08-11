# Character Animation CLI Unity — AI contract / AI 工具契约

This is machine-facing operational context, not a human tutorial. The English section is authoritative; the Chinese section is a debug mirror with the same structure.

Current versions / 当前版本：

- Unity package: `0.1.0` / Unity 包：`0.1.0`
- QuickServer: `2.1.0` — project-local Kimodo and ARDY generation runtime. / QuickServer：`2.1.0`——项目级 Kimodo 与 ARDY 生成运行时。

## English

### 1. Boundary

Use Kimodo only when an existing Unity project needs humanoid animation generation, analysis, pose editing, constraints, baking, retargeting, Animator import, or Timeline animation work. Use the Unity automation mechanism already present in the environment. Leave project creation, unrelated scene work, rendering, and general Timeline editing to other tools. The runtime belongs to the Unity project; never install or manage it as a machine-wide service.

### 2. Install into an existing project

1. Confirm the target contains `Assets`, `Packages`, and `ProjectSettings`.
2. Read `ProjectSettings/ProjectVersion.txt`. The package requires Unity `2022.3` or newer.
3. Preserve the existing `Packages/manifest.json` and add only this dependency when absent:

   ```json
   "com.nvlab.character-animation-cli-unity": "file:C:/nvlab/Character_Animation_CLI_Unity"
   ```

   Use a user-supplied Git/Gitee URL or local `file:` path when requested.
4. Let the existing Unity automation refresh the project. Diagnose package resolution from Unity logs; never edit `Library/PackageCache`.

### 3. Discover commands before use

Resolve the installed package root from an embedded package, the manifest's `file:` target, or `Library/PackageCache`. The public framework-neutral C# entry point is:

```csharp
CharacterAnimationCli.Unity.Command.command_dispatcher.GetCommandDefinitionsJson();
CharacterAnimationCli.Unity.Command.command_dispatcher.Invoke(commandName, argumentsJson);
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

A pose locator is `{"source":"<source>","frame":<integer>}`. A character source samples a read-only Timeline pose. A `<character>.Poses` source is writable. Use `pose_copy` before modifying a read-only pose, then use `pose_get`/`pose_set`. Pose data is one nested object containing `muscles` (49 values in Unity Muscle index order `0-14,21-54`) plus `root`, `hands.left/right`, and `feet.left/right`; every transform is `{t:[x,y,z],q:[x,y,z,w]}` with translation in meters. These channels have the same semantics as MuscleClip RootTQ, HandTQ, and FootTQ. `pose_set` is a partial patch and does not clamp finite Muscle values. Root2D converts RootTQ through the Avatar on the pose's owning track.

Inline generation constraints are anonymous values with `frame` and `type`. The two core types are:

- `fullbody`: reads a pose locator as a complete body pose. It constrains the full-body joints and also includes the root bone position and heading.
- `root2d`: constrains only the root bone position and heading on the ground plane. It does not constrain the rest of the body. It may use a pose locator or direct `position` and `heading`.
- `left_hand`, `right_hand`, `left_foot`, `right_foot`: read the matching complete HandTQ or FootTQ from a pose locator.

At the same frame, `fullbody` supplies the base pose, `root2d` overrides RootTQ, and hand/foot constraints override their matching HandTQ or FootTQ. No general mask or weight is applied outside ClipConstraint. Use the exact current schema and start with few constraints.

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

### 7. Analysis, bake, retarget, and Animator

- `kimodo_analyze` accepts either one named animation or a Session frame range. Save its `analysis_id` for `query_picture`.
- `kimodo_bake_range` bakes a half-open Session range and can retarget to another current Session character. A valid Humanoid Avatar is required.
- `session_try_add` imports a humanoid character, AnimationClip, or Animator content according to its current schema. Deterministic clip-to-clip transitions may be baked; BlendTree branch choice and unrelated Timeline placement belong to the surrounding Unity tool.
- Query current Session state before mutation and use returned safe names rather than scene guesses.

### 8. Project-owned runtime and diagnosis

Normal generation prepares and starts the project-local runtime as needed. `kimodo_debug_install_server` is debug-only incremental repair/setup; inspect its current help before use. Do not expose it as a global installer.

The default runtime root is the Unity project's `NvlabKimodoQuickServer~`, with its Python environment at root `.venv`. When Auto Sync Server is enabled and the installed runtime is older than the packaged version, major sync clears everything, minor sync keeps `models`, and patch sync keeps `models` plus root `.venv`. Relevant logs are `log/setup.log` and `log/bridge_server.log`. On failure, capture the command JSON/result, Unity version, `Editor.log`, runtime logs, Session state, and whether the failure occurred during package resolution, setup, model provisioning, generation, bake, or playback.

Runtime `SetRoot2DTarget` treats a target inside `arrivalThresholdMeters` as already reached and does not stage a constraint.

### 9. Backend developer seam

For backend maintenance only, `NvlabKimodoQuickServer~/core` is the source of server routing, setup, protocol, and ARDY behavior; `NvlabKimodoQuickServer~/kimodo` contains Kimodo model/motion code. Launchers call `core.quickserver_cli`. Treat current source and tests as authoritative.

## 中文调试对照

### 1. 边界

仅在一个现有 Unity 项目需要人形动画生成、分析、Pose 编辑、约束、Bake、Retarget、Animator 导入或 Timeline 动画工作时使用 Kimodo。复用环境中已经存在的 Unity 自动化机制。项目创建、无关场景工作、渲染以及一般 Timeline 编辑交给其他工具。运行时归 Unity 项目所有，禁止安装或管理成机器级全局服务。

### 2. 安装到现有项目

1. 确认目标包含 `Assets`、`Packages` 和 `ProjectSettings`。
2. 读取 `ProjectSettings/ProjectVersion.txt`；当前包要求 Unity `2022.3` 或更新版本。
3. 保留现有 `Packages/manifest.json`，缺少时只增加以下依赖：

   ```json
   "com.nvlab.character-animation-cli-unity": "file:C:/nvlab/Character_Animation_CLI_Unity"
   ```

   用户明确指定时改用其 Git/Gitee 地址或本地 `file:` 路径。
4. 让现有 Unity 自动化刷新项目。从 Unity 日志诊断包解析错误；禁止编辑 `Library/PackageCache`。

### 3. 使用前发现命令

依次从 embedded package、manifest 的 `file:` 目标或 `Library/PackageCache` 解析安装后的包根目录。公开且框架无关的 C# 入口是：

```csharp
CharacterAnimationCli.Unity.Command.command_dispatcher.GetCommandDefinitionsJson();
CharacterAnimationCli.Unity.Command.command_dispatcher.Invoke(commandName, argumentsJson);
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

Pose locator 是 `{"source":"<source>","frame":<整数>}`。角色来源表示 Timeline 上的只读采样 Pose；`<角色>.Poses` 来源可写。修改只读 Pose 前先调用 `pose_copy`，之后使用 `pose_get`/`pose_set`。Pose 数据是一个嵌套对象：`muscles`（49 个值，Unity Muscle 索引顺序为 `0-14,21-54`），以及 `root`、`hands.left/right`、`feet.left/right`；每个变换均为 `{t:[x,y,z],q:[x,y,z,w]}`，位移单位为米。这些通道分别与 MuscleClip 的 RootTQ、HandTQ、FootTQ 语义一致。`pose_set` 是局部 Patch，不会 Clamp 有限的 Muscle 值。Root2D 通过该 Pose 所在轨道的 Avatar 转换 RootTQ。

生成内联 Constraint 是包含 `frame` 和 `type` 的匿名值。本节先明确两个核心类型：

- `fullbody`：从 Pose locator 读取完整人体姿态，约束全身关节位置，并同时包含根骨骼的位置与朝向。
- `root2d`：只约束根骨骼在地面平面上的位置与朝向，不约束其他身体关节。可以使用 Pose locator，也可以直接提供 `position` 和 `heading`。
- `left_hand`、`right_hand`、`left_foot`、`right_foot`：从 Pose locator 读取对应的完整 HandTQ 或 FootTQ。

同一帧中，`fullbody` 提供基础 Pose，`root2d` 覆盖 RootTQ，手脚约束覆盖对应的 HandTQ 或 FootTQ。除 ClipConstraint 外不应用通用 Mask 或 Weight。严格使用当前 schema，并从少量约束开始。

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

### 7. Analysis、Bake、Retarget 与 Animator

- `kimodo_analyze` 接受一个命名动画或一段 Session 帧区间；保存返回的 `analysis_id` 供 `query_picture` 使用。
- `kimodo_bake_range` Bake 半开 Session 区间，并可 Retarget 到当前 Session 的另一角色；角色必须具有有效 Humanoid Avatar。
- `session_try_add` 按当前 schema 导入 Humanoid 角色、AnimationClip 或 Animator 内容。确定性的 Clip-to-Clip 过渡可以 Bake；BlendTree 分支选择和无关 Timeline 摆放由外围 Unity 工具负责。
- 修改前先查询当前 Session 状态，使用返回的安全名称，不猜测场景名称。

### 8. 项目级运行时与诊断

普通生成会按需准备并启动项目级运行时。`kimodo_debug_install_server` 仅用于调试式增量修复/安装，使用前先读取当前 help；不得把它暴露成全局安装器。

默认运行根目录是 Unity 项目下的 `NvlabKimodoQuickServer~`，Python 环境位于该根目录的 `.venv`。启用 Auto Sync Server 后，运行时版本低于包内版本时会按 major/minor/patch 层级同步：major 清空全部内容，minor 保留 `models`，patch 保留 `models` 和根目录 `.venv`。相关日志为 `log/setup.log` 和 `log/bridge_server.log`。失败时保存命令 JSON/返回、Unity 版本、`Editor.log`、运行时日志、Session 状态，并标明问题发生在包解析、setup、模型准备、生成、Bake 还是播放阶段。

运行时 `SetRoot2DTarget` 会把 `arrivalThresholdMeters` 范围内的目标视为已经到达，不会暂存约束。

### 9. 后端开发边界

仅在维护后端时，`NvlabKimodoQuickServer~/core` 是服务器路由、setup、协议与 ARDY 行为的源码；`NvlabKimodoQuickServer~/kimodo` 保存 Kimodo 模型/动作代码。启动脚本调用 `core.quickserver_cli`。以当前源码和测试为准。
