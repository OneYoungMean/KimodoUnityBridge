# Kimodo Generate Pipeline (Editor + Runtime Split)

## Goal
- Remove generation execution from `KimodoPlayableClipEditor`.
- Route generation through `EditorGenerateSessionRunner -> KimodoEditorGeneratePipeline`.
- Keep editor behavior unchanged for asset writeback, bake, retarget, and timeline refresh.

## Current Split
- Runtime (`Runtime/Generation/Pipeline`): request/result/stage types and backend invocation pipeline.
- Editor (`Editor/Core/GenerationPipeline`):
  - constraint building from timeline markers,
  - clip asset writeback + bake,
  - retarget + curve filter,
  - generation orchestration and progress staging.

## TimelineInject Avatar API Split
- Runtime accessors moved to `TimelineInject/Runtime/AvatarRuntimeAccess.cs`:
  - `GetAvatarPostRotationOrIdentity`
  - `GetAvatarAxisLengthOrZero`
  - `GetSkeletonBoneParentNameOrEmpty`
- Editor-only importer/avatar auto-generation remains in `TimelineInject/Editor/AvatarSetupToolExtension.cs`.

## Runtime Real-time Retarget
Runtime real-time retargeting is implemented by `KimodoRuntimeMotionDriver` and the runtime motion player:
1. Generated compact JSON or KMB motion is parsed into a runtime skeleton/motion buffer.
2. The driver streams generated segments and applies them to a target Humanoid Animator.
3. Optional foot-target driving and runtime leg IK correction are applied during playback.
4. Editor asset bake/writeback remains a separate authoring path.

## Non-goals (This Phase)
- No runtime `.anim` asset persistence.
- No migration of importer/AssetDatabase-dependent avatar creation into Runtime.
- No BVH preview commandization (legacy path stays).
