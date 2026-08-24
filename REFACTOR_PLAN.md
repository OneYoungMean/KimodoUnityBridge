# KimodoUnityBridge 代码整理计划（测试先行）

## 目标

先固定现有公开行为，再做一批可合并、可回滚的结构整理。所有重构必须先有能失败的测试；测试通过后才允许删除旧实现或兼容入口。

本批次只处理两条主线：

1. Command：命令定义、帮助和路由的重复维护。
2. Generation：生成结果的重复 DTO/Result 表示和字段搬运。

Session 运行时/持久化模型、`command_capture.cs` 图片渲染、`KimodoRuntimeMotionDriver`、Motion JSON 解析器合并、Constraint 全面收敛、BridgeService 拆分列为后续批次，不在本批次交叉修改。

## 当前基线与保护规则

- 基线提交：`4563553`（本计划提交会紧接其后）。
- 当前工作树已经有用户未提交修改；这些修改不属于本批次，不得覆盖、回滚或顺手提交。
- `Command/command_dispatcher.cs` 是公开入口；命令名称、schema、help、返回 envelope 和错误代码保持兼容。
- 现有 Unity 2022.3 asmdef 和 Newtonsoft JSON 依赖保持不变。
- 不删除兼容 facade，除非先有迁移测试并在本批次明确记录。

## 测试先行契约

测试 worktree 先提交测试和旧测试清理，测试应覆盖：

- `GetCommandDefinitionsJson()` 与 `Invoke()` 使用同一命令注册信息。
- 所有当前命令名称、必填字段、枚举边界和未知命令错误 envelope 保持稳定。
- `kimodo_help` 的 commands/models/constraints 分支保持稳定。
- Generation bridge result 到 pipeline result 的字段一一保留：motion JSON/bytes、format、status、message、fingerprint、seed、frame range、analysis、KMB attachments。
- 空 KMB、空 motion JSON、analysis-only attachment 等现有边界保持稳定。
- 旧 facade（`command_dispatcher`、`command_kimodo`、`command_session`）仍可调用。

测试 agent 可以删除仅重复旧契约、没有独立行为的测试；不得删除唯一覆盖错误边界、资产不可变性或协议兼容性的测试。新测试先按预期契约提交，允许在代码分支合并前失败。

## Worktree 分工

### `refactor/tests`

负责：

- 盘点现有 Editor Tests，补齐上述 characterization/contract tests。
- 清理重复或已经不表达当前 live schema 的老测试，并说明删除理由。
- 不修改 Runtime/Command 实现；测试需要的新类型只按计划中的契约引用，不能私自扩大 API。

交付：一个测试提交，包含测试文件和必要的测试说明；不得提交生成资产或用户文档改动。

### `refactor/command`

负责：

- 将命令定义、schema 生成、help 查找和 dispatch 的重复逻辑收敛到一个内部注册/路由实现。
- 保留现有公开 facade 和全部命令名；`command_context` 只保留兼容转发与业务调用，不改变 Session/analysis/pose 的行为。
- 不在本分支重写 Session 模型，不修改 `command_capture.cs` 的渲染实现。
- 通过测试分支提供的契约后，再删除同义的旧路由代码。

交付：一个可独立 cherry-pick/merge 的 Command 提交，附静态测试结果。

### `refactor/generation`

负责：

- 在不改变 wire request 字段的前提下，收敛 `KimodoBridgeGenerationResult`、`KimodoGenerationResultDto`、`KimodoBridgeCommandResult` 之间的重复结果字段。
- 保留 Editor 专用的 `KimodoEditorGenerationResult`，但只保存资产写回所需字段；统一 Runtime result 到 pipeline 的转换位置。
- 维持空结果、KMB、analysis、seed 和 frame range 的现有语义；不顺手重写 Motion JSON 解析器。
- 不修改 Command 路由，不修改 RuntimeMotionDriver 状态机。

交付：一个可独立 merge 的 Generation 提交，附结果映射/边界测试通过记录。

## 主分支合并顺序

1. 主 worktree 提交本文件，作为唯一基线提交。
2. 从该提交创建三个独立 worktree 和对应分支。
3. 先合并 `refactor/tests`，运行测试；测试必须先红后绿，不能让实现分支绕过测试。
4. 合并 `refactor/command`，运行 Command boundary/schema 测试。
5. 合并 `refactor/generation`，运行 Generation result/pipeline 测试。
6. 主分支最后清理路由层：删除仅剩的重复 facade 内部转发，但保留公开 API 兼容入口。
7. 运行静态检查、可用 Unity Editor tests 和最小 command schema smoke check；记录哪些验证未能在当前环境执行。

任何冲突优先保留用户未提交改动；不得使用 `reset --hard` 或覆盖式 checkout 解决冲突。若主分支 dirty 状态阻止 merge，先创建可恢复的临时 stash，合并后立即恢复并检查 diff。

## 本批次完成标准

- Command schema、help、dispatch 只有一个内部事实来源。
- Generation result 不再有无意义的逐字段重复搬运；边界字段全部保留。
- 公开命令 facade 仍然可用。
- 新增/保留测试覆盖上述契约，且没有因为重构删除唯一行为测试。
- 所有合并提交可单独回滚；主分支没有夹带当前文档清理的未提交改动。

## 后续批次（不在本次实施）

1. 合并 Runtime/Editor Motion JSON parser 和坐标转换。
2. 合并 Session runtime/document model。
3. 拆分 `command_capture` evidence renderer。
4. 拆分 `KimodoRuntimeMotionDriver` generation/constraint/playback。
5. 收敛 Constraint mode/mask/transform 工具和 BridgeService 生命周期。
