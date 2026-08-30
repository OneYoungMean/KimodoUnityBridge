---
name: kimodo-session
description: Prepare, reuse, and close the current animation Session and load only explicitly requested content.
---

# Session tool / Session 工具

负责 `session_get_or_create`、`session_add` 和 `session_close` 的生命周期编排。

`session_get_or_create` 会立即建立专用 Preview Scene（基础地面、灯光、角色容器与 Timeline Director）。加入角色时复制源对象到该场景，移除 `CharacterController` 并清空 `Animator.runtimeAnimatorController`；源场景对象不会被用于后续评估或渲染。

- 新 Session 默认为空；只添加请求明确指定的角色、Clip 或 Animator。
- 始终复用运行时返回的安全名称，不用文件名、显示标签或猜测名称替代。
- 切换或关闭 Session 可能取消活动生成；有活动任务时先读取其状态并在报告中说明影响。
- `session_close` 只关闭编辑上下文，保留 Timeline、资产和 Session JSON。

输出至少包含 `session_id`、已加载对象的安全名称和运行时警告。
