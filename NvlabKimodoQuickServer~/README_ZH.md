# NvlabKimodoQuickServer1（中文）

## 语言说明
- 中文说明：`README_ZH.md`
- 英文说明：`README.md`

## 功能介绍
- 使用 `uv` 构建运行环境。
- 启动 QuickServer TCP supervisor，并在其内部排队执行 bridge 生成任务。
- 复用同一条 TCP 连接处理 `generate / cancel / quit`。
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
- `generate` 使用 `text_encoder_mode`，不再接受 `highvram` 或 `force_cpu`；Force CPU UI 会发送 `simulate_vram_gb=0`。
- `generate` 的 `task_id` 现在是可选的；如果调用方不传，QuickServer 会在入队前自动补一个稳定任务标识。
- 一旦任务标识确定，该任务后续所有响应都会带同一个 `task_id`。
- 任务会先后经历 `queued`、`loading`、`progress`、`cancelling` 等中间态，并最终落到 `done`、`error` 或 `cancelled`。
- `cancel` 同样支持可选 `task_id`；若未传，则取消当前队列中第一个可取消任务，并在响应里回传实际命中的任务标识。
- FlatBuffer 返回仍保持 `byte_length` 后紧跟该任务自己的二进制 payload，不会夹入其他任务的数据头。

## 参数文档
- 见 `PARAMETERS.md`
