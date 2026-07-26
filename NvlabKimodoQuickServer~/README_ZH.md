# NvlabKimodoQuickServer1（中文）

## 语言说明
- 中文说明：`README_ZH.md`
- 英文说明：`README.md`

## 功能介绍
- 使用 `uv` 构建运行环境。
- 启动 QuickServer TCP supervisor，并在其内部排队执行 bridge 生成任务。
- 复用同一条 TCP 连接处理 Session、Generate、Cancel 和直接 KMB 结果。
- 返回按任务 id 归属的 `queued / loading / progress / cancelling / cancelled / done / error` 状态。

## 环境要求
- Windows 10/11 x64
- 默认自动下载到本地 `models\` 目录；只有测试或共享缓存需要覆盖路径时才传 `--models-root`。
- 需要 `uv`。如果本机缺失，`run_server.bat` / `run_server.sh` 会在首次运行时尝试下载一份本地 unmanaged `uv` 到 `program\exe\uv\`。它自己的包缓存仍然走 `uv` 默认的全局缓存目录。

## 安装
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat setup --output console
```

如果你已经有 `C:\nvlab\LLMVec-GGUF\KIMODO-Meta3_llm2vec_FP16` 这份 baked FP16 文本编码器，可以先本地生成 CPU INT8 资产：
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
program\exe\uv\uv.exe run --python 3.12 --no-project python tools\build_llm2vec_int8.py --verify
```

Linux：
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh setup --output console
```

## Example
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat --model Kimodo-SOMA-RP-v1 --output console
```

Linux：
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh --model Kimodo-SOMA-RP-v1 --output console
```

文本编码器由 `text_encoder_mode=high_precision|high_performance` 选择精度偏好，再按实时剩余显存和设备能力自动放置。QuickServer 先确保 motion 模型至少有约 2GB 可用空间，加载后再次检查剩余显存；NF4/INT8/FP16 的 GPU 预算分别为 6GB/8GB/16GB。显式 `simulate_free_vram_gb=0` 会让整个运行时走 CPU。

TCP 冒烟测试：
```bat
example\example_run_server_tpose.bat
```

控制台实时日志版本：
```bat
example\example_run_server_tpose_console_live.bat
```

Bridge TCP 返回格式：
- 默认 `generate` 返回 `motion_json_compact`。
- 如需让 QuickServer 直接返回 BVH 文本，可设置：
```bat
set KIMODO_BRIDGE_OUTPUT_FORMAT=bvh
set KIMODO_BRIDGE_BVH_STANDARD_TPOSE=1
```
- 开启后响应中将返回 `motion_bvh`，不再返回 `motion_json_compact`。这个模式适合直接接 QuickServer TCP 协议的外部客户端，不适用于当前 Unity 客户端链路。

TCP 协议补充：
- 所有请求都可携带 `request_id`，该请求的全部响应会原样回传，用于在一条持久 TCP 上安全复用命令。
- `session.open` 会为当前 TCP 创建并绑定显式 Session；未调用时使用 `session:default`。
- 每个 Session 维护上限为 32 的 Generate 指令 FIFO；每次 Generate 只返回一个最终 result。
- `session.close` 只关闭显式 Session；关闭 `session:default` 会关闭 QuickServer。旧 `quit` 保持相同的全局关闭效果。
- `generate` 使用 `text_encoder_mode`，不再接受 `highvram` 或 `force_cpu`；Force CPU UI 会发送 `simulate_free_vram_gb=0`。
- `generate` 的 `task_id` 现在是可选的；如果调用方不传，QuickServer 会在入队前自动补一个稳定任务标识。
- 新的 ARDY Generate 不会取消正在执行的 Horizon；当前请求完成后再执行，并且等待队列只保留最新的 ARDY 更新。

## KMB 直接传输

`generate` 使用 `output_format=kmb_v1`。ARDY 成功响应是一行带 `byte_length` 的 JSON，后面立即跟随非空 KMB1 区间；返回后的可播放长度一定超过当前 Playback Reserve。

ARDY Generate 携带正数 `duration` 时采用固定长度语义：创建新的逻辑生成，可通过 clip constraint 初始化显式 History，后端按需执行多个 Horizon，返回精确长度的一份 KMB 后释放该逻辑时间线。ARDY Generate 缺省 `duration` 时采用流式语义：客户端发送 Session 相对的 `time_as_double`，QuickServer 保留该 Session 的 RNG、history 与时间线，后续 Generate 持续更新，直到 `session.close`。`duration: 0` 非法，不作为流式别名。

在 ARDY 流式模式下，QuickServer 根据当前模型 FPS 转帧，只在 GPU 保留 profile 对应的 history，并在 CPU 缓存时间线以支持 seek。`ardy_playback_reserve_seconds` 默认 1 秒；`ardy_adaptive_playback_reserve` 默认开启，根据后端实测响应耗时调整实际储备。缺省 `prompt` 或 `constraints_json` 表示保持；`[]` 表示清空完整 constraint 快照。更新 prompt/constraint 时保留到 `time_as_double + Playback Reserve`，再重新生成并返回受影响的绝对 KMB 区间。

`time_as_double` 减小视为 seek。普通响应从上次已交付尾部追加；seek 和重规划响应可能与旧帧重叠，客户端必须从 `start_frame` 替换时间线。

History/Future KMB 输入使用 JSON `kmb_attachments` 清单描述连续 offset/length，随后发送拼接的 KMB1 数据；clip constraint 用 `format=kmb_attachment_v1` 和从 0 开始的 `attachment` 索引引用。KMB1 FlatBuffer schema 不变。`ardy_file_v1` 仍只用于显式调试。

### ARDY clip constraint

clip 缺少 `is_history` 时按完整 history 处理，且 history 不能携带 mask。Future clip 示例：

```json
{
  "type": "clip",
  "format": "kmb_attachment_v1",
  "attachment": 0,
  "start_frame": 0,
  "end_frame_exclusive": 40,
  "is_history": false,
  "mask": [false, false, false, false, true, true, true]
}
```

Future mask 必须是完整的一维 bool 数组，长度为 `4 + (joint_count - 1) * 3`，顺序严格为 `Root.x, Root.y, Root.z, RootHeading`，随后按 KMB/ARDY Profile 骨骼顺序排列每个非 Root 骨骼的 `x, y, z`。`RootHeading` 同时控制内部 cos/sin；`true` 表示约束，`false` 表示自由生成。多个 clip 从 future 第 0 帧开始写入，后出现的 clip 会覆盖同帧同通道，后者的 `false` 也会清除前者约束。

Python 会从 KMB 的 Root position 与 local quaternion 重建 ARDY 特征。扩散步数受 checkpoint 原生 10-step 时间轴限制，合法范围为 1–10。
- 一旦任务标识确定，该任务后续所有响应都会带同一个 `task_id`。
- 任务会先后经历 `queued`、`loading`、`progress`、`cancelling` 等中间态，并最终落到 `done`、`error` 或 `cancelled`。
- `cancel` 同样支持可选 `task_id`；若未传，则取消当前队列中第一个可取消任务，并在响应里回传实际命中的任务标识。
- ARDY 在 Horizon 内不可中断；Cancel 只取消当前等待中的 Generate 响应，Session 时间线由 `session.close` 销毁。
- KMB 返回保持 `byte_length` 后紧跟该任务的二进制 payload。

## 参数文档
- 见 `PARAMETERS.md`
