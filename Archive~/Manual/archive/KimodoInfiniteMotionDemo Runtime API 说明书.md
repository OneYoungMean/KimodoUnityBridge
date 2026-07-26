# KimodoInfiniteMotionDemo Runtime API 说明书

这份说明书面向后续接手 `KimodoInfiniteMotionDemo` 的智能体或工程师。

目标文件：

- `Packages/KimodoUnityBridge/Runtime/Demo/KimodoInfiniteMotionDemo.cs`
- `Packages/KimodoUnityBridge/Runtime/Demo/KimodoInfiniteMotionDemo.Api.cs`
- `Packages/KimodoUnityBridge/Runtime/Demo/KimodoInfiniteMotionDemo.Ik.cs`

## 当前行为摘要

`KimodoInfiniteMotionDemo` 现在除了原有的启动、停止、重置、切角色和时长控制之外，还补了运行时 prompt / constraint API。

新增的运行时能力：

- 查询当前正在播放的 prompt
- 默认 prompt 归一到 `idle`
- 非锁定 prompt 在成功生成一段后会自动回到 `idle`
- 支持 `LockPrompt()` / `UnlockPrompt()`
- 支持向“下一段生成”插入一次性的手脚 / Root2D 约束
- 支持获取当前角色位置
- 上述 API 都有 `_Prompt` 字符串包装版本

## Prompt 语义

### 默认 prompt

- `defaultPrompt` 的运行时默认值改成了 `idle`
- 如果老场景里还序列化着旧默认值 `A person dancing with energetic rhythm.` 或旧 fallback `A person dancing.`，`Awake/OnEnable` 会自动把它归一成 `idle`

### SetPrompt

- `SetPrompt(string prompt)` 不再只是改一个字段
- 它会：
  - 更新 `promptDraft`
  - 清掉还没播放的 upcoming queue
  - 让正在进行中的旧生成结果失效
  - 如果当前没有生成进行中，就立刻用新 prompt 生成下一段

### 自动回到 idle

- 如果 `promptLocked == false`
- 某次生成成功入队后，`promptDraft` 会立刻重置回 `idle`
- 这意味着一个普通 `SetPrompt("wave")` 默认只影响“下一次成功生成的一段”

注意：

- 代码层面是在“生成成功入队”后回到 `idle`
- 不是在“这一段视觉上播完”后再回到 `idle`
- 这是因为当前 scheduler 会在播放期间提前生成下一段，这样才能保证 prompt 只作用一段

### Lock / Unlock

- `LockPrompt()` 会锁定 prompt，不再自动回到 `idle`
- 如果当前有正在播放的 segment，它优先锁定“当前正在播放的 prompt”
- 否则锁定当前 `ResolvePrompt()` 的结果
- `UnlockPrompt()` 会解除锁定，并把 `promptDraft` 设回 `idle`
- 这两个方法都会刷新 upcoming segment，让变化尽快生效

## 当前播放 prompt 查询

### `GetCurrentPrompt()`

返回规则：

- 如果当前 `RawMotionPlayer` 正在播 segment，返回该 segment 的 `PromptText`
- 否则返回当前 `ResolvePrompt()` 结果

对应字符串包装：

- `GetCurrentPrompt_Prompt()`

## 位置查询

### `GetPosition()`

返回 `Vector3`。

优先级：

- 当前激活角色的 `Hips` 世界坐标
- `RawMotionPlayer` 当前 profile root 坐标
- `profileSkeletonRoot.position`

对应字符串包装：

- `GetPosition_Prompt()`

返回格式：

- `"(x, y, z)"`

## 运行时约束插入

### 支持的方法

- `SetLeftHandConstraint(float x, float y, float z, float duration = 1f)`
- `SetRightHandConstraint(float x, float y, float z, float duration = 1f)`
- `SetLeftFootConstraint(float x, float y, float z, float duration = 1f)`
- `SetRightFootConstraint(float x, float y, float z, float duration = 1f)`
- `SetRoot2D(float x, float z, float duration = 1f)`

它们都有 `_Prompt` 包装版本：

- `SetLeftHandConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRightHandConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetLeftFootConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRightFootConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRoot2D_Prompt(string x, string z, string duration = "1")`

### duration 的含义

- `duration` 代表“约束插入到下一段生成里的时间点（秒）”
- 会被 clamp 到 `[0, 当前 segment 时长]`

### 约束如何生成

#### Hand / Foot 约束

当前实现不是单独做一套新 IK 求解。

而是：

1. 从 `profileSkeletonRoot` 抓取当前 pose sample
2. 保持当前局部关节旋转不变
3. 计算“目标 end-effector 位置”和“当前该关节位置”的差值
4. 把 sample 的 root 整体平移这段 offset
5. 把它作为 `left-hand/right-hand/left-foot/right-foot` constraint 插入下一段

这意味着：

- 能快速插入一个可用 constraint
- 选中的手/脚目标点会对上
- 但这不是单独拉手臂/腿去追点的专用 IK
- 更像“当前姿态整体平移后，在指定时刻约束该 end-effector”

如果后面要升级成更强的约束编辑：

- 推荐在插入 constraint 前先做一层运行时 IK 或 pose edit
- 再从编辑后的 profile skeleton 采样成 `KimodoMarkerSampleResult`

#### Root2D 约束

`SetRoot2D(...)` 会：

1. 抓当前 pose sample
2. 保留当前 heading
3. 只把 root 的 x/z 调到目标值
4. 生成 `root2d` constraint

### 约束是一次性的

- `pendingConstraintSamples` 只服务于“下一次成功生成”
- 一旦本次生成成功入队，这些 pending constraints 会被清空
- 如果在下一次生成前连续调用多个 `Set...Constraint(...)`
  - 它们会一起进入同一个 `constraints_json`

## upcoming segment 刷新机制

下列操作都会刷新 upcoming generation：

- `SetPrompt(...)`
- `LockPrompt()`
- `UnlockPrompt()`
- `SetLeftHandConstraint(...)`
- `SetRightHandConstraint(...)`
- `SetLeftFootConstraint(...)`
- `SetRightFootConstraint(...)`
- `SetRoot2D(...)`

刷新逻辑：

1. `generationRequestVersion++`
2. 清掉当前 queue 里还没播的 segment
3. 如果旧生成请求还在飞，等它回来后直接丢弃
4. 如果当前没在生成，就立刻生成新的一段

这保证：

- 新 prompt / 新 constraint 尽量作用到“下一段”
- 不会被已经排好的旧 segment 吃掉

## `_Prompt` 包装约定

### 输入

- 所有数字字符串用 `InvariantCulture`
- 请使用 `.` 作为小数点
- 例如：
  - `"1"`
  - `"0.5"`
  - `"-2.25"`

### 输出

- Getter 会返回字符串结果
- Setter / Command 型方法返回状态字符串
- 解析失败时返回类似：
  - `Invalid float for x: 'abc'.`

## 现有 public API 总表

### 原有 API

- `StartDemo()`
- `StopDemo()`
- `ResetDemo()`
- `SetAnimationDurationSeconds(float seconds)`
- `SetPrompt(string prompt)`
- `SwitchToNextCharacter()`

### 新增 / 扩展 API

- `GetCurrentPrompt()`
- `LockPrompt()`
- `UnlockPrompt()`
- `SetLeftHandConstraint(float x, float y, float z, float duration = 1f)`
- `SetRightHandConstraint(float x, float y, float z, float duration = 1f)`
- `SetLeftFootConstraint(float x, float y, float z, float duration = 1f)`
- `SetRightFootConstraint(float x, float y, float z, float duration = 1f)`
- `SetRoot2D(float x, float z, float duration = 1f)`
- `GetPosition()`

### `_Prompt` 包装

- `StartDemo_Prompt()`
- `StopDemo_Prompt()`
- `ResetDemo_Prompt()`
- `SetAnimationDurationSeconds_Prompt(string seconds)`
- `SetPrompt_Prompt(string prompt)`
- `GetCurrentPrompt_Prompt()`
- `LockPrompt_Prompt()`
- `UnlockPrompt_Prompt()`
- `SetLeftHandConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRightHandConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetLeftFootConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRightFootConstraint_Prompt(string x, string y, string z, string duration = "1")`
- `SetRoot2D_Prompt(string x, string z, string duration = "1")`
- `GetPosition_Prompt()`
- `SwitchToNextCharacter_Prompt()`

## 对下一个智能体的建议

如果你后面要继续扩展这一套运行时控制，优先看下面几个点：

- prompt 行为：
  - `SetPromptInternal`
  - `LockPromptInternal`
  - `UnlockPromptInternal`
  - `GenerateNextSegmentAsync`

- upcoming 刷新：
  - `RefreshUpcomingGenerationAsync`
  - `generationRequestVersion`

- 约束注入：
  - `pendingConstraintSamples`
  - `BuildActiveGenerationConstraints`
  - `TryCreateShiftedConstraintSample`
  - `TryCreateRoot2DConstraintSample`

- 当前播放状态：
  - `RawMotionPlayer.CurrentPromptText`
  - `RawMotionPlayer.CurrentRootPosition`

## 当前限制

- 手脚约束目前是“当前 pose + root 平移”的快速实现，不是单独 IK 解算
- 约束是一次性的，不支持自动跨多段持续保留
- `GetPosition()` 目前返回的是当前激活角色的 `Hips` 世界坐标，语义上更接近“角色重心位置”，不是角色根 GameObject 的 transform.position
