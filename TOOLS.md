# Kimodo Unity Motion Tools — AI contract / AI 工具契约

This is machine-facing operational context, not a human tutorial. The English section is authoritative; the Chinese section is a debug mirror with the same structure.

Current versions / 当前版本：

- Unity package: `3.0.0` / Unity 包：`3.0.0`
- QuickServer: `2.2.7` — project-local Kimodo and ARDY generation runtime with representative non-root pose analysis. / QuickServer：`2.2.7`——带代表性非 Root 姿势分析的项目级 Kimodo 与 ARDY 生成运行时。

## English

### 1. Boundary

Use Kimodo for humanoid animation generation, analysis, pose editing, constraints, recording, retargeting, Animator import, and Timeline animation work in an existing Unity project. Use the environment's Unity automation mechanism and the project-owned runtime.

### 2. Install into an existing project

1. Confirm the target contains `Assets`, `Packages`, and `ProjectSettings`.
2. Read `ProjectSettings/ProjectVersion.txt`. The package requires Unity `2022.3` or newer.
3. Preserve the existing `Packages/manifest.json` and add only this dependency when absent:

   ```json
   "com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
   ```

   Use a user-supplied Git/Gitee URL or local `file:` path when requested.
4. Let the existing Unity automation refresh the project and use Unity logs to diagnose package resolution.

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


### 5. Minimal generation workflow

```text
session_open {}
query_current_session {"query":"characters"}
kimodo_generate_animation {"character":"<safe name>","prompt":"stand still and breathe naturally","duration_frames":60}
kimodo_get_generation {"request_id":"<request_id>"}  # repeat to terminal state
session_close {}
```

Before each non-trivial call, request that command's current help. Verify the final response, generated animation asset, Session metadata, and Unity Console. Generation completion produces a draft; apply the animation quality gate below before final acceptance.

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

### 7. Animation quality gate

After every successful generation, query the returned animation safe name and Session range, run `kimodo_analyze`, and pass its `analysis_id` to `query_picture`. Open and inspect the returned `image_path`; checking only that the path exists is not visual inspection. Compare the key poses with the prompt and check action readability, silhouette, balance, support/contact, facing and travel direction, left/right limb assignment, penetrations, and visibly broken or implausible poses. If the result fails, identify the failing frames, revise the prompt or constraints, regenerate, and repeat the gate.

For `loop:true` or a requested cycle, inspect relative frames `0`, `1`, `duration_frames-2`, and `duration_frames-1` and compare both sides of the wrap. Check pose, root position and heading, motion direction, phase, and planted hand/foot contacts. An in-place loop should return to the same root transform and pose within a small visual tolerance. A locomotion loop may retain cycle displacement, but displacement, heading, phase, and velocity must continue across the seam; do not force its root back to the origin. Endpoint stills alone do not prove temporal continuity. Inspect playback when available; otherwise report temporal seam verification as incomplete.

Report visual acceptance as `passed`, `needs_revision`, or `not_verified`. Never report `passed` unless the image was actually opened and inspected. Sparse key-pose images do not verify foot sliding, popping, timing, acceleration, or other temporal qualities; report them as unverified unless playback or sufficiently dense samples were inspected.

### 8. Analysis, recording, retargeting, and Animator

- `kimodo_analyze` accepts one named animation, a Session frame range, or two or more explicit pose locators. The first two routes use QuickServer. `keyframes.max_count` defaults to `4`: QuickServer selects real representative frames greedily so their linear span reconstructs maximum non-root pose variance; root translation and root-joint rotation are excluded when available. Each keyframe returns `non_root_subspace_explained_variance`. `issues.max_count` defaults to `8`; issues are ranked by severity and report the discontinuity transition as `from_frame` to `frame`. `continuity_quality_score`/quality score concerns numeric continuity only, not motion semantics. A `poses` request compares each adjacent pair locally: `muscle.cosine_similarity`/`cosine_distance` use complete Humanoid muscles, while `root_motion` reports XYZ delta, planar distance, vertical delta, and shortest yaw delta. Pose comparison does not replace temporal clip analysis. Save the `analysis_id` returned by clip/range analysis for `query_picture`.
- `kimodo_record_range` records a half-open Session range into a source-character AnimationClip.
- `kimodo_retarget_animation` retargets one loaded animation to another current Session character. Valid source and target Humanoid Avatars are required.
- `session_try_add` imports a humanoid character, AnimationClip, or Animator content according to its current schema. Deterministic clip-to-clip transitions may be recorded; BlendTree branch choice and unrelated Timeline placement belong to the surrounding Unity tool.
- Query current Session state before mutation and use returned safe names rather than scene guesses.

### 9. Project-owned runtime and diagnosis

Normal generation prepares and starts the project-local runtime as needed. `kimodo_debug_install_server` performs incremental repair/setup; inspect its current help before use.

The default runtime root is the Unity project's `NvlabKimodoQuickServer~`, with its Python environment at root `.venv`. When Auto Sync Server is enabled and the installed runtime is older than the packaged version, major sync clears everything, minor sync keeps `models`, and patch sync keeps `models` plus root `.venv`. Relevant logs are `log/setup.log` and `log/bridge_server.log`. On failure, capture the command JSON/result, Unity version, `Editor.log`, runtime logs, Session state, and whether the failure occurred during package resolution, setup, model provisioning, generation, recording, retargeting, or playback.

### 10. Backend developer seam

For backend maintenance only, `NvlabKimodoQuickServer~/core` is the source of server routing, setup, protocol, and ARDY behavior; `NvlabKimodoQuickServer~/kimodo` contains Kimodo model/motion code. Launchers call `core.quickserver_cli`. Treat current source and tests as authoritative.

## 中文调试对照

### 1. 边界

在现有 Unity 项目中，使用 Kimodo 进行人形动画生成、分析、Pose 编辑、Constraint、录制、Retarget、Animator 导入和 Timeline 动画工作。复用环境中的 Unity 自动化机制和项目级运行时。

### 2. 安装到现有项目

1. 确认目标包含 `Assets`、`Packages` 和 `ProjectSettings`。
2. 读取 `ProjectSettings/ProjectVersion.txt`；当前包要求 Unity `2022.3` 或更新版本。
3. 保留现有 `Packages/manifest.json`，缺少时只增加以下依赖：

   ```json
   "com.unity.kimodo_unity_motion_tools": "https://github.com/OneYoungMean/KimodoUnityBridge.git"
   ```

   用户明确指定时改用其 Git/Gitee 地址或本地 `file:` 路径。
4. 让现有 Unity 自动化刷新项目，并从 Unity 日志诊断包解析错误。

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


### 5. 最小生成工作流

```text
session_open {}
query_current_session {"query":"characters"}
kimodo_generate_animation {"character":"<安全名称>","prompt":"stand still and breathe naturally","duration_frames":60}
kimodo_get_generation {"request_id":"<request_id>"}  # 重复至终态
session_close {}
```

每个非平凡调用前先查询该命令的当前 help。验证最终响应、生成的动画资产、Session Metadata 和 Unity Console。生成完成只会得到草稿；最终验收前必须执行下方动画质量门。

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

### 7. 动画质量门

每次生成成功后，查询返回的动画安全名称和 Session 区间，执行 `kimodo_analyze`，并将其 `analysis_id` 传给 `query_picture`。实际打开并检查返回的 `image_path`；仅检查路径存在不算视觉检查。将关键姿势与 Prompt 比较，检查动作可读性、剪影、平衡、支撑/接触、朝向与移动方向、左右肢体、穿插，以及明显损坏或不合理的姿势。结果未通过时，指出失败帧，修改 Prompt 或 Constraint，重新生成并重复质量门。

当使用 `loop:true` 或用户要求循环动作时，检查相对帧 `0`、`1`、`duration_frames-2` 和 `duration_frames-1`，比较接缝两侧。检查姿态、Root 位置和朝向、运动方向、相位以及手脚支撑接触。原地循环的首尾 Root Transform 和姿态应在较小视觉容差内一致；位移循环可以保留周期位移，但位移、朝向、相位和速度必须跨接缝连续，不要强制把 Root 拉回原点。仅凭首尾静帧不能证明时间连续性；可以播放时应检查播放，否则将时间接缝验证报告为不完整。

视觉验收只报告为 `passed`、`needs_revision` 或 `not_verified`。没有实际打开并检查图像时，绝不能报告 `passed`。稀疏关键姿势图不能验证滑步、跳变、时序、加速度或其他时间质量；除非检查过播放或足够密集的采样，否则将其报告为未验证。

### 8. Analysis、录制、Retarget 与 Animator

- `kimodo_analyze` 接受一个命名动画、一段 Session 帧区间，或两个及以上显式 Pose locator。前两种路径使用 QuickServer。`keyframes.max_count` 默认是 `4`：QuickServer 贪心选择真实代表帧，使其线性张成空间重构最多非 Root 姿势方差；排除 Root 平移，并在可用时排除 Root 关节旋转。每个关键帧返回 `non_root_subspace_explained_variance`。`issues.max_count` 默认是 `8`；问题按严重度排序，并以 `from_frame` 到 `frame` 表示跳变过渡。`continuity_quality_score`/质量分数只表示数值连续性，不表示动作语义。`poses` 请求在本地比较每一对相邻 Pose：`muscle.cosine_similarity`/`cosine_distance` 使用完整 Humanoid muscle，`root_motion` 返回 XYZ 差值、平面距离、垂直差值和最短 Yaw 差值。Pose 比较不能替代时序 Clip 分析。保存 Clip/区间分析返回的 `analysis_id`，供 `query_picture` 使用。
- `kimodo_record_range` 将半开 Session 区间录制为源角色的 AnimationClip。
- `kimodo_retarget_animation` 将一个已加载动画 Retarget 到当前 Session 的另一角色；源角色和目标角色都必须具有有效 Humanoid Avatar。
- `session_try_add` 按当前 schema 导入 Humanoid 角色、AnimationClip 或 Animator 内容。确定性的 Clip-to-Clip 过渡可以录制；BlendTree 分支选择和无关 Timeline 摆放由外围 Unity 工具负责。
- 修改前先查询当前 Session 状态，使用返回的安全名称，不猜测场景名称。

### 9. 项目级运行时与诊断

普通生成会按需准备并启动项目级运行时。`kimodo_debug_install_server` 用于增量修复/安装，使用前先读取当前 help。

默认运行根目录是 Unity 项目下的 `NvlabKimodoQuickServer~`，Python 环境位于该根目录的 `.venv`。启用 Auto Sync Server 后，运行时版本低于包内版本时会按 major/minor/patch 层级同步：major 清空全部内容，minor 保留 `models`，patch 保留 `models` 和根目录 `.venv`。相关日志为 `log/setup.log` 和 `log/bridge_server.log`。失败时保存命令 JSON/返回、Unity 版本、`Editor.log`、运行时日志、Session 状态，并标明问题发生在包解析、setup、模型准备、生成、录制、Retarget 还是播放阶段。

### 10. 后端开发边界

仅在维护后端时，`NvlabKimodoQuickServer~/core` 是服务器路由、setup、协议与 ARDY 行为的源码；`NvlabKimodoQuickServer~/kimodo` 保存 Kimodo 模型/动作代码。启动脚本调用 `core.quickserver_cli`。以当前源码和测试为准。
