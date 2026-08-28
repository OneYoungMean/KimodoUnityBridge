# Generate

Use this sub-skill when the task asks for a new animation or a corrected derived result.

## Workflow

1. Translate the requested outcome into motion intent: action, phase, direction/path, speed or energy, body/contact state, and ending or loop condition.
2. If a source or current result exists, inspect it before deciding what must change. Pure text-to-motion has no source evidence.
3. Select only the operations and constraints required by the request; clarify any missing target, range, or pose that would materially change the result.
4. Construct the generation prompt and start the supported generation workflow described by the API help.
5. Wait for a terminal result and retain warnings, fallbacks, and returned output identity.
6. Inspect the generated result against the requested outcome. Create another derived result only when evidence shows a correctable failure.

## Prompt structure

```text
Start state + main action + phase + direction/path + speed/energy
+ body/contact requirements + ending or loop condition + relevant style
```

Preserve preparation → main action → recovery/end. When the input is shorthand, keep only semantics established by the request or visible context; do not expand unknown abbreviations.

## Cautions

- Do not invent a source, target character, range, pose, path, contact, or constraint.
- Analysis-selected frames are evidence; they are not automatically generation constraints.
- A loop request does not prove the generated result is seamless. Inspect available boundary evidence and mark playback-only properties as unverified when necessary.
- Preserve completed animation results and create corrections as derived outputs.
- A failed, canceled, or fallback result must be reported as returned; do not claim an option or visual result succeeded without evidence.
- Use the API help for exact parameters, conditional requirements, and supported capabilities instead of copying examples from memory.

## 中文

当任务要求创建新动画或修正后的派生结果时使用本子 Skill。

### 使用流程

1. 将请求结果转换成动作意图：动作、阶段、方向/路径、速度或能量、身体/接触状态，以及结束或循环条件。
2. 存在源结果或当前结果时，先检查它再决定需要修改什么；纯文本生成没有源证据。
3. 只选择请求所需的操作和约束；缺少目标、范围或姿势且会实质改变结果时，应先澄清。
4. 构造生成提示词，并按照 API Help 启动当前支持的生成流程。
5. 等待终态，并保留 warning、fallback 和返回的输出身份。
6. 将生成结果与请求目标对照；只有证据显示存在可修正问题时，才继续创建派生结果。

### 提示词结构

```text
起始状态 + 主动作 + 阶段 + 方向/路径 + 速度/能量
+ 身体/接触要求 + 结束或循环条件 + 相关风格
```

保留“准备 → 主动作 → 恢复/结束”的结构。输入是缩写时，只保留请求或可见上下文能够确认的语义，不能猜测未知缩写。

### 注意事项

- 不能臆造源、目标角色、范围、姿势、路径、接触或约束。
- 分析选择的帧属于证据，不会自动成为生成约束。
- 循环请求不能证明生成结果已经无缝；应检查可用边界证据，必要时将只能通过播放确认的属性标为未验证。
- 保留已经完成的动画结果，修正时创建派生输出。
- 失败、取消或回退结果必须按实际返回报告；没有证据时不能声称某个选项或视觉结果成功。
- 精确参数、条件必选关系和支持能力以 API Help 为准，不能凭记忆复制示例。
