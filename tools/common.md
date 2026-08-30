---
name: kimodo-common
description: Shared execution contract for all Kimodo capability tools.
---

# Common tool contract / 公共工具契约

所有工具都遵守同一套输入、证据、状态和报告规则。工具只实现自身能力，不重复定义安装流程。

## Input and Session

- 只使用用户明确提供的 source、target、range、pose、path 和 constraint。
- `session_get_or_create` 立即创建并返回专用 Preview Scene；后续生成、采样、分析和渲染均在该场景执行。场景仅包含基础地面、灯光、角色和 Timeline Director。
- `session_add(kind="character")` 将源角色复制到 Preview Scene，移除所有 `CharacterController`，清空 `Animator.runtimeAnimatorController`，保留 Humanoid Avatar。
- `session_add` 只添加缺少且明确请求的角色、Clip 或 Animator。
- 后续命令只使用运行时返回的安全名称和 `{track,index}` 引用。
- 关闭或切换 Session 可能取消活动生成；报告中必须保留该副作用。

## Evidence

- `animation_analyze` 的图片必须实际打开后才能用于视觉判断。
- 数值、文件名、标签和选帧数量只能辅助判断，不能替代视觉证据。
- 静态图片不能证明播放连续性、滑步、跳变、加速度或速度连续性。
- 必要证据缺失时返回 `not_verified` 或 `insufficient_evidence`，不得猜测为通过。

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
