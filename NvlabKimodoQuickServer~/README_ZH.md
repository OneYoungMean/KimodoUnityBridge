# NvlabKimodoQuickServer1（中文）

## 语言说明
- 中文说明：`README_ZH.md`
- 英文说明：`README.md`

## 功能介绍
- 使用 `uv` 构建运行环境。
- 启动 QuickServer TCP supervisor，并在其内部排队执行 bridge 生成任务。
- 复用同一条 TCP 连接处理 Session、Generate、Cancel 和动画 Handle 命令。
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

文本编码器由 `text_encoder_mode=high_precision|high_performance` 选择精度偏好，再按有效显存和设备能力自动放置。Kimodo 预留约 2GB；NF4/INT8/FP16 的加速器门槛分别为 6GB/8GB/18GB，显式 `simulate_vram_gb=0` 会让整个运行时走 CPU。

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
- 每个 Session 维护上限为 32 的 Generate FIFO。Kimodo 单次任务原子执行；持久 ARDY 流每轮公平调度一个完整 Horizon。
- `session.close` 只关闭显式 Session；关闭 `session:default` 会关闭 QuickServer。旧 `quit` 保持相同的全局关闭效果。
- `generate` 使用 `text_encoder_mode`，不再接受 `highvram` 或 `force_cpu`；Force CPU UI 会发送 `simulate_vram_gb=0`。
- `generate` 的 `task_id` 现在是可选的；如果调用方不传，QuickServer 会在入队前自动补一个稳定任务标识。

## 动画 Handle

QuickServer 会把上传或生成的 KMB 动画保存在进程内、受容量限制的资源库中。存在二进制请求体时，协议为一行 JSON，随后紧跟 `byte_length` 个原始字节。

- `animation.upload`：发送 `format=flatbuf_motion_v1`、`byte_length`、可选 `description`，然后发送 KMB；返回 `handle_info`。
- `animation.info`：发送 `handle`；返回 `handle_info`。
- `animation.download`：发送 `handle`；返回 JSON 头和后续 KMB 字节。
- `animation.release`：发送 `handle`；无任务引用时立即释放，否则在任务解除 pin 后释放。
- `generate` 使用 `output_format=kmb_handle_v1` 时只返回 `handle_info`，不内联 KMB；`flatbuf_motion_v1` 保留为旧的二进制直返模式。

`animation.upload` 的端到端上限为 3 秒。静态 Handle 在同一服务器实例内全局可见、不可变、可重复下载，并使用基于容量的 LRU 兜底。ARDY 的 `kmb_handle_v1` 是 Session 绑定的流式 Handle，内部使用两个固定缓冲区：Generate 无需等待首个 Horizon 即返回 Handle；下载会交换缓冲并破坏性消费当前有效帧；Cancel 关闭整个流，正在计算的完整 Horizon 会运行完毕但结果被丢弃。缓冲容量按 `duration × FPS` 向上补齐到 Horizon 整数倍。

QuickServer 重启后 Handle 失效；基于容量的 LRU 只负责清理未显式释放的资源，Handle 不会因存放时间过长而过期。生产 clip constraint 使用 `format=kmb_handle_v1`，`ardy_file_v1` 仅在显式测试开关下可用。
- 一旦任务标识确定，该任务后续所有响应都会带同一个 `task_id`。
- 任务会先后经历 `queued`、`loading`、`progress`、`cancelling` 等中间态，并最终落到 `done`、`error` 或 `cancelled`。
- `cancel` 同样支持可选 `task_id`；若未传，则取消当前队列中第一个可取消任务，并在响应里回传实际命中的任务标识。
- ARDY Cancel 会先返回 `cancelling`；不可中断的 Horizon 完成并清理资源后，同一 TCP 会收到一个不带 `request_id` 的异步 `{"status":"event","event":"task.closed",...}` 消息。
- FlatBuffer 返回仍保持 `byte_length` 后紧跟该任务自己的二进制 payload，不会夹入其他任务的数据头。

## 参数文档
- 见 `PARAMETERS.md`
