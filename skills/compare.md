# Compare

Use this sub-skill when the task asks which of two animations has higher relative quality.

## Workflow

1. Identify the comparison goal and any motion semantics explicitly supplied by the request.
2. Obtain comparable evidence for both animations and preserve their mapping throughout the comparison.
3. Select criteria that matter for the visible or stated motion, such as pose continuity, coherent trajectory, plausible contacts, body control, and a suitable ending or loop boundary.
4. Weigh conflicting evidence without allowing one metric to decide the entire result.
5. Choose one animation only when the evidence supports a reliable distinction; otherwise return `insufficient_evidence`.

## Prompt structure

```text
Compare candidate 1 and candidate 2 for: [relevant quality criteria].
Use only returned visual and structured evidence.
State the stronger candidate, supporting differences, and unverified properties.
```

When no target action is supplied, compare internal quality without inventing one.

## Cautions

- Apply the same evidence conditions and criteria to both animations.
- Do not use universal thresholds for displacement, contacts, selected frames, or any other single metric.
- Missing evidence is `not_verified`, not an automatic defect.
- Numerical comparison can support a judgment but cannot replace inspected visual evidence.
- Static evidence cannot establish playback-only qualities such as sliding, popping, acceleration, or velocity continuity.

## 中文

当任务要求比较两个动画的相对质量时使用本子 Skill。

### 使用流程

1. 确认比较目标，以及请求明确提供的动作语义。
2. 为两个动画取得可比较的证据，并在整个比较过程中保持对应关系。
3. 选择与可见或指定动作有关的标准，例如姿态连续性、轨迹连贯性、合理接触、身体控制和合适的结束或循环边界。
4. 综合冲突证据，不能让单一指标决定总体结果。
5. 只有证据支持可靠区分时才选择一个动画，否则返回 `insufficient_evidence`。

### 提示词结构

```text
按照以下相关质量标准比较候选 1 和候选 2：[质量标准]。
只使用返回的视觉和结构化证据。
说明更优候选、支持差异和无法验证的属性。
```

请求没有提供目标动作时，只比较内部质量，不能凭空添加动作目标。

### 注意事项

- 两个动画必须采用相同的证据条件和判断标准。
- 不要对位移、接触、选帧或其他单一指标使用通用阈值。
- 缺少证据表示 `not_verified`，不能自动判为缺陷。
- 数值比较可以支持判断，但不能替代实际检查过的视觉证据。
- 静态证据不能确认滑步、跳变、加速度或速度连续性等播放属性。
