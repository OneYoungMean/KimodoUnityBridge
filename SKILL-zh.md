---
name: kimodo-unity-animation-zh
description: 在当前 Unity Editor 中发现并执行 Kimodo 人形动画命令。
---

# Kimodo Unity 动画命令

英文原文：[SKILL.md](SKILL.md)。

使用包内公开 Editor 入口：

```csharp
using KimodoUnityBridge.Command;

string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

把 `schema.tools` 的每一项暴露为 AI 工具，工具实现用其名称和 JSON 对象调用 `Invoke`。实时 Schema 与命令错误是最终依据。每个结果都有 `ok`；失败格式为 `{"ok":false,"error":"..."}`。可选键应省略，不要传 `null`。

## 从发现开始验证

1. 调用 `kimodo_help({})`，严格执行返回的 `workflow` 数组，不猜测调用顺序或参数。
2. 首次使用某命令前调用 `kimodo_help({"command":"<name>"})` 查看完整 Schema。
3. 只有需要改变默认模型或 Text Encoder 时，才调用 `kimodo_help({"section":"models"})`。

Help 返回的最小生成自检为：

```text
session_open({})
query_current_session({"query":"characters"})
kimodo_generate_animation({"character":"<返回的角色名>","prompt":"stand still and breathe naturally","duration_frames":60})
kimodo_get_generation({"request_id":"<返回的 request_id>"})，轮询到终态
session_close({})
```

生成运行期间不要关闭 Session。终态是 `completed`、`failed` 或 `canceled`。成功后查询 `character_animations`，确认生成动画已追加：

```json
{"query":"character_animations","character":"<返回的角色名>"}
```

## 当前契约

- 命令在当前 Unity Editor 的 Edit Mode 中执行。
- 所有公开时间值都是 60 FPS 整数帧，区间使用 `[start_frame,end_frame)`。
- 角色参数是安全的场景/Session 名称。场景有重名时，先用层级路径调用 `session_try_add`，再执行其他角色命令。
- `session_open` 创建或重开当前保留的 Session；`session_close` 保存并关闭编辑环境，不删除生成动画资产。
- 无当前 Session 时，`kimodo_generate_animation` 可创建并在完成后关闭保留的 `__KimodoAuto__` Session；多步骤 AI 工作流应显式使用 Session。
- 不要传 `timeline_session_id`、外部模型路径或实时 Schema 中不存在的参数。

## 工具分组

- 发现：`kimodo_help`。
- Session：`session_open`、`session_close`、`query_current_session`、`session_try_add`、`session_try_remove`。
- Pose/约束：`pose_create`、`pose_get`、`pose_set`、`pose_copy`、`kimodo_build_root2d_path`。
- 动画：`kimodo_generate_animation`、`kimodo_get_generation`、`kimodo_cancel_generation`、`kimodo_analyze`、`query_picture`、`kimodo_bake_range`。
- Debug 维护：`kimodo_debug_install_server`；只在明确诊断本地 QuickServer 安装时使用。

Pose/Constraint 示例见 [Manual/AI 工作流示例与 Constraint API 设计](Manual/AI%20工作流示例与%20Constraint%20API%20设计.md)。复制示例前始终检查命令的实时 Schema。
