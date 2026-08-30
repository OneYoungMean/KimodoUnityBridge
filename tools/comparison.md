---
name: kimodo-animation-comparison
description: Compare two Session animations under identical visual and structured evidence conditions.
---

# Comparison tool / Comparison 工具

## Decision program / 决策程序

```pseudo
#define YES             1
#define NO              0
#define UNKNOWN        -1
#define NOT_APPLICABLE -2

#define CANDIDATE_1 1
#define CANDIDATE_2 2
#define TIE         0

#define RESULT_CANDIDATE_1_STRONGER "candidate_1_stronger"
#define RESULT_CANDIDATE_2_STRONGER "candidate_2_stronger"
#define RESULT_NO_RELIABLE_DIFFERENCE "no_reliable_difference"
#define RESULT_INSUFFICIENT_EVIDENCE "insufficient_evidence"

COMPARISON_GOAL = REQUIRED("<quality goal / 质量目标>")
TARGET_SEMANTICS = OPTIONAL("<requested action semantics / 指定动作语义>")

#define GENERIC_QUALITY_GOAL         request_supplies_no_specific_criteria()
#define POSE_CONTINUITY_REQUIRED     goal_requires("pose continuity") or GENERIC_QUALITY_GOAL
#define ROOT_TRAJECTORY_REQUIRED     goal_requires("root trajectory") or GENERIC_QUALITY_GOAL
#define CONTACT_QUALITY_REQUIRED     goal_requires("contacts") or generic_goal_and_contacts_apply_to_both()
#define BODY_CONTROL_REQUIRED        goal_requires("body control") or GENERIC_QUALITY_GOAL
#define ENDING_BOUNDARY_REQUIRED     goal_requires("ending or loop boundary") or GENERIC_QUALITY_GOAL
#define RANGE_OR_TRANSITION_REQUIRED request_explicitly_names_range_or_transition()

#define VISUAL_OPENED             UNKNOWN
#define CANDIDATE_MAPPING_VALID   UNKNOWN

#define POSE_CONTINUITY_WINNER UNKNOWN
#define ROOT_TRAJECTORY_WINNER UNKNOWN
#define CONTACT_QUALITY_WINNER UNKNOWN
#define BODY_CONTROL_WINNER    UNKNOWN
#define ENDING_BOUNDARY_WINNER UNKNOWN

#define UNRESOLVED_CONFLICT      UNKNOWN
#define HAS_DECISIVE_EVIDENCE    UNKNOWN
#define OVERALL_WINNER           UNKNOWN

function compare(candidate_1, candidate_2):
    session = session_get_or_create({name: OPTIONAL_SESSION_NAME})
    session_id = session.session_id

    candidate_1 = ensure_loaded_with_session_add(session, candidate_1)
    candidate_2 = ensure_loaded_with_session_add(session, candidate_2)

    analysis = animation_analyze({
        session_id: session_id,
        clips: [
            {
                role: "source",
                character: candidate_1.character,
                clip: candidate_1.clip
            },
            {
                role: "target",
                character: candidate_2.character,
                clip: candidate_2.clip
            }
        ],
        level: "middle",
        resolution: 512
    })

    image_path = analysis.pictures.image_path
    picture_map = analysis.pictures.images
    candidate_1_tiles, candidate_2_tiles =
        map_tiles_by_role_and_character_and_clip(picture_map)
    CANDIDATE_MAPPING_VALID =
        mapping_is_unambiguous(candidate_1_tiles, candidate_2_tiles)
    VISUAL_OPENED = OPEN_WITH_AVAILABLE_VISUAL_TOOL(image_path)

    supplemental_numeric_evidence = NOT_APPLICABLE
    if RANGE_OR_TRANSITION_REQUIRED == YES:
        if candidate_1.character != candidate_2.character:
            return comparison_report(RESULT_INSUFFICIENT_EVIDENCE, [])
        ASSERT request explicitly supplies both half-open local frame ranges
        supplemental_numeric_evidence = animation_compare({
            session_id: session_id,
            character: candidate_1.character,
            origin: {
                animation: candidate_1.clip,
                range: [
                    candidate_1.start_frame,
                    candidate_1.end_frame_exclusive
                ]
            },
            target: {
                animation: candidate_2.clip,
                range: [
                    candidate_2.start_frame,
                    candidate_2.end_frame_exclusive
                ]
            }
        })

    COMPARISON_PROMPT = """
    Compare candidate 1 and candidate 2 for: {COMPARISON_GOAL}.
    Target semantics, if supplied: {TARGET_SEMANTICS}.

    Apply identical evidence conditions. Inspect both returned visuals.
    Fill each required *_WINNER with CANDIDATE_1, CANDIDATE_2, TIE, or UNKNOWN.
    Use structured and optional range evidence only as support. Do not calculate
    OVERALL_WINNER by score, vote, magnitude, displacement, contact count,
    or selected-frame count. Resolve conflicting criteria by relevance to the
    stated goal; leave unresolved evidence UNKNOWN.

    按相同证据条件比较两个候选并实际检查两者图像。每个必需的 *_WINNER
    只填写 CANDIDATE_1、CANDIDATE_2、TIE 或 UNKNOWN。结构化证据和区间
    数值只能辅助。不能用分数、投票、幅度、位移、接触数或选帧数机械决定
    OVERALL_WINNER；冲突证据按比较目标的重要性处理，无法解决时保留 UNKNOWN。
    """

    comparison_observations = fill_winner_macros_from(
        prompt = COMPARISON_PROMPT,
        composite_visual = image_path,
        candidate_1_tiles = candidate_1_tiles,
        candidate_2_tiles = candidate_2_tiles,
        structured_evidence = analysis,
        supplemental_evidence = supplemental_numeric_evidence
    )

    OVERALL_WINNER, HAS_DECISIVE_EVIDENCE, UNRESOLVED_CONFLICT =
        holistic_judgment_without_scoring(
            goal = COMPARISON_GOAL,
            semantics = TARGET_SEMANTICS,
            criterion_winners = required_criterion_winners()
        )

    return comparison_result(comparison_observations)

function comparison_result(comparison_observations):
    if CANDIDATE_MAPPING_VALID != YES:
        return comparison_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            comparison_observations
        )

    if VISUAL_OPENED != YES:
        return comparison_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            comparison_observations
        )

    if required_criterion_winners() contains UNKNOWN:
        return comparison_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            comparison_observations
        )

    if UNRESOLVED_CONFLICT == YES or OVERALL_WINNER == UNKNOWN:
        return comparison_report(
            RESULT_INSUFFICIENT_EVIDENCE,
            comparison_observations
        )

    if OVERALL_WINNER == CANDIDATE_1 and HAS_DECISIVE_EVIDENCE == YES:
        return comparison_report(
            RESULT_CANDIDATE_1_STRONGER,
            comparison_observations
        )

    if OVERALL_WINNER == CANDIDATE_2 and HAS_DECISIVE_EVIDENCE == YES:
        return comparison_report(
            RESULT_CANDIDATE_2_STRONGER,
            comparison_observations
        )

    if OVERALL_WINNER == TIE:
        return comparison_report(
            RESULT_NO_RELIABLE_DIFFERENCE,
            comparison_observations
        )

    return comparison_report(
        RESULT_INSUFFICIENT_EVIDENCE,
        comparison_observations
    )

function comparison_report(result, comparison_observations):
    return {
        result: result,
        overall_winner: OVERALL_WINNER,
        criterion_winners: required_criterion_winners(),
        evidence: concise_differences_by_criterion(comparison_observations),
        unverified: criteria_with_UNKNOWN_evidence()
    }

function ensure_loaded_with_session_add(session, candidate):
    if candidate.character is not in session.session.characters:
        added_character = session_add({
            session_id: session.session_id,
            kind: "character",
            character: candidate.character
        })
        candidate.character = added_character.character.name

    if candidate.clip is not under candidate.character:
        added_clip = session_add({
            session_id: session.session_id,
            kind: "clip",
            character: candidate.character,
            clip: candidate.clip
        })
        candidate.clip = added_clip.animation.name

    return candidate

function required_criterion_winners():
    return [
        required(POSE_CONTINUITY_REQUIRED, POSE_CONTINUITY_WINNER),
        required(ROOT_TRAJECTORY_REQUIRED, ROOT_TRAJECTORY_WINNER),
        required(CONTACT_QUALITY_REQUIRED, CONTACT_QUALITY_WINNER),
        required(BODY_CONTROL_REQUIRED, BODY_CONTROL_WINNER),
        required(ENDING_BOUNDARY_REQUIRED, ENDING_BOUNDARY_WINNER)
    ]

function required(required_flag, evidence):
    return evidence if required_flag == YES else NOT_APPLICABLE

ASSERT identical_criteria_and_render_conditions_for_both_candidates()
ASSERT missing_evidence_means_UNKNOWN_not_defect()
ASSERT no_universal_threshold_decides_quality()
ASSERT numerical_evidence_never_replaces_opened_visual_evidence()

if evidence_is_static_only():
    PLAYBACK_CONTINUITY_WINNER = UNKNOWN
    SLIDING_WINNER             = UNKNOWN
    POPPING_WINNER             = UNKNOWN
    ACCELERATION_WINNER        = UNKNOWN
    VELOCITY_CONTINUITY_WINNER = UNKNOWN
```
