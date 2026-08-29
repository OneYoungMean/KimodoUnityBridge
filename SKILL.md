---
name: kimodo-unity-bridge
description: Install and use KimodoUnityBridge for character-animation recognition, comparison, and generation while deferring exact commands and parameters to the maintained API help.
---

# KimodoUnityBridge

```pseudo
// Shared states / 公共状态
#define YES             1
#define NO              0
#define UNKNOWN        -1
#define NOT_APPLICABLE -2

API_HELP = "Command/help.json"
```

## Installation / 安装

```pseudo
PACKAGE_IMPORTED = project_contains_package()
UNITY_READY      = unity_import_and_compile_completed()
FIRST_USE        = project_runtime_has_never_been_initialized()
UPGRADE          = package_version_changed()
RECOVERY         = runtime_diagnostics_report_missing_or_broken_components()

if PACKAGE_IMPORTED != YES:
    add_package_to_target_unity_project()

wait_until(UNITY_READY == YES)

if FIRST_USE == YES or UPGRADE == YES or RECOVERY == YES:
    install_or_refresh_project_runtime(
        entry = read(API_HELP, section = "installation")
    )

if ordinary_compile_or_import_or_editor_restart() == YES:
    // Ordinary lifecycle events do not request another installation.
    // 普通编译、导入或 Editor 重启不触发重复安装。
    skip_runtime_installation()

WORKFLOW_READY =
    PACKAGE_IMPORTED == YES
    and UNITY_READY == YES
    and required_project_components_available() == YES

ASSERT WORKFLOW_READY == YES before running animation work
```

## Task router / 任务路由

```pseudo
#define ASKS_RECOGNITION <YES|NO>
#define ASKS_COMPARISON  <YES|NO>
#define ASKS_GENERATION  <YES|NO>

if ASKS_RECOGNITION == YES:
    READ_AND_EXECUTE("skills/recognize.md")

if ASKS_COMPARISON == YES:
    READ_AND_EXECUTE("skills/compare.md")

if ASKS_GENERATION == YES:
    READ_AND_EXECUTE("skills/generate.md")

// Combined requests follow data dependency, not wording order.
// 组合请求按结果依赖顺序执行，而不是按句子出现顺序。
if ASKS_GENERATION == YES and
   (ASKS_RECOGNITION == YES or ASKS_COMPARISON == YES):
    inspect_existing_evidence_before_generating_correction()

if ASKS_RECOGNITION == NO and
   ASKS_COMPARISON == NO and
   ASKS_GENERATION == NO:
    return unsupported_or_needs_clarification
```

## Decision protocol / 决策协议

```pseudo
// REQUEST_* is filled only from the user's request and established project state.
// REQUEST_* 只能由用户请求和已确认的项目状态填写。
REQUEST_* = <YES|NO|UNKNOWN>

// EVIDENCE_* starts UNKNOWN and changes only after the returned evidence is read.
// EVIDENCE_* 初始为 UNKNOWN，只能在读取实际返回证据后改变。
EVIDENCE_* = UNKNOWN

ASSERT filenames_internal_labels_candidate_order_are_not_evidence()
ASSERT unstated_source_target_range_pose_path_constraint_is_never_invented()
ASSERT completed_results_are_preserved()
ASSERT corrections_create_derived_results()

if returned_visual_exists() == YES and visual_was_not_opened() == YES:
    EVIDENCE_VISUAL = UNKNOWN
    NEVER return passed

if evidence_is_static_only() == YES:
    EVIDENCE_PLAYBACK_CONTINUITY = UNKNOWN
    EVIDENCE_SLIDING            = UNKNOWN
    EVIDENCE_POPPING            = UNKNOWN
    EVIDENCE_ACCELERATION       = UNKNOWN
    EVIDENCE_VELOCITY_CONTINUITY = UNKNOWN

if operation_is_unsupported() == YES:
    return unsupported

if required_evidence_contains(UNKNOWN) == YES:
    return not_verified
```

## API help / API 帮助

```pseudo
at task start:
    schema = read(API_HELP)

before every API call:
    validate(
        command_name,
        required_fields,
        optional_fields,
        enum_values,
        defaults,
        nested_objects
    )

if prose_guidance_conflicts_with(runtime_result_or_error):
    follow(runtime_result_or_error)
```
