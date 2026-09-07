---
name: kimodo-animation-recognition
description: Identify a Session animation's semantic action from visual evidence and expose the motion profile needed by generation.
---

# Recognition tool / Recognition 工具

Recognition is an evidence step, not a generation step. It analyzes one clip,
opens the returned composite image, and returns one semantic choice plus a
machine-readable motion profile. Never infer semantics from an asset filename,
candidate order, clip id, or saliency alone.

## Fixed recognition program / 固定识别程序

Recognition uses a closed task set. The caller supplies the semantic alternatives;
the recognizer may not invent another task, label, threshold, or quality score.
Every task returns `value`, `confidence`, and `evidence`. Missing, unreadable, or
conflicting evidence is `UNKNOWN` (or `CONFLICT`); low-confidence guesses must
never be promoted to a positive result.

```pseudo
#define YES             1
#define NO              0
#define UNKNOWN        -1
#define CONFLICT       -2
#define NOT_APPLICABLE -3

#define CONF_HIGH       "high"
#define CONF_MEDIUM     "medium"
#define CONF_LOW        "low"

VISUAL_TASKS = [
    "trajectory_shape",
    "action_semantics",
    "visual_motion_quality",
    "contact_pattern"
]

STRUCTURED_TASKS = [
    "loop_endpoint",
    "keyframe_heading",
    "trajectory_turn",
    "trajectory_length"
]

ALLOWED_TRAJECTORY_SHAPE = ["closed_loop", "open_path", "near_static", "unclear"]
ALLOWED_MOTION_QUALITY   = ["smooth_appearance", "visible_break", "unclear"]
ALLOWED_CONTACT_PATTERN  = ["alternating", "left_dominant", "right_dominant", "unclear"]
ALLOWED_LOOP_ENDPOINT    = ["candidate", "not_candidate", "unknown"]
ALLOWED_KEYFRAME_HEADING = ["consistent", "inconsistent", "unknown"]

function recognize_clip(analysis, semantic_alternatives, clip_index = 0):
    image = analysis.pictures.image_path
    picture_map = tiles_for_clip(analysis.pictures.images, clip_index)
    ASSERT OPEN_WITH_AVAILABLE_VISUAL_TOOL(image) == YES

    result = {}
    for TASK in VISUAL_TASKS:
        if TASK == "action_semantics" and semantic_alternatives is empty:
            result[TASK] = task_result(NOT_APPLICABLE, CONF_HIGH, [])
        else:
            result[TASK] = run_visual_task(
                TASK, image, picture_map, analysis, semantic_alternatives
            )

    for TASK in STRUCTURED_TASKS:
        result[TASK] = run_structured_task(TASK, analysis.clips[clip_index])

    return finalize_recognition(result)

function run_structured_task(TASK, clip_analysis):
    profile = clip_analysis.motion_profile

    if TASK == "loop_endpoint":
        return structured_value(
            profile.is_loop_candidate,
            true  => "candidate",
            false => "not_candidate"
        )
    if TASK == "keyframe_heading":
        return structured_value(
            profile.heading_consistent,
            true  => "consistent",
            false => "inconsistent"
        )
    if TASK == "trajectory_turn":
        return numeric_value(profile.heading_change_degrees)
    if TASK == "trajectory_length":
        return numeric_value(profile.path_length_xz)

    return task_result(UNKNOWN, CONF_LOW, [])

function finalize_recognition(result):
    required = [
        "trajectory_shape",
        "visual_motion_quality",
        "loop_endpoint",
        "keyframe_heading",
        "trajectory_turn",
        "trajectory_length"
    ]
    if result["action_semantics"].value != NOT_APPLICABLE:
        required.append("action_semantics")

    if any(result[T].value in [UNKNOWN, "unknown", "unclear", CONFLICT]
           for T in required):
        status = "not_verified"
    elif any(result[T].confidence == CONF_LOW for T in required):
        status = "needs_review"
    else:
        status = "verified"

    return {
        "status": status,
        "analysis_handoff": result
    }
```

### Recognition prompt / 识别提示词

The following prompt is the only visual-task instruction. Substitute one `TASK`
per call; do not ask the model to solve several tasks in one free-form answer.

```text
You are a constrained animation-evidence recognizer.

Inputs:
- TASK: exactly one task from the fixed task list;
- composite_image: the opened animation_analyze composite image;
- picture_map: tile id, rect, presentation, and frames;
- analysis_json: the returned structured analysis;
- semantic_alternatives: the caller-provided alternatives, if any.

Rules:
1. Locate the relevant tile from picture_map before reading the image.
2. Report visible facts only. Do not use filenames, clip names, candidate order,
   common-sense expectations, or unstated thresholds.
3. Tile ids and frame numbers printed in the image are secondary labels. Use
   picture_map and analysis_json for exact ids and frames.
4. A static image cannot prove playback continuity, velocity, acceleration,
   sliding, or popping. For those claims return UNKNOWN.
5. If the image is unreadable or evidence is missing, return unknown. If image
   and JSON conflict, return unknown and state `CONFLICT` in reason. Never
   resolve a conflict by guessing.
6. Return only an allowed value for TASK. Do not create synonyms or extra fields.
7. confidence is high only when the requested fact is directly visible or comes
   from the named structured field; otherwise use medium or low.

TASK definitions:
- trajectory_shape: inspect root2d_pelvis_projection only; return closed_loop,
  open_path, near_static, or unclear.
- action_semantics: use only semantic_alternatives and inspect keyframes,
  foot_transitions, and test_pose tiles; if alternatives cannot be separated,
  return unknown.
- visual_motion_quality: report smooth_appearance only for an unbroken visible
  drawing; report visible_break for an explicit visual discontinuity; otherwise
  unclear. Do not call this playback smoothness.
- contact_pattern: inspect foot_transitions; blue is left-foot and red is
  right-foot event. Return alternating, left_dominant, right_dominant, or unclear.

Return strict JSON and nothing else:
{
  "task": "<TASK>",
  "value": "<allowed value>",
  "confidence": "high|medium|low",
  "evidence": [{
    "tile_id": "<id>",
    "presentation": "<presentation>",
    "frame": "<frame or null>",
    "observation": "<one visible fact>"
  }],
  "reason": "<one short sentence>"
}
```

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

    recognition = recognize_clip(analysis, alternatives, clip_index = 0)
    choice = recognition.analysis_handoff.action_semantics
    profile = analysis.clips[0].motion_profile
    return {
        status: recognition.status,
        semantic: choice.value,
        profile: profile,
        evidence: recognition.analysis_handoff,
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
