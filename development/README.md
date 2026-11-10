# 开发文档导航

本目录面向包维护者和 AI Agent，不是普通使用者的入门文档。用户请先阅读根目录的 [`README.md`](../README.md) 或 [`README.zh-CN.md`](../README.zh-CN.md)。

## 文档职责

- [`AGENTS.md`](AGENTS.md)：仓库维护、协作和验证规则。
- [`DEVELOPMENT.md`](DEVELOPMENT.md)：当前命令面和产品边界的开发快照；[后端生成编排](DEVELOPMENT.md#backend-owned-generation-2026-11-09)包含阶段职责、TCP 请求、KMB 附件、Loop 帧范围和 Debug 返回/写入说明。
- [`plan.md`](plan.md)：文档与兼容性维护计划，包含当前 Constraint 输入、类型推断与协议折叠边界。
- [`animation-workflow-test-cases.md`](animation-workflow-test-cases.md)：通用动画循环交互式验收测试案例。
- [`KIMODO_CPP_INTEGRATION_TODO.md`](KIMODO_CPP_INTEGRATION_TODO.md)：原生后端接入 TODO。
- 根目录 [`SKILL.md`](../SKILL.md)：用途、安装和文档导航。
- [`tools/common.md`](../tools/common.md)：范围、任务分流、能力编排、循环状态、命令、证据、识别、比较、修订和人工交接规则。
- [`Command/help.json`](../Command/help.json)：实时命令与参数 schema。
- [`archive/`](../archive/)：旧四阶段执行文档和历史能力手册，仅供追溯。

## 交接说明

执行任务时从根目录 `SKILL.md` 开始，读取 `tools/common.md`，再按任务需要查询 `Command/help.json` 或实时 `kimodo.help`。归档材料不作为当前工作流指令或新实现的范例。Constraint 相关工作先读 [当前契约](plan.md#当前-constraint-契约)，不要从历史 composer、carry 或旧 mask 测试恢复已废弃语义。

修改命令或执行规则时，更新其归属文档，并运行静态链接/命令审计与 `git diff --check`。
