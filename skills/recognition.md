# Recognition

Use this skill when judging whether rendered animation evidence expresses a textual motion request.

## Procedure

1. Read the requested action, phase, direction/path, contacts, body state, and ending/loop condition.
2. Open the PNG returned by `animation_analyze`; do not infer from filenames, IDs, or numeric scores alone.
3. Read `pictures.images` in temporal order. Use only the presentations returned by that result: Humanoid output currently provides grouped keyframe/foot-transition panels and, at higher levels, individual pose panels; Mesh-only output provides Mesh pose panels.
4. Return `match`, `not_match`, or `insufficient_evidence` for each candidate and cite visible pose/path/contact evidence.

Humanoid analysis can support keyframe and foot-contact reasoning. Mesh-only analysis can support visible pose/path evidence but cannot establish Humanoid bone or foot-contact semantics. `animation_compare` is a Humanoid-only numeric boundary check and does not replace semantic visual judgment.

## 中文

当任务是判断渲染出的动画证据是否表达文字动作时使用本文件：

1. 读取动作、阶段、方向/路径、接触、身体状态和结束/循环条件。
2. 实际打开 `animation_analyze` 返回的 PNG；不能只根据文件名、ID 或数字分数推断。
3. 按 `pictures.images` 的时间顺序检查；只使用该结果实际返回的 presentation：当前 Humanoid 输出提供分组关键帧/脚切换面板，并在较高级别提供单独姿势面板；Mesh-only 输出提供 Mesh 姿态面板。
4. 对每个候选输出 `match`、`not_match` 或 `insufficient_evidence`，引用可见的姿势/路径/接触证据。

Humanoid 分析可以支持关键帧和脚接触判断；Mesh-only 分析只能支持可见姿势/路径证据，不能证明 Humanoid 骨骼或脚接触语义。`animation_compare` 是 Humanoid-only 的数值边界检查，不能替代语义视觉判断。
