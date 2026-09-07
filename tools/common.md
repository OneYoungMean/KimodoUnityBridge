---
name: kimodo-common
description: Shared execution contract for all Kimodo capability tools.
---

# Common tool contract / 公共工具契约

所有工具都遵守同一套输入、证据、状态和报告规则。工具只实现自身能力，不重复定义安装流程。

## Input and Session

- 只使用用户明确提供的 source、target、range、pose、path 和 constraint。
- 只要生成请求指定了动作，先检查当前活动场景对应 Session 中的语义匹配动画；“修复/改进/替换/续作/变体 + 指定动作”绝不能跳过这一步。检查结果要记录为上下文证据，不能把发现的动画自动升级为生成约束。
- `session_get_or_create` 立即创建并返回专用可见 Session GameObject；后续生成、采样、分析和渲染均在该对象下执行。对象仅包含基础地面、灯光、角色和 Timeline Director。
- Session 切换时只激活当前 Session 根对象，旧 Session 根对象保持禁用但不销毁，便于调试。
- `session_add(kind="character")` 将源角色复制到 Session 根对象，保留源角色组件（包括已有的 `CharacterController`），仅清空 Session 副本的 `Animator.runtimeAnimatorController`，保留 Humanoid Avatar。
- `session_add` 只添加缺少且明确请求的角色、Clip 或 Animator。
- 角色来源优先级固定为当前活动场景中用户打开/选中的 `Animator` 所属角色；不得用保存的 prefab 路径替代活动场景对象。已有 prefab 实例直接使用，不创建持久化 prefab 副本；非 prefab 场景对象仅在请求的输出目录创建一个 prefab 后使用。
- 后续命令只使用运行时返回的安全名称和 `{track,index}` 引用。
- 关闭或切换 Session 可能取消活动生成；报告中必须保留该副作用。

## Evidence

- `animation_analyze` 的图片必须实际打开后才能用于视觉判断。
- 数值、文件名、标签和选帧数量只能辅助判断，不能替代视觉证据。
- 静态图片不能证明播放连续性、滑步、跳变、加速度或速度连续性。
- 必要证据缺失时返回 `not_verified` 或 `insufficient_evidence`，不得猜测为通过。

### Analysis handoff / 分析交接边界

`animation_analyze` 的证据交给下一个环节时，必须分成两类，不能互相替代：

- **图像识别**：`trajectory_shape`、`action_semantics`、
  `visual_motion_quality`、`contact_pattern`。每项必须实际打开对应 tile，
  并返回 `value`、`confidence`、`evidence`。
- **结构化分析**：`loop_endpoint`、`keyframe_heading`、
  `trajectory_turn`、`trajectory_length`。数值和布尔值只能读取返回的
  `motion_profile`（`phase_anchor_heading_consistent` 优先），不能从像素估算；字段缺失时返回 `UNKNOWN`。

没有时间序列或播放采样时，`playback_continuity`、`velocity_smoothness`、
`acceleration_quality` 固定为 `UNKNOWN`。`confidence` 是证据可靠度，不是
动作质量分数；任何必需项为 `UNKNOWN`、`CONFLICT` 或低置信度时，交接状态不得
报告为 `verified`。

### Phase track / 连续阶段轨道

Humanoid analysis 使用破坏性的新契约 `analysis_schema_version=2-phase-track-v1`
与 `phase_track_version=1-temporal-cluster-v1`。图片同时返回
`pictures.render_version`；识别/比较必须记录并校验这些版本，版本不匹配时返回
`UNKNOWN` 并要求更新提示词。旧的均分关键帧
列表不再是阶段证据；`phase_track` 是唯一阶段来源。每项必须覆盖连续帧区间，
区间首尾相接、无重叠、无空洞，并包含：`start_frame`、`end_frame`、
`duration_frames`、`duration_seconds`、`anchor_frame`、`kind` 和 `confidence`。
`kind` 只能是 `phase` 或 `transition`；聚类器不得直接命名 `walk`、`wave` 等语义。
Mesh analysis 对该字段返回 `NOT_APPLICABLE`。

比较多个目标语义时，按用户给出的优先级计算时长先验：第 `i` 个语义的权重为
`w_i = 1/(i*i)`，目标占比为 `p_i = w_i / sum(w_j)`。比较使用实际阶段覆盖
时长与该先验的偏差；不把权重相加做总质量分数。允许语义覆盖重叠（例如边走边挥手），
但 `idle`/站立背景阶段的总覆盖不得超过 50%，除非请求明确要求长时间 idle。

交接结果的最小外壳为：

```json
{
  "status": "verified|needs_review|not_verified",
  "analysis_handoff": {
    "<task>": {
      "value": "<fixed enum or number>",
      "confidence": "high|medium|low",
      "evidence": ["<tile id or structured field>"]
    }
  }
}
```

## Async tasks

- `kimodo_install_server` 与 `kimodo_generate_animation` 都返回 `request_id`；保存它并按固定间隔轮询 `kimodo_get_generation`。
- 安装终态为 `done`、`error`；生成终态为 `completed`、`failed`、`canceled`。安装任务当前不能取消，生成任务可用 `kimodo_cancel_generation` 取消。
- 每次状态查询以 `progress`、`eta_seconds` 和 `message` 为进度依据；不自行重新估算剩余时间。
- 超时、过期 request 或未知状态按未验证/失败报告，不无限重试。
- assembly reload、Editor 退出、切换场景和进入 Play Mode 导致的取消必须如实保留。
- 自动修正最多一次；修正结果是新的派生 Clip，原完成结果必须保留。

## Handles and paths

- 安全角色名和动画名是 Session handle。
- `animation_analyze` 返回的 `{track,index}` 可作为 Root Path 或 Pose 引用。
- 生成结果的 `path` 是 Unity 资产元数据，不是 Session Clip handle；若返回中没有派生资产路径，不自行推断。

## Report envelope

每个工具返回统一外壳：

```text
{
  result,
  output,
  criteria,
  evidence,
  unverified,
  runtime_warnings
}
```

工具可以附加专属字段，例如 comparison 的候选胜负、generation 的 request_id，或 pose 的 `{track,index}`。
