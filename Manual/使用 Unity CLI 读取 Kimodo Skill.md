# 使用 Unity CLI 读取 Kimodo Skill

执行 Kimodo 动画相关任务前，定位 Kimodo 包根目录并完整读取 [SKILL.md](../SKILL.md)。需要中文对照时，同时读取 [SKILL-zh.md](../SKILL-zh.md)。Skill 是工具调用顺序、参数约束与验证标准的唯一来源。

1. 在目标 Unity 项目中定位 Kimodo 包根目录；Git 包、本地包和完整示例项目的路径可以不同。
2. 读取根目录 SKILL.md 全文，并按其安装与手动基础生成验证流程准备项目与运行环境。
3. 使用 Skill 中的精确工具名和 JSON 对象；不要根据旧文档自行补充参数。
4. 先建立当前 Session、查询角色和模型，再启动生成；保存 request_id 并轮询到终态。
5. 需要 Timeline、分析或 Bake 时，继续遵循当前 Session 语义，不传 timeline_session_id。

本文件不定义任何动画工具实现或参数。当前工具集合与 Skill 不一致时，停止猜测并以 Tool Schema 为准。
