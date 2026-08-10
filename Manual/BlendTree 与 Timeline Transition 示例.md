# BlendTree 与 Timeline Transition 示例

本例的边界是：Unity 负责创建 BlendTree；Kimodo 导入其中的候选 AnimationClip，但不替 AI 选择 BlendTree 分支；AI 使用所在平台的 Timeline API 明确制作需要的过渡。

## 创建 BlendTree

通过 Unity Editor API（可以由 Unity Pipeline 的 `eval` 执行）创建 Controller、参数和 BlendTree：

```csharp
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

string path = "Assets/AIExamples/Locomotion.controller";
AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);
controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
AnimatorStateMachine machine = controller.layers[0].stateMachine;
BlendTree tree = new BlendTree { name = "Locomotion", blendParameter = "Speed" };
AssetDatabase.AddObjectToAsset(tree, controller);
AnimatorState locomotion = machine.AddState("Locomotion");
locomotion.motion = tree;
tree.AddChild(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Idle.anim"), 0f);
tree.AddChild(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Walk.anim"), 1f);
tree.AddChild(AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Run.anim"), 2f);
EditorUtility.SetDirty(controller);
AssetDatabase.SaveAssets();
```

将 Controller 赋给场景 Animator 后导入：

```json
session_try_add({
  "kind":"animator",
  "character":"Alice",
  "animator":"Actors/BlendTreeSource"
})
```

`animations` 会返回 BlendTree 的具体候选。涉及 BlendTree 的 Transition 不会自动生成；`skipped` 会给出 `from_candidates`、`to_candidates` 和 Timeline 操作提示。

## 选择并制作 Transition

```json
query_current_session({"query":"character_animations","character":"Alice"})
```

假设 AI 选择 `Walk → Run`，它应使用所在 MCP/CLI 平台的 Timeline API：

1. 通过 Session 名称查找 TimelineAsset 和角色主 AnimationTrack。
2. 把 Walk 和 Run 放到需要的全局帧并重叠，例如 18 帧。
3. 设置 Walk ease-out 与 Run ease-in 为 18 帧。
4. Bake 重叠区间，再 Analyze 和截图确认。

```text
walk.start_frame = 300
walk.duration_frames = 90
run.start_frame = 372
run.duration_frames = 120
walk.ease_out_frames = 18
run.ease_in_frames = 18
```

```json
kimodo_bake_range({
  "character":"Alice",
  "start_frame":300,
  "end_frame":492,
  "name":"WalkToRun"
})
```

```json
kimodo_analyze({"character":"Alice","animation":"WalkToRun"})
```

```json
query_picture({"analysis_id":"<analysis_id>"})
```

如果存在多组候选，AI 可以分别构造、Bake、Analyze 和截图，再保留语义最符合的版本。Kimodo 不决定 BlendTree 参数，也不自动枚举全部组合。
