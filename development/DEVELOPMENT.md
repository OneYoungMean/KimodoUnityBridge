# 开发备忘录 / Development memo

> Temporary development snapshot; use [tools/common.md](../tools/common.md) and the command catalog for execution details.
>
> 当前文件只是临时开发快照。日常任务的执行规则位于 [tools/common.md](../tools/common.md)，命令定义位于 [Command/help.json](../Command/help.json)。

## Current command surface (主要命令面)

The maintained public commands are:

- Startup and discovery: `server.install`, `kimodo.help` (`server.install` starts an asynchronous project-local installation task and returns `install:<guid>`; poll it with `generation.get`)
- Scene inputs: commands resolve the active scene and return explicit asset/evidence references
- Async tasks: `animation.generate` returns a generation `request_id`; `generation.get` polls either task type; `generation.cancel` cancels generation only
- Analysis/evidence: `animation.analysis`, `pose.analysis`, `pose.fromvideo`, `pose.frompicture`
- Pose editing: `pose.get`, `pose.set`
- Asset output: `animation.retarget`

## Current boundaries

- Commands resolve a scene Humanoid Animator or renderable Mesh directly. A Mesh-only character rejects Humanoid clips, while a Humanoid target retargets a generic clip before appending it.
- Completed Clips are immutable. Corrections, retargets, and generated variants append new Clips rather than replacing an existing Clip.
- `animation.analysis` accepts one explicit scene Clip and returns structured motion analysis plus a fixed 20-tile SceneView image. Its `camera` object is shared with synchronous `pose.analysis`; both use the active SceneView camera and return image data inline with no request ID. `pose.analysis` previews the same point constraints accepted by `animation.generate`, separately by default or together as opaque poses with `combine=true`.
- Humanoid `animation.analysis.clip.motion_profile.force_line` measures left/right arm swing balance. It converts each shoulder-to-hand vector into character-root-local sagittal angle, sums absolute frame-to-frame angle deltas over the full Clip and each `phase_track` interval, and reports `imbalance_ratio = abs(left-right) / max(left,right)`. A ratio at or below `0.15` is `balanced`; stationary or incomplete samples return `insufficient_evidence`.
- Point constraints use explicit `root`, `pose`, and `effector` inputs through `animation.generate.constraints`; the command infers Root2D, FullBody, or Effector after sampling. Pose plus effector is exported as IK-solved FullBody. See [the current constraint contract](plan.md#当前-constraint-契约). Humanoid analysis stores and returns a reusable Root Path consumed through `root_path`; Path Override uses absolute Unity-yaw begin/end angles, preserves measured walking distance unless overridden, and coexists with Loop. The backend applies heading to the composed trajectory and the final reference root tracks while preserving Root XZ positions. Generation and Pose sampling require a valid Humanoid Avatar; Mesh-only characters are analysis-only. There is no standalone root-transform application command.
- `pose.get` creates a new External Pose marker for a Humanoid character. Use its `{track,index}` reference inside a point constraint, preview that constraint with `pose.analysis`, then edit the marker with `pose.set` if needed.

## Active documentation work

- Keep `../Command/help.json` aligned with the dispatcher, and keep concrete command details out of `../SKILL.md` and the capability tools under `../tools/`.
- Keep raw object lookup details confined to `../Command/help.json` and the single entry sentence in `../SKILL.md`.

## Verification items

- Run a Unity Editor compile/import check after documentation and command-surface changes.
- Validate representative generation, analysis image opening, External Pose/Path editing, and immutable-Clip append behavior in the maintained project. Scene changes and Editor reloads may cancel active generations. Keep Foot IK/Raycast or other unexposed capabilities documented as boundaries unless a public command and project asset prove them.

## Backend-owned generation (2026-11-09)

本节记录本次生成链路改造后的实际实现，协议与 Debug 说明补充于 2026-11-10。范围为固定时长的 Editor/Command 生成，Session/History 3.0 和 ARDY 流式会话保持各自职责。

### 职责与代码变化

Unity 采样 Timeline 或原始 AnimationClip，将参考动画 retarget 为目标模型骨架 KMB，然后提交一次完整的 `generate` 请求。QuickServer 根据设置与真实约束选择阶段并执行。生成过程中仍可返回进度消息；阶段之间的数据由后端内部传递，Unity 无需接收 Stage 2 轨迹后再发起 Stage 3。

| 层 | 当前职责 |
|---|---|
| Unity Editor/Command | 读取场景、Timeline 与约束设置；采样/retarget 参考 KMB；发送请求；将最终结果烘焙为动画资产；按 Debug 元数据切分和展示调试动画 |
| QuickServer 共用编排 | 阶段选择、行走距离测量、轨迹组合、Post Apply、Loop 约束、Anchor 处理、最终输出范围及 Debug 汇总 |
| Kimodo/ARDY 执行器 | 执行各自模型调用、长动画窗口或自回归 horizon，并保留每次实际生成结果 |

Editor 与 Command 共用请求构造。原 C# 阶段编排、窗口规划与 Post Apply 旧链路已退出活动代码，移出的文件和 `.meta` 保存在 `archive/backend-generation-v3-20261109/`。各阶段采用独立约束集合，不再反复修改同一个前端 request 后恢复状态。

Inspector、Command schema/help/examples 同步调整：Inside 独立于 In/Out，保留 Post Apply；AutoBegin 与 Relative Offset 已移出活动生成路径。普通生成只写最终动画资产，Loop Time 直接设置在该资产上。

### Request and reference contract

以下描述 Unity 与 QuickServer 之间的 TCP 协议。公开 `animation.generate` 的输入以 [Command/help.json](../Command/help.json) 和实时 `kimodo.help` 为准；例如 Command 的 `inside.source.clip` 会先在 Unity 中解析为动画并采样，服务器收到的是 KMB 附件及 ClipConstraint。

传输沿用 UTF-8 JSON 行加二进制数据：先写完整 JSON 和换行，随后紧接附件字节。没有附件时只写 JSON 行。`constraints_json` 在外层请求中是字符串，其内容为约束数组；`stage_plan`、`window_constraints` 和附件目录则是 JSON 对象或数组。

| 请求字段 | 当前语义 |
|---|---|
| `cmd` | 固定为 `generate` |
| `task_id` | 本次生成任务 ID；桥接客户端未指定时自动生成 |
| `model`、`text_encoder_mode`、`models_root` | 模型、文本编码器模式和模型目录配置 |
| `prompt`、`seed`、`diffusion_steps` | 提示词、种子与模型采样步数 |
| `duration` | 基础模型生成时长，单位秒；包含必要上下文时可长于可见动画 |
| `timeline_segments` | 可选的 `{prompt, duration}` 数组，描述连续的多提示词段 |
| `stage_plan` | 帧范围、轨迹和 Loop 设置；后端据此选择实际阶段 |
| `window_constraints` | 长动画分窗衔接窗口及约束采样密度 |
| `constraints_json` | 点约束和 ClipConstraint 数组序列化后的 JSON 字符串 |
| `kmb_attachments`、`attachment_byte_length` | 输入 KMB 附件目录及随后二进制数据的总字节数 |
| `debug` | 是否额外返回全部实际生成结果的拼接动画与分段/约束信息 |
| `output_format` | 当前 Unity 生成链路请求 `kmb_v1`；Debug 响应可升级为 `kmb_attachments_v1` |

帧号从 0 开始，结束帧不包含在区间内。服务器协议中的帧数使用模型帧率；Command/Inspector 的 60 FPS 窗口参数先换算为模型帧。`start_time`、`duration` 和 `timeline_segments[].duration` 使用秒。

#### stage_plan 与窗口参数

| 字段 | 当前语义 |
|---|---|
| `stage_plan.version` | 本生成计划格式版本，当前为 `1`，与包版本独立 |
| `target_frame_count` | 基础模型生成帧数，可包含 Outside 上下文；Loop 的额外帧由后端计算 |
| `output_start_frame`、`output_frame_count` | 基础请求中需要保留的输出范围；Loop 在后端内部调整实际裁剪起点并保持输出数量 |
| `trajectory.enabled` | 是否启用轨迹处理；Unity 同时发送 `root_trajectory` 标志 |
| `trajectory.override_path_angle` | 按指定起止方向组合路径 |
| `trajectory.path_begin_angle_degrees`、`path_end_angle_degrees` | Unity Yaw 角度，单位度；后端转换为模型坐标 |
| `trajectory.override_distance`、`distance` | 使用指定行走距离，跳过 Stage 1 测距 |
| `trajectory.override_heading`、`heading_degrees` | 设置最终平面朝向，保留组合后的 Root XZ 路径 |
| `loop.enabled` | 请求 Loop 处理；真实 Outside Out 优先，由后端关闭本次 Loop 处理 |
| `loop.window_frames`、`sample_count` | Loop 连续参考窗口宽度与窗口内约束采样数 |
| `loop.lock_position_x/y/z`、`lock_rotation_x/y/z` | 尾部窗口整体变换时需要锁定的位置和旋转通道 |
| `window_constraints.window_frames` | 长动画相邻窗口的衔接/重叠宽度 |
| `window_constraints.num_transition_frames` | 沿用原生窗口语义；Unity 当前与 `window_frames` 同值发送，两者同时存在时后端采用 `window_frames` |
| `window_constraints.sample_count` | 衔接窗口内约束采样密度，必须对应不同模型帧，不能超过窗口宽度 |

Unity 发送目标与设置，后端推导 `stage1/stage2/stage3` 的执行条件。各参考记录的 `post_apply` 和轨迹下的 FullBody/Effector 类型用于推导统一 Post Apply 集合；前端不需要额外指定阶段开关。

以下为 30 FPS 模型、2 秒输出、12 帧 Loop 窗口的服务器请求示例，省略可选运行配置和连接层关联字段：

```json
{
  "cmd": "generate",
  "model": "ARDY-SOMA-RP-30FPS-Horizon60",
  "prompt": "A person walks forward.",
  "duration": 2,
  "seed": 1729,
  "diffusion_steps": 10,
  "output_format": "kmb_v1",
  "constraints_json": "[]",
  "stage_plan": {
    "version": 1,
    "target_frame_count": 60,
    "output_start_frame": 0,
    "output_frame_count": 60,
    "trajectory": { "enabled": false },
    "loop": {
      "enabled": true,
      "window_frames": 12,
      "sample_count": 3
    }
  },
  "window_constraints": {
    "window_frames": 8,
    "num_transition_frames": 8,
    "sample_count": 2
  },
  "debug": true
}
```

#### 参考 KMB 与 ClipConstraint

参考动画复用现有 ClipConstraint 和 `kmb_attachments`，当前没有单独的顶层 `reference_kmb` 字段。每个输入附件目录项为 `{index, offset, byte_length}`，`offset` 相对于此次请求附带二进制数据的起点。ClipConstraint 的 `attachment` 引用目录索引。

| ClipConstraint 字段 | 当前语义 |
|---|---|
| `type`、`format` | 分别为 `clip` 和 `kmb_attachment_v1` |
| `attachment` | 目标骨架连续 KMB 的附件索引 |
| `start_time`、`duration` | 参考在生成时间轴上的起点和持续时间；`duration` 对应 KMB 帧数/帧率 |
| `target_frame_indices` | 可选的逐帧目标映射；数量与 KMB 帧数相同，严格递增 |
| `purpose` | Unity 参考使用 `inside`、`outside_in`、`outside_out`；后端 Loop 另构造 `loop_begin`、`loop_end` |
| `mask` | 根节点位置/朝向/旋转以及各目标骨骼的约束通道 |
| `sample_count` | 窗口或 Inside 均匀关键帧采样数量，默认 2 |
| `sampling_mode` | Inside 的 `uniform`、`foot_exchange`、`mixed` |
| `post_apply` | 是否在 Stage 3 前按 Stage 2 结果改写根轨迹或偏移整个窗口 |

Unity 发送连续 KMB，后端选出 `source_frame_indices` 用于稀疏 conditioning。运动转 tensor、速度计算保留原始帧间隔，采样帧去重。Inside 的混合模式在均匀采样之外合并足部切换中点；无法解析足部事件时回退均匀采样并警告。

Inside 使用原始 AnimationClip 的时间和姿势。长度不匹配时警告并只覆盖重叠范围；普通 Inside 不接受额外轨迹，需要轨迹时启用 Post Apply。Inside 与 Loop 可同时存在，保留 Inside 约束。Outside 的 In/Out 仍保留，但仅允许 `mode:"outside"`。

### Stages and Post Apply

| 阶段 | 执行条件与用途 |
|---|---|
| Stage 1 | `trajectory.enabled && override_path_angle && !override_distance`。生成后测量可见区间的空间行走距离，用于后端路径组合；不推导动画时长 |
| Stage 2 | 存在轨迹、Loop 或任一 Post Apply 时执行，取得实际轨迹和 Loop 参考姿势 |
| Stage 3 | 使用本阶段独立的最终约束集合生成结果，随后由后端裁剪 |

Post Apply 是 Stage 3 前的一次统一约束准备，不单独调用模型：

- Outside：对整段参考窗口应用同一个 XZ/Yaw 锚点变换，平移的 Y 分量为 0。
- Inside：保留原始身体姿势；启用 Post Apply 时使用 Stage 2 对应时间的根 XZ 和 Yaw，保留高度、Pitch/Roll 和身体关节。
- Trajectory 下的 FullBody/Effector：同样使用 Stage 2 根 XZ 和 Yaw；显式 Effector 世界空间目标继续遵循现有约束语义。

RootY 在此明确指 Yaw。路径角度与距离组合继续使用原 Bezier/路径语义，并延伸至必要的 Outside 上下文。测得距离和 Stage 2 轨迹均为后端内部数据。

每次模型调用按以下顺序执行：组装该阶段真实约束 → 应用 Post Apply → 调用现有 Anchor 归一化 → 模型生成 → 使用同一 Anchor 恢复输出 → 进行窗口拼接和最终裁剪。无真实约束时跳过 Anchor 归一化。请求不注入额外单帧 Begin 约束，Stage 3 后也不追加改变原点的 Root 变换。

### Loop and long motions

Loop 固定取 Stage 2 可见输出区间的第 0 帧开始的窗口，用窗口起点计算整段尾部参考的位置和旋转偏移，再应用锁定通道。足部切换中点仍用于 Inside 关键帧选择。

设基础可见区间起点为 `S`、输出帧数为 `N`、有效窗口宽度为 `W`，当前内部映射为：

- 开头参考：`[S, S + W)`；这里的 `S` 即可见动画的第 0 帧。
- 尾部参考：`[S + N - 1, S + N - 1 + W)`。
- Stage 3 内部增加 `W - 1` 帧；最终保留 `[S + W - 1, S + W - 1 + N)`，仍返回 `N` 帧。

Loop 窗口上限沿用 `max(1, floor(N * (sample_count - 1) / sample_count))`，同时受源帧数限制；缩短窗口时返回警告。长动画使用模型原生连续窗口/多提示词路径及配置的窗口采样密度，各次实际窗口生成均保留到结果列表。

Unity 的 Kimodo 曲线烘焙路径可能额外请求一个真实末端采样用于覆盖动画结束时间。该采样保留给烘焙，显示/分析结果仍使用原目标帧数；这是最终曲线端点处理，阶段和上下文裁剪仍由后端完成。

### Debug 返回与 Unity 写入

后端将每次实际模型生成结果保存在普通 `GenerationArtifact` 列表中。一个阶段可能产生多个长动画窗口或 ARDY horizon，因此 Debug 分段数取决于实际模型调用，不固定为三个。

| 模式 | 响应和二进制内容 | Unity 资产行为 |
|---|---|---|
| 普通 | `output_format:"kmb_v1"`，JSON 头中的 `byte_length` 指定随后一个最终 KMB 的字节数 | 写最终动画资产，原地应用 Loop Time |
| Debug | `output_format:"kmb_attachments_v1"`，随后依次放最终 KMB 和全部生成结果拼接的 KMB | 写最终动画，按元数据切分调试动画并加入现有 Debug Timeline |

Debug 的 `kmb_attachments` 目录将两个输出分别标记为 `purpose:"final"`、`purpose:"debug"`。各目录项包含 `index`、`offset`、`byte_length`、`start_frame`、`end_frame_exclusive`；`offset` 是相对于响应二进制块的字节偏移。最后一次生成的完整结果用于裁剪最终动画，不能从最后一个 Debug 窗口代替裁剪。

Debug 额外返回两组元数据：

| 字段 | 坐标与用途 |
|---|---|
| `debug_segments[].name`、`purpose` | 分段名称与用途，如 `Stage 2 / Window 1`、`trajectory_loop`、`final` |
| `debug_segments[].start_frame`、`end_frame_exclusive` | 该窗口在所属阶段生成时间轴上的范围 |
| `debug_segments[].debug_start_frame`、`debug_end_frame_exclusive` | 该窗口在拼接 Debug KMB 中的范围，Unity 用它切分 |
| `debug_segments[].constraint_count` | 该窗口记录的约束条数 |
| `debug_constraints[].artifact` | 对应 segment 的名称 |
| `debug_constraints[].start_frame`、`end_frame_exclusive` | 该约束组对应的拼接 Debug KMB 范围 |
| `debug_constraints[].constraints` | 该窗口实际使用的约束；其中 `frame_indices` 已转换为窗口内部帧号 |

ClipConstraint 的调试信息展开为可独立展示的 FullBody 姿势记录，包含根位置和关节旋转，并在 `reference` 中保留源约束、目标映射等信息。Kimodo 长动画窗口也记录衔接约束。Debug 数据保留每个实际窗口的完整结果，包含内部扩展和重叠，最终 KMB 则只保留要求的输出范围。

Unity 当前使用 `WriteResampledTimelineCacheClips` 设置发送 `debug:true`，单独的日志开关不控制此数据返回。收到结果后，Debug writer 按拼接帧范围切分、写入已有分段布局和 Overview，并按窗口内部帧号在对应时间附加约束标记；Unity 不重新决定阶段或调用模型。

以下是无前置上下文、`N=60`、`W=12`、跳过 Stage 1 的 Kimodo 实测例子，不包含额外烘焙端点：

| 内容 | 阶段内帧范围 | 拼接 Debug KMB 范围 | 帧数 |
|---|---|---|---:|
| Stage 2 / Window 1 | `[0, 60)` | `[0, 60)` | 60 |
| Stage 3 / Window 1 | `[0, 71)` | `[60, 131)` | 71 |
| 最终动画 | 从 Stage 3 的 `[11, 71)` 裁剪 | 独立 `final` 附件 | 60 |

本例 Loop 开头参考为 `[0, 12)`，尾部参考为 `[59, 71)`。Debug 拼接动画为 131 帧，最终动画仍为 60 帧。若 `frame_indices:[6]` 属于第二段，Unity 应把标记放在该段开始后 `6 / fps` 秒；拼接坐标则是第 66 帧。ARDY 对同样时长可能执行多个 horizon，分段数以响应为准。

### 实现入口

- [KimodoPlayableClipGenerationHostService.cs](../Editor/Core/KimodoPlayableClipGenerationHostService.cs)：Editor/Command 请求、帧范围、轨迹/Loop 设置与 Debug 开关。
- [BridgeProtocolClient.cs](../Runtime/Bridge/BridgeProtocolClient.cs)、[KimodoClipConstraintProtocol.cs](../Runtime/Constraints/KimodoClipConstraintProtocol.cs)：TCP 请求及输入 KMB 序列化。
- [kimodo_runtime.py](../NvlabKimodoQuickServer~/core/kimodo_runtime.py)：阶段选择与执行、Post Apply、Loop、最终裁剪和 Debug 汇总。
- [ardy_backend.py](../NvlabKimodoQuickServer~/core/ardy_backend.py)：固定时长 ARDY 阶段执行与 horizon 记录。
- [KimodoDebugTimelineWriter.cs](../Editor/Core/KimodoDebugTimelineWriter.cs)：按后端分段切分并写入 Debug Timeline 和约束标记。

### Results and verification

Fixed-duration ARDY uses the same stage/window plan through its own model executor; streaming ARDY remains independent. Dense KMB is converted to motion tensor before sparse conditioning frames are selected. A genuine one-frame reference temporarily receives a duplicate for velocity conversion, while conditioning keeps only the original frame.

Focused checks cover stage selection, distance/frame mapping, Post Apply XZ/Yaw preservation, Inside/Loop coexistence, anchor restoration, window sampling, output length, and Debug mapping. Real-model verification runs in the diagnostic copy using installed weights without downloading models. Short-reference generation extension and pose-window search remain TODO; pose matching will ignore root XZ and Yaw.

Verification completed on 2026-11-09:

- Python: 147 focused tests passed (`core.test_quickserver_sessions`, `kimodo.test_constraints`, `core.test_animation_analysis`, `core.test_retargeting`).
- Unity: 47 focused Edit Mode tests passed, including static/live Command help, Kimodo/ARDY reference sampling, final output ranges, and Debug segment-local constraint times. Result: `C:\nvlab\KimodoUnityBridge_FullDemo\Logs\backend-v3-complete.xml`.
- Real inference: 14 cases passed in `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_20261109_141042\results.json` and `backend_v3_20261109_142726\results.json`. Cases include long multiprompt motion, Loop, measured distance, Inside, Post Apply, FullBody/Effector, and Unity-sampled Outside/history KMB. The Unity inputs came from test fixtures, not a byte-for-byte replay of the original reported failure.
- TCP: two real ARDY Outside/Post Apply requests passed through the diagnostic supervisor on port 58723. Normal mode returned only 60 final frames; Debug returned the same 60-frame final output plus four complete generation segments. Result: `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_tcp_20261109_144526\results.json`. The diagnostic supervisor was stopped afterward.
- Loop clarification: the reference window and alignment anchor stay at output-local frame 0. After this change, all 147 Python tests passed, including frame-zero selection with/without foot events and preceding context. Real Kimodo v1.1 Loop and ARDY Inside+Loop+Heading checks both returned 60 final frames, with Debug confirming frame-zero reference poses and the full tail-window mapping. Result: `C:\nvlab\archive\QuickServerDiagnostics_20261104\log\backend_v3_20261109_161408\results.json`.
- The tested production Python files match the repository sources. FullDemo's installed QuickServer copy was not replaced; Editor/Command changes require the matching new backend.
- Installed Kimodo v1.1 and ARDY SOMA30 weights were used. Kimodo v1.2 weights were unavailable, and no weights were downloaded. Visual playback has not been accepted; the focused results do not imply a clean full-project test suite.

### AnimationClip Inspector API (2026-11-02)

- `Editor/Clip/AnimationClipInspector.cs` is the single typed fact source for AnimationClip settings, curve metadata, root-motion displacement/speed, rig type, and static AnimatorController consumers.
- `AnimationClipInspector.Read`, `ReadSettings`, `ReadCurves`, `ReadRootMotion`, `ReadConsumers`, and `WriteSettings` form the reusable editor API. `InspectJson` and `SetSettingsJson` are the command-facing mapping methods.
- The Unity panel, `clip.inspect`, `clip.settings.set`, and `generation.get.clip_settings` all use this API. `KimodoClipInspector` remains only as a narrow compatibility adapter and contains no fact calculation.

## SceneView rendering cleanup (2026-11-10)

The unused pose-depth Shader/HLSL and picture-compositing Compute Shader, together with their original asset and folder `.meta` files, are retained in [the rendering archive](../archive/scene-rendering-legacy-20261110~/README.md). No active code or serialized asset references were found in the package or the FullDemo project's Assets, Packages and ProjectSettings. The archive directory ends in `~` so Unity does not import these resources.

Current analysis images use the active SceneView camera through `CaptureInject/Editor/SceneViewCaptureBridge.cs`, then CPU tile composition in `Command/command_scene_capture_layout.cs`. Text labels use the Unity font atlas; the default `Graphics.Blit` only reads that atlas back. Temporary RenderTextures remain part of these readback paths.

Shader use remains in scene materials: `Command/command_capture.cs` selects an existing built-in pipeline Shader for trajectory/helper LineRenderers, and `Editor/Constraints/Preview/KimodoConstraintPoseRigFactory.cs` copies the character's existing materials for pose previews. These are part of SceneView rendering and remain active.

Verification: all eight moved files retained their SHA256 hashes; active package and FullDemo reference scans and `git diff --check` passed. Unity 2022.3.62f3c1 completed import/compilation and the focused `PoseRigClone_CopiesStaticMeshRenderer` Edit Mode test (1 passed, 0 failed). Result: `C:\nvlab\KimodoUnityBridge_FullDemo\Logs\shader-archive-20261110_131349.xml`. This was a headless check; screenshot appearance has not been visually verified.
