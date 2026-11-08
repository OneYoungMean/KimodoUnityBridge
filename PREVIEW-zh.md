# KimodoUnityBridge 文档中文预览

> 这是根 `SKILL.md` 和 `tools/common.md` 的中文审阅稿，不作为活动指令。正式工作流以英文原文和连接的 Unity 实时 schema 为准。

## Skill：通用动画循环

当用户要在安装了 KimodoUnityBridge 的 Unity 项目中创建、续接、重定向、检查、比较或改进动画时，使用根 Skill。它面向 Humanoid 角色和简单 Transform 物体；复杂绑定、四足、非人形生物、车辆、物理和最终逐帧表演润色需要其他工具或动画师。

统一循环是：

```text
理解意图和场景
→ 拆分可独立检查的动作
→ 定义验收条件
→ 收集必要证据并构造约束
→ 生成候选
→ 分析、识别和比较
→ 只修正失败条件
→ 保留最佳派生结果，或交给人工
```

语义识别和动画比较都是循环内部的证据操作。根据用户意图和返回的分析证据检验语义；比较时保持条件一致并逐项判断标准。

## Common：共享规则

命令入口是 `KimodoUnityBridge.Command.command_dispatcher`。选择命令或参数前读取实时 schema 或 `kimodo.help`；检查顶层 `ok`，保存异步任务的准确 `request_id`，轮询到终态。使用 `session.context` 返回的角色、Clip、Timeline 和对象引用，不从显示名称猜句柄；Pose 槽位由 `pose.get` 返回。

视觉观察、结构化测量和播放观察分开报告。打开返回图片后作视觉判断；使用时序测量或播放检查连续性、速度、加速度、脚滑和跳变。缺失或冲突的证据标记为未知。已完成 Clip 保留，修正产生派生输出。

Kimodo 命令用于生成和检查 Humanoid Clip、采样和修改生成 Pose、施加 Root／末端约束、重定向和捕获 Transform。道具曲线、Timeline 轨道和手部绑定可通过适合的 Unity／项目 API 制作，并在集成后的场景中验证。
