# 开发文档导航

本目录面向包维护者和 AI Agent，不是普通使用者的入门文档。用户请先阅读根目录的 [`README.md`](../README.md) 或 [`README.zh-CN.md`](../README.zh-CN.md)。

## 文档职责

- [`AGENTS.md`](AGENTS.md)：仓库维护、协作和验证规则。
- [`DEVELOPMENT.md`](DEVELOPMENT.md)：当前命令面与产品边界的开发快照。
- [`plan.md`](plan.md)：文档与兼容性维护计划。
- [`KIMODO_CPP_INTEGRATION_TODO.md`](KIMODO_CPP_INTEGRATION_TODO.md)：原生后端接入 TODO。
- 根目录 [`SKILL.md`](../SKILL.md)：安装门槛、任务入口、能力工具编排和公共执行规则。
- [`tools/*.md`](../tools/)：公共规则，以及 Session、generation、recognition、comparison、pose 和派生输出等能力工具。
- [`Command/help.json`](../Command/help.json)：实时命令与参数 schema。

## 交接说明

不再维护独立的 handoff 文件。阅读顺序为：

1. 根目录 README，确认产品范围和用户/开发者入口；
2. 根目录 `SKILL.md`，执行安装门槛并选择能力工具；
3. 对应的 `tools/*.md`，执行具体任务流程；
4. `Command/help.json`，在命令调用边界检查参数、返回值和错误结构；
5. 本目录中的维护规则、开发快照和计划。

修改命令或执行规则时，更新对应归属文档，并运行静态审计与 `git diff --check`。
