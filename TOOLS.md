# KimodoUnityBridge — AI operational contract / AI 操作契约

Use the live command schema for exact parameters. This document defines only the shared workflow, evidence, and boundary rules.

## English

### 1. Authority and startup

- Use `KimodoUnityBridge.Command.command_dispatcher`.
- `GetCommandDefinitionsJson()` describes the current command/input surface. After install, `kimodo_help({})` provides the manual; `kimodo_help({"section":"models"})` lists viable registered models; `kimodo_help({"section":"constraints"})` is the constraint reference. Read the schema before building arguments.
- After Unity finishes compiling/importing, call `kimodo_install_server({})` once before runtime-dependent commands. It refreshes the package-local QuickServer, preserves models/Python, and restarts the server.
- Treat returned Session names, Clip names, IDs, `request_id`, `{track,index}` pose/path references, paths, and error envelopes as opaque. Read `session_json_path` after Session-changing commands when complete state is needed.

### 2. Sessions and content

- Use `session_get_or_create({"name":"<stable name>"})` to select or create a Session. A new Session is empty.
- Add content explicitly with `session_add`: `kind:"character"` accepts a scene Humanoid Animator or renderable Mesh object; `kind:"clip"` appends a project AnimationClip to a Session character; `kind:"animator"` imports supported Animator state clip candidates and supported transitions as Timeline records.
- For `session_add(kind:"clip")`, a Mesh-only character rejects a Humanoid clip; a Humanoid character retargets a generic clip before appending it. Preserve the returned `retargeted` result.
- Save the safe names returned by `session_add` exactly. Appended clips have a fixed four-frame safezone.
- Use `session_close` to close the selected editing Session; it preserves the Timeline, assets, and AI-readable Session JSON, but cancels that Session's active generation. Selecting another Session also cancels active work in the previous one.

### 3. Time, immutability, and asynchronous generation

- Public time is 60 FPS integer frames. Ranges are half-open `[start_frame,end_frame)`; Pose frames are local to the referenced Clip.
- Completed Session Clips are immutable. Generate, correct, record, or retarget by appending a new Clip; never overwrite or retime the source.
- `kimodo_generate_animation` is asynchronous. Save `request_id` and poll `kimodo_get_generation` until `completed`, `failed`, or `canceled`. Use `kimodo_cancel_generation` only for an active request.
- Generation accepts registered `model`/`text_encoder_model`, deterministic `seed`, `diffusion_steps`, `output_mode`, `analysis_option`, `loop`, `name`, and `output_folder` where supported by the live schema. Query the schema/help instead of guessing defaults.
- `output_mode` is `humanoid_muscle`, `character_bone`, or `model_bone`. All current generation modes require the selected Session character to be a valid scene Humanoid Animator with a valid Avatar. `model_bone` changes the generated output representation; it does not enable Mesh-only generation. Generation also requires the selected model to provide a valid Humanoid origin Avatar.

### 4. Analysis and visual evidence

- `animation_analyze` accepts one or two explicit `{character,clip,role?}` references from the current Session. `role` is `source` or `target`; defaults follow item order.
- Choose `level` deliberately and inspect the actual `pictures.images` list. For Humanoid targets, `low` contains the grouped test panels, `middle` adds time-ordered keyframe poses plus explicit start/end poses, and `high` additionally adds time-ordered foot-transition poses. Individual pose tiles are labeled with their source frame in the lower-left corner. Mesh-only targets return Mesh pose panels rather than Humanoid contact panels. `resolution` controls final tile size, accepts 64–4096 pixels, and defaults to 512.
- The result contains analysis data and a composite PNG at `pictures.image_path`, plus a self-describing `pictures.images` tile list. Open the PNG; filenames, IDs, and numbers alone are not visual proof.
- Humanoid analysis internally samples HumanBodyBones and returns keyframe/foot-contact data plus pictures. Non-Humanoid renderable Mesh analysis is Mesh-only pose evidence and must not be reported as Humanoid contact evidence.
- Compare action, phase, direction/path, silhouette, balance, root trajectory, pelvis, contacts, and ending state with the request. Check object contact only when an external fixture or visible scene evidence supplies it. `animation_compare` is a Humanoid-only numeric boundary check: it reports root/yaw, mean-muscle, and end-effector deltas, but currently has no compatible foot-contact comparison. It does not replace semantic visual judgment.

### 5. Poses and constraints

- `pose_get({"source":{"character":"<character>","clip":"<clip>","frame":<local frame>}})` samples one valid local frame and creates a new External Pose slot. It returns the reusable `{track,index}` reference; `full_data:true` requests the complete payload.
- `pose_get`, Pose editing/contracting, and `animation_compare` require Humanoid pose sampling. A Mesh-only Session character can be analyzed, but has no supported public pose-sampling, comparison, or generation workflow; creating a Path alone does not add one.
- Edit that External Pose with `pose_set_root_transform` or `pose_set_muscle`. `pose_contract` aligns selected end-effectors (`left_hand`, `right_hand`, `left_foot`, `right_foot`) between two Pose references and creates another slot; preserve any returned residual/error data.
- `pose_create_path` creates an External Path slot. `type` is `forward`, `turn_left`, `turn_right`, or `bezier`; `length` must be positive; `inverse` is optional; `knots` is valid only for `bezier`. Pass its returned `{track,index}` only as `root_path.path`.
- Generation point constraints are sparse per-frame objects. Each item requires a non-negative `frame` and at least one of `fullbody`, `root2d`, `left_hand`, `right_hand`, `left_foot`, or `right_foot`; multiple fields may coexist in one item. Each value uses a Pose reference, while `root2d` may use `pose` or direct `position` plus `heading`.
- A `root_path` item contains the path reference and may set its first `frame` (default 0); it cannot be combined with point-constraint fields or overlap another root-path frame range. It expands over the remaining generated clip. At the same frame, `fullbody` is the base, `root2d` overrides Root2D, effector fields override matching channels, and explicit `root2d` wins over `root_path`.

### 6. Quality gate and loop seam

After every completed generation, correction, record, or retarget:

1. Analyze the output Clip with `animation_analyze`.
2. Open `pictures.image_path` and inspect the tiles in temporal order.
3. Check requested action, phase, direction/path, silhouette, balance, root trajectory, contacts, and ending state.
4. Append another Clip when evidence fails; retain the evidence and state what changed.

For Humanoid loops, use `pose_get` at the first/last local frames and compare the corresponding analysis tiles. Check root position/heading, contact phase, and velocity continuity. In-place loops should return to the initial root/pose; locomotion loops may retain cycle displacement. For Mesh-only loops, use only the available Mesh analysis panels and do not invent Pose/contact evidence. Static images do not prove timing, sliding, popping, or acceleration; without playback/dense samples report those qualities as `not_verified`.

Use only these visual statuses: `passed`, `needs_revision`, or `not_verified`. `passed` requires actually opening the returned PNG.

### 7. Animator boundaries and output commands

- `session_add(kind:"animator")` imports supported Animator state clip candidates and materializes supported same-Layer State-to-State transitions as logical Timeline `transition_clip` records; it does not bake a new transition AnimationClip.
- Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped. If projected imports exceed the safety limit, use the live schema's `ignore_warning` option only when importing all variants is intended.
- `kimodo_record_range` records a Session range into a new AnimationClip. `kimodo_retarget_animation` retargets one loaded animation to another Session character and appends the result; both source and target require valid Humanoid Avatars.
- If a public command cannot perform a requested edit, complete the supported analysis and report the boundary instead of claiming completion.

## 中文对照

### 1. 权威与启动

- 使用 `KimodoUnityBridge.Command.command_dispatcher`。
- `GetCommandDefinitionsJson()` 描述当前命令/输入面；安装后用 `kimodo_help({})` 返回手册；用 `kimodo_help({"section":"models"})` 查询可用模型；用 `kimodo_help({"section":"constraints"})` 查询约束结构。构造参数前先读取 schema。
- Unity 编译/导入完成后，依赖运行时的命令前先调用一次 `kimodo_install_server({})`。它刷新项目内 QuickServer，保留模型/Python 环境并重启服务。
- Session 名称、Clip 名称、ID、`request_id`、`{track,index}` Pose/Path 引用、路径和错误 envelope 都按 opaque 值保存。需要完整状态时，在 Session 变更命令后读取 `session_json_path`。

### 2. Session 与内容

- 用 `session_get_or_create({"name":"<稳定名称>"})` 选择或创建 Session；新 Session 为空。
- 用 `session_add` 显式加入内容：`kind:"character"` 可加入场景 Humanoid Animator 或可渲染 Mesh；`kind:"clip"` 将项目 AnimationClip 追加到 Session 角色；`kind:"animator"` 将支持的 Animator 状态 Clip 候选和支持的过渡导入为 Timeline 记录。
- `session_add(kind:"clip")` 中，Mesh-only 角色会拒绝 Humanoid Clip；Humanoid 角色会先将 generic Clip Retarget 后再追加。保存返回的 `retargeted` 结果。
- 原样保存 `session_add` 返回的安全名称。追加 Clip 带固定四帧 safezone。
- 用 `session_close` 关闭当前编辑 Session；Timeline、资产和 AI 可读 Session JSON 会保留，但该 Session 的活动生成会被取消。切换到另一个 Session 也会取消前一个 Session 的活动工作。

### 3. 时间、不可变性与异步生成

- 公共时间是 60 FPS 整数帧；区间为半开区间 `[start_frame,end_frame)`；Pose 帧是所引用 Clip 的局部帧。
- 已完成 Session Clip 不可变。生成、修正、Record 或 Retarget 都追加新 Clip，不能覆盖或重定时源 Clip。
- `kimodo_generate_animation` 是异步的。保存 `request_id`，用 `kimodo_get_generation` 轮询到 `completed`、`failed` 或 `canceled`；只有活动请求才使用 `kimodo_cancel_generation`。
- 生成可使用注册的 `model`/`text_encoder_model`、确定性 `seed`、`diffusion_steps`、`output_mode`、`analysis_option`、`loop`、`name` 和 `output_folder`；具体默认值以实时 schema/help 为准。
- `output_mode` 为 `humanoid_muscle`、`character_bone` 或 `model_bone`。当前所有生成模式都要求所选 Session 角色是带有效 Avatar 的场景 Humanoid Animator。`model_bone` 只改变生成输出表示，不会启用 Mesh-only 生成。生成模型本身也必须提供有效 Humanoid origin Avatar。

### 4. 分析与视觉证据

- `animation_analyze` 接收当前 Session 中一个或两个显式 `{character,clip,role?}`；`role` 为 `source` 或 `target`，省略时按项目顺序推断。
- 按目的选择 `level`，并以实际返回的 `pictures.images` 列表为准。Humanoid 目标中，`low` 仅包含当前测试分组面板，`middle` 增加按时间排序的关键帧姿势及明确的首尾帧，`high` 再增加按时间排序的脚切换姿势。单帧图片左下角会标记来源帧数。Mesh-only 目标返回 Mesh 姿态面板，不返回 Humanoid 接触面板。`resolution` 控制最终 tile 尺寸，可取 64–4096 像素，默认 512。
- 结果返回分析数据、`pictures.image_path` 组合 PNG 和自描述的 `pictures.images` tile 列表。必须实际打开 PNG；文件名、ID 和数字本身不是视觉证明。
- Humanoid 分析内部采样 HumanBodyBones，并返回关键帧/脚接触数据和图片；非 Humanoid 可渲染 Mesh 只提供 Mesh-only 姿态证据，不能声称有 Humanoid 接触证据。
- 将动作、阶段、方向/路径、剪影、平衡、Root 轨迹、骨盆、接触和结束状态与要求对照。只有外部 fixture 或可见场景证据提供物体接触时，才检查物体接触。`animation_compare` 是 Humanoid-only 的数值边界检查：它返回 Root/yaw、平均 muscle 和末端差异，但当前没有兼容的脚接触比较；它不能替代语义视觉判断。

### 5. Pose 与约束

- `pose_get({"source":{"character":"<角色>","clip":"<Clip>","frame":<局部帧>}})` 采样有效局部帧并创建新的 External Pose slot，返回可复用的 `{track,index}`；`full_data:true` 请求完整数据。
- `pose_get`、Pose 编辑/Contract 和 `animation_compare` 都要求 Humanoid 姿态采样。Mesh-only Session 角色可以分析，但当前没有支持的公开 Pose 采样、比较或生成工作流；单独创建 Path 不会增加该能力。
- 用 `pose_set_root_transform` 或 `pose_set_muscle` 修改该 External Pose。`pose_contract` 在两个 Pose 引用之间对齐指定手脚并创建另一个 slot；保存返回的 residual/error 数据。
- `pose_create_path` 创建 External Path slot。`type` 为 `forward`、`turn_left`、`turn_right` 或 `bezier`；`length` 必须为正；`inverse` 可选；`knots` 仅适用于 `bezier`。返回的 `{track,index}` 只能作为 `root_path.path` 使用。
- 生成点约束是按帧稀疏对象。每项必须有非负 `frame`，并且至少含 `fullbody`、`root2d`、`left_hand`、`right_hand`、`left_foot`、`right_foot` 之一；同一项可以组合多个字段。值使用 Pose 引用，`root2d` 也可用直接 `position` 加 `heading`。
- `root_path` 项包含 Path 引用，可指定首帧（默认 0），不能和点约束字段混用，也不能和另一个 root-path 帧区间重叠；它会扩展到生成 Clip 剩余部分。同帧优先级为：`fullbody` 作为基础，`root2d` 覆盖 Root2D，手脚字段覆盖对应通道，显式 `root2d` 优先于 `root_path`。

### 6. 质量门与循环接缝

每次生成、修正、Record 或 Retarget 完成后：

1. 用 `animation_analyze` 分析输出 Clip。
2. 打开 `pictures.image_path`，按时间顺序检查 tile。
3. 检查动作、阶段、方向/路径、剪影、平衡、Root 轨迹、接触和结束状态。
4. 证据失败时追加另一个 Clip；保留证据并说明变化。

Humanoid 循环要用 `pose_get` 检查首尾局部帧，并对照分析 tile 检查 Root 位置/朝向、接触相位和速度连续性。原地循环应回到初始 Root/姿势；位移循环可以保留周期位移。Mesh-only 循环只能使用现有的 Mesh 分析面板，不能虚构 Pose/接触证据。静态图片不能证明时序、滑步、跳变或加速度；没有播放/密集采样时报告 `not_verified`。

视觉状态只使用 `passed`、`needs_revision`、`not_verified`；实际打开 PNG 才能报告 `passed`。

### 7. Animator 边界与输出命令

- `session_add(kind:"animator")` 会导入支持的 Animator 状态 Clip 候选，并将支持的同 Layer State-to-State 过渡物化为逻辑 Timeline `transition_clip`，不会 Bake 新的过渡 AnimationClip。
- Any State、Entry、Exit、StateMachine 和 OverrideController 过渡会报告为 skipped。预计导入量超过安全限制时，只有确实需要导入全部变体才使用实时 schema 的 `ignore_warning`。
- `kimodo_record_range` 将 Session 时间范围记录为新的 AnimationClip；`kimodo_retarget_animation` 将已加载动画 Retarget 到另一个 Session 角色并追加结果；源和目标都要求有效 Humanoid Avatar。
- 公开命令无法执行某项修改时，完成可支持的分析并报告边界，不能声称完成。
