# Character Animation CLI Unity — AI operational contract / AI 操作契约

Use the live command schema for exact parameters. This document keeps only shared workflow and evidence rules.

## English

### 1. Entry and session

Use `CharacterAnimationCli.Unity.Command.command_dispatcher`. Start with `kimodo_help({})`, then `session_get_or_create({"name":"<stable name>"})`. A new Session is empty: add the scene Humanoid with `session_add({"kind":"character",...})`, then add project Clips or Animator content explicitly. Save the returned Session, character, and Clip names exactly.

### 2. Time, immutability, and async generation

- Public time is 60 FPS integer frames; ranges are half-open `[start_frame,end_frame)` and animation Pose frames are local to the Clip.
- A completed Session Clip is immutable. Generate, record, retarget, or correct by appending a new Clip; never overwrite or retime the source.
- `kimodo_generate_animation` is asynchronous. Save `request_id` and poll `kimodo_get_generation` until `completed`, `failed`, or `canceled`.
- Use `kimodo_install_server` only for explicit runtime repair/diagnosis. Wait for Unity compilation/import to finish before mutating commands.

### 3. Analyze and inspect visual evidence

Use `animation_analyze` with one or two explicit `{character,clip,role?}` entries. Choose `level` deliberately: `low` for compact ghost/trajectory evidence, `middle` for key poses, `high` for key poses plus foot-contact transitions, and `-test` only for renderer validation. The result contains analysis data and a composite PNG at `pictures.image_path`; read and open that file. Use `pictures.images` to understand the tile presentations. There is no separate picture command.

Analysis is evidence, not semantic proof. Compare key poses, ghost root path, pelvis trajectory, foot contacts, direction, phase, object contact, and ending state with the requested motion. `animation_compare` can compare two ranges without mutating the Session.

### 4. Pose cache and generation constraints

`pose_get({"source":"<clip>","frame":<local frame>})` reads a source pose and returns a writable Pose Cache marker. Preserve that cache locator. Modify it only with `pose_set_root_transform` or `pose_set_muscle`; use `pose_contract` to align selected end-effectors and record its `residual_error`.

Generation constraints use sparse same-frame objects. Use `fullbody` for a complete pose, `root2d` for planar root position/heading, and pose-based `left_hand`, `right_hand`, `left_foot`, or `right_foot` for contacts. The exact shape comes from `kimodo_help({"section":"constraints"})`. There is no standalone Root2D path command; prepare the root samples/poses yourself or use the surrounding Unity tooling.

### 5. Quality gate and loop seam

After every completed generation or correction:

1. Analyze the output Clip with `animation_analyze`.
2. Open `pictures.image_path` and inspect the returned tiles.
3. Check requested action, direction/path, phase, silhouette, balance, root trajectory, contacts, and ending state.
4. Revise by appending another Clip when the evidence fails; retain the evidence and report what changed.

For a loop, inspect first/last poses with `pose_get` and compare the corresponding analysis tiles. Check root position/heading, gait/contact phase, and velocity continuity. In-place loops should return to the initial root/pose; locomotion loops may retain cycle displacement and must not be forced back to world origin. Still images do not prove timing, sliding, popping, or acceleration; use playback/dense samples when supplied and otherwise report `not_verified`.

Visual status is `passed`, `needs_revision`, or `not_verified`. `passed` requires actually opening the returned PNG.

### 6. Animator transitions and boundaries

`session_add(kind:"animator")` imports supported same-Layer State-to-State transitions as logical Timeline `transition_clip` records; it does not bake a new AnimationClip. Any State, Entry, Exit, StateMachine, and OverrideController transitions are reported as skipped. If the public commands cannot perform a requested edit, complete the supported analysis and report the boundary instead of claiming completion.

## 中文对照

### 1. 入口与 Session

使用 `CharacterAnimationCli.Unity.Command.command_dispatcher`。先调用 `kimodo_help({})`，再调用 `session_get_or_create({"name":"<稳定名称>"})`。新 Session 为空：用 `session_add({"kind":"character",...})` 显式加入场景 Humanoid，再显式加入项目 Clip 或 Animator。原样保存返回的 Session、角色和 Clip 名称。

### 2. 时间、不可变性与异步生成

- 公开时间为 60 FPS 整数帧；区间为半开区间 `[start_frame,end_frame)`，动画 Pose 帧是 Clip 局部帧。
- 已完成的 Session Clip 不可变。生成、Record、Retarget 或修正都追加新 Clip，不覆盖或重定时源 Clip。
- `kimodo_generate_animation` 是异步的。保存 `request_id`，轮询 `kimodo_get_generation` 直到 `completed`、`failed` 或 `canceled`。
- `kimodo_install_server` 只用于用户明确要求的运行时修复/诊断。Unity 编译或导入期间等待后再执行变更命令。

### 3. 分析与视觉证据

用一个或两个显式 `{character,clip,role?}` 项调用 `animation_analyze`。按目的选择 `level`：`low` 为紧凑 ghost/轨迹证据，`middle` 增加关键姿势，`high` 再增加脚接触切换，`-test` 仅验证渲染器。结果返回分析数据和 `pictures.image_path` 组合 PNG；实际读取并打开该文件，并用 `pictures.images` 理解子图类型。没有独立图片命令。

Analysis 是证据，不是语义证明。将关键姿势、ghost 根路径、骨盆轨迹、脚接触、方向、相位、对象接触和结束状态与动作要求对照。`animation_compare` 可以在不修改 Session 的情况下比较两个区间。

### 4. Pose Cache 与生成约束

`pose_get({"source":"<clip>","frame":<局部帧>})` 读取源姿势并返回可写 Pose Cache marker。保存该 cache locator；只用 `pose_set_root_transform` 或 `pose_set_muscle` 修改；使用 `pose_contract` 对齐指定末端，并记录 `residual_error`。

生成约束使用按帧稀疏对象：`fullbody` 表示完整姿势，`root2d` 表示平面 Root 位置/朝向，`left_hand`、`right_hand`、`left_foot`、`right_foot` 使用 Pose 表示接触。准确结构以 `kimodo_help({"section":"constraints"})` 为准。本 command surface 没有独立 Root2D path 命令；请自行准备根采样/姿势，或使用外围 Unity 工具。

### 5. 质量门与循环接缝

每次生成或修正完成后：

1. 用 `animation_analyze` 分析输出 Clip。
2. 打开 `pictures.image_path` 并检查返回子图。
3. 检查动作、方向/路径、阶段、剪影、平衡、根轨迹、接触和结束状态。
4. 证据失败时追加另一个 Clip 重新修正，保存证据并说明变化。

循环需要用 `pose_get` 检查首尾姿势，并结合分析子图比较。检查 Root 位置/朝向、步态/接触相位和速度连续性。原地循环应回到初始 Root/姿势；位移循环可以保留周期位移，不能强制回到世界原点。静态图不能证明时序、滑步、跳变或加速度；有播放/密集采样时检查，否则报告 `not_verified`。

视觉状态只能是 `passed`、`needs_revision` 或 `not_verified`。实际打开返回 PNG 才能报告 `passed`。

### 6. Animator 过渡与边界

`session_add(kind:"animator")` 将支持的同 Layer State→State 过渡导入为逻辑 Timeline `transition_clip`，不会 Bake 新 AnimationClip。Any State、Entry、Exit、StateMachine 和 OverrideController 过渡会报告为跳过。公开 command 无法执行某项修改时，完成可支持的分析并报告边界，不能声称已完成。
