---
name: kimodo-session
description: Prepare, reuse, and close the current animation Session and load only explicitly requested content.
---

# Session tool / Session 工具

负责 `session_get_or_create`、`session_add` 和 `session_close` 的生命周期编排。

`session_get_or_create` 会立即建立专用 Preview Scene（基础地面、灯光、角色容器与 Timeline Director）。加入角色时复制源对象到该场景，保留源角色组件（包括已有的 `CharacterController`），仅清空预览副本的 `Animator.runtimeAnimatorController`；源场景对象不会被用于后续评估或渲染。

项目设置中的 `Activate Preview Scene (Debug)` 默认开启：Session 打开后会尝试将该 Preview Scene 设为 Unity 活动场景，便于在编辑器中检查角色、Timeline 和生成结果；Session 关闭或切换时恢复原活动场景。Unity 不接受 Preview Scene 作为活动场景时会保留原有静默隔离行为并输出警告。

- 新 Session 默认为空；只添加请求明确指定的角色、Clip 或 Animator。
- `kind=character` 的角色必须从当前活动场景中用户打开/选中的 `Animator` 所属对象解析；调用 `session_get_or_create` 或 `session_add` 时使用 `character="@active_animator"`，不要根据默认 prefab、题库路径或磁盘中同名资产替代它。优先使用已有 prefab 实例；只有对象不是 prefab 时，才在请求的输出目录创建一个 prefab，不得创建已有 prefab 的持久化副本。
- 始终复用运行时返回的安全名称，不用文件名、显示标签或猜测名称替代。
- 切换或关闭 Session 可能取消活动生成；有活动任务时先读取其状态并在报告中说明影响。
- `session_close` 只关闭编辑上下文，保留 Timeline、资产和 Session JSON。

输出至少包含 `session_id`、已加载对象的安全名称和运行时警告。
