# Kimodo Generate Pipeline (Editor + Runtime Split)

## Goal
- Remove generation execution from `KimodoPlayableClipEditor`.
- Route generation through `KimodoEditorCommandManager -> KimodoEditorRuntimeGeneratePipeline`.
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

## Runtime Real-time Retarget (Future)
This phase does **not** implement runtime real-time retarget execution yet. Planned direction:
1. Build a generated skeleton graph at runtime from generated motion outputs.
2. Add a MonoBehaviour bridge to sample body/muscle state from generated skeleton.
3. Drive target model muscle handles from sampled runtime state.
4. Keep editor asset bake path as-is for authoring workflows.

## Non-goals (This Phase)
- No runtime `.anim` asset persistence.
- No migration of importer/AssetDatabase-dependent avatar creation into Runtime.
- No BVH preview commandization (legacy path stays).
