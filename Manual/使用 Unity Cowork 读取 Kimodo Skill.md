# 使用 Unity Cowork 读取 Kimodo Skill

让 Unity Cowork 执行 Kimodo 动画任务前，向当前任务提供包根目录的完整 [SKILL.md](../SKILL.md)。需要中文对照时同时提供 [SKILL-zh.md](../SKILL-zh.md)。Skill 是唯一的调用流程与约束来源。

1. 让当前任务能够访问目标 Unity 项目与 Kimodo 包根目录。
2. 明确要求完整读取 SKILL.md 后再执行安装、生成、Timeline 或 Bake 操作。
3. 首次使用时，严格执行 Skill 的安装与手动基础生成验证：准备运行环境、打开含 Humanoid Animator 的场景，并显式完成一次基础生成。
4. 之后只使用 Skill 中列出的工具、参数和当前 Session 规则；先查询、后修改，异步生成后轮询结果。
5. 不需要临时 Timeline 时，按 Skill 的关闭或卸载规则处理，不删除生成出的动画资产。

不要在本文件中推断工具参数，也不要复用旧工作流中的 timeline_session_id、外部模型路径或 Project 角色资产。
