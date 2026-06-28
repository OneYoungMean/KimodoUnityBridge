# NvlabKimodoQuickServer1（中文）

## 语言说明
- 中文说明：`README_ZH.md`
- 英文说明：`README.md`

## 功能介绍
- 使用 `uv` 构建运行环境。
- 启动 Kimodo bridge 服务并支持模型参数。
- 提供 TCP 示例链路（`ping -> generate -> quit`）。

## 环境要求
- Windows 10/11 x64
- 可用模型目录（推荐）：`C:\nvlab\models~`
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
run_server.bat --model Kimodo-SOMA-RP-v1 --models-root C:\nvlab\models~ --output console
```

Linux：
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh --model Kimodo-SOMA-RP-v1 --models-root /mnt/c/nvlab/models~ --output console
```

低显存运行现在默认使用 `models\KIMODO-Meta3_llm2vec_INT8` 这份本地 Torch CPU INT8 文本编码器资产。

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

## 参数文档
- 见 `PARAMETERS.md`
