---
name: kimodo-unity-animation-zh
description: 在当前 Unity Editor 中通过 Kimodo 命令发现、生成、检查并迭代人形动画。
---

# Kimodo Unity 动画

使用公开 Editor 入口：

```csharp
using CharacterAnimationCli.Unity.Command;
string schema = command_dispatcher.GetCommandDefinitionsJson();
string result = command_dispatcher.Invoke(commandName, argumentsJson);
```

只把实时 `schema.tools` 中的条目暴露为工具。结果始终使用 vNext 的 `ok` envelope。命令定义、返回的 ID、名称、路径和缓存 locator 都是权威的不透明句柄。

## 标准工作流

1. `kimodo_help({})`，然后 `session_get_or_create({"name":"<稳定名称>"})`。
2. `session_add({"kind":"character","character":"<场景名称或层级路径>"})`；保存返回的安全角色名。使用 `kind:"clip"` 或 `kind:"animator"` 显式加入项目 Clip 或 Animator；Animator 只把同 Layer 的 State→State 过渡组合为 Timeline `transition_clip`。检查 128 个过渡片段的 warning，只有确实需要全量展开时才使用 `ignore_warning:true`。
3. 通过 `kimodo_generate_animation` 生成；保存 `request_id`；轮询 `kimodo_get_generation` 直到终态。
4. 使用一个或两个显式的 `{character,clip,role?}` 项调用 `animation_analyze`。`level` 默认是 `middle`；紧凑的 ghost/trajectory 对使用 `low`，需要五个关键姿势和六个脚接触子图时使用 `high`。`-test` 仅用于验证渲染器：每个角色输出一张 512×512 的正交 ghost-3D 图与一张 512×512 的正交骨盆轨迹图。相同不可变 Clip 与有效 level 会直接返回既有分析和图片。
5. 读取返回的组合 PNG `pictures.image_path` 及其自描述的 `pictures.images` 子图列表。没有独立图片命令或公开的 `analysis_id`。
6. 将视觉证据与 prompt 比较。修正稀疏约束、端点姿势或 prompt，然后迭代。

将返回的 `session_json_path` 作为紧凑的 Session 索引读取；它包含视觉子图描述符但不含图片字节。每个写入 Session 且已完成的 Clip 都不可变：不得覆盖、重定时或替换；生成、Record、Retarget 或修正结果必须追加新 Clip。新 Session 为空；通过 `session_add` 显式加入场景 Humanoid、Clip 或 Animator 内容。
Transition 是由 Timeline 片段组成的逻辑复合动画，不会 Bake 新的 AnimationClip；Any State、Entry、Exit、StateMachine 和 OverrideController 过渡会报告为跳过。

## 视觉验收

- 关键姿势图必须表达请求的动作、方向、身体状态、接触/对象关系和结束状态。
- 按返回的 `keyframes` 的 saliency 降序检查关键帧；按 `foot_contacts` 的持续时间从短到长检查脚接触切换。
- Ghost 子图必须符合预期根路径、位移、朝向，且没有无法解释的漂移。
- 轨迹子图必须展示合理的骨盆路径，并通过线条颜色和 alpha 表示速度与加速度。
- 循环动画需要检查首末姿势、根朝向与位置、脚接触相位和速度连续性。出现可见接缝时，继续生成或修正。

## Pose 工作

使用 `pose_get` 取得来源姿势和可写 Pose Cache marker。保存返回 marker locator，供 `pose_set_root_transform`、`pose_set_muscle` 使用。需要完整 muscle 时使用 `full_data:true`。使用 `pose_contract` 对齐一个或多个手脚目标，并将多末端拟合返回的 residual 纳入判断。

数据集动画名称应改写为简洁自然语言 prompt：保留动作、阶段、方向/路径、速度、接触/对象和结束状态；移除不承载动作语义的 take ID、演员 ID 和内部变体后缀。
