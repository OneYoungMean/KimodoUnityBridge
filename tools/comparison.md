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

#define QUALITY_CRITERIA [
    "semantic_correctness",
    "trajectory_quality",
    "foot_alternation_quality",
    "required_phase_coverage",
    "expression_strength"
]

#define PHASE_KIND "phase|transition"

COMPARISON_GOAL = REQUIRED("<quality goal / 质量目标>")
TARGET_SEMANTICS = OPTIONAL("<requested action semantics / 指定动作语义>")

#define VISUAL_OPENED             UNKNOWN
#define CANDIDATE_MAPPING_VALID   UNKNOWN

#define SEMANTIC_CORRECTNESS_WINNER UNKNOWN
#define TRAJECTORY_QUALITY_WINNER UNKNOWN
#define FOOT_ALTERNATION_QUALITY_WINNER UNKNOWN
#define REQUIRED_PHASE_COVERAGE_WINNER UNKNOWN
#define EXPRESSION_STRENGTH_WINNER UNKNOWN

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
    ASSERT analysis.analysis_schema_version == "2-phase-track-v1"
    ASSERT analysis.pictures.render_version == "37-phase-track-clustering"

    image_path = analysis.pictures.image_path
    picture_map = analysis.pictures.images
    candidate_1_tiles, candidate_2_tiles =
        map_tiles_by_role_and_character_and_clip(picture_map)
    CANDIDATE_MAPPING_VALID =
        mapping_is_unambiguous(candidate_1_tiles, candidate_2_tiles)
    VISUAL_OPENED = OPEN_WITH_AVAILABLE_VISUAL_TOOL(image_path)

    candidate_1_recognition = recognize_clip(
        analysis, TARGET_SEMANTICS, clip_index = 0
    )
    candidate_2_recognition = recognize_clip(
        analysis, TARGET_SEMANTICS, clip_index = 1
    )
    criterion_observations = compare_quality_criteria(
        candidate_1_recognition, candidate_2_recognition, TARGET_SEMANTICS
    )

    COMPARISON_PROMPT = """
    Compare candidate 1 and candidate 2 for: {COMPARISON_GOAL}.
    Target semantics, if supplied: {TARGET_SEMANTICS}.

    Apply identical evidence conditions. Inspect both returned visuals through
    the fixed recognition task set. Use each candidate's analysis_handoff and
    phase_track for trajectory shape, semantics, visual quality, contacts,
    loop endpoint, keyframe heading, turn, path length, phase order, phase
    coverage, and phase duration. Do not invent a task, threshold, score, or
    synonym. If a required handoff value is UNKNOWN or CONFLICT, leave that
    criterion UNKNOWN.

    Compare exactly these criteria:
    1. semantic_correctness: does the requested action appear and remain clear?
    2. trajectory_quality: is the visible path unbroken and structurally coherent?
    3. foot_alternation_quality: do left and right contact events alternate without noise?
    4. required_phase_coverage: are required phases present, ordered, and long enough?
    5. expression_strength: does each required phase have a clear anchor pose and
       sustained expression, rather than a momentary gesture?

    For n ordered semantic phases, use the duration prior w_i = 1/(i*i),
    normalized as p_i = w_i / sum(w_j). Compare observed phase coverage to this
    prior by deviation, not by a universal pass threshold. Phase durations are
    measured from phase_track intervals; transition intervals may be reported
    separately. Idle/standing may be present as a background phase, but its
    total coverage must not exceed 0.5 unless the request explicitly requires it.
    Overlapping semantic coverage is allowed; do not double-count it as a defect.

    A phase is not semantically named by the clustering algorithm. Match the
    caller's target semantics to phase_track evidence, anchor poses, and visual
    tiles. A short accidental contact or ambiguous transition must not satisfy a
    required semantic phase merely because it has an anchor frame.
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
        structured_evidence = {
            "analysis": analysis,
            "candidate_1_recognition": candidate_1_recognition,
            "candidate_2_recognition": candidate_2_recognition,
            "criterion_observations": criterion_observations
        },
        supplemental_evidence = NOT_APPLICABLE
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
        analysis_handoff: {
            candidate_1: candidate_1_recognition.analysis_handoff,
            candidate_2: candidate_2_recognition.analysis_handoff
        },
        unverified: criteria_with_UNKNOWN_evidence()
    }

function compare_quality_criteria(candidate_1, candidate_2, target_semantics):
    # Each criterion is independent. Never add winners or numeric values into a score.
    return for_each(QUALITY_CRITERIA, CRITERION => compare_one_quality_criterion(
        CRITERION, candidate_1, candidate_2, target_semantics
    ))

function compare_one_quality_criterion(CRITERION, candidate_1, candidate_2, target_semantics):
    if CRITERION == "semantic_correctness":
        compare_requested_semantics_against_visual_and_phase_evidence()
    if CRITERION == "trajectory_quality":
        compare_visible_trajectory_continuity_and_structured_trajectory_metrics()
    if CRITERION == "foot_alternation_quality":
        compare_foot_contacts_in_time_order()
    if CRITERION == "required_phase_coverage":
        compare_required_semantic_phase_presence_order_and_duration()
    if CRITERION == "expression_strength":
        compare_anchor_pose_and_phase_expression_against_target_semantics()

    return {
        "winner": CANDIDATE_1 | CANDIDATE_2 | TIE | UNKNOWN,
        "confidence": "high|medium|low",
        "evidence": ["tile:<id>" or "phase_track:<index>" or "analysis:<field>"],
        "reason": "one concise fact"
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
        SEMANTIC_CORRECTNESS_WINNER,
        TRAJECTORY_QUALITY_WINNER,
        FOOT_ALTERNATION_QUALITY_WINNER,
        REQUIRED_PHASE_COVERAGE_WINNER,
        EXPRESSION_STRENGTH_WINNER
    ]

function required(required_flag, evidence):
    return evidence if required_flag == YES else NOT_APPLICABLE

ASSERT identical_criteria_and_render_conditions_for_both_candidates()
ASSERT missing_evidence_means_UNKNOWN_not_defect()
ASSERT no_universal_threshold_decides_quality()
ASSERT numerical_evidence_never_replaces_opened_visual_evidence()
ASSERT every_quality_criterion_returns_winner_confidence_and_evidence()
```
