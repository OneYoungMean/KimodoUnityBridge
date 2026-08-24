# SampleResult 70D 清理与重新接入计划

## 目标

从 `618762d` 重新接入最终确认的 SampleResult 70D 方案，删除旧 IK、root-space effector、重复 AutoSample/拖拽写回和 CharacterPose 中间旁路。

唯一采样链路：

```text
BoneSample(local bones) → SkeletonCache 重建 → 读取 Hips/手/脚 world Transform → KimodoMarkerSampleResult → command / preview / export 边界
```

## 固定语义

70D 布局：`49 muscle + 7 rootTQ + 7 leftFootTQ + 7 rightFootTQ`。

`enableMask` 只表示通道是否参与；数据始终可解码，默认 muscle=0、position=0、quaternion=identity。

`root2DOverride` 始终是角色 hips 的完整 world position + world rotation，与 rootTQ 无关，优先级高于 rootTQ；无有效 override 时才使用 rootTQ。

四个 effector 的 position 始终是 world position。手使用 `q-rig = q-current-in-root * inverse(q-bind-world)`；该 q 直接发送给 IKGoal，不再做任何 world-space 反解。脚使用 `q-cube = q-current-world * inverse(q-bind-world)`，同样直接发送给 IKGoal。

姿势顺序：`70D FK → root2DOverride 覆盖 hips → 保留 world effector targets → display/export`。底层协议保持不变，但 IK 中间解算全部移除。

AutoSample 从 BoneSample 重建 SkeletonCache 后读取世界 Hips/手/脚，Rig 只由 SampleResult 还原，不能反向覆盖 SampleResult。非 AutoSample 的 EditorWindow/Inspector 编辑同一份 SampleResult，拖拽只写 world root2DOverride/effectors；关闭窗口清理 preview。

## 必须删除或隔离的旧逻辑

1. `TrySampleMarkerFromProfileSkeletonRaw` 中的 profile-root-local effector 计算。
2. `WorldToBodyRelativeEffector`、`BodyRelativeEffectorToWorld`、旧 IK goal/endpoint 转换及生产调用。
3. 把 `HumanPose.bodyPosition` 直接当作 hips world position。
4. `ResolveRoot2DOverride` 用 rootTQ/CharacterPose.root 作为 Root2D fallback。
5. CharacterPose 作为 SampleResult 持久化或场景语义来源；只允许留在 HumanPose/协议临时边界。
6. 先生成 root-space CharacterPose、再通过 `TryEncode` 回写 SampleResult 的旁路。
7. AutoSample 和拖拽分别维护 root/effector 数据的重复 merge。
8. 没有明确 HumanPose 边界时，用 `SkeletonRootWorldPosition` 伪装 rootTQ 为 hips world 的 fallback。

## 分阶段实施

### Phase 0：回退基线与计划

分支回到 `618762d`；后续错误实现仅保留在历史 commit 中，不重新合并。

提交：`checkpoint: reset canonical sample rewrite baseline`

### Phase 1：清理旧坐标和 IK 旁路

删除 raw profile-root effector 坐标计算、旧 IK helper/生产调用，并扫描 `IK`、`goalPosition`、`WorldToBody`、`profileRootJoint` 残余调用。

提交：`checkpoint: remove legacy ik and root-space sampling`

### Phase 2：唯一 world 采样入口

应用 BoneSample 后只从 SkeletonCache Transform 读取 world Hips/手/脚，写入 70D、完整 world root2DOverride、world effectors；统一手/脚旋转转换，不执行 IK；增加一致性断言。

提交：`checkpoint: rebuild sample result from world skeleton targets`

### Phase 3：统一 AutoSample 与编辑写回

AutoSample 单向 SampleResult → Rig；非 AutoSample Rig → SampleResult，只写 world root2DOverride/effectors；Window 和 Inspector 共用同一 utility，删除重复 merge。

提交：`checkpoint: unify autosample and editor sample writeback`

### Phase 4：Composer/Preview/Export 边界

Composer 只按 enableMask/creationOrder 合并，不做 FK 或 IK；Root2D 只在协议投影时转换；Preview 使用统一 world SampleResult；Export 只在唯一协议边界转换。

提交：`checkpoint: isolate protocol boundary from canonical sample`

### Phase 5：验证

重写 70D、BoneSample、world target、AutoSample、拖拽写回测试；实际验证 AutoSample 开关前后 Hips/四个 effector world Transform 一致；FullBody sampling、Generate、Unity 2022.3 编译通过。

提交：`checkpoint: verify world sample pipeline`

## 恢复规则

恢复时执行：

```powershell
git status --short --branch
git log --oneline -8
Get-Content SAMPLE_RESULT_70D_REWRITE_PLAN.md
Get-Content SAMPLE_RESULT_70D_REWRITE_CHECKPOINTS.md -Tail 120
```

每个阶段必须先编译、记录 checkpoint、单独提交，再进入下一阶段。

---

## External Pose Marker 增量改造计划（从 CP87 开始）

### 目标

将 `PoseGet` 的结果定义为可持久化、可编辑、可再次读取的外部姿势 Marker：

```text
当前帧采样
→ KimodoMarkerSampleResult
→ PoseCacheTrack 上的 KimodoConstraintMarker(type=external)
→ 返回 marker locator
```

`CharacterPose` 不再参与 PoseGet、PoseSet、PoseContract、PoseCache 和 Constraint 主链路；如果仍需兼容旧 JSON，只允许停留在 Command 输入/输出边界。

### 固定语义

- `KimodoMarkerSampleResult.sampleData` 是唯一的 70D 基础姿势来源。
- `sampleData` 的 RootTQ/FootTQ 保持 body-relative canonical 语义。
- `effectors` 保持 world-space hand/foot target 语义。
- `constraintMode` 只表示 `root2d/fullbody/effector/mix`，不承载 Marker 类型。
- 新增的 `external` 是 Marker 语义类型，不是 Constraint mode。
- external Marker 可被 PoseGet/Read/Edit 访问，但默认不进入 Constraint 求解、协议导出或普通 Constraint 预览集合。
- PoseGet 以采样帧为 Marker 时间，重复 Get 时按稳定 locator 更新同一个 Marker，而不是无条件创建重复 Marker。

### 分批实施

#### Phase A / CP87：冻结行为并定义 External Marker 边界

只做类型和入口盘点，不改变行为：

1. 确认 `PoseCacheTrack` 是 PoseGet 的持久化轨道。
2. 确认 `KimodoConstraintMarker` 是现有 Marker 容器。
3. 定义 `markerType = constraint | external` 的持久化语义。
4. 列出所有 Marker 收集、Preview、Composer、Exporter 入口，标记 external 必须跳过的位置。
5. 保留现有 PoseGet 行为作为回归基线。

验收：编译不变；现有 PoseGet/pose_set/pose_contract 行为不变；checkpoint 记录所有入口。

#### Phase B / CP88：直接生成 External SampleResult Marker

改造 `PoseGet`：

1. 直接从当前帧得到 `KimodoMarkerSampleResult`。
2. 直接写入 `sampleData`、`enableMask`、`validMask`、`effectors`、`sampleTime`。
3. 在 `PoseCacheTrack` 创建或更新 `KimodoConstraintMarker`。
4. 标记 `markerType=external`，关闭 `autoSample`。
5. 返回 `session_id/track/frame/marker_id` 和可选的 JSON 投影。

禁止路径：`SampleResult → CharacterPose → JSON → CharacterPose → SampleResult`。

验收：FootTQ 不发生 world-space 覆盖；重复 PoseGet 得到稳定 Marker；Marker 重载后数据一致。

#### Phase C / CP89：迁移读取和编辑命令

迁移：

- `ReadPose` 直接读取 external Marker 的 SampleResult。
- `PoseSetRootTransform` 直接修改 `rootOverride` 和对应 mask。
- `PoseSetMuscle` 直接修改 `sampleData.data[0..48]`。
- `PoseContract` 直接读写 SampleResult 的 effectors/root。
- session persistence 直接保存/恢复 external Marker，不再解码 CharacterPose。

验收：编辑前后 70D、FootTQ、effectors、mask 均可 round-trip；旧 locator 仍能定位。

#### Phase D / CP90：隔离并删除 CharacterPose 主链路

1. 将 `CharacterPoseJson` 限定为旧 Command JSON 兼容边界。
2. 删除 PoseGet/PoseSet/PoseContract 对 `CharacterPose` 的依赖。
3. 删除 `CharacterPose.muscleSample` 以及重复的 root/foot/muscle 存储。
4. 将需要显示的属性改为 SampleResult 的显式访问器。
5. 处理 `float[] muscles` 的可变数组调用，避免用副本伪装为实时视图。

验收：Runtime/Editor/Command 代码无 CharacterPose 引用；命令 JSON 由 SampleResult 直接投影。

#### Phase E / CP91：统一消费端和过滤规则

1. Preview、Bake、Generate、Constraint Composer 统一消费 SampleResult。
2. 所有普通 Constraint 收集器显式过滤 `markerType=external`。
3. PoseCache/External Marker 编辑器只负责数据编辑，不隐式启动 Constraint 求解。
4. 需要把 external 用作 Constraint 时，显式执行 `External → Constraint` 的复制/转换动作。

验收：external Marker 可编辑/读取但不会偷偷参与导出；显式转换后才进入 Constraint 管线。

#### Phase F / CP92：回归与删除旧兼容转换

验证：

- PoseGet → 读取 → 编辑 → 再读取数据一致。
- 所有 effector mask 关闭时基础姿势不变。

### 实施状态（CP92）

CP87–CP92 已完成：External Marker 类型、PoseGet 直接 SampleResult、读取/编辑/Contract、分析与预览消费端迁移、普通 Constraint 过滤以及独立 Unity package probe 编译验证均已落地。没有创建或复用 `KimodoAnalysisKeyframeMarker` 作为姿势容器。随后按用户确认执行 CP93，彻底删除 CharacterPose 兼容类型；当前未创建 Git commit，以保留仓库中用户已有的未提交修改。

### CP93 — CharacterPose 全量删除

用户确认仓库尚未发布，直接删除旧语义。已移除 `CharacterPose`、`CharacterPoseJson`、`CharacterPoseMuscleAdapter`、`KimodoSampleResultPoseUtility` 及其 `.meta` 文件；命令 JSON 由 `KimodoMarkerSampleResult` 直接投影，Session persistence 只保存 `sample_result`。同时删除 4 个已由 `#if false` 禁用的旧 CharacterPose 测试文件及其 `.meta`，并清理旧 Editor 参数和 Runtime 注释。
- Root2D 修改只影响 `rootOverride`，不改 RootTQ/FootTQ。
- FootTQ 在 PoseGet 及 Marker 重载前后保持 canonical body-relative 语义。
- external Marker 不进入普通 Constraint Preview/Bake/Export。
- Unity package probe 编译与 Command schema 静态检查通过；`C:\tuanjie\Example` 场景回归未执行，仍受已有 Unity/ArtifactDB 锁影响。

### 每批提交和回滚规则

每个 CP 只完成一个阶段，顺序固定：

```text
静态引用扫描
→ 最小代码改动
→ git diff --check
→ Unity package probe 编译
→ Example/Command 回归
→ 更新 checkpoint
→ 单独提交
```

任何阶段失败，回退到上一个 CP；不跨阶段混合清理 CharacterPose、Marker 类型和 Preview 行为。
