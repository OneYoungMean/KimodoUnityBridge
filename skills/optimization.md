# Optimization

Use this skill when an existing Session Clip needs diagnosis and a corrected appended Clip.

## Loop

1. For a Humanoid Clip, analyze the source with `animation_analyze`; use `high` when foot transitions matter, then open `pictures.image_path`.
2. If the Clip was generated with an accepted `loop:true` request, rely on the generator's loop contract and do not add a separate boundary-pose seam check. For existing or imported loops without that contract, use `pose_get` on the available boundary frames among `0`, `1`, `N-2`, and `N-1` when the Clip length is `N` (clamp/deduplicate for very short Clips). A Mesh-only Clip is limited to Mesh analysis evidence.
3. For a Humanoid Clip, decide in-place versus locomotion. Repair the smallest supported cause with materialized External Pose edits, `pose_contract`, point constraints, a `pose_create_path` trajectory, or replacement generation. For Mesh-only evidence, report the unsupported correction boundary rather than inventing a Pose workflow.
4. Append a new Clip. Analyze it again, open its PNG, and report temporal qualities as `not_verified` without playback or dense samples.

## General correction

- Analyze before editing.
- Keep the source Clip and unrequested body regions unchanged.
- `pose_get` creates a new External Pose slot; edit that slot with `pose_set_root_transform` or `pose_set_muscle`.
- Use `kimodo_record_range` only when intent preflight explicitly requests cropping, splicing, or extraction of a specified Session range. Use `kimodo_retarget_animation` when the output belongs on another loaded Session character with a valid Humanoid Avatar. Retargeting requires valid Humanoid source and target Avatars. Do not call `record_range` as a generic post-generation or looping step.
- Completed Clips are immutable. There is no supported in-place correction command.
- A public-command limitation is a valid result; report it instead of inventing an edit path.

## Transition diagnosis

For Humanoid Clips, use `animation_compare` and endpoint `pose_get` to compare root, heading, mean-muscle, and end-effector deltas. Use opened `animation_analyze` foot-contact/pose evidence to judge support foot and phase: `animation_compare` currently has no compatible foot-contact comparison. A direct imported transition may be a logical `transition_clip`, not a baked asset. Generate a separate bridge only when evidence shows the direct transition is unsafe and the public workflow can express the bridge.

## Evidence status

- `passed`: requested visual properties are supported by an opened `animation_analyze` PNG.
- `needs_revision`: opened evidence shows a defect that can be addressed by another appended Clip.
- `not_verified`: the available static evidence cannot establish timing, sliding, popping, acceleration, or another requested temporal property.

## 中文

用于诊断并修正已有 Session Clip，并追加修正版。

### 循环

1. 对 Humanoid Clip，先用 `animation_analyze` 分析源 Clip；需要脚切换时使用 `high`，并打开 `pictures.image_path`。
2. 如果 Clip 来自无回退警告且已接受的 `loop:true` 请求，依赖生成器的循环契约，不再额外检查边界 Pose 接缝；对于没有该契约的已有或导入循环 Clip，长度为 `N` 时才用 `pose_get` 检查 `0`、`1`、`N-2`、`N-1` 中实际存在的边界帧（短 Clip 要限制并去重）。Mesh-only Clip 仅限 Mesh 分析证据。
3. 对 Humanoid Clip 区分原地/位移。用实体化 External Pose 编辑、`pose_contract`、点约束、`pose_create_path` 轨迹或替代生成修复最小支持原因。Mesh-only 证据应报告不支持的修正边界，不能虚构 Pose 工作流。
4. 追加新 Clip，再次分析并打开 PNG；没有播放/密集采样时，时间质量报告 `not_verified`。

### 通用修正

- 先分析再编辑。
- 保持源 Clip 和未请求的身体区域不变。
- `pose_get` 会创建新的 External Pose slot；用 `pose_set_root_transform` 或 `pose_set_muscle` 编辑该 slot。
- 只有意图预判明确要求裁剪、拼接或提取指定 Session 区间时才用 `kimodo_record_range`。需要把结果放到另一个带有效 Humanoid Avatar 的已加载角色时用 `kimodo_retarget_animation`。Retarget 要求源和目标都有有效 Humanoid Avatar；不能把 `record_range` 当作通用的生成后处理或循环步骤。
- 已完成 Clip 不可变，没有支持的原地修正命令。
- 公开命令无法实现某项修改时，报告边界，不要虚构编辑路径。

### 过渡诊断

对 Humanoid Clip，用 `animation_compare` 和端点 `pose_get` 对照 Root、朝向、平均 muscle 和末端差异；支撑脚和相位应通过打开的 `animation_analyze` 脚接触/姿态证据判断，因为 `animation_compare` 当前没有兼容脚接触比较。直接导入的过渡可能只是逻辑 `transition_clip`，不是 Bake 出来的资产；只有证据表明不安全且公开流程能表达时，才生成独立桥接 Clip。

### 证据状态

- `passed`：打开的 `animation_analyze` PNG 支持所要求的视觉属性。
- `needs_revision`：打开的证据显示缺陷，且可通过另一个追加 Clip 修复。
- `not_verified`：现有静态证据不能证明时序、滑步、跳变、加速度或其他时间属性。
