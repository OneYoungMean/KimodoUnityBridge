# AI 工作流示例与 Pose/Constraint API

本文是当前命令协议的 AI 使用说明。Session Timeline 固定为 60 FPS；所有公开时间下标均为整数帧，区间统一使用 `[start_frame,end_frame)`。

## 核心语义

Pose 使用统一的二字段定位：

```json
{"source":"UnityChan","frame":75}
```

- `source` 是角色名时，这是 Timeline 上的只读 Pose；JSON 本身就是 Pose，不要调用 `pose_create`。
- `source` 是 `<角色>.Poses` 时，这是可通过 `pose_get`/`pose_set` 读写的 Pose。
- 需要修改只读 Pose 时调用 `pose_copy`。
- Pose 可以跨角色使用；Pose API 不负责 Retarget。
- 对外 Pose 数据只有真实 Root、Muscle 名称字典和 Foot IK，不暴露 Profile Skeleton、`RootT` 或 `RootQ`。

每个角色轨道下都有一个 Transform Override 子轨道：

```text
UnityChan
└─ UnityChan.Poses
```

Pose 子轨道只用于持久化可写 Pose。Constraint 不进入该轨道，只在 Generate 时写入实际 Clip。

## Pose API

读取角色第 75 帧：

```json
pose_get({"pose":{"source":"UnityChan","frame":75}})
```

复制为可写 Pose：

```json
pose_copy({
  "character":"UnityChan",
  "pose":{"source":"UnityChan","frame":75}
})
```

返回的真实位置类似：

```json
{"pose":{"source":"UnityChan.Poses","frame":0}}
```

修改 Muscle：

```json
pose_set({
  "pose":{"source":"UnityChan.Poses","frame":0},
  "data":{"muscles":{"Left Upper Leg Front-Back":0.45}}
})
```

直接创建可写 Pose：

```json
pose_create({
  "character":"UnityChan",
  "pose":{
    "root":{"position":[0,0,0],"rotation":[0,0,0,1]},
    "muscles":{},
    "foot_ik":{}
  }
})
```

不提供 `pose_delete`；可写 Pose 由 Session 管理。

## 内联 Constraint

Constraint 是 Generate 的匿名值对象，不创建、不命名、不缓存。普通 Constraint 包含相对帧、类型和 Pose：

```json
{
  "frame":0,
  "type":"fullbody",
  "pose":{"source":"UnityChan.Poses","frame":0}
}
```

Constraint 可以同帧重合，不存在全局名称或唯一性问题。

普通 Kimodo 类型包括：

```text
fullbody, root2d, left_hand, right_hand, left_foot, right_foot
```

AI 不使用 `in/out` 别名；开头和结尾语义直接由相对 `frame` 表达。本协议暂不处理 ARDY Clip Constraint。

Root2D 可以从 Pose 构造，也可以直接传 Position/Heading：

```json
{
  "frame":120,
  "type":"root2d",
  "position":[2.0,1.5],
  "heading":[0.707,0.707]
}
```

`frame` 始终表示未来生成 Clip 内的相对帧。

## 生成动画

Generate 直接接受内联 Constraint：

```json
kimodo_generate_animation({
  "character":"UnityChan",
  "prompt":"a short dance",
  "duration_frames":300,
  "constraints":[
    {"frame":0,"type":"fullbody","pose":{"source":"UnityChan.Poses","frame":0}},
    {"frame":299,"type":"root2d","position":[3.0,0.0],"heading":[0.0,1.0]}
  ]
})
```

执行顺序是：验证所有内联 Constraint → 读取 Pose 快照 → 创建 `KimodoPlayableClip` → 写入对应相对帧 → 调用生成 API。角色的 `.Poses` 来源只保存可写 Pose。

## Analysis 与截图

分析返回一组带注释的 Pose：

```json
kimodo_analyze({
  "character":"UnityChan",
  "start_frame":0,
  "end_frame":300
})
```

结果缓存在 `analysis_id` 下；Analysis 不创建 Pose 实体。
缓存的权威数据是 `Library/KimodoCache/Commands/analysis_<analysis_id>.json`，因此脚本编译造成的 Domain Reload 不会使它失效；AI 只传递短 `analysis_id`，无需持有整段 JSON。

统一截图 API 接受三种互斥输入：

```json
query_picture({
  "poses":[
    {"source":"UnityChan","frame":0},
    {"source":"UnityChan.Poses","frame":0}
  ],
  "resolution":512,
  "scale":1.0
})
```

```json
query_picture({"analysis_id":"<analysis_id>"})
```

```json
query_picture({
  "constraints":[{"frame":0,"type":"fullbody","pose":{"source":"UnityChan","frame":75}}]
})
```

截图使用只渲染临时 Pose 副本的方形相机，在同一世界坐标中合成右、上、前、3D 四个视角；编号按输入顺序，根节点按顺序连线。

## Root2D 路径

```json
kimodo_build_root2d_path({
  "shape":"turn",
  "duration_frames":300,
  "max_speed":2.5,
  "acceleration":2.5,
  "direction":"left",
  "turn_degrees":90
})
```

支持 `line`、`turn`、`s`、`circle`。转弯角度支持 `0/45/90/135/180`：

- `45/90/135` 使用三点二次贝塞尔。
- `0` 默认只返回前向的起止两点。
- `180` 默认只返回两点，起点 Heading 向前、终点 Heading 向后。
- S 曲线使用纯数学三次贝塞尔，圆使用解析圆公式。
- 路径不依赖 `com.unity.splines`。

## 常用工作流

### 基本生成

1. `session_open`
2. `query_current_session({"query":"characters"})`
3. `kimodo_generate_animation`
4. 轮询 `kimodo_get_generation`

全局只允许一个活动 Session；打开新 Session 前会关闭旧 Session。
Session 结构和内部动画标识写入 Timeline Metadata，但公开 API 始终只返回安全名称。脚本重载后，下一次命令会恢复当前 Session、角色轨道、Pose 来源和动画索引。

### 基本截图

1. `query_current_session({"query":"animation","character":"角色","animation":"动画"})` 获取动画全局起始帧和时长。
2. `kimodo_analyze` 分析该区间并取得 `analysis_id`。
3. `query_picture({"analysis_id":"..."})` 一次返回右、上、前、3D 四视角合成图；姿势按结果顺序编号并连接根节点。
4. 若构图偏大或偏小，调整 `scale` 后重新调用同一截图命令。

### 修改关键姿势并生成

1. `kimodo_analyze` 找到语义关键帧。
2. `pose_copy` 将只读 Pose 复制到角色的可写 Pose 来源。
3. `pose_set` 修改 Muscle/Root/Foot IK。
4. `query_picture` 视觉确认。
5. 将 Pose 定位直接放入 `kimodo_generate_animation.constraints`。

### Bake

```json
kimodo_bake_range({
  "character":"UnityChan",
  "start_frame":0,
  "end_frame":300,
  "speed":1.0,
  "name":"Dance_Baked"
})
```

Bake 的 Retarget 参数只负责 Retarget 动画，不属于 Pose 访问协议。
Session 中的角色始终具有有效 Humanoid Avatar。添加非 Muscle Clip 时系统先用目标角色 Avatar 尝试 Retarget；失败则不加入。Bake 可传 `"remove_root_motion":true`，移除水平根位移与 Yaw、保留垂直运动。

### 直线、转弯或曲线路径

1. 调用 `kimodo_build_root2d_path`，由 `duration_frames`、`max_speed` 和 `acceleration` 构造 Root2D 点列。
2. 把需要保留的点转换为 `{frame,type:"root2d",position,heading}`。
3. 直接放入 Generate 的 `constraints` 数组。

### 从动画采样并修改起始约束

1. 用 `{source:<角色>,frame:<全局帧>}` 表示只读采样 Pose。
2. `pose_copy` 复制到目标角色的可写 Pose 来源。
3. `pose_get` 后修改 Muscle，并用 `pose_set` 把 Root 对齐到新动画起点。
4. 把该 Pose 作为相对帧 0 的 FullBody Constraint 直接传给 Generate。

### 构建循环动画

1. 根据动作是否有位移决定是否同时使用 Root2D Constraint。
2. 先生成基本动画，再 Analyze 并 Capture 关键帧拼图。
3. 选语义最合适的 Pose，分别 `pose_copy` 两次。
4. 将两个 Pose 的 Root 调整为首尾重合，创建相对帧 `0` 与 `duration_frames-1` 的同姿 FullBody Constraint。
5. Generate 后再次截图首尾帧；视觉和 Root 连续都通过后再 Bake。

### 拼接与过渡动画

1. 用前一段末帧和后一段首帧作为两个只读 Pose。
2. 分别 `pose_copy` 到过渡目标角色的 `.Poses` 来源；它是持久的临时载体，不需要临时打开 Timeline。
3. 构造相对帧 `0` 和 `duration_frames-1` 的匿名 FullBody Constraint。
4. 将两个 Constraint 直接传给 Generate，生成一段短过渡 Clip。

生成任务状态同时写入 `Library/KimodoCache/Commands/generation_<request_id>.json`。生成期间允许 Unity 编译，但程序集重载会延迟到生成结果、Session Metadata 和 Job JSON 全部保存之后。
