# Recognize

Use this sub-skill when the task asks whether an animation expresses a requested motion.

## Workflow

1. Translate the request into observable motion criteria.
2. Obtain analysis evidence for each animation under comparable conditions.
3. Inspect visual evidence in temporal order and map every observation to the correct animation.
4. Judge the requested action and phase first, then use direction, path, body state, contacts, and ending evidence where relevant.
5. Return `match`, `not_match`, or `insufficient_evidence`, with concise evidence for the decision.

## Prompt structure

Describe the target without adding unstated details:

```text
Action + phase + direction/path + body/contact state + ending or loop condition + relevant style
```

Turn that description into a checklist of visible outcomes before judging the animation.

## Cautions

- Do not use filenames, internal labels, candidate order, or motion magnitude as semantic proof.
- Resolve direction from the character's forward orientation and observed trajectory, not an assumed world axis.
- More movement, displacement, contacts, or selected frames does not automatically mean a better semantic match.
- If the requested property is not visible or structurally supported, return `insufficient_evidence` rather than guessing.
- Static images do not prove temporal continuity, sliding, popping, or acceleration.

## 中文

当任务要求判断动画是否表达指定动作时使用本子 Skill。

### 使用流程

1. 将请求转换成可观察的动作标准。
2. 在可比较的条件下取得每个动画的分析证据。
3. 按时间顺序检查视觉证据，并将每项观察映射到正确的动画。
4. 先判断指定动作与阶段，再按需检查方向、路径、身体状态、接触和结束证据。
5. 返回 `match`、`not_match` 或 `insufficient_evidence`，并给出简洁依据。

### 提示词结构

不添加请求未说明的细节，按以下结构描述目标：

```text
动作 + 阶段 + 方向/路径 + 身体/接触状态 + 结束或循环条件 + 相关风格
```

判断动画前，先把描述转换成可见结果清单。

### 注意事项

- 不能把文件名、内部标签、候选顺序或运动幅度当作语义证明。
- 方向应结合角色前向与观察到的轨迹判断，不能假设世界轴。
- 动作更多、位移更大、接触更多或选帧更多，并不自动代表语义更匹配。
- 请求属性缺少可见或结构化支持时返回 `insufficient_evidence`，不能猜测。
- 静态图片不能证明时间连续性、滑步、跳变或加速度。
