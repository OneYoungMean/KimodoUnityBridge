# Pickup Apple Generation Handoff

Copy the following context to the next GPT:

```text
你接手的是 C:\nvlab\KimodoUnityBridge 的 Unity 动画任务。请继续完成“角色走到苹果前、停下、抓取苹果”的流程。

先读取：
- C:\nvlab\KimodoUnityBridge\AGENTS.md
- C:\nvlab\KimodoUnityBridge\development\AGENTS.md
- C:\nvlab\.agents\skills\kimodo-unity-bridge\SKILL.md
- C:\Users\47694\.codex\plugins\cache\personal\unity\0.1.3-beta\skills\unity-cli\SKILL.md

工程规则：
- 不要使用 remove、删除或永久清理；需要移除的文件/资产移动到 archive。
- 不要提交或推送 Git。
- 保留当前工作区已有改动，不要 reset、checkout 或覆盖无关修改。
- Unity Editor 场景/资产修改通过 Unity CLI/Pipeline 完成，不要直接手写 Unity YAML。
- 先运行 git status --short 和 git diff --stat。

当前 Unity：
- 版本：6000.5.7f1
- 项目：C:\tmp\KimodoUnityBridge_FullDemo-main
- 先运行：unity status --format json --no-banner
- 再检查活动 Scene、Director、Animator、Timeline track、角色和苹果对象。
- 不要假设之前实验留下的 Director/Scene/Timeline 状态是干净的。

本阶段明确暂停：
- 不要继续修改 animation_analyze 的协议大修。
- 不要处理 clips[].role、pose_get.timeline_time_seconds、旧 keyframe 参数兼容等问题。
- 这些事项已经记录在 C:\nvlab\KimodoUnityBridge\development\plan.md 的“暂缓 TODO：Analysis / Pose 协议整理”中。
- 本任务只使用现有稳定 generation 链；如必须调用当前 animation_analyze，只读取结果，不修改其协议实现。

任务语义：
1. 角色从当前场景位置走到苹果桌前锚点。
2. 在苹果前停下并保持站定。
3. 后续生成抓取动画：使用 A 的末尾姿势衔接，角色用右手抓取苹果。
4. 已知起点、终点和到达时间，因此不要使用 PathAngle。
5. 只使用 Root2D 位置和 heading 约束；禁止从 prompt 自动推断 PathAngle。
6. 右手 effector 约在抓取动画开始 1 秒附近；右手位置使用苹果世界坐标；没有明确要求时不要传右手 rotation；只启用右手位置约束。
7. 角色复制保持原位置；Character Track 使用 ApplySceneOffsets；必须检查 scene offset 是否重复应用。

已有结果位于：
C:\tmp\KimodoUnityBridge_FullDemo-main\PickupExecution_20260921

已有 Clip：
- Assets/KimodoGeneratedClips/ApplePickup_20260921/Kimodo_ApplePickup_A_WalkStop.anim
- Assets/KimodoGeneratedClips/ApplePickup_20260921/Kimodo_ApplePickup_A_WalkStop_Corrected.anim
- Assets/KimodoGeneratedClips/ApplePickup_20260921/Kimodo_ApplePickup_A_Root2D_Stop.anim
- Assets/KimodoGeneratedClips/ApplePickup_20260921/Kimodo_ApplePickup_A_Root2D_Stand.anim

结果判断：
- ApplePickup_A_WalkStop：错误使用隐式 start_angle=90/end_angle=90，角色向右急转，不要继续使用。
- ApplePickup_A_WalkStop_Corrected：急转消失，但路径持续推进，末尾没有稳定停步，不作为最终 A。
- ApplePickup_A_Root2D_Stop：无 PathAngle，Root2D 起点/到达/末尾保持约束，是优先验证的 A。
- ApplePickup_A_Root2D_Stand：无 PathAngle，末尾 Timeline 骨骼位置已接近苹果位置，但还没有完成完整视觉和 Play Mode 验收。

A 的已知 Root2D 约束（60 FPS command frame）：
- frame 0 -> model frame 0
- frame 510 -> model frame 255
- frame 575 -> model frame 287
- 起点 XZ 约为 (-3.02651, 2.62533)
- 到达/末尾 XZ 约为 (-3.16253, 12.31)
- 末尾必须保持同一 Root2D，形成停步区间。

之前 A_Root2D_Stand 直接 Timeline Evaluate 的参考位置约为：
- Hips (-3.167293, 0.879240334, 12.3662977)
- RightHand (-2.99687934, 0.889414549, 12.49139)
- LeftFoot (-3.259979, 0.1477412, 12.3982887)
- RightFoot (-3.14291286, 0.09808248, 12.2242908)

当前最重要的技术问题是采样坐标一致性：
- pose_get 的 Clip-local frame/time
- Timeline absolute time
- Character world pose
- Track/scene offset
- Kimodo model/profile constraint space
- generated AnimationClip playback space

不要只看 .anim 的局部 RootT。必须同时比较：
- 直接 PlayableDirector.Evaluate() 后 Animator.GetBoneTransform() 的世界位置
- pose_get 返回的 pose
- transform_capture 的世界位置
- 生成 Clip 在 Timeline 上播放时的世界位置
- TrackOffset、Animator.applyRootMotion 和 root 曲线

验证顺序：
1. 检查当前 git diff，确认没有隐式 PathAngle 推断重新出现。
2. 重新编译 Unity 项目。
3. 优先验证 A_Root2D_Stand/A_Root2D_Stop 的多个本地采样点：0、480、510、545、575（按当前命令协议换算为 Clip-local 秒，不要擅自使用 Timeline-global pose_get 协议）。
4. 将 pose_get、transform_capture、直接 Timeline Evaluate 的 hips/right hand/left foot/right foot 世界坐标比较。
5. A 的 generation completed、asset connected、static analysis、pose/capture、Play Mode、visual accepted 必须分别报告。
6. A 通过后，再生成 B：使用 A 末尾作为 In 约束，右手位置约束指向苹果世界坐标，不传右手 rotation。
7. B 通过后，再设置苹果 ParentConstraint：抓取前苹果在桌上，抓取时切换到右手，确保桌上和手上不会同时出现；验证停止、拖动、重播和重新打开场景。

不要继续扩大 prompt 试错。优先修复和验证坐标空间、Clip-local 时间、Track/scene offset 和实际 Timeline 播放结果。
```
