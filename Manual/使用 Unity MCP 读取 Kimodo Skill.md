# 使用 Unity MCP 读取 Kimodo Skill

开始 Kimodo 动画任务前，完整读取包根目录的 [SKILL.md](../SKILL.md)。需要中文对照时，同时读取 [SKILL-zh.md](../SKILL-zh.md)。这两份文件是角色、Session、生成、分析与 Bake 的唯一操作规范。

1. 确认 Unity 项目已引用 Kimodo 包，并解析该包根目录。
2. 将 SKILL.md 全文加载到当前任务上下文；不要只读取一个章节。
3. 首次使用时，完整执行 Skill 的安装与手动基础生成验证。
4. 只调用 Skill 中列出的 Kimodo 工具名和 JSON 参数；先查询模型和当前 Session，再开始生成。
5. 结束时依照 Skill 的 Session 关闭或卸载规则处理临时 Timeline。

不要把本入口页当作 API 参考，也不要传递旧工作流的 timeline_session_id、外部模型路径或 Project 角色资产。
