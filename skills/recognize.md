# Recognize / 识别

## Decision program / 决策程序

```pseudo
#define YES             1
#define NO              0
#define UNKNOWN        -1
#define NOT_APPLICABLE -2

#define RESULT_MATCH                 "match"
#define RESULT_NOT_MATCH             "not_match"
#define RESULT_INSUFFICIENT_EVIDENCE "insufficient_evidence"

// Fill from the request; do not infer omitted semantics.
// 根据请求填写，不能补写请求未提供的语义。
TARGET_ACTION          = REQUIRED("<action / 动作>")
TARGET_PHASE           = OPTIONAL("<phase / 阶段>")
TARGET_DIRECTION       = OPTIONAL("<character-relative direction / 角色相对方向>")
TARGET_PATH            = OPTIONAL("<path / 路径>")
TARGET_BODY_STATE      = OPTIONAL("<body state / 身体状态>")
TARGET_CONTACT_STATE   = OPTIONAL("<contact state / 接触状态>")
TARGET_ENDING_OR_LOOP  = OPTIONAL("<ending or loop / 结束或循环>")
TARGET_STYLE           = OPTIONAL("<relevant style / 相关风格>")

#define ACTION_REQUIRED         YES
#define PHASE_REQUIRED          is_present(TARGET_PHASE)
#define DIRECTION_REQUIRED      is_present(TARGET_DIRECTION)
#define PATH_REQUIRED           is_present(TARGET_PATH)
#define BODY_REQUIRED           is_present(TARGET_BODY_STATE)
#define CONTACT_REQUIRED        is_present(TARGET_CONTACT_STATE)
#define ENDING_OR_LOOP_REQUIRED is_present(TARGET_ENDING_OR_LOOP)
#define STYLE_REQUIRED          is_present(TARGET_STYLE)

#define VISUAL_OPENED       UNKNOWN
#define ACTION_MATCH        UNKNOWN
#define PHASE_MATCH         UNKNOWN
#define DIRECTION_MATCH     UNKNOWN
#define PATH_MATCH          UNKNOWN
#define BODY_MATCH          UNKNOWN
#define CONTACT_MATCH       UNKNOWN
#define ENDING_OR_LOOP_MATCH UNKNOWN
#define STYLE_MATCH         UNKNOWN

function recognize(request, character_ref, clip_ref):
    help = read("Command/help.json")

    session = session_get_or_create({name: OPTIONAL_SESSION_NAME})
    session_id = session.session_id
    // Add only missing content and keep the safe names returned by the runtime.
    // 只添加 Session 中缺少的内容，并使用运行时返回的安全名称。
    if character_ref is not a safe name in session.session.characters:
        added_character = session_add({
            session_id: session_id,
            kind: "character",
            character: character_ref
        })
        character_ref = added_character.character.name

    if clip_ref is not a safe animation name under character_ref:
        added_clip = session_add({
            session_id: session_id,
            kind: "clip",
            character: character_ref,
            clip: clip_ref
        })
        clip_ref = added_clip.animation.name

    analysis = animation_analyze({
        session_id: session_id,
        clips: [{
            role: "source",
            character: character_ref,
            clip: clip_ref
        }],
        level: "middle",
        resolution: 512
    })

    image_path = analysis.pictures.image_path
    picture_map = analysis.pictures.images
    VISUAL_OPENED = OPEN_WITH_AVAILABLE_VISUAL_TOOL(image_path)

    // Structured Humanoid trajectory supports the image judgment; it never replaces it.
    // Humanoid 结构化轨迹仅支持图像判断，不能替代图像判断。
    trajectory_support = {
        path:                   analysis.clips[0].root_trajectory.path,
        samples:                analysis.clips[0].root_trajectory.samples,
        path_length_xz:         analysis.clips[0].root_trajectory.path_length_xz,
        net_displacement_xz:    analysis.clips[0].root_trajectory.net_displacement_xz,
        net_distance_xz:        analysis.clips[0].root_trajectory.net_distance_xz,
        average_speed_xz:       analysis.clips[0].root_trajectory.average_speed_xz,
        heading_change_degrees: analysis.clips[0].root_trajectory.heading_change_degrees,
        delta_y_range:          analysis.clips[0].root_trajectory.delta_y_range
    } if present else NOT_APPLICABLE

    EVALUATION_PROMPT = """
    Target / 目标:
      action={TARGET_ACTION}; phase={TARGET_PHASE};
      direction={TARGET_DIRECTION}; path={TARGET_PATH};
      body={TARGET_BODY_STATE}; contact={TARGET_CONTACT_STATE};
      ending_or_loop={TARGET_ENDING_OR_LOOP}; style={TARGET_STYLE}.

    Inspect the returned images in temporal order and keep each observation
    mapped to this animation. Judge action and phase first. Use direction,
    path, body, contact, ending/loop, and style only when marked REQUIRED.
    Resolve direction from character forward plus observed trajectory.
    Structured trajectory may support, but may not replace, visual evidence.

    按时间顺序检查返回图像，始终保持证据与本动画对应。先判断动作和阶段；
    仅判断标记为 REQUIRED 的方向、路径、身体、接触、结束/循环与风格。
    方向依据角色前向和观察轨迹；结构化轨迹只能辅助，不能替代视觉证据。

    Fill each *_MATCH with YES, NO, UNKNOWN, or NOT_APPLICABLE.
    对每个 *_MATCH 只填写 YES、NO、UNKNOWN 或 NOT_APPLICABLE。
    """

    observations = fill_match_macros_from(
        prompt = EVALUATION_PROMPT,
        visual = image_path,
        picture_map = picture_map,
        structured_support = trajectory_support
    )

    return recognition_result(observations)

function recognition_result(observations):
    if VISUAL_OPENED != YES:
        return recognition_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            observations
        )

    required_matches = [
        ACTION_MATCH,
        required(PHASE_REQUIRED,          PHASE_MATCH),
        required(DIRECTION_REQUIRED,      DIRECTION_MATCH),
        required(PATH_REQUIRED,           PATH_MATCH),
        required(BODY_REQUIRED,           BODY_MATCH),
        required(CONTACT_REQUIRED,        CONTACT_MATCH),
        required(ENDING_OR_LOOP_REQUIRED, ENDING_OR_LOOP_MATCH),
        required(STYLE_REQUIRED,          STYLE_MATCH)
    ]

    if required_matches contains UNKNOWN:
        return recognition_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            observations
        )

    if required_matches contains NO:
        return recognition_report(RESULT_NOT_MATCH, observations)

    return recognition_report(RESULT_MATCH, observations)

function recognition_report(result, observations):
    return {
        result: result,
        criteria: required_matches_only(),
        evidence: concise_observations_mapped_to_criteria(observations),
        unverified: criteria_with_UNKNOWN_evidence()
    }

function required(required_flag, evidence):
    return evidence if required_flag == YES else NOT_APPLICABLE

ASSERT filename_label_order_or_motion_magnitude_is_not_semantic_proof()
ASSERT more_displacement_contacts_or_selected_frames_is_not_automatic_match()
ASSERT missing_humanoid_trajectory_is_NOT_APPLICABLE_not_failure()

if evidence_is_static_only():
    PLAYBACK_CONTINUITY_MATCH = UNKNOWN
    SLIDING_MATCH             = UNKNOWN
    POPPING_MATCH             = UNKNOWN
    ACCELERATION_MATCH        = UNKNOWN
    VELOCITY_CONTINUITY_MATCH = UNKNOWN
```
