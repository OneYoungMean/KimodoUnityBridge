---
name: kimodo-unity-animation-zh
description: 在当前 Unity Editor 中操作 Kimodo 人形动画。用于 Timeline Session 的创建与查询、场景角色动画生成、姿态采样、角色或 Clip 编辑、生成分析，以及 Timeline 区间 Bake 或 Retarget。
---

# Kimodo Unity Animation API 中文对照

English source: [SKILL.md](SKILL.md).

以实时工具 Schema 和工具返回的错误为最终准则。仅发送 JSON 对象。每个响应都含有 `ok`；失败时为 `{"ok":false,"error":"..."}`。

## 执行契约

- 只对当前 Unity Editor 场景的 Edit Mode 执行操作。
- 所有 Timeline 操作都针对当前 Session。不要传 `timeline_session_id`、`director_ref` 或 Timeline 资产引用作为输入。
- 使用前序工具返回的 `character_ref`、`animation_id`、`request_id`、`asset_ref` 和 `clip_ref`；它们都是不透明标识符。
- 生成、采样、重定向或 TryAdd 所用角色必须是带有效 Humanoid Avatar 的场景对象。Project 资产永远不能作为生成角色。
- 使用 `query_current_session` 的 `type` 参数。
- 对可选参数直接省略键，不要传 `null`。

## 必须执行一次的最小自检

在进行实质工作前，完整执行一次以下流程。它使用已配置的默认模型；除非需要切换模型，不要调用 `Kimodo_list_models`。

1. `session_open_timeline({})`。
2. `query_current_session({"type":"character","pattern":"*","long":true})`；从返回项中选择 `avatar` 为 `valid_humanoid` 的角色，保存 `character_ref`。
3. `kimodo_generate_animation_asset({"character_ref":"...","prompt":"stand still and breathe naturally","duration_seconds":1})`；保存 `request_id`。
4. 重复调用 `kimodo_get_generation({"request_id":"..."})`，直到 `status` 为 `completed`、`failed` 或 `canceled`。
5. 若为 `completed`，调用 `query_current_session({"type":"animation","character_ref":"...","pattern":"*","long":true})`；确认新条目包含 `animation_id`、`global_start`、`global_end` 和 `asset_path`。

请求运行时不要关闭 Session。只有没有运行中的生成任务时才调用 `session_close_timeline({})`。

## 工具索引

| 工具 | 用途 | 是否需要当前 Session |
| --- | --- | --- |
| `kimodo_list_characters` | 列出有效 Humanoid 角色。 | 否 |
| `Kimodo_list_models` | 为显式切换模型列出合法模型/编码器组合。 | 否 |
| `kimodo_help` | 返回后端能力文本。 | 否 |
| `session_open_timeline` | 创建全新当前 Session，或加载有名称的内存 Session。 | 否 |
| `session_close_timeline` | 关闭并保留当前 Session。 | 是 |
| `query_current_session` | 通过 ls 风格选择器列出 Session、角色或动画对象。 | 是 |
| `session_locate_animation` | 选中 Timeline 动画并 Evaluate 到指定时间。 | 是 |
| `session_sample_pose` | 在全局 Session 时间采样角色姿态。 | 是 |
| `session_try_add` / `session_try_remove` | 添加或移除角色 Track 或 Clip。 | 是 |
| `kimodo_generate_animation_asset` | 启动异步文本到动画生成。 | 是 |
| `kimodo_get_generation` / `kimodo_cancel_generation` | 轮询或取消生成。 | request id |
| `kimodo_analyze_timeline_range` | 返回保存的分析和 Timeline 区间分析。 | 是 |
| `kimodo_bake_timeline_range` | 把全局区间 Bake 为 AnimationClip；可选 Retarget。 | 是 |

## 读取 API

### `kimodo_list_characters`

**签名：** `kimodo_list_characters({ include_project_assets?, max_results? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `include_project_assets` | boolean | 否 | `false` | 否 | 包含 `Assets` 下的 Humanoid Prefab/模型资产。它们仅用于发现；不能用作生成角色。 |
| `max_results` | integer | 否 | `100` | 否 | 会被限制在 `1..1000`。 |

**返回：** `characters[]` 和 `count`。每项包含 `character_ref`、`name`、`source`（`scene` 或 `project`）、`avatar`、`asset_path`、`scene_path` 和 `active`。生成只能使用 `source: "scene"` 的 `character_ref`。

### `Kimodo_list_models`

**签名：** `Kimodo_list_models({})`

只在用户要求改变 `model` 或 `text_encoder_model` 时调用。

**参数：** 无。

**返回：** `configs[]` 和 `count`。选择 `available: true` 的条目，并将其 `model` 与 `text_encoder_model` 原样传给生成工具。

### `kimodo_help`

**签名：** `kimodo_help({})`

**参数：** 无。**返回：** 后端 help/能力 payload。仅在需要本 Skill 未定义的后端特定选项时使用。

### `query_current_session`

**签名：** `query_current_session({ type?, pattern?, objects?, long?, head?, tail?, show_type?, character_ref?, character_name? })`

这是 Maya `ls` 风格查询 API，只列出当前 Session 对象。`pattern` 与 `objects` 的每一项均支持 `*` 匹配任意序列、`?` 匹配一个字符，且忽略大小写。

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `type` | enum | 否 | `session` | 否 | `session`、`character` 或 `animation`。 |
| `pattern` | string | 否 | `*` | 否 | 通配符选择器。`objects` 非空时忽略。 |
| `objects` | string[] | 否 | 省略 | 是 | 一个或多个名称、`character_ref` 或 `animation_id` 通配符选择器；任一项匹配即返回。 |
| `long` | boolean | 否 | `true` | 否 | `true` 返回完整对象详情；`false` 返回紧凑标识。 |
| `head` | integer | 否 | 省略 | 否 | 非负数。保留前 N 项。 |
| `tail` | integer | 否 | 省略 | 否 | 非负数。在 `head` 后执行；保留剩余结果的最后 N 项。 |
| `show_type` | boolean | 否 | `false` | 否 | 为每个返回对象添加 `type`。 |
| `character_ref` | string | 否 | 省略 | 否 | `type: "animation"` 时限制到该角色。 |
| `character_name` | string | 否 | 省略 | 否 | `type: "animation"` 时的角色限制替代项。 |

**选择规则：** 优先使用 `character_ref`，因为名称可能重名。`type: "animation"` 时，`objects` 可匹配 `name` 或 `animation_id`。不传 `character_ref` 和 `character_name` 时，列出所有当前 Session 角色的动画。

**返回：** `{ session_name, type, pattern, count, objects[] }`。`long: true` 时：

| 对象类型 | 返回字段 |
| --- | --- |
| `session` | `session_name`、`director_ref`、`timeline_asset_ref`、`timeline_asset_path`、`characters`、`current_time`、`current`。 |
| `character` | `character_ref`、`name`、`animator_ref`、`avatar_ref`、`avatar`、`avatar_error`、`track_ref`、`animation_count`、`next_start_seconds`、`animations`。 |
| `animation` | `animation_id`、`name`、`source`、`global_start`、`global_end`、`duration`、`clip_in`、`time_scale`、`asset_ref`、`asset_path`、`frame_rate`、`frame_count`、`is_human_motion`、`analysis`，以及 `character`。 |

## Session 生命周期 API

### `session_open_timeline`

**签名：** `session_open_timeline({ session_name? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `session_name` | string | 否 | 省略 | 否 | 省略时创建唯一命名的新 Session。当前 Unity Domain 中仍存在同名 Session 时加载它；其他名称会创建命名 Session。 |

**作用：** 创建临时 Director 和 TimelineAsset、扫描场景 Animator、创建动画 Track、展平已发现的 Animator Clip、启用 Timeline Preview 并打开 Timeline Window。

**返回：** 与 `query_current_session` 中 `type: "session"` 的完整 Session 对象相同。

### `session_close_timeline`

**签名：** `session_close_timeline({})`

**参数：** 无。

**前置条件：** 当前 Session 中没有运行的生成任务。

**作用：** 清空 Timeline 选中、保存 TimelineAsset，并关闭其编辑环境。当前 Session、其 Director 和 TimelineAsset 都会保留，可在之后以 `session_open_timeline({"session_name":"..."})` 重新打开；本工具绝不删除资产或场景对象。

**返回：** `session_name`、`timeline_asset_path`、`session_saved: true`、`session_retained: true`、`closed: true`。

## Timeline 定位和采样 API

### 共用对象引用规则

以下工具出现的每一组选择器，除非另有说明，都至少要提供一个值。

| 选择器 | 可接受值 | 规则 |
| --- | --- | --- |
| 角色选择器 | `character_ref` 或 `character_name` | 至少有一个能解析为当前 Session 角色。优先 `character_ref`；已知引用时不传名称。 |
| 动画选择器 | `animation_id` 或 `animation_name` | 至少有一个能在已选角色下解析。优先 `animation_id`；已知 id 时不传名称。 |

### `session_locate_animation`

**签名：** `session_locate_animation({ character_ref|character_name, animation_id|animation_name, session_global? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | 至少一个 | — | 否 | 角色选择器。 |
| `animation_id` / `animation_name` | string | 至少一个 | — | 否 | 动画选择器。 |
| `session_global` | number | 否 | 所选动画的 `global_start` | 否 | 非负有限的全局 Session 秒数。 |

**返回：** `session_name`、`character`、完整 `animation`、`session_global`、`located: true`。

### `session_sample_pose`

**签名：** `session_sample_pose({ character_ref|character_name, session_global })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | 至少一个 | — | 否 | 角色选择器。 |
| `session_global` | number | 是 | — | 否 | 非负有限的全局 Session 秒数。 |

**返回：** `pose_sample_id`、`session_name`、`character`、`session_global`、`root_position`、`root_rotation`、`body_position`、`body_rotation`、`muscles[]` 和 `bones[]`（`bone`、世界 `position`、世界 `rotation`）。

## Session 编辑 API

### `session_try_add`

**签名：** `session_try_add({ kind, character_ref?, character_name?, clip_ref? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `kind` | enum | 是 | — | 否 | `character` 或 `clip`。 |
| `character_ref` | string | 条件必填 | — | 否 | `kind: "character"` 时必填，必须解析到场景 GameObject 或 Animator。`kind: "clip"` 时作为角色选择器必填，除非传了 `character_name`。 |
| `character_name` | string | 条件必填 | — | 否 | 仅允许在 `kind: "clip"` 时用作当前 Session 角色选择器；不能据此新增角色。 |
| `clip_ref` | string | 条件必填 | — | 否 | `kind: "clip"` 时必填；AnimationClip GlobalObjectId 或 `Assets/...` 路径。 |

**规则：** 添加角色会尝试解析或生成 Humanoid Avatar。失败时返回 `avatar_required` 错误，且不会保留半成品 Track。添加 Clip 永远追加到角色 Track 尾部，绝不填补空洞。

**返回：** 添加角色返回 `{ added: true, kind: "character", character }`；添加 Clip 返回 `{ added: true, kind: "clip", animation }`。

### `session_try_remove`

**签名：** `session_try_remove({ kind, character_ref|character_name, animation_id|animation_name? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `kind` | enum | 是 | — | 否 | `character` 或 `clip`。 |
| `character_ref` / `character_name` | string | 至少一个 | — | 否 | 当前 Session 角色选择器。 |
| `animation_id` / `animation_name` | string | 条件必填 | — | 否 | 仅 `kind: "clip"` 时必填；动画选择器。 |

**规则：** 移除 Clip 不会移动余下 Clip、压缩虚拟全局时间、重建 Track 或复用被移除的全局地址。

**返回：** 移除 Clip 返回 `removed: true`、`kind: "clip"`、`animation_id`；移除角色返回 `removed: true`、`kind: "character"`、`character_ref`。

## 生成 API

### `kimodo_generate_animation_asset`

**签名：** `kimodo_generate_animation_asset({ character_ref, prompt, duration_seconds?, model?, text_encoder_model?, seed?, diffusion_steps?, output_mode?, output_folder?, asset_name?, loop?, analysis_option?, pose_refs?, times?, constraint_types? })`

**前置条件：** 必须存在当前 Session。`character_ref` 必须解析到场景 Humanoid 角色；若该角色尚未在 Session 中，会先被追加。

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` | string | 是 | — | 否 | 场景 GameObject 或 Animator GlobalObjectId；Project 资产路径会被拒绝。 |
| `prompt` | string | 是 | — | 否 | 非空的动作提示词。 |
| `duration_seconds` | number | 否 | `5` | 否 | 正的有限秒数。 |
| `model` | string | 否 | Project Settings 默认值 | 否 | 已注册模型/配置 id，绝不能是路径。显式切换前先用 `Kimodo_list_models` 查询。 |
| `text_encoder_model` | enum | 否 | Project Settings 默认值 | 否 | `high_performance` 或 `high_precision`。只要传了它或 `model`，该组合就必须当前可用。 |
| `seed` | integer | 否 | 随机非负整数 | 否 | 可复现种子。 |
| `diffusion_steps` | integer | 否 | 模型默认值 | 否 | 标准 Kimodo：限制为 `1..1000`，默认 `100`；ARDY：限制为选中 Profile 的范围，默认 `0`。 |
| `output_mode` | enum | 否 | `humanoid_muscle` | 否 | `humanoid_muscle`、`character_bone` 或 `model_bone`。前两者需要有效目标 Humanoid Avatar。 |
| `output_folder` | string | 否 | `Assets/KimodoGeneratedClips` | 否 | 必须是 `Assets` 或其子目录；不能含 `.` 或 `..` 目录段。 |
| `asset_name` | string | 否 | 带时间戳的角色名 | 否 | 不带扩展名的资产基名。 |
| `loop` | boolean | 否 | `false` | 否 | 仅标准 Kimodo 模型。服务端先生成种子动作，再用其第 0 帧身体姿态约束第 `0` 与 `frame_count - 1` 帧；种子动作不会保存，最终 Clip 会启用 `loopTime`。根位移不加约束。 |
| `analysis_option` | object | 否 | 省略 | 否 | 后端分析对象。仅传 `kimodo_help` 支持的字段；支持时 `keyframes.enabled: true` 请求截图关键帧。 |
| `pose_refs` | string[] | 否 | 省略 | 是 | 用作姿态约束的场景 GameObject 或 Animator GlobalObjectId。 |
| `times` | number[] | 条件可选 | 从首帧到末帧均匀分布 | 是 | 只允许与 `pose_refs` 一起传；数量必须完全相等；每一项有限且在 `[0, duration_seconds]` 内。 |
| `constraint_types` | enum[] | 条件可选 | 每个姿态均为 `fullbody` | 是 | 只允许与 `pose_refs` 一起传；数量必须完全相等；每项为 `fullbody` 或 `root2d`。 |

**多值规则：** `pose_refs`、`times` 和 `constraint_types` 按下标一一对应。没有 `pose_refs` 时传 `times` 或 `constraint_types` 会失败。

**立即返回：** `request_id`、`status: "running"`、`character`、`output_mode`、`model`、`text_encoder_model`、`seed`、`session_name`、`timeline_start_seconds`、`timeline_duration_seconds`。Timeline 位置在此时仅被预留，不能认为资产已完成。

### `kimodo_get_generation`

**签名：** `kimodo_get_generation({ request_id })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `request_id` | UUID string | 是 | — | 否 | `kimodo_generate_animation_asset` 返回的标识。 |

**返回：** `request_id`、`status`、`stage`、`message`、`error`、`started_at_utc`、`target_alive`；有结果后还会返回 `asset_path`、`raw_bone_asset_path`、`seed`、`prompt`、可选 `analysis`；Session 写回后增加 `session_name`、`timeline_start_seconds`、`timeline_duration_seconds`、`timeline_clip_asset_ref`、`animation_id` 和可选 `analysis_track_ref`。只在 `completed`、`failed` 或 `canceled` 时停止轮询。

### `kimodo_cancel_generation`

**签名：** `kimodo_cancel_generation({ request_id, reason? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `request_id` | UUID string | 是 | — | 否 | 生成任务返回的标识。 |
| `reason` | string | 否 | `Generation canceled by command.` | 否 | 取消原因。 |

**返回：** 正常生成状态 payload，外加表示是否已执行取消的 `canceled`。再轮询一次以确认终态。

## 分析和 Bake API

### `kimodo_analyze_timeline_range`

**签名：** `kimodo_analyze_timeline_range({ character_ref|character_name, start_global, end_global, analysis_option? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | 至少一个 | — | 否 | 当前 Session 角色选择器。 |
| `start_global` | number | 是 | — | 否 | 有限的包含式全局时间；范围必须满足 `0 <= start_global < end_global`。 |
| `end_global` | number | 是 | — | 否 | 有限的排他式全局时间。 |
| `analysis_option` | object | 否 | `{}` | 否 | 后端分析选项。该 API 强制 `analysis_only: true`。 |

**返回：** `session_name`、`character`、`start_global`、`end_global`、`analyses[]` 与 `analysis`。每个重叠的生成动画均返回已保存分析。只有重叠 Clip 保留 KMB 数据时才运行后端分析；否则 `analysis` 由已保存生成结果构成，可能没有 issues 或 keyframes。

### `kimodo_bake_timeline_range`

**签名：** `kimodo_bake_timeline_range({ character_ref|character_name, start_global, end_global, retarget_character_ref?, asset_name?, output_folder? })`

| 参数 | 类型 | 必填 | 默认值 | 多值 | 作用与约束 |
| --- | --- | :---: | --- | :---: | --- |
| `character_ref` / `character_name` | string | 至少一个 | — | 否 | 源当前 Session 角色选择器。 |
| `start_global` | number | 是 | — | 否 | 有限的包含式全局时间；范围必须满足 `0 <= start_global < end_global`。 |
| `end_global` | number | 是 | — | 否 | 有限的排他式全局时间。 |
| `retarget_character_ref` | string | 否 | 源角色 | 否 | 目标场景角色引用或当前 Session 目标角色名。没有 Track 时会尝试 TryAdd；目标必须有有效 Humanoid Avatar。 |
| `asset_name` | string | 否 | 带时间戳的源角色名 | 否 | 不带扩展名的输出 AnimationClip 基名。 |
| `output_folder` | string | 否 | `Assets/KimodoGeneratedClips` | 否 | 与生成相同的仅 `Assets` 路径规则。 |

**返回：** `baked: true`、`asset_ref`、`asset_path`、`character`、`source_character`、`start_global`、`end_global` 和完整的已追加 `animation`。未 Retarget 时输出源角色骨骼曲线；指定 Retarget 时输出 Humanoid muscle clip，并追加到目标 Track。
