# 开发文档导航

本目录面向包维护者和 AI Agent，不是普通使用者的入门文档。用户请先阅读根目录的 [`README.md`](../README.md) 或 [`README.zh-CN.md`](../README.zh-CN.md)。

## 文档职责

- [`AGENTS.md`](AGENTS.md)：仓库维护、协作和验证规则。
- [`DEVELOPMENT.md`](DEVELOPMENT.md)：当前命令面与产品边界的开发快照。
- [`plan.md`](plan.md)：文档与兼容性维护计划。
- [`KIMODO_CPP_INTEGRATION_TODO.md`](KIMODO_CPP_INTEGRATION_TODO.md)：原生后端接入 TODO。
- 根目录 [`SKILL.md`](../SKILL.md) 与 [`skills/*.md`](../skills/)：动画 Agent 的执行规则和任务子流程。
- [`Command/help.json`](../Command/help.json)：实时命令与参数 schema。

## 交接说明

不再维护独立的 handoff 文件。阅读顺序为：

1. 根目录 README，确认产品范围和用户/开发者入口；
2. 根目录 `SKILL.md`，确认 Agent 执行规则；
3. `skills/*.md`，按任务读取子流程；
4. `Command/help.json`，确认实时命令、参数、返回值和错误结构；
5. 本目录中的维护规则、开发快照和计划。

实时 schema、运行时返回值和错误信息优先于叙述性文档。修改命令或执行规则时，更新归属文档并运行静态审计与 `git diff --check`。
