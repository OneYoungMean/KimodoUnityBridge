# Recognition

Use this when judging whether animation images express a textual motion request.

1. Read the requested action, phase, direction/path, contacts, body state, and ending/loop condition.
2. Open the PNG returned by `animation_analyze`; do not infer from filenames or IDs.
3. Read tiles in temporal order, using key-pose, foot-contact, ghost, and trajectory descriptors from `pictures.images`.
4. Return `match`, `not_match`, or `insufficient_evidence` for each candidate and cite visible pose/path/contact evidence. The text request is part of the interpretation.

For candidate comparison, `animation_compare` is numeric/range evidence; it does not replace semantic visual judgment.

## 中文

当任务是判断动画图像是否表达文字动作时使用本文件：读取动作、阶段、方向/路径、接触、身体状态和结束/循环条件；实际打开 `animation_analyze` 返回的 PNG；按 `pictures.images` 的关键姿势、脚接触、ghost 和轨迹子图按时间顺序检查；逐个输出 `match`、`not_match` 或 `insufficient_evidence`，引用可见姿势/路径/接触证据。文件名和 ID 不是语义证据，文字要求会参与解释。
