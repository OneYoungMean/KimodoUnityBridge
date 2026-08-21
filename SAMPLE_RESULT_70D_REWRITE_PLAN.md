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

四个 effector 的 position 始终是 world position。手使用 `q-rig = q-current-in-root * inverse(q-initial-in-root)`，显示时 `q-current = q-rig * q-initial`；脚不经过 pelvis/root 空间，使用 `q-current = q-cube * q-initialFoot`。

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
