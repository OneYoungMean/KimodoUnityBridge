---
name: kimodo-unity-bridge
description: Install KimodoUnityBridge and route Unity animation work through its cooperating capability tools.
---

# KimodoUnityBridge

根 Skill 只负责安装门槛、任务入口、工具编排和统一结果；公共规则见 `tools/common.md`，具体命令参数由各工具在调用边界校验。

```pseudo
# Shared states / 公共状态
#define YES             1
#define NO              0
#define UNKNOWN         -1
#define NOT_APPLICABLE  -2

WORKFLOW_READY = UNKNOWN
```

## Installation gate / 安装门槛

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
    install = kimodo_install_server({})
    install_request_id = install.request_id
    poll kimodo_get_generation({request_id: install_request_id})
        until status in {"done", "error"}
    ASSERT status == "done"

# 普通编译、导入或 Editor 重启不重复安装。
WORKFLOW_READY = (
    PACKAGE_IMPORTED == YES
    and UNITY_READY == YES
    and required_project_components_available() == YES
)

ASSERT WORKFLOW_READY == YES before animation work
```

## Capability tools / 能力工具

工具按能力协作，不按单个命令拆分：

| Tool | 责任 | 入口文档 |
|---|---|---|
| common | Session、证据、状态和报告公共规则 | `tools/common.md` |
| session | 创建/复用/关闭 Session，加载角色、动画和 Animator | `tools/session.md` |
| generation | 生成、轮询、取消，以及 Record/Retarget 派生结果 | `tools/generation.md` |
| recognition | 根据明确语义识别动作 | `tools/recognition.md` |
| comparison | 在相同证据条件下比较两个候选 | `tools/comparison.md` |
| pose | 创建、编辑和复用 External Pose | `tools/pose.md` |

命令目录只在调用边界使用；不要把完整参数结构复制到工具文档中。

## Orchestration loop / 总编排 Loop

```pseudo
function run_kimodo_task(request):
    intent = classify_intent(request)
    tool = route_to_capability_tool(intent)

    ensure_installation_gate()
    session = tool.prepare_session(request)
    inputs = tool.resolve_safe_session_names(session, request)
    inputs = tool.validate_command_arguments(inputs)

    result = tool.execute(inputs)
    evidence = tool.read_returned_evidence(result)

    if tool.supports_bounded_correction() and evidence.requires_correction():
        preserve_completed_output()
        result = tool.execute(derive_one_supported_correction(inputs, evidence))
        evidence = tool.read_returned_evidence(result)

    return unified_report(result, evidence)
```

路由至少覆盖 installation、Session、generation、recognition、comparison、pose、record 和 retarget；未匹配到能力时才返回 `unsupported` 或 `needs_clarification`。

## Shared execution rules / 公共执行规则

- 只使用运行时返回的安全角色名、动画名和 `{track,index}` 引用；生成结果的 `path` 是资产元数据，不是 Session Clip handle。
- `session_get_or_create` 和 `session_add(kind=character)` 必须以当前活动场景中用户打开/选中的 `Animator` 所属角色为权威来源；不要从保存的 prefab 路径猜测角色。已有 prefab 实例直接使用，不创建持久化 prefab 副本；仅当该场景角色不是 prefab 时，才在请求的输出目录创建一个 prefab 并继续使用它。
- `session_get_or_create` 会建立专用可见 Session 根 GameObject；角色复制到该对象下时保留源角色组件（包括已有的 `CharacterController`），仅清空 Session 副本的 `Animator.runtimeAnimatorController`，后续制作、采样、分析和渲染均限定在该对象下。根对象不保存到场景，关闭或切换时仅禁用。
- 只要生成请求点名动作（例如 walk/run），生成前必须检查当前场景对应 Session 中是否已有语义匹配的角色/动画；修复、改进、替换、续作或变体请求绝不能跳过这一步。找到的动画先作为上下文证据分析，只有请求明确要求参考、复用或约束时才传入生成约束。
- 视觉结论必须建立在实际打开的返回图像上；静态证据不足时不得报告视觉通过。
- 已完成 Clip 不覆盖；修正、Record、Retarget 和生成变体均追加派生 Clip。
- 安装和生成都返回 `request_id`，并通过 `kimodo_get_generation` 轮询。安装终态为 `done` 或 `error`；生成终态为 `completed`、`failed` 或 `canceled`。轮询必须有固定间隔和总超时；过期或未知请求按失败/未验证报告。
- assembly reload、Editor 退出、切换场景或进入 Play Mode 导致的取消必须如实报告，不声称自动恢复。
- 工具报告至少包含 `result`、`output`、`criteria`、`evidence`、`unverified`；工具可附加专属字段。
- 请求未声明的 source、target、range、pose、path 或 constraint 不得自行补全。

需要获取原始 Unity 数据时，使用 `session_get_raw`。
