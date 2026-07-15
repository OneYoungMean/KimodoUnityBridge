# ARDY Unity / QuickServer 原子窗口接入计划

## 0. 文档状态

- 状态：已按现有 Unity 与 QuickServer 源码修订，待实施。
- 本文只描述计划，不执行代码改造。
- 范围：`KimodoUnityBridge`、`NvlabKimodoQuickServer~` 与 ARDY 推理后端。
- 核心目标：Unity 管理时序状态并拼接固定 Horizon；Python 只执行可排队、可取消、可重试的原子生成任务。
- 首个目标：ARDY G1 模型；具体 checkpoint 名称与能力值在接入模型 registry 时固定。

本次修订实际核对了以下链路：

- `KimodoPlayableClip : AnimationPlayableAsset`
- `KimodoPlayableClipGenerationExecutionService`
- `KimodoPlayableClipGenerationHostService`
- `KimodoEditorGeneratePipeline`
- `KimodoBridgeCommand`
- `KimodoRuntimeMotionDriver`
- `KimodoRuntimeMotionPlayer`
- `BridgeProtocolClient`
- `KimodoBridgeService`
- `NvlabKimodoQuickServer~/PARAMETERS.md`
- ARDY 与 Kimodo 两套 `constraints.py` / `motion_rep`

---

## 1. ARDY Constraint 数量与 Kimodo 的差异

### 1.1 精确计数

ARDY 原生有两种正确的计数口径：

- **3 个结构实现族**：`Root2DConstraintSet`、`FullBodyConstraintSet`、`EndEffectorConstraintSet`。
- **7 个可序列化 `type`**：`root2d`、`fullbody`、`end-effector`、`left-hand`、`right-hand`、`left-foot`、`right-foot`。

后四种是 `EndEffectorConstraintSet` 的命名特化，不是四套完全不同的 conditioning 机制。

| ARDY 原生 `type` | 结构族 | 实际约束内容 |
|---|---|---|
| `root2d` | Root2D | 指定帧的 root X/Z，可选 heading |
| `fullbody` | FullBody | 全身 global positions，加 root X/Z、root Y、heading |
| `end-effector` | EndEffector | 自定义 joint 集合的 global positions / rotations，加 root 信息 |
| `left-hand` | EndEffector 特化 | 左手相关 joint 集合 |
| `right-hand` | EndEffector 特化 | 右手相关 joint 集合 |
| `left-foot` | EndEffector 特化 | 左脚相关 joint 集合 |
| `right-foot` | EndEffector 特化 | 右脚相关 joint 集合 |

ARDY 的 motion representation 最终可以填入 5 类 conditioning 通道：root X/Z、heading、root Y、global joint positions、global joint rotations。它们是底层信号，不应再被算成 5 种面向协议的 Constraint。

### 1.2 与 Kimodo 的共同点

当前 Kimodo 源码同样是 **3 个结构族、7 个序列化 `type`**，名字也相同。因此不需要为 ARDY 重新发明 `PoseConstraint`、`TrajectoryConstraint` 等额外类型。

两边当前的 `fullbody` 都有一个容易误读的细节：JSON 中会保存 local joint rotations，用它们通过 FK 还原 global positions；但 `update_constraints()` 都明确没有把 full-body global rotations 写入 conditioning。真正同时写入 position 与 rotation conditioning 的是 `EndEffectorConstraintSet`。

### 1.3 需要适配的差异

| 项目 | Kimodo | ARDY | 接入规则 |
|---|---|---|---|
| root 轨迹内部字段 | `smooth_root_2d` | 主要使用 `root_2d`，反序列化兼容 `smooth_root_2d` | Unity 继续输出现有字段，ARDY adapter 统一转换 |
| Root2D heading | 当前 Unity/Kimodo 链路发送二维 heading 向量 | ARDY `Root2DConstraintSet.update_constraints()` 按弧度角再转 cos/sin | adapter 必须把二维向量转为标量 angle，不能直接透传 |
| `fullbody` | positions + root/heading；rotations 不直接进入 conditioning | 相同 | 无需为 ARDY改变 Unity 类型 |
| `end-effector` | positions + rotations + root/heading | 相同 | 复用现有 JSON 结构 |
| 手脚快捷类型的 joint 集合 | 当前类常量只有对应手或脚 | ARDY 当前类常量包含对应手/脚和 `Hips` | 以各后端原生类定义为准，并加入 fixture 测试 |
| Skeleton 兼容 | 有 SOMA30/SOMA77 转换与较完整 device/dtype 处理 | 以 ARDY G1/Core skeleton 为主 | 第一阶段只接受与目标 ARDY 模型完全一致的 rig/fps |
| 生成语义 | 对一次请求的整段序列做 inpainting | history 之后生成固定 Horizon，并可读取 future constraints | adapter 负责 history offset 与 future frame rebase |

结论：**两边 Constraint 名称高度兼容，但 JSON 不能不经检查地直接共用**。第一阶段保留 Unity 现有 exporter，差异集中在 Python ARDY adapter。

### 1.4 ClipConstraint 的计数位置

`ClipConstraint` 不是 ARDY 原生 `TYPE_TO_CLASS` 中的第 8 个类，而是本集成协议增加的统一 Constraint：

```text
Bridge Constraint（8 个可见 type）
├── clip                 → ARDY init_history_sequence
├── root2d               → future observed_motion / motion_mask
├── fullbody             → future observed_motion / motion_mask
├── end-effector         → future observed_motion / motion_mask
├── left-hand            → future observed_motion / motion_mask
├── right-hand           → future observed_motion / motion_mask
├── left-foot            → future observed_motion / motion_mask
└── right-foot           → future observed_motion / motion_mask
```

因此：

- 问“ARDY 原生有多少种”时，答案是 3 个结构族、7 个序列化类型。
- 问“接入后的统一协议有多少种”时，答案是上述 7 种加 `ClipConstraint`，共 8 种。
- `ClipConstraint` 表示一段满足 token 对齐要求的显式 history；单帧初始姿势应使用生成区第 0 帧的 `fullbody` constraint，不伪装成 clip。Clip 在模型内部单独映射到 `init_history_sequence`。

---

## 2. 已确认的架构决策

### 2.1 Unity 是时序状态的唯一权威

Unity 负责：

- 当前播放段、已提交边界与尚可替换的未来段。
- prompt、future constraints 与本地 request version。
- 生成结果队列、取消、过期结果丢弃和窗口拼接。
- Unity 只保留生成后的 clip/MotionPacket cache 与服务器返回的 handle，不保留 normalized flattened tensor。
- Timeline 为每个 ARDY 窗口显式构造 ClipConstraint；外部/Authoring/重定向动作先写成现有 `flatbuf_motion_v1 / KMB1 MotionPacket` 文件，窗口循环内部的已生成前缀只发送 QuickServer 返回的 handle。
- Runtime 保存已提交/排队 segment 对应的 handle，并在每次 ARDY generate 时显式发送滚动 history handle 列表。

Python 不保存角色播放位置、constraint revision 或“当前连接上一结果”。它只维护 handle → 现有 KMB1 MotionPacket 文件的技术缓存；读取 handle/file 时用 root positions 与 local rotations 有损重建 ARDY normalized history，velocities 与 foot contacts 由 motion representation 重新计算。handle 被淘汰后请求直接失败，Unity 不自动重试或 fallback。

### 2.2 Python 只执行原子窗口

```text
显式 ClipConstraint（实际 clip 数据或一个/多个 handle）
                    + Prompt + Future Constraints + Seed
                                 ↓
                         一个 checkpoint Horizon
                                 ↓
                  新增窗口 MotionPacket + 新 clip handle
```

每个 ARDY 请求只使用本次 `constraints_json` 中显式提供的 ClipConstraint；没有 clip 就从空 history 开始。TCP 重连、多个角色和 Timeline/Runtime 交错不会改变请求语义。模型与 text embedding 可缓存，但不存在连接级隐式 history。

### 2.3 Horizon 是模型能力

- `gen_horizon_len`、原生 FPS、`num_frames_per_token` 和最大上下文来自模型 profile/checkpoint。
- 对 ARDY 而言 `generate.duration` 不参与推理长度计算；返回永远是 checkpoint 固定 Horizon。
- Timeline 的“目标 10 秒”是 Unity 外层任务长度，不是单个 ARDY 请求的 Horizon。
- Unity 按 `history + Horizon + future lookahead <= max_context_frames` 选择最近 K 个 clip handles；正常连续生成不再只保留一个 Horizon。

### 2.4 流式粒度

一个扩散窗口完成前只流式返回 `queued/loading/progress/cancelling` 状态；完成后一次返回该窗口的 MotionPacket。Runtime 的无限流由 Unity 连续提交原子请求形成，不是 Python 在一次请求中无限生成。

---

## 3. 对应现有 Unity 链路的总体架构

### 3.1 Timeline / Authoring 真实链路

仓库中的实际类型是 `KimodoPlayableClip : AnimationPlayableAsset`，当前调用链是：

```text
KimodoPlayableClipEditor
  → KimodoPlayableClipGenerationExecutionService
  → KimodoPlayableClipGenerationHostService.BuildRequest
  → KimodoEditorGeneratePipeline
  → KimodoBridgeCommand : IKimodoGeneratePipeline
  → KimodoBridgeService.GenerateAsync
  → BridgeProtocolClient
  → QuickServer cmd=generate
  → KimodoRawMotionData
  → 现有 Bake / Retarget / Writeback
```

ARDY 不新增一条平行 Timeline 链路。只在 `KimodoEditorGeneratePipeline` 内按 model profile 选择：

- Kimodo：保持当前单次整段 `generate`。
- ARDY：由一个 pipeline 内部的窗口协调 helper 多次调用现有 `KimodoBridgeCommand`，合并 `KimodoRawMotionData` 后继续走原有 Bake / Retarget / Writeback。

`KimodoPlayableClip.generationFrames` 继续表示最终目标 AnimationClip 长度；它不再被误当成 ARDY 单窗口长度。

### 3.2 Runtime 真实链路

```text
KimodoRuntimeMotionDriver.RunSchedulerLoopAsync
  → MaybeQueueNextGeneration
  → GenerateNextSegmentAsync
  → KimodoGenerationRequestDto
  → KimodoBridgeService / BridgeProtocolClient
  → QuickServer cmd=generate
  → KimodoRuntimeGeneratedSegment
  → KimodoRuntimeMotionPlayer queue / playback
```

`KimodoRuntimeMotionDriver` 已经具备：

- `generationInFlight` 与串行 segment scheduler。
- `segmentIndex`。
- `generationRequestVersion` 与 stale result 拒绝。
- `activeGenerationCts` 与服务器 `cancel(task_id)`。
- queued segment 清空和 segment index 回退。
- prompt staging/locking。
- staged/pending constraints。
- future generation refresh。

所以不新增 `ArdyWindowScheduler` 或第二套 motion stream state。ARDY 接入现有 segment 抽象：

- `segmentIndex` 就是 ARDY window index。
- `generationRequestVersion` 继续作为 Unity 本地 revision。
- `pendingConstraintSamples` 继续形成 future constraints。
- 当前仍有效 prefix（已提交或仍保留的 queued segment）的 handles 形成 `ClipConstraint`；被清队列的 handle 不再进入新请求。
- ARDY 返回的一个 Horizon 与 handle 一起封装为 `KimodoRuntimeGeneratedSegment` 并交给现有 player。

当前 `nextConstraintPoses + loopHint` 是 Kimodo 用少量 fullbody pose 连接独立 segment 的方案。ARDY 已有 dense ClipConstraint，不应再把同一尾段反向放成 future fullbody overlap；该路径对 Kimodo 保持不变，对 ARDY 禁用或忽略。

### 3.3 QuickServer 真实链路

`PARAMETERS.md` 与实现确认当前只有：

- `generate`
- `cancel`
- `quit`

同一 TCP 连接可连续发送命令；任务以 `task_id/id` 为真相；中间态为 `queued/loading/progress/cancelling`；终态为 `done/error/cancelled`；FlatBuffer 响应是 JSON header 后紧跟该任务的 binary payload。

ARDY 必须接入这条现有队列，而不是建立第二套命令或 socket server。

---

## 4. 模型识别与 Horizon 契约：不新增命令

### 4.1 不增加 `describe_model`

第一阶段不增加 `describe_model`。固定能力进入现有 model registry：

| Profile 字段 | 用途 |
|---|---|
| `model_name` / aliases | 继续使用现有 `generate.model` 选择模型 |
| `backend` | QuickServer 内部选择 Kimodo 或 ARDY executor，不进入新命令 |
| `source_fps` | constraint 时间换算与结果校验 |
| `horizon_frames` | Unity 每次期望的固定窗口 |
| `frames_per_token` | ClipConstraint 尾段裁切对齐 |
| `max_context_frames` | history + generation + future constraint 校验 |
| `rig_profile` | joint order / skeleton 校验 |
| `max_diffusion_steps` | 在进入 CUDA 推理前拒绝越界 denoising steps |
| `cfg_text_weight` / `cfg_constraint_weight` | 固定 ARDY text/constraint guidance 语义 |
| `motion_rep_fingerprint` | checkpoint、normalization stats、feature layout 与 handle 兼容性校验 |
| `postprocess` | G1 第一阶段固定为禁用，与 ARDY 官方生成路径一致 |

Unity 侧把当前 `SupportedModelNames` 从纯字符串列表扩展为可查询 profile；QuickServer 的 model/asset registry 保存服务器侧 profile。服务器加载真实 checkpoint 后必须断言 profile 与 `model.motion_rep.fps`、`model.gen_horizon_len`、`model.num_frames_per_token`、`model.diffusion.num_base_steps` 一致。ARDY model 本体不假定存在 Kimodo 的 `model.fps` / `model.name` 属性，adapter 负责输出元数据。

### 4.2 正常响应就是能力校验

Unity 从返回的现有 `flatbuf_motion_v1` 读取：

- `model_name`
- `fps`
- `num_frames`
- `num_joints` / `joint_names` / `joint_parents`

ARDY 每次返回帧数必须等于 profile 的固定 `horizon_frames`。不一致时 Unity 把该结果视为错误，不拼入队列。

ARDY 响应 header 额外返回 `clip_handle`、`motion_rep_fingerprint` 与 `resolved_seed`。Unity 必须拒绝 fingerprint 不匹配的结果；handle 只标识服务器缓存中的 immutable KMB1 Horizon，不进入 MotionPacket FlatBuffer schema。

### 4.3 `duration` 的兼容语义

现有 `generate.duration` 保留：

- Kimodo backend：仍决定本次整段生成长度。
- ARDY backend：字段为兼容现有 DTO 而保留，但数值无效；服务器忽略它并固定返回 checkpoint Horizon。Unity 可填 `horizon_frames / source_fps`，只用于日志。
- Timeline 的总目标时长只存在于 Unity 外层窗口循环。

---

## 5. 原子窗口请求：复用现有 `generate`

### 5.1 协议变化预算

第一阶段严格限制为：

- **0 个新增 TCP command**。
- **0 个 framing 变化**。
- **0 个 session id / stream id**。
- **1 个新增 Constraint type：`clip`**。
- **0 个连接级 history 字段**。
- **1 组响应元数据：`clip_handle`、`motion_rep_fingerprint`、`resolved_seed`**。

不增加 `generate_window`、`client_context`、`stream_id`、`drop_history`、服务器 revision 或请求 binary attachment。

### 5.2 ARDY 请求示例

仍发送现有命令；为可读性，下面把 `constraints_json` 字符串内部的 JSON 展开表示：

```json
{
  "cmd": "generate",
  "task_id": "ardy-segment-10-v17",
  "id": "ardy-segment-10-v17",
  "model": "<registered-ardy-g1-model>",
  "prompt": "turn left and raise the right hand",
  "duration": 2.08,
  "diffusion_steps": 20,
  "seed": 184291,
  "constraints_json": "[ClipConstraint(handle), Root2DConstraint, ...]",
  "loop_hint": false,
  "segment_index": 10,
  "transition_duration": 0.0,
  "output_format": "flatbuf_motion_v1",
  "highvram": false,
  "force_cpu": false,
  "models_root": "...",
  "force_hf_download": false,
  "owner_pid": 12345
}
```

说明：

- backend 由现有 `model` registry 决定，不新增 `backend` 字段。
- `segment_index` 沿用现有字段，只用于日志/诊断，不是 Python session。
- `loop_hint` 和 `transition_duration` 在 ARDY 第一阶段不参与推理。
- ARDY history 完全来自本次 `constraints_json`；没有 clip 就从空 history 开始。
- Unity 在发送前解析随机 seed，ARDY 请求不发送 `null`；重试同一 window 复用同一 seed。
- request version 只保留在 `KimodoRuntimeMotionDriver`；旧任务通过 task id、取消令牌和 await 后的 version 比较被拒绝，不要求服务器回显 Unity 状态。

### 5.3 显式 History 选择

每个 ARDY `generate` 只按请求内容解析 history：

1. 按 `constraints_json` 出现顺序解析零个或多个 `type=clip`。
2. `format=ardy_handle_v1` 从 QuickServer handle cache 读取现有 `flatbuf_motion_v1 / KMB1` 文件。
3. `format=ardy_file_v1` 从受管路径读取同一种 KMB1 文件；用于 Timeline/外部 clip 和显式测试文件。
4. Python 从 KMB1 的 root positions/local rotation quaternions 重建 normalized tensor。
5. 多个 clip 沿时间维拼接，再按 `max_context_frames - horizon - future lookahead` 从头部裁到最近 K 个 token-aligned frames。
6. 没有 clip 时从空 history 开始。

Timeline 每个窗口都显式发送 ClipConstraint：初始/Authoring prefix 使用 `ardy_file_v1`；同一次窗口循环后续请求只发送前面结果的 handles。Runtime 每次 ARDY generate 都发送当前有效 prefix 的 handles；首段确实没有 history 时才不发送 clip。

不存在连接断开清历史、model switch 清历史或 stale connection history 的分支。handle 的 model/fingerprint/FPS/rig 不匹配，或 handle 不存在时，返回结构化 `clip_handle_not_found` 并结束本次请求；Unity 不自动改用 file、空 history 或重新提交。初始 root/heading 使用模型默认值；需要指定起点时使用 ARDY 自己的 `root2d` / `fullbody` constraint。

### 5.4 状态、取消与结果

完全沿用当前协议：

```text
queued/loading/progress/cancelling
                 ↓
done + flatbuf_motion_v1，或 error/cancelled
```

- `cancel` 继续按 `task_id/id` 命中 active 或 queued task。
- 最终 FlatBuffer 只包含新生成 Horizon，不包含输入 ClipConstraint。
- ARDY 把返回 Unity 的同一份 KMB1 payload 原子写入 QuickServer spool，并在响应 header 返回 immutable `clip_handle`；不创建 ARDY 专属 FlatBuffer。
- `error/cancelled` 不创建可见 handle；active cancel 必须在 denoising step 间被检查，最大延迟不超过一个 step。
- Kimodo 与 ARDY 都继续使用同一个 `KimodoBridgeGenerationResult` / `KimodoRawMotionData` 解码链路。

---

## 6. ClipConstraint 数据契约：放入 `constraints_json`

### 6.1 两种 Clip 引用，共用一种现有 FlatBuffer

**Handle 引用**用于 Timeline 内部续窗和 Runtime：

```json
{
  "type": "clip",
  "format": "ardy_handle_v1",
  "handle": "ardy:sha256:...",
  "start_frame": 0,
  "end_frame_exclusive": 20
}
```

**文件引用**用于 Timeline/外部 clip 和测试：

```json
{
  "type": "clip",
  "format": "ardy_file_v1",
  "path": "C:/project-managed-cache/history.kmb",
  "start_frame": 0,
  "end_frame_exclusive": 20
}
```

两种格式最终读取的文件内容完全相同：现有 `flatbuf_motion_v1 / KMB1 MotionPacket`，包含 FPS、frame/joint count、joint names/parents、root positions 与 local rotation quaternions。不新增或扩展任何 FlatBuffer schema。

- `ardy_handle_v1`：只接受 QuickServer 已签发的不可变 handle；handle 映射到 spool 中的 KMB1 文件。
- `ardy_file_v1`：读取客户端指定的 KMB1 文件。生产模式只允许 Unity/项目受管 cache 根目录；显式 test flag 可额外开放配置的测试目录。
- 两者都允许半开 slice `[start_frame,end_frame_exclusive)`；slice 后不足一个 token 时请求失败。
- Python 解析 KMB1 后通过 ARDY motion representation 有损重建 normalized history；velocities 和 foot contacts 重新计算。此误差是已接受的契约，但必须通过边界连续性测试。
- Unity 构造 `ardy_file_v1` 时，只把上一 clip 采样/重定向到 ARDY G1 skeleton/FPS，提取 root positions 与 local rotations，再用现有 MotionPacket writer 写 KMB1；Unity 不计算或保存 normalized features。该动作只能发生在新请求提交前，不能由 handle miss 自动触发。

### 6.2 一个请求允许多个 ClipConstraint

`constraints_json` 中允许零个、一个或多个 `type=clip`。服务器按它们在数组中的出现顺序解析、转换并沿时间维拼接：

```json
[
  {"type":"clip","format":"ardy_handle_v1","handle":"ardy:sha256:window_08"},
  {"type":"clip","format":"ardy_file_v1","path":"C:/project-managed-cache/window_09.kmb"},
  {"type":"root2d","frame_indices":[51],"smooth_root_2d":[[1.2,3.5]]}
]
```

每个 KMB1 先校验 model、FPS、joint names/parents、frame count 与数组长度，再重建 ARDY features；handle 还必须匹配 cache 记录的 `motion_rep_fingerprint`。拼接后保留尾部最近 K 个 history frames，并按 `frames_per_token` 对齐。Unity 永远不保存或传输 normalized tensor。

### 6.3 Handle 与 spool 生命周期

- ARDY `autoregressive_step()` 返回包含 history 的 normalized explicit motion tensor；server 只切出新 Horizon并 inverse 成现有 KMB1 payload。
- 同一份 KMB1 bytes 一次用于响应、一次原子写入 QuickServer spool；写入完成后才发布 handle。
- handle 使用 KMB1 内容哈希或等价的不可复用 immutable id，并在 cache record 中绑定 `motion_rep_fingerprint`。
- cache 以磁盘字节容量为主限制，按 last-access LRU 淘汰；不使用会让历史热点永久存活的纯 LFU。
- active/queued 请求引用的 handles 临时 pin；新 handle 有最短保留期，不能刚返回就被淘汰。
- Unity 不发送 handle delete；handle 生命周期完全由 QuickServer quota/LRU 管理。
- handle miss 返回全部缺失 handles并结束请求；Unity 不自动用 file、clip 重定向或空 history 重试。
- 使用现有 KMB1 identifier 与严格 vector-length 校验，限制单文件字节数，并在启动/空闲时清理损坏、过期和超过 quota 的文件。
- ClipConstraint 最后一帧是下一窗口的时间边界；新 Horizon 不重复该帧。

如果 Runtime 使用 `EffectiveLastFrameIndex` 裁掉 trail，显式 history 也必须在同一帧结束。future constraints 仍以“新 Horizon 第 0 帧”为 origin，Python 在构造 mask 时加上最终拼接后的 history 长度。

### 6.4 第一阶段仍不增加 binary request attachment

第一阶段请求只在 `constraints_json` 中发送：

- QuickServer 签发的 opaque handle；或
- Timeline/外部输入所需的 KMB1 文件路径。

不把 motion bytes 塞进 JSON，也不修改 TCP framing。`ardy_file_v1` 在生产模式只读取配置的 Unity/项目受管目录；test flag 只增加额外测试目录，不改变文件格式。跨机器部署成为真实需求时，再给现有 `generate` 增加 binary request attachment。

### 6.5 Constraint 合并与优先级

Clip 与 future constraint 不在同一时间域：Clip 独占 history。rebase 后落入 history 区域的 `fullbody/root2d/end-effector` 视为调用错误，不靠“Clip 覆盖”掩盖。

生成区和 future lookahead 按 conditioning channel 合并：

- root 通道：`fullbody > root2d > end-effector` 附带的 root X/Z、root Y 与 heading。
- joint position/rotation：`fullbody > end-effector`。
- 多个 end-effector 对不同 joint/channel 合并；只有同一 frame、同一 joint、同一 channel 冲突时，按 `constraints_json` 出现顺序由后者覆盖。
- adapter 在创建 ARDY ConstraintSet 前完成冲突解析，不能依赖 ARDY 当前重复 index 的隐式选择行为。

---

## 7. `KimodoRuntimeMotionDriver` 接入规则

### 7.1 复用现有状态

| ARDY 所需概念 | 现有 Unity 对应物 |
|---|---|
| window index | `segmentIndex` |
| generation in flight | `generationInFlight` |
| 本地 revision | `generationRequestVersion` |
| active cancellation | `activeGenerationCts` + `cancel(task_id)` |
| future motion buffer | `KimodoRuntimeMotionPlayer` queued segments |
| future user constraints | `pendingConstraintSamples` |
| stale result rejection | `requestVersion != generationRequestVersion` |
| window playback | `KimodoRuntimeGeneratedSegment` |
| history reference | `KimodoRuntimeGeneratedSegment` 保存 `clipHandle` / fingerprint |
| Unity generated cache | 保存服务器原始 `KMB1 MotionPacket` bytes，不保存 normalized tensor |

只需要增加“从有效 segment handles 生成滚动 ClipConstraint 列表”的小型 serializer/state 引用，不建立第二套 scheduler。原始 MotionPacket bytes 用于播放、缓存和显式 `ardy_file_v1` 构造，不用于 handle miss 自动重试。

### 7.2 正常连续生成

1. `RunSchedulerLoopAsync` 继续启动首段并轮询。
2. `MaybeQueueNextGeneration` 继续保证队列与 in-flight 规则。
3. ARDY profile 决定本次固定 `duration` 展示值和期望帧数。
4. 新生成链首段没有有效 history 时不发送 clip；后续每个请求都从当前有效 prefix 选取最近 K 个 handles，构造成 `format=ardy_handle_v1` 的 ClipConstraint。
5. `BuildNextConstraintsJson` 把 handles 与 pending future constraints 合并为同一个 JSON 数组；history budget 同时扣除 Horizon 与 future lookahead。
6. 使用现有 `KimodoGenerationRequestDto` 和 `GenerateAsync`。
7. 收到结果后先验证 version、model、fps、rig、fingerprint 与固定 Horizon。
8. 合法结果的原始 KMB1 bytes、解析后的 MotionPacket 与 `clip_handle` 一起封装进 `KimodoRuntimeGeneratedSegment` 并 enqueue。

### 7.3 Prompt / Constraint 更新

继续复用 `RefreshUpcomingGenerationAsync`：

1. `generationRequestVersion++`。
2. 清空尚未播放的 queued segment，并回退 `segmentIndex`。
3. 如有 in-flight 任务，触发现有 cancel。
4. 等待 generation slot。
5. 从仍保留的已提交 prefix 选择最近 K 个 handles；只有调用前已明确没有可用 handle 的外部/重定向 clip，才预先写成 `ardy_file_v1`。
6. 新请求显式携带 handles、完整 prompt、future constraints 与确定的 seed；被取消的旧任务即使迟到，也没有连接 history 可以污染。
7. 迟到结果由现有 version 检查丢弃。

Python 不需要 `update_constraint`。正常直线生成、branch、revision refresh 与重连都使用 explicit ClipConstraint。服务器返回 `clip_handle_not_found` 后，本次 Runtime generation 直接失败并保留错误状态，不自动提交第二个请求。

### 7.4 只改对应一段的边界

第一阶段的最小行为与当前 driver 一致：

- 正在播放的 current segment 视为已提交，不中途切断。
- queued / in-flight future segment 可以被取消并重新生成。
- 约束更新从下一个完整 Horizon 生效。

如果以后要求修改 current segment 中尚未播放的一小段，需要先在 Unity 增加“当前 segment 内 committed frame”以及 raw motion crop/replace；这仍是 Unity buffer 功能，不应转移到 Python session。ARDY 的最小重生成单位仍是完整 Horizon，Unity 可以在合并时裁切使用其中一部分，但不能要求 checkpoint 生成任意短窗口。

### 7.5 Kimodo 与 ARDY 连续性路径分开

- Kimodo：保留现有 `ConstraintOverlapPoses`、`loopHint`、trail trim 与 independent segment root offset 逻辑。
- ARDY：连续性的主要输入是 dense ClipConstraint；不再把同一 history 尾段复制造成 future fullbody overlap。
- ARDY 若启用 trail trim，最后一个 handle ClipConstraint 必须用 `end_frame_exclusive = EffectiveLastFrameIndex + 1` 表示与 playback 相同的尾边界；多个 clips 拼接后从头部裁成 token-aligned history，不改变尾帧。若剩余历史不足一个 token，则第一阶段拒绝该 trim 或禁用 ARDY trail trim。

---

## 8. Python ARDY executor

### 8.1 接入现有队列

保留 `quickserver_cli.py` 当前 client worker、task queue、task id、streaming status、cancel event 和 response writer。只在 runtime/model dispatch 与 `_execute_generate` 内部按 model registry 选择 Kimodo 或 ARDY executor。

### 8.2 单请求流程

```text
1. 从现有 generate 读取 model/prompt/seed/diffusion_steps/constraints_json
2. 校验 seed 非空、diffusion_steps 上限、model profile 与 motion_rep_fingerprint
3. 解析 constraints_json，按出现顺序分离零个或多个 type=clip
4. ardy_handle_v1 从 spool 读取 KMB1；handle miss 返回 clip_handle_not_found 并结束
5. ardy_file_v1 从受管路径读取相同 KMB1；test flag 只扩展可读目录
6. 校验 KMB1 model/fps/rig/frame/vector lengths，并从 root positions/local rotations 有损重建 normalized tensor
7. 多个 clip 沿时间维顺序拼接；没有 clip 时从空 history 开始
8. 按 history + Horizon + future lookahead budget 裁切并完成 token alignment
9. 按 conditioning channel 解析 fullbody/root2d/end-effector 冲突并规范化 future constraints：
   - smooth_root_2d → ARDY root_2d
   - Root2D heading vector → atan2(x, z) heading angle
   - frame_indices += history_frame_count
10. 根据 history + fixed Horizon + 最远 future constraint 构造对齐后的 num_frames
11. 使用 profile 固定的 cfg_text_weight/cfg_constraint_weight 调用 autoregressive_step(init_history_sequence=...)
12. ARDY cancel callback 在每个 denoising step 间检查 cancel_event
13. 从返回值中切出新 Horizon
14. inverse 为现有 flatbuf_motion_v1 / KMB1，并把同一 payload 原子写入 spool
15. 返回 done/task_id/id/byte_length/clip_handle/motion_rep_fingerprint/resolved_seed + KMB1 payload；MotionPacket schema/version 不变
```

### 8.3 Bounded handle cache

- handle cache 是 QuickServer 全局的、与 TCP 连接无关的技术缓存；不增加 `session_id` 或 stream registry。
- 每个 handle 只指向一个 immutable KMB1 Horizon，并在 cache record 中记录 model/fingerprint/FPS/rig/byte size/last access。
- handle 可跨重连使用；model switch 不删除文件，但 fingerprint 不匹配时拒绝读取。
- LRU 以总字节 quota 为边界；active/queued handles 临时 pin，清理不影响正在执行的任务。
- `error/cancelled` 不发布 handle；取消必须在写 spool 之前再次检查。
- Unity 不主动销毁 handle；`clip_handle_not_found` 是终态 error，不触发 Unity fallback 或重试。
- seed 在 Unity 请求中显式设置，服务器回显 `resolved_seed`；同一 window 重试复用相同 seed。
- G1 第一阶段明确禁用 postprocess；不能继承其他 ARDY skeleton 的默认行为。
- 模型和 text embedding 可继续缓存；不再增加连接 history cache。
- server 只返回新窗口，不替 Unity 决定 append、replace 或 committed boundary。

---

## 9. Timeline / `KimodoPlayableClip` 接入

### 9.1 保留现有 Authoring 入口

用户仍在 `KimodoPlayableClip` 上设置：

- `bridgeModelName`
- `motionPrompt`
- `generationFrames`
- `diffusionSteps`
- seed
- In/Out 与 Timeline markers 产生的 constraints

`KimodoPlayableClipGenerationHostService.BuildRequest` 仍负责形成最终目标 duration 和整段 constraints。ARDY 特殊逻辑放在后面的 pipeline，不把窗口状态塞进 PlayableAsset。

### 9.2 Editor pipeline 的 ARDY 窗口循环

```text
BuildRequest：目标 10 秒 + 整段 constraints
  ↓
KimodoEditorGeneratePipeline 检测 ARDY profile
  ↓
Window 0：把 Authoring/外部 prefix 写成现有 KMB1，以 ardy_file_v1 显式发送；无 prefix 时为空 history
  ↓
Window N：显式发送窗口循环当前滚动 prefix 的 ardy_handle_v1 ClipConstraints
  ↓
合并每次返回的新 Horizon，直到覆盖目标时长
  ↓
裁到目标时长 / 明确重采样
  ↓
进入现有 Analyze → Bake → Retarget → FinalizeGeneration
```

ARDY 窗口循环应是 editor pipeline 内部 helper，不复用 Runtime MonoBehaviour，也不新增 transport command。运行中的 handle 列表只属于本次生成任务；handle 元数据保存在 Unity 现有生成状态/cache 中，不写入或扩展 MotionPacket schema，也不创建独立 handle 资产。

正常窗口链保持严格顺序：上一条 `done` 后才提交下一条。每个窗口都显式发送 ClipConstraint；handle miss 令本次 Timeline 生成直接失败，不自动从 clip/file 重建和重提。用户重新发起生成时，新的任务可从实际 Timeline/Authoring clip 重新写 KMB1。

### 9.3 Timeline constraint 分窗

当前 Unity exporter 把时间按固定 30 FPS 转为 frame index；ARDY 原生 FPS 可能不同。因此实施时必须：

1. 给 `KimodoConstraintJsonExporter` 增加显式 export FPS，Kimodo 默认仍为 30。
2. Timeline marker 先以秒作为中间真相，再用 `floor(seconds * source_fps)` 映射到 ARDY frame index。
3. 每个 ARDY 请求选取当前窗口/模型 lookahead 可见的 future constraints。
4. 把它们 rebase 到“新 Horizon 第 0 帧”。
5. Python adapter 再统一加 history offset。
6. 全部窗口使用半开区间：window N 对应全局帧 `[N*H, (N+1)*H)`；`H-1` 属于当前窗口，`H` 与下一窗口 frame 0 是同一全局采样点。
7. 约束位于全局 `H` 时，可作为当前窗口 future lookahead，同时在下一窗口 rebase 为 frame 0；两次引用必须表示同一目标，不得复制或偏移一帧。

不能把当前 30 FPS `frame_indices` 原样传给 25 FPS ARDY。

Phase 4 同时统一 `N/fps` 覆盖时长与 `(N-1)/fps` 最后采样时间：raw motion 合并按 `[0,N)` 裁切；播放/Bake 若要覆盖完整 `N/fps`，需明确保持最后采样一帧或在 `N/fps` 写入重复末帧 key，不能混用两个 duration 定义。

Unity 世界坐标、Timeline clip-local 坐标与 ARDY model-space history 原点的转换必须在 Phase 4 实施前通过源码分析固定为公式和 golden fixture；在该结论确定前，不把 retarget 后 Humanoid 动画直接编码为 ARDY history。

### 9.4 输出、Seek 与倒放

- Editor 端合并完成后仍生成普通 AnimationClip，因此支持现有 Timeline seek、倒放和缓存。
- 最终 AnimationClip 可保留 ARDY 原生 FPS，也可明确重采样到现有 30 FPS bake 约定；不能静默把 25 FPS 帧号当 30 FPS。
- Unity 与 QuickServer 都只保存/读取现有 KMB1 MotionPacket cache，不创建 normalized history 文件。
- `KimodoEditorClipWritebackService` 只继续管理最终 AnimationClip；不扩展为 Python cache manager。
- Timeline 对每个成功窗口保留服务器原始 KMB1 bytes 作为 Unity 侧 generated cache；该 cache 可按 Unity 现有生成缓存策略清理。
- handle/fingerprint/seed 作为 KMB1 之外的生成元数据保存。Unity 不向 Python 发送 handle delete，spool 由 QuickServer quota/LRU 清理。
- 未来重新编辑或分支时，Unity 可在新任务提交前从上一 clip 重定向并写出 `ardy_file_v1`；这不是 handle miss 后的自动 retry。

---

## 10. 最小代码改动面

### 10.1 Unity

优先修改或扩展现有文件：

- `KimodoServerRuntimeUtil` / model selection：把 ARDY 固定能力加入 model profile。
- `KimodoConstraintJsonExporter`：支持显式 FPS，并能与 `clip` JSON 合并。
- `KimodoRuntimeMotionDriver`：按 profile 选择 Kimodo overlap 或 ARDY handle ClipConstraint 路径，并随 segment 保存 handle/fingerprint。
- `KimodoEditorGeneratePipeline`：对 ARDY 执行多次现有 bridge command 并合并 raw motion。
- `KimodoRawMotionUtility`：只补当前 pipeline 确实需要的 crop/concatenate，以及用现有 KMB1 schema 写入受管 `ardy_file_v1` cache。
- `KimodoBridgeGenerationResult` / `KimodoRuntimeGeneratedSegment`：增加 `clipHandle`、`motionRepFingerprint`、`resolvedSeed` 元数据。

允许增加少量职责单一的 helper，例如：

```text
KimodoMotionModelProfile
ArdyClipConstraintSerializer
ArdyEditorWindowGenerationCoordinator
```

不新增：

- `ArdyWindowScheduler`
- 第二套 Bridge service/client API
- session id / stream registry
- 新的 TCP command DTO

`KimodoGenerationRequestDto` 不增加 history/session 字段；ClipConstraint 仍放入已有 `constraints_json`。Bridge 只解析响应 JSON header 中新增的 `clip_handle`、`motion_rep_fingerprint`、`resolved_seed`；现有 MotionPacket FlatBuffer schema、version 与 framing 全部不变。

### 10.2 QuickServer / Python

建议最小增加一个 ARDY executor/adapter，并在现有 registry 和 generate dispatch 中接入：

```text
quickserver_cli.py                    # 保留命令/队列，增加 backend dispatch
quickserver_assets.py                 # 增加 ARDY model spec/profile
ardy_backend.py                       # load、KMB1→history adapter、handle spool、atomic generate
ardy/model/ardy_model.py              # autoregressive_step 增加 cooperative cancel callback
```

ARDY 必须安装到 QuickServer 的同一套 venv，不增加 worker 或第二套环境。当前依赖表有一个实施前必须解决的硬冲突：

- ARDY：Python `>=3.10`，`transformers==5.8.1`。
- QuickServer/Kimodo：Python `>=3.8`，`transformers==5.1.0`。

Phase 0 把 QuickServer Python 基线提升到 `>=3.10`，在同一 venv 中收敛一个 transformers 版本，并分别跑一次 Kimodo 与 ARDY 最小推理。只有两者都通过才进入协议实现；不预设“能 import 就算兼容”。

候选 venv 必须生成完整依赖 lock 并运行 `pip check`；除 transformers 外同时确认 ARDY 的 `numpy<2`、`torch>=2.4.0a0`、`peft>=0.19` 与 `gradio_client>=2.0` 不会破坏 Kimodo runtime。

---

## 11. 分阶段实施

### Phase 0：先固定骨架与单 venv

- [ ] 复用 `NvlabKimodoQuickServer~/tools/export_kimodo_neutral_poses.py` 的 `export_one()`，从 ARDY `ardy/assets/skeletons` 只导出 `g1skel34_neutral.json`。
- [ ] 用导出结果固定 joint name、parent、neutral position、root index 和单位，并建立 Unity G1 rig profile 校验。
- [ ] QuickServer venv 的 Python 基线提升到 `>=3.10`。
- [ ] 建立一个与正式 QuickServer venv 同构、可随时丢弃的候选 venv；不直接破坏当前可用 Kimodo 环境。
- [ ] 在候选 venv 中把 `transformers` 专门升级并固定为 ARDY 已验证的 `5.8.1`，处理 Kimodo 当前 `==5.1.0` pin 的最小兼容修改。
- [ ] 生成完整依赖 lock 并运行 `pip check`，覆盖 numpy/torch/peft/gradio_client 等交集，不只检查 transformers。
- [ ] 升级前记录当前 Kimodo 基线；升级后依次回归：模型加载、无约束 `generate`、带 `constraints_json` 的 `generate`、`cancel`、`flatbuf_motion_v1` 解析与现有 Unity bake 路径。
- [ ] 只有全部回归通过，才把该候选 venv 升格为唯一正式 QuickServer venv；未通过则先修 Kimodo 的 5.8.1 兼容性，不进入 ARDY 协议/骨架工作。
- [ ] 在正式单 venv、同一进程生命周期中分别完成一次 Kimodo 与一次 ARDY 最小推理。

验收：骨架 JSON 可被 Unity 读取且与 ARDY checkpoint 一致；Kimodo 在 `transformers==5.8.1` 下的全部最小回归通过；同一正式 venv 的 Kimodo 与 ARDY 最小推理都成功。

### Phase 1：Constraint 与 Clip 格式

- [ ] 固定 7 个 ARDY 原生 constraint type。
- [ ] 固定 `ardy_handle_v1` / `ardy_file_v1` 两种 JSON 引用；两者都读取现有 `flatbuf_motion_v1 / KMB1`，不新增 FlatBuffer schema。
- [ ] 多个 clip 按 `constraints_json` 出现顺序拼接。
- [ ] Python 从 KMB1 root/local rotations 有损重建 normalized history，并重新计算 velocities/foot contacts。
- [ ] handle 半开 slice 合法，effective-tail crop 保留相同尾帧并在拼接后从头部完成 token 对齐。
- [ ] 建立 Unity exporter → ARDY adapter fixture。
- [ ] 覆盖 `smooth_root_2d/root_2d` 与 heading vector/angle 转换。
- [ ] 按 channel 实现 `fullbody > root2d > end-effector root` 与 end-effector 同 joint 后者覆盖。
- [ ] ARDY constraint 只接受 Phase 0 固定的 ARDY G1 rig。

验收：handle/file KMB1 都能进入 `init_history_sequence`；允许的有损重建不产生边界跳变，多 clip 拼接帧序与冲突优先级正确。

### Phase 2：QuickServer ARDY 原子 `generate`

- [ ] 现有 model registry 可选择 ARDY。
- [ ] `duration` 被忽略并固定返回 Horizon。
- [ ] 无显式 clip 时始终从空 history 开始，不读取连接状态。
- [ ] 将响应使用的同一份 KMB1 payload 保存到 QuickServer spool，并返回 immutable `clip_handle` / fingerprint / resolved seed。
- [ ] handle cache 按字节 quota + LRU + active pin 管理；handle miss 返回结构化错误。
- [ ] handle miss 是终态 error；Unity 不 fallback、不 retry、不发送 handle delete。
- [ ] 校验 cfg text/constraint weights、diffusion steps 上限与 G1 postprocess 禁用。
- [ ] `autoregressive_step` 支持 cooperative cancel；`error/cancelled` 不发布 handle。
- [ ] 复用现有 `generate/cancel/quit`、progress、task id 与 MotionPacket。

验收：不新增命令即可用显式 handles 连续生成；多角色/多调用方共用连接不会串 history，重连后 handle 仍按 cache 契约工作。

### Phase 3：现有 Runtime Driver 接入

- [ ] 每次 ARDY generate 都发送当前有效 prefix 的最近 K 个 handle ClipConstraints；确实无 history 时为空。
- [ ] segment 保存 handle/fingerprint/resolved seed，cancel/revision 后只从仍有效 prefix 重建 handles。
- [ ] handle miss 时本次 Runtime generation 失败并保持错误状态，不提交第二个请求。
- [ ] 合并 pending ARDY future constraints。
- [ ] 禁用 ARDY duplicate overlap pose continuity。
- [ ] 复用现有 refresh/cancel/version/queue。
- [ ] 校验返回 Horizon、fps、rig 与 motion representation fingerprint。

验收：不新增 scheduler 连续运行至少 2 分钟；prompt/constraint 更新不会继承 stale history。

### Phase 4：Timeline 多窗口 Bake 与 writeback

- [ ] `generationFrames` 作为最终目标长度。
- [ ] editor pipeline 严格串行调用原子 `generate`。
- [ ] 每个 Timeline 窗口显式发送 ClipConstraint：初始实际 clip 写成 `ardy_file_v1 / KMB1`，内部续窗使用临时 handles。
- [ ] constraint 按 `floor(seconds * source_fps)`、半开窗口 `[N*H,(N+1)*H)` 分窗/rebase。
- [ ] 固定 `H-1 / H / 下一窗口 0` 的 lookahead 与重复引用规则。
- [ ] 统一 `N/fps` 覆盖时长与 `(N-1)/fps` 最后采样时间，明确末帧保持/重复 key。
- [ ] 固定 Unity world/clip-local → ARDY model-space history 原点转换并建立 golden fixture。
- [ ] raw motion 合并、精确裁切与显式重采样。
- [ ] 复用现有 bake/retarget/finalize。

验收：生成 10 秒 AnimationClip，窗口边界连续且无一帧时长偏差；handle miss 明确终止本次任务，用户新发起的任务可从实际 Timeline clip 重新建立。

### Phase 5：性能优化（最后）

- [ ] handle spool 读写、cache hit/miss 与多 clip 拼接 P50/P95。
- [ ] text embedding cache。
- [ ] 只有 KMB1 文件路径方案无法覆盖跨机器部署时，才给现有 `generate` 增加 binary request attachment。

---

## 12. 测试矩阵

### 12.1 Constraint 兼容

- [ ] 7 个原生 `type` 都能从 Unity JSON 被 Kimodo 读取。
- [ ] ARDY adapter 正确处理 `smooth_root_2d`。
- [ ] Root2D heading 二维向量正确转 angle。
- [ ] fullbody 不被误判为 rotation conditioning。
- [ ] ARDY 手/脚快捷类型包含预期 Hips 行为。
- [ ] custom `end-effector.joint_names` 正确。
- [ ] root channel 遵守 `fullbody > root2d > end-effector`。
- [ ] 多个 end-effector 对不同 joint 合并，对同 joint/channel 按后者覆盖。
- [ ] rebase 后落入 history 的 future constraint 被拒绝。

### 12.2 ClipConstraint

- [ ] 任意连接无 clip 时都从空历史开始。
- [ ] 同一连接、不同连接和多个角色交错请求都不共享隐式 history。
- [ ] 多个 handle/file KMB1 clip 按出现顺序拼接。
- [ ] handle 可跨 TCP 重连读取；不存在、淘汰或 fingerprint 不匹配时返回结构化错误。
- [ ] active/queued 引用的 handle 不会被 LRU 清理。
- [ ] cancelled/error task 不发布 handle，不留下可见临时文件。
- [ ] 磁盘满、原子 rename 失败和写入中断时返回 error，不发布半成品 handle。
- [ ] 长时间未使用 handle 被 LRU 淘汰后，本次请求失败且 Unity 不自动 retry。
- [ ] `ardy_file_v1` 与 handle 缓存文件都是现有 KMB1 bytes，Unity/Python 可交叉读取。
- [ ] 生产 file path 只能位于项目受管 cache；test flag 只额外开放测试目录。
- [ ] 最小/最大 token-aligned history。
- [ ] history 过长或不对齐。
- [ ] handle/KMB1 model/FPS/rig/fingerprint/vector length 不匹配。
- [ ] 超大、损坏、过期 KMB1 spool 文件。
- [ ] KMB1→ARDY 有损重建的 root、foot 与 joint 边界连续性。
- [ ] effective tail crop 与 playback boundary 一致。

### 12.3 现有协议回归

- [ ] Kimodo `generate` 行为不变。
- [ ] 同一 TCP 连接连续 ARDY `generate`。
- [ ] queued/active task cancel。
- [ ] active cancel 最大延迟不超过一个 denoising step，且不发布 handle。
- [ ] model switch、disconnect 与 transparent reconnect 不改变显式 handle history。
- [ ] `task_id/id` 在所有状态中一致。
- [ ] FlatBuffer header 与 payload 不被其他任务穿插。
- [ ] server 完成后、Unity 收到 payload 前断线；同 seed/history 重试同一 window。
- [ ] `quit` 行为不变。

### 12.4 Runtime 更新

- [ ] prompt 更新清空 queued future 并重新生成。
- [ ] constraint 更新只影响未提交 Horizon。
- [ ] 旧 request version 结果被丢弃。
- [ ] active cancel 后立即提交新请求。
- [ ] cancel/revision 后只使用仍有效 prefix handles，不继承 stale result。
- [ ] 两个 Runtime Driver 交错生成不会串 history。
- [ ] Timeline Bake 与 Runtime 同时运行不会串 history。
- [ ] Standalone Player 下 handle cache、file input、终态失败、重连与清理可用。
- [ ] current segment 播放完成与下一段 history 边界一致。

### 12.5 Timeline 帧与坐标

- [ ] `floor(seconds * source_fps)` 对整数、非整数秒和负/越界时间一致。
- [ ] 约束位于 `0 / H-1 / H / H+1` 时的分窗与 lookahead 正确。
- [ ] `N/fps` 覆盖时长、`(N-1)/fps` 末采样时间和末帧保持无一帧偏差。
- [ ] 25 FPS ARDY → 30 FPS Bake 的最终帧数、clip length 与约束时间一致。
- [ ] Unity world/clip-local → ARDY model-space 原点、heading 与 root trajectory golden fixture。
- [ ] 相同 history/prompt/seed 的重试结果满足定义的确定性。

### 12.6 连续性与性能

在每个边界记录：

- root position / velocity jump。
- joint quaternion angular jump。
- 足端位置、速度与接触跳变。
- 单 Horizon 生成时间。
- 剩余播放时间与 underrun。
- handle cache hit/miss、spool 保存/读取和多 clip 拼接耗时。
- spool 总字节数、LRU 淘汰数、损坏/过期清理数。

---

## 13. 明确延期的范围

第一阶段只实现 **ARDY skeleton 上的 ARDY constraint**：

- ARDY G1 模型只接收按 Phase 0 导出骨架构造的 G1 root/fullbody/end-effector constraints。
- 不把现有 SOMA constraint JSON 直接解释为 ARDY constraint。
- 不在第一阶段实现 SOMA → ARDY constraint 自动转换、自动 joint mapping 或任意 FPS/rig 转换。

后续确实需要 SOMA constraint 时，再单独实现转换层。最小可行路径是先把 SOMA constraint pose 生成成一帧临时 clip，经 Unity retarget 到 ARDY G1，再从该 G1 帧提取 ARDY constraint；它不阻塞当前原子窗口链路。

---

## 14. 完成定义

1. ARDY 与 Kimodo Constraint 的 3/7 计数和差异有 fixture 验证。
2. 不新增 TCP command、framing、session id、`drop_history` 或 ARDY 专属 FlatBuffer；`ardy_handle_v1` / `ardy_file_v1` 都复用现有 KMB1 MotionPacket。
3. Timeline 与 Runtime 都继续通过现有 `KimodoBridgeCommand → KimodoBridgeService → BridgeProtocolClient → generate` 链路。
4. `KimodoRuntimeMotionDriver` 复用现有 scheduler、queue、cancel 与 version，不存在平行 `ArdyWindowScheduler`。
5. ARDY `duration` 不控制长度，每次只返回 checkpoint 固定 Horizon。
6. 每个 ARDY 请求只使用显式 handle/file KMB1 ClipConstraints；Timeline/Runtime/多角色交错与重连不会串 history。
7. QuickServer 以 immutable handle、fingerprint、字节 quota、LRU 和 active pin 管理 KMB1 spool；Unity 不发送 handle delete。
8. Timeline 每个窗口显式发送 ClipConstraint；初始/外部 clip 用 `ardy_file_v1`，同一次内部窗口循环复用临时 handles，最终资产仍是普通 AnimationClip。
9. Runtime 每次生成发送最近 K 个有效 handles；handle miss 是终态失败，Unity 不 fallback、不 retry。
10. cfg text/constraint weights、diffusion steps 上限、resolved seed、G1 postprocess 与 cooperative cancel 均有测试。
11. `fullbody/root2d/end-effector` 按 conditioning channel 的优先级和冲突覆盖规则有 fixture。
12. Phase 4 固定 floor 帧换算、半开窗口、`N/fps` 时长与 Unity→ARDY history 原点公式。
13. ARDY 与 Kimodo 在同一 QuickServer venv 中通过完整 lock、`pip check`、最小推理与连续生成测试。
14. 10 秒 Timeline Bake 和至少 2 分钟 Runtime 连续生成通过连续性、取消、stale-result 与多调用方交错测试。
15. 现有 Kimodo `generate/cancel/quit`、KMB1 FlatBuffer schema/version/framing 和 bake 路径回归通过。
