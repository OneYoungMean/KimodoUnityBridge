# KimodoUnityBridge 文档清理与自迭代计划

## 目标

让当前文档只描述可验证的命令、工作流和证据边界；把历史重构记录从维护面移除；让文档每轮都能用 `AnimationEval` 题库回归。

## 权威顺序

1. `Command/command_dispatcher.cs` 暴露的入口。
2. `GetCommandDefinitionsJson()` 的 live schema。
3. `kimodo_help`、返回的名称/ID/路径、状态和错误 envelope。
4. `AnimationEval/Assets/EvalBank` 的题库契约与 Unity 资产证据。
5. 当前文档。

文档不能覆盖实时 schema；不确定的参数只链接到 schema/help，不复制猜测性的完整结构。

## 当前文档状态

| 文档 | 状态 | 处理 |
|---|---|---|
| `AGENTS.md` | 当前规则 | 保留；只规定入口、所有权、验证和边界 |
| `README.md` | 入门说明 | 保留；不作为 API 契约 |
| `SKILL.md` | AI 入口 | 维护安装、任务路由、产品边界和 API Help 入口 |
| `Command/help.json` | API Help | 维护命令、参数、必选关系和嵌套 schema |
| `skills/recognize.md` | 当前专题 | 维护识别 API 链、视觉提示词、证据宏与决策程序 |
| `skills/compare.md` | 当前专题 | 维护比较 API 链、视觉提示词、标准宏与决策程序 |
| `skills/generate.md` | 当前专题 | 维护生成 API 链、意图/证据宏、约束、验证与修正程序 |
| `DEVELOPMENT.md` | 临时快照 | 允许短期记录；不得取代 `SKILL.md` 或 API Help |
| `COMMAND_BOUNDARY_TEST_PLAN.md` | 测试计划 | 保留为命令边界回归入口，避免重复写进操作契约 |
| `SAMPLE_RESULT_70D_REWRITE_*` | 历史 | 从根目录移除；历史版本由 git history 保留 |

## 题库现状

- `suite-a`、`suite-b`、`suite-c` 都存在，不能只引用 C。
- A：128 道当前单选语义识别题；public 有题目/候选/图片路径，private 额外保存正确答案。
- B：128 道质量对比题；当前 public/private JSON 内容相同，包含 low/high 配对元数据，不能把它描述成已匿名且有隐藏评分。真正匿名化和评分应由外部 evaluator/harness 持有。
- C：12 道生成/生产流程题；public 是执行题，private 是评测表模板。Foot IK/Raycast 等能力只能在有公开命令和 fixture 证据时写成已实现，否则标作目标或边界。
- `EvalBank/public` 与 `EvalBank/private` 顶层目录目前为空；有效数据在各 suite 子目录。
- A/B 的图片路径指向 `EvalOutput`，当前不是 128 题素材全齐；没有图片时不能报告视觉通过。

## 过时、兼容与冗余审计

### 过时内容

- 旧的 `pose_get` “创建或复用”表述：当前实现创建新的 External Pose slot。
- 只写 Humanoid 的分析描述：当前 `session_add(kind:"character")` 和 `animation_analyze` 支持可渲染 Mesh 的 Mesh-only 路径。
- 漏掉的命令：`session_close`、`pose_set_root_transform`、`pose_set_muscle`、`kimodo_cancel_generation`、`kimodo_record_range`、`kimodo_retarget_animation`。（旧的独立路径创建命令已移除，轨迹改由分析返回。）
- 漏掉的生成参数/边界：`output_mode`、`text_encoder_model`、`analysis_option`、`seed`、`diffusion_steps`、`resolution`、loop 超限回退和 `ignore_warning`。
- 把 transition 说成 Bake 资产、把 Foot IK/Raycast 题库目标说成已实现命令。

### 兼容层/边界

- `session_add(kind:"animator")` 只物化支持的同 Layer State-to-State transition；Any State、Entry、Exit、StateMachine、OverrideController 会 skipped。
- 已完成 Session Clip 不可变；修正、Record、Retarget 和生成都追加新 Clip。
- 当前所有 `output_mode` 都要求目标是带有效 Avatar 的场景 Humanoid Animator；`model_bone` 只改变输出表示，不启用 Mesh-only 生成。生成模型仍需有效 Humanoid origin Avatar。
- Mesh-only 分析不能提供 Humanoid 脚接触语义，且当前没有公开 Pose/Compare/Generation 工作流；静态 PNG 不能证明时间连续性。

### 冗余内容

- 重复的执行契约和旧专题已移除；流程由三个任务子 Skill 维护，具体 API 只由 `Command/help.json` 维护。
- `DEVELOPMENT.md` 不再复制完整执行契约，只保留临时命令面/边界快照。
- 70D 重构计划和 checkpoints 不再作为当前文档维护，移除后由 git history 保留。

## 代码清理候选（审计，不在本轮删除）

| 候选 | 已确认事实 | 清理前置条件 | 建议处理 |
|---|---|---|---|
| Mesh-only 命令边界 | `session_add` 和 `animation_analyze` 支持可渲染 Mesh；生成、Pose 采样和 `animation_compare` 仍要求有效 Humanoid Avatar | 明确产品目标：Mesh 只分析，或支持完整生成/编辑 | P0：保持当前文档边界；后续要么在 schema 返回能力标志，要么先实现完整 Mesh 工作流，再放宽文档 |
| `root2DOverride` 别名 | `KimodoMarkerSampleResult` 已有 canonical `rootOverride`，但 `root2DOverride` 仍被命令、编辑器和测试广泛调用，并承担序列化/源码兼容 | 全仓库调用迁移；旧资产反序列化回归；至少一个发布周期的兼容策略 | P1：先统一新代码到 `rootOverride`，保留 `[FormerlySerializedAs]` 和只读/兼容层；不要直接删除 |
| Legacy Spline Path 迁移 | `KimodoPlayableSplinePathUtility` 会将旧场景对象迁移到 Clip 的 spline 数据，并在迁移后删除旧对象 | 搜索/迁移真实旧资产；建立迁移前后回归场景；确认支持窗口结束 | P1：保留自动迁移；增加一次性迁移报告。达到退出条件后才移除旧组件/迁移代码 |
| 旧 QuickServer `.venv` 与脚本 | runtime 安装会迁移 `kimodo/.venv`；启动器仍会检测并明确拒绝旧离线脚本 | 覆盖旧安装升级、Windows/macOS/Linux 启动和回滚路径 | P1：保留迁移和检测。发布说明宣布支持窗口结束后，再单独删除，不和文档清理混做 |
| `animation_compare` 的名称与能力 | 当前只返回 Root/yaw、mean-muscle、末端差异；contact compatibility 固定为 false | 决定是否实现接触/相位比较 | P2：短期维持文档边界；长期可增加明确 `capabilities` 字段或实现可验证的 contact comparison |
| EvalBank B 的“匿名”重复数据 | public/private JSON 当前相同，且暴露 low/high、clip、seed、step 元数据 | 外部 harness 持有隐藏标签与评分键 | P0：不要把 B 当盲测；先做脱敏/随机化 harness，再考虑删除重复 private 文件 |

这些候选都不是“见到 legacy 就删”的任务。任何兼容层的删除都必须先证明：旧资产已迁移、升级路径可回归、且版本支持窗口已结束。

## 清理阶段

### Phase 1 — 已完成：契约校准

- 更新 `AGENTS.md` 的文档所有权和验证规则。
- 重写 `SKILL.md` 的安装、路由、抽象边界和 API Help 入口。
- 将专题拆分为 recognize、compare、generate，并从中移除具体 API 结构。
- 降级 `DEVELOPMENT.md`，明确它不是契约。

### Phase 2 — 当前：历史与孤立文件清理

- 删除根目录 `SAMPLE_RESULT_70D_REWRITE_PLAN.md` 及其 `.meta`。
- 删除根目录 `SAMPLE_RESULT_70D_REWRITE_CHECKPOINTS.md` 及其 `.meta`。
- 修正 `AnimationEval/Assets/EvalBank` 的总览与评测规则：A 当前为单选，B 尚未盲测，C 的 Foot IK/Raycast 是条件能力边界。
- 不删除题库、不改 A/B JSON、不改 C 的题目/评分内容，也不改运行时兼容代码。

### Phase 3 — 命令边界回归

- 对 `GetCommandDefinitionsJson()` 做命令名/必填字段静态快照。
- 用 `kimodo_help` 验证 commands/models/constraints 三个 section。
- 至少覆盖：Session 创建/加入角色、Pose/Path、生成轮询终态、分析 PNG、Clip 追加不可变性。
- 题库 C 只把公开命令能完成的部分标为已验证；其余标 `blocked` 或 `not_verified`。

### Phase 4 — 题库驱动自迭代

1. 从 A/B/C 读取一个最小代表题。
2. 将题目中的命令/证据要求与 live schema 对照。
3. 在 `AnimationEvalScene`、`CommandApiProbe`、`EvaluationCharacter` 和现有输出资产上跑最小检查。
4. 记录 `passed`、`needs_revision`、`blocked` 或 `not_verified`。
5. 只有证据通过才修改契约文档；若能力不存在，更新边界而不是伪造支持。

### Phase 5 — 兼容层收敛（后续代码任务）

1. 先为每个候选写一个最小迁移/升级回归：旧序列化字段、旧 Spline、旧 `.venv`、旧启动脚本。
2. 用真实项目/资产扫描确认仍有消费者，再决定保留窗口。
3. 先把新写入路径收敛为 canonical 名称，再保留只读或迁移兼容层一个发布周期。
4. 在 release note 中标注最后支持版本；下一次主版本再删除兼容代码和测试。
5. 每次删除后验证 live schema、Unity 导入、旧资产迁移、QuickServer 启动和 A/B/C 最小题库路径。

## 完成标准

- 根目录没有 70D 历史文档副本。
- 当前文档不再引用已删除命令、已不存在的题库路径或未实现的 Foot IK/Raycast 命令。
- live schema、`kimodo_help`、文档和 A/B/C 题库对同一能力的描述一致。
- 每个声称 `passed` 的视觉结论都有实际打开的 PNG；时间性质没有播放证据时保持 `not_verified`。
