---
name: kimodo-animation-recognition
description: Identify a Session animation's semantic action from visual evidence and expose the motion profile needed by generation.
---

# Recognition tool / Recognition 工具

Recognition is an evidence step, not a generation step. It analyzes one clip,
opens the returned composite image, and returns one semantic choice plus a
machine-readable motion profile. Never infer semantics from an asset filename,
candidate order, clip id, or saliency alone.

## Semantic identification

When a caller supplies semantic alternatives, compare them against the opened
analysis image and temporal evidence. Alternatives must differ in observable
action or phase, not merely in speed, wording, or an arbitrary suffix. Return
the selected semantic, evidence, and confidence as separate fields; the caller
owns any external answer-label or scoring format.

```pseudo
function identify_semantics(alternatives, character_ref, clip_ref):
    session = session_get_or_create({name: OPTIONAL_SESSION_NAME})
    character = ensure_character_in_session(session, character_ref)
    clip = ensure_clip_in_session(session, character, clip_ref)
    analysis = animation_analyze({
        session_id: session.session_id,
        clips: [{role: "source", character: character, clip: clip}],
        level: "middle",
        resolution: 512
    })

    image_path = analysis.pictures.image_path
    picture_map = analysis.pictures.images
    ASSERT OPEN_WITH_AVAILABLE_VISUAL_TOOL(image_path) == YES

    observations = inspect_temporal_tiles(
        image_path,
        picture_map,
        structured_support = analysis.clips[0]
    )
    choice = choose_semantic(alternatives, observations)
    profile = derive_motion_profile(analysis.clips[0], observations)
    return {
        semantic: choice.semantic,
        profile: profile,
        evidence: observations,
        confidence: choice.confidence
    }
```

## Motion profile / 动画运动画像

Every non-mesh Humanoid recognition result must report these fields, even when
the answer is `UNKNOWN`:

```json
{
  "action": "walk",
  "phase": "loop",
  "is_loop_candidate": true,
  "endpoint_pose": {
    "status": "ok",
    "mean_muscle_delta": 0.02,
    "root_transform_included": true,
    "root_height_delta": 0.01,
    "root_rotation_delta_euler_degrees": [1.2, 0.3, -0.7]
  },
  "has_clear_path": false,
  "path_length_xz": 0.04,
  "net_distance_xz": 0.01,
  "heading_change_degrees": 0.8,
  "heading_consistent": true,
  "should_override_path": "defer_to_task_semantics",
  "should_override_heading": "defer_to_task_semantics"
}
```

`endpoint_pose` compares the first and last body poses through the shared
Humanoid motion math. It also reports the complete root Transform: XYZ
translation (including height) and pitch/yaw/roll. `root2d` is only a planar
path/heading override; it never removes the sampled root's Y, pitch, or roll.
For loop continuity, evaluate body-pose continuity separately from intentional
planar displacement. `is_loop_candidate` also requires the endpoint root
height, pitch, and roll to remain continuous; intentional XZ displacement and
yaw are reported separately and do not erase those motion signals.

`is_loop_candidate` combines the source Clip loop flag with endpoint body-pose,
root-height, and root-tilt continuity. `has_clear_path` and
`heading_consistent` describe observed planar motion.
The two `should_override_*` fields are decisions for the generation task: keep
`defer_to_task_semantics` until the selected semantic and user intent are known.
For a known semantic, set them only when the requested result requires a path
or heading different from the observed source.

## Evidence rules / 证据规则

- Inspect image tiles in temporal order and map every observation to this clip.
- Use structured Root Path and endpoint-pose metrics as support, never as a
  replacement for visual evidence.
- Missing Humanoid trajectory or endpoint samples is `insufficient_evidence`,
  not a failed action.
- Static images cannot prove playback continuity, sliding, or velocity smoothness.
- A selected keyframe is analysis evidence. It becomes a generation constraint
  only after `pose_get` materializes that frame and the generation request
  explicitly includes the returned `{track,index}` pose.

ASSERT alternatives_are_distinct_enough_to_be_visually_decidable()
ASSERT filename_order_ids_and_saliency_are_not_semantic_proof()
ASSERT endpoint_pose_comparison_reports_complete_root_motion()
ASSERT override_decisions_are_not_invented_without_task_semantics()
