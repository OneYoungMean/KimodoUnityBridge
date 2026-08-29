# KimodoUnityBridge 文档与兼容性维护计划

## 目标

让维护文档只描述可验证的产品能力、命令边界、工作流和证据限制；具体命令与参数始终以实时 schema 和 `Command/help.json` 为准。

## 权威顺序

1. `Command/command_dispatcher.cs` 暴露的入口。
2. `GetCommandDefinitionsJson()` 返回的实时 schema。
3. `kimodo_help`、运行时返回值、状态和错误 envelope。
4. 当前 Unity 项目和包内样例资产的实际结果。
5. 维护文档。

文档不应复制猜测性的完整参数结构，也不应把验证脚本或临时工具作为产品依赖。

## 文档职责

| 文档 | 职责 |
|---|---|
| `development/AGENTS.md` | 仓库协作规则、文档所有权和验证要求 |
| `../README.md` | 人类用户的安装与入门说明 |
| `../README.zh-CN.md` | `README.md` 的简体中文版本 |
| `../SKILL.md` | AI 入口、安装状态、任务路由、产品边界和 API Help 入口 |
| `../Command/help.json` | 维护命令、参数、必选关系和嵌套 schema |
| `../skills/recognize.md` | 语义动作识别工作流和证据决策 |
| `../skills/compare.md` | 动画质量比较工作流和证据决策 |
| `../skills/generate.md` | 动画生成、验证、约束和派生修正工作流 |
| `DEVELOPMENT.md` | 临时开发快照，不是执行契约 |
| `README.md` | 面向维护者的文档导航页 |

## 当前产品边界

- `session_add(kind:"character")` 和 `animation_analyze` 支持可渲染 Mesh 的分析路径。
- 生成、Pose 采样和 `animation_compare` 仍要求有效 Humanoid Avatar。
- 已完成 Session Clip 不可变；修正、Record、Retarget 和生成都会追加派生 Clip。
- `animation_compare` 只比较显式区间的结构化差异，不等同于语义识别或质量评分。
- 静态图片不能单独证明播放连续性、滑步、跳变、加速度或速度连续性。
- Foot IK、Raycast 或其他外部工作流只有在公开命令和项目资产实际支持时才能写成已实现能力。

## 兼容性维护

- 保留 `root2DOverride` 等旧序列化字段的兼容层，直到旧资产迁移和发布支持窗口完成。
- 保留旧 Spline 对象迁移逻辑，直到真实旧资产完成迁移回归。
- 保留 QuickServer 旧 `.venv` 检测与迁移，直到升级和回滚路径验证完成。
- 新代码优先使用 canonical 字段和实时 schema；兼容别名不得继续扩散。

## 验证要求

1. 静态检查文档中的命令名、路径和旧术语。
2. 在维护的 Unity 项目中完成最小导入/编译检查。
3. 按需验证 Session、分析图片、Pose/Path、生成终态和派生 Clip 不可变性。
4. 分开报告静态、运行时、图像、场景和播放证据。
5. 只有实际打开返回的视觉证据后，才能报告视觉通过；证据不足时使用 `not_verified`、`needs_revision` 或 `unsupported`。

## 完成标准

- 产品仓库不引用包外的验证夹具、评分键或监督路径。
- 文档、实时 schema 和运行时返回值对同一能力的描述一致。
- 不把兼容层或实验性功能描述成稳定产品能力。
- 每个视觉结论都能追溯到实际打开的返回证据。
