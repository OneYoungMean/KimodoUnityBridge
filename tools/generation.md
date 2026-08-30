---
name: kimodo-animation-generation
description: Generate, verify, and derive Unity animation Clips from explicit motion requests.
---

# Generation tool / Generation 工具

## Decision program / 决策程序

```pseudo
#define YES             1
#define NO              0
#define UNKNOWN        -1
#define NOT_APPLICABLE -2

#define RESULT_PASSED       "passed"
#define RESULT_NOT_VERIFIED "not_verified"
#define RESULT_NEEDS_REVISION "needs_revision"
#define RESULT_FAILED       "failed"

// Fill these macros only from the request and established project state.
// 这些宏只能根据请求和已确认的项目状态填写。
#define REQUEST_IS_RANGE_OPERATION      <YES|NO>
#define REQUEST_IS_RETARGET_ONLY        <YES|NO>
#define HAS_SOURCE_ANIMATION            <YES|NO>
#define SHOULD_LOOP                     <YES|NO>
#define SHOULD_REUSE_ANALYZED_PATH       <YES|NO>
#define SHOULD_OVERRIDE_PATH_DIRECTIONS <YES|NO>
#define SHOULD_OVERRIDE_FIXED_HEADING   <YES|NO>
#define NEED_FULLBODY_POSE              <YES|NO>
#define NEED_ROOT2D_POSE                <YES|NO>
#define NEED_LEFT_HAND_POSE             <YES|NO>
#define NEED_RIGHT_HAND_POSE            <YES|NO>
#define NEED_LEFT_FOOT_POSE             <YES|NO>
#define NEED_RIGHT_FOOT_POSE            <YES|NO>
#define SHOULD_RETARGET_AFTER_GENERATION <YES|NO>

CHARACTER       = REQUIRED("<safe character / 安全角色名>")
SOURCE_CLIP     = REQUIRED_IF(
    HAS_SOURCE_ANIMATION or REQUEST_IS_RETARGET_ONLY,
    "<safe clip / 安全动画名>"
)
TARGET_CHARACTER = REQUIRED_IF(
    REQUEST_IS_RETARGET_ONLY or SHOULD_RETARGET_AFTER_GENERATION,
    "<safe target character / 安全目标角色名>"
)

#define ACTION_REQUIRED not (
    REQUEST_IS_RANGE_OPERATION or REQUEST_IS_RETARGET_ONLY
)

ACTION                 = REQUIRED_IF(ACTION_REQUIRED, "<main action / 主动作>")
START_STATE            = OPTIONAL("<start state / 起始状态>")
PHASE                  = OPTIONAL("<phase / 阶段>")
DIRECTION_OR_PATH      = OPTIONAL("<direction or path / 方向或路径>")
SPEED_OR_ENERGY        = OPTIONAL("<speed or energy / 速度或能量>")
BODY_OR_CONTACT        = OPTIONAL("<body or contact / 身体或接触>")
ENDING_OR_LOOP         = OPTIONAL("<ending or loop / 结束或循环>")
STYLE                  = OPTIONAL("<relevant style / 相关风格>")

DURATION_FRAMES        = OPTIONAL("<60 FPS frames / 60FPS 帧数>")
PATH_BEGIN_YAW_DEGREES = REQUIRED_IF(SHOULD_OVERRIDE_PATH_DIRECTIONS, "<absolute Unity yaw>")
PATH_END_YAW_DEGREES   = REQUIRED_IF(SHOULD_OVERRIDE_PATH_DIRECTIONS, "<absolute Unity yaw>")
FIXED_HEADING_DEGREES  = REQUIRED_IF(SHOULD_OVERRIDE_FIXED_HEADING, "<absolute Unity yaw>")
EXPLICIT_CONSTRAINT_FRAMES = OPTIONAL("<frame list / 约束帧列表>")

#define MAX_AUTOMATIC_CORRECTIONS 1
CORRECTION_ATTEMPTS = 0

#define GENERATION_COMPLETED UNKNOWN
#define VISUAL_OPENED        UNKNOWN
#define ACTION_MATCH         UNKNOWN
#define LOOP_MATCH           UNKNOWN
#define PATH_MATCH           UNKNOWN
#define HEADING_MATCH        UNKNOWN
#define POSE_MATCH           UNKNOWN
#define CONTACT_MATCH        UNKNOWN
#define ENDING_MATCH         UNKNOWN
LAST_COMPLETED_SEED = UNKNOWN

GENERATION_PROMPT = JOIN_EXPLICIT_FIELDS(
    start_state=START_STATE,
    action=ACTION,
    phase=PHASE,
    direction_or_path=DIRECTION_OR_PATH,
    speed_or_energy=SPEED_OR_ENERGY,
    body_or_contact=BODY_OR_CONTACT,
    ending_or_loop=ENDING_OR_LOOP,
    style=STYLE,
)

# JOIN_EXPLICIT_FIELDS preserves the supplied token text and order, joining
# only non-empty fields with natural punctuation; omitted fields are absent.
# Only fields explicitly supplied by the caller are emitted. Do not infer
# preparation/recovery structure, breathing, contacts, root displacement, or
# other unstated semantics; preserve unknown abbreviations verbatim.

function execute_generate_skill(request):
    ASSERT not (
        REQUEST_IS_RANGE_OPERATION == YES and
        REQUEST_IS_RETARGET_ONLY == YES
    )

    session = session_get_or_create({name: OPTIONAL_SESSION_NAME})
    session_id = session.session_id
    character = ensure_character_with_session_add(session, CHARACTER)

    if REQUEST_IS_RANGE_OPERATION == YES:
        ASSERT request_explicitly_supplies_start_frame_end_frame_and_character()
        final_output = kimodo_record_range({
            session_id: session_id,
            start_frame: request.start_frame,
            end_frame: request.end_frame,
            character: character,
            remove_root_motion: request.remove_root_motion if supplied,
            speed: request.speed if supplied,
            name: request.name if supplied,
            output_folder: request.output_folder if supplied
        })
        final_ref = {
            character: final_output.character,
            clip: final_output.animation.name
        }
        GENERATION_COMPLETED = YES
        return verify_final_output(
            session_id,
            final_ref,
            runtime_evidence = final_output
        )

    if REQUEST_IS_RETARGET_ONLY == YES:
        ASSERT request_explicitly_supplies_source_animation_and_target_character()
        source_clip = ensure_clip_with_session_add(
            session,
            character,
            SOURCE_CLIP
        )
        target_character = ensure_character_with_session_add(
            session,
            TARGET_CHARACTER
        )
        final_output = kimodo_retarget_animation({
            session_id: session_id,
            source_character: character,
            animation: source_clip,
            target_character: target_character,
            name: request.name if supplied,
            output_folder: request.output_folder if supplied
        })
        final_ref = {
            character: final_output.character,
            clip: final_output.animation.name
        }
        GENERATION_COMPLETED = YES
        return verify_final_output(
            session_id,
            final_ref,
            runtime_evidence = final_output
        )

    source_analysis = NOT_APPLICABLE
    if HAS_SOURCE_ANIMATION == YES:
        source_clip = ensure_clip_with_session_add(session, character, SOURCE_CLIP)
        source_analysis = animation_analyze({
            session_id: session_id,
            clips: [{
                role: "source",
                character: character,
                clip: source_clip
            }],
            level: "middle",
            resolution: 512
        })
        source_image_path = source_analysis.pictures.image_path
        source_picture_map = source_analysis.pictures.images
        ASSERT OPEN_WITH_AVAILABLE_VISUAL_TOOL(source_image_path) == YES

    constraints = []

    if SHOULD_REUSE_ANALYZED_PATH == YES:
        ASSERT HAS_SOURCE_ANIMATION == YES
        path_ref = source_analysis.clips[0].root_trajectory.path
        ASSERT path_ref has exactly {track, index} returned by animation_analyze
        constraints.append({
            frame: request.path_start_frame if supplied else 0,
            root_path: {path: path_ref}
        })

    if any_pose_constraint_is_needed() == YES:
        ASSERT HAS_SOURCE_ANIMATION == YES
        for each explicitly_established_constraint_frame:
            pose_ref = pose_get({
                source: {
                    character: character,
                    clip: source_clip,
                    frame: explicitly_established_constraint_frame
                },
                full_data: request.needs_full_pose_data
            }).pose

            if request_explicitly_requires_root_transform_edit:
                pose_ref = pose_set_root_transform({
                    pose: pose_ref,
                    root: request.help_validated_root
                }).pose

            if request_explicitly_requires_muscle_edit:
                pose_ref = pose_set_muscle({
                    pose: pose_ref,
                    muscles: request.help_validated_muscles
                }).pose

            if request_explicitly_requires_effector_alignment:
                pose_ref = pose_contract({
                    origin: request.origin_pose,
                    target: pose_ref,
                    endeffectors: request.endeffectors,
                    components: request.components,
                    mode: request.contract_mode
                }).pose

            constraints.append(
                build_help_validated_point_constraint(
                    frame = explicitly_established_constraint_frame,
                    pose = pose_ref,
                    use_fullbody = NEED_FULLBODY_POSE,
                    use_root2d = NEED_ROOT2D_POSE,
                    use_left_hand = NEED_LEFT_HAND_POSE,
                    use_right_hand = NEED_RIGHT_HAND_POSE,
                    use_left_foot = NEED_LEFT_FOOT_POSE,
                    use_right_foot = NEED_RIGHT_FOOT_POSE
                )
            )

    args = {
        character: character,
        prompt: GENERATION_PROMPT
    }

    if DURATION_FRAMES is supplied:
        args.duration_frames = DURATION_FRAMES

    if SHOULD_LOOP == YES:
        args.loop = true

    if SHOULD_OVERRIDE_PATH_DIRECTIONS == YES:
        // Supply both values deliberately; do not rely on the omitted-peer default.
        // 明确提供起点和终点，不能依赖缺省另一端为零。
        args.path_begin_angle_degrees = PATH_BEGIN_YAW_DEGREES
        args.path_end_angle_degrees = PATH_END_YAW_DEGREES

    if SHOULD_OVERRIDE_FIXED_HEADING == YES:
        args.override_heading_degrees = FIXED_HEADING_DEGREES

    if constraints is not empty:
        args.constraints = constraints

    copy_only_user_supplied_optional_fields(
        request,
        args,
        fields = [
            "model", "text_encoder_model", "seed", "diffusion_steps",
            "output_mode", "output_folder", "name", "analysis_option"
        ]
    )

    generation = kimodo_generate_animation(args)
    request_id = generation.request_id

    poll_interval_seconds = 2
    poll_deadline = now() + configured_generation_timeout()
    do:
        wait(poll_interval_seconds)
        if user_requests_cancellation() == YES:
            kimodo_cancel_generation({
                request_id: request_id,
                reason: request.cancellation_reason if supplied
            })
        state = kimodo_get_generation({request_id: request_id})
    until state.status in {"completed", "failed", "canceled"} or now() >= poll_deadline

    if now() >= poll_deadline and state.status not in {"completed", "failed", "canceled"}:
        return {
            result: RESULT_NOT_VERIFIED,
            output: {request_id: request_id},
            criteria: [],
            evidence: [],
            unverified: ["generation_completion"],
            runtime_warnings: {accepted: generation, last: state, reason: "poll_timeout"}
        }

    if state.status != "completed":
        GENERATION_COMPLETED = NO
        return {
            result: RESULT_FAILED,
            status: state.status,
            runtime_payload: {
                accepted: generation,
                terminal: state
            }
        }

    GENERATION_COMPLETED = YES
    LAST_COMPLETED_SEED = state.seed
    generated_ref = {
        character: character,
        clip: state.animation,
        path: state.path
    }

    runtime_payload = {
        accepted: generation,
        terminal: state
    }

    if SHOULD_RETARGET_AFTER_GENERATION == YES:
        target_character = ensure_character_with_session_add(
            session,
            TARGET_CHARACTER
        )
        retargeted_output = kimodo_retarget_animation({
            session_id: session_id,
            source_character: character,
            animation: generated_ref.clip,
            target_character: target_character,
            name: request.retarget_name if supplied,
            output_folder: request.output_folder if supplied
        })
        generated_ref = {
            character: retargeted_output.character,
            clip: retargeted_output.animation.name
        }
        runtime_payload.retargeted = retargeted_output

    return verify_final_output(
        session_id,
        generated_ref,
        runtime_evidence = runtime_payload
    )

function verify_final_output(session_id, final_ref, runtime_evidence):
    final_analysis = animation_analyze({
        session_id: session_id,
        clips: [{
            role: "target",
            character: final_ref.character,
            clip: final_ref.clip
        }],
        level: "middle",
        resolution: 512
    })

    final_image_path = final_analysis.pictures.image_path
    final_picture_map = final_analysis.pictures.images
    VISUAL_OPENED = OPEN_WITH_AVAILABLE_VISUAL_TOOL(final_image_path)

    VERIFICATION_PROMPT = """
    Compare the generated result with the declared request intent.
    Fill ACTION_MATCH, LOOP_MATCH, PATH_MATCH, HEADING_MATCH, POSE_MATCH,
    CONTACT_MATCH, and ENDING_MATCH with YES, NO, UNKNOWN, or NOT_APPLICABLE.
    Only required intent fields participate in the final decision.
    A requested loop is not proof of a seamless boundary.

    将生成结果与已声明请求意图对照，只填写 YES、NO、UNKNOWN 或
    NOT_APPLICABLE。只有请求要求的项目参与最终决策；请求循环不等于
    已证明边界无缝。
    """

    verification_observations = fill_verification_macros_from(
        prompt = VERIFICATION_PROMPT,
        visual = final_image_path,
        picture_map = final_picture_map,
        structured_support = final_analysis
    )

    required_matches = [
        required(ACTION_REQUIRED, ACTION_MATCH),
        required(SHOULD_LOOP, LOOP_MATCH),
        required(
            SHOULD_REUSE_ANALYZED_PATH or SHOULD_OVERRIDE_PATH_DIRECTIONS,
            PATH_MATCH
        ),
        required(SHOULD_OVERRIDE_FIXED_HEADING, HEADING_MATCH),
        required(NEED_FULLBODY_POSE or NEED_ROOT2D_POSE, POSE_MATCH),
        required(
            NEED_LEFT_HAND_POSE or NEED_RIGHT_HAND_POSE or
            NEED_LEFT_FOOT_POSE or NEED_RIGHT_FOOT_POSE,
            CONTACT_MATCH
        ),
        required(is_present(ENDING_OR_LOOP), ENDING_MATCH)
    ]

    if GENERATION_COMPLETED != YES or VISUAL_OPENED != YES:
        return generation_report(
            RESULT_NOT_VERIFIED,
            final_ref,
            verification_observations,
            runtime_evidence
        )

    if runtime_reported_fallback_for_requested_behavior(runtime_evidence):
        return generation_report(
            RESULT_NEEDS_REVISION,
            final_ref,
            verification_observations,
            runtime_evidence
        )

    if required_matches contains UNKNOWN:
        return generation_report(
            RESULT_NOT_VERIFIED,
            final_ref,
            verification_observations,
            runtime_evidence
        )

    if required_matches contains NO:
        if CORRECTION_ATTEMPTS < MAX_AUTOMATIC_CORRECTIONS:
            correction = derive_supported_correction_from_failed_macros()
            if correction exists:
                CORRECTION_ATTEMPTS += 1
                preserve_completed_output()
                return execute_generate_skill(correction)
        return generation_report(
            RESULT_NEEDS_REVISION,
            final_ref,
            verification_observations,
            runtime_evidence
        )

    return generation_report(
        RESULT_PASSED,
        final_ref,
        verification_observations,
        runtime_evidence
    )

function generation_report(result, final_ref, observations, runtime_evidence):
    return {
        result: result,
        output: final_ref,
        criteria: required_generation_matches_only(),
        evidence: concise_observations_mapped_to_criteria(observations),
        unverified: criteria_with_UNKNOWN_evidence(),
        runtime_warnings_and_fallbacks:
            copy_only_returned_warnings_and_fallbacks(runtime_evidence)
    }

function ensure_character_with_session_add(session, character_ref):
    if character_ref is already a safe character name in session.session.characters:
        return character_ref

    added_character = session_add({
        session_id: session.session_id,
        kind: "character",
        character: character_ref
    })
    return added_character.character.name

function ensure_clip_with_session_add(session, character_ref, clip_ref):
    if clip_ref is already a safe animation name under character_ref:
        return clip_ref

    added_clip = session_add({
        session_id: session.session_id,
        kind: "clip",
        character: character_ref,
        clip: clip_ref
    })
    return added_clip.animation.name

function derive_supported_correction_from_failed_macros():
    if PATH_MATCH == NO and explicit_path_directions_exist():
        return same_seed_request_with(
            seed = LAST_COMPLETED_SEED,
            path_begin_angle_degrees = PATH_BEGIN_YAW_DEGREES,
            path_end_angle_degrees = PATH_END_YAW_DEGREES
        )

    if HEADING_MATCH == NO and explicit_fixed_heading_exists():
        return same_seed_request_with(
            seed = LAST_COMPLETED_SEED,
            override_heading_degrees = FIXED_HEADING_DEGREES
        )

    if POSE_MATCH == NO or CONTACT_MATCH == NO:
        return request_with_pose_constraints_only_if_pose_get_refs_exist()

    if LOOP_MATCH == NO and SHOULD_LOOP == YES:
        return request_with_loop_enabled_unless_runtime_declares_loop_fallback()

    return no_supported_correction

function required(required_flag, evidence):
    return evidence if required_flag == YES else NOT_APPLICABLE

ASSERT analysis_selected_frames_are_evidence_not_constraints()
ASSERT pose_constraints_use_pose_get_returned_track_and_index()
ASSERT analyzed_path_is_a_constraint_only_when_root_trajectory_path_is_reused()
ASSERT path_override_and_loop_are_independent_and_may_coexist()
ASSERT fixed_heading_overrides_path_tangent_heading_but_not_path_positions()
ASSERT explicit_root2d_at_a_frame_overrides_root_path_at_that_frame()
ASSERT same_frame_precedence_is_fullbody_then_root2d_then_effectors()
ASSERT no_source_target_range_pose_path_contact_or_constraint_is_invented()
ASSERT completed_outputs_are_preserved_and_corrections_are_derived_outputs()
ASSERT failed_canceled_or_fallback_results_are_reported_as_returned()

if evidence_is_static_only():
    LOOP_SEAMLESSNESS       = UNKNOWN
    PLAYBACK_CONTINUITY_MATCH = UNKNOWN
    SLIDING_MATCH           = UNKNOWN
    POPPING_MATCH           = UNKNOWN
    ACCELERATION_MATCH      = UNKNOWN
    VELOCITY_CONTINUITY_MATCH = UNKNOWN
```
