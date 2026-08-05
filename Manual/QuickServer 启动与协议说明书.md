# QuickServer 启动与协议说明书

QuickServer 是 Kimodo Unity Bridge 的本地生成服务。Unity 通常会自动启动它；外部工具也可以直接通过 TCP 协议调用它。

本文面向两类用户：

- Unity 用户：了解服务器如何启动、日志在哪里、哪些选项会影响生成。
- 外部客户端开发者：直接连接 QuickServer TCP，发送 JSON 命令并读取动画结果。

## 运行目录

QuickServer 运行目录通常是：

- 编辑器模式：项目根目录下的 `NvlabKimodoQuickServer~`
- 发布版：`Assets/StreamingAssets/NvlabKimodoQuickServer~`

目录中的关键文件：

| 路径 | 作用 |
| --- | --- |
| `run_server.bat` | Windows 启动入口 |
| `run_server.sh` | macOS / Linux 启动入口 |
| `quickserver.py` | setup wrapper，只负责引导 `core/quickserver_setup.py` |
| `core/quickserver_cli.py` | 当前 TCP supervisor 主入口 |
| `core/kimodo_runtime.py` | Kimodo 生成、模型加载、输出转换和设备自检 |
| `models/` | 默认模型与文本编码器目录 |
| `log/setup.log` | 环境安装日志 |
| `log/bridge_server.log` | supervisor / 生成运行日志 |
| `serverport` | 当前 TCP endpoint，内容通常是 `127.0.0.1:<port>` |

## 启动方式

### 方式一：Unity 自动启动（推荐）

普通用户不需要手动运行脚本。点击 Timeline Clip 的 **Generate & Bake**，或 Runtime Motion Driver 进入 Play Mode 时，Unity 会：

1. 找到当前 QuickServer Directory。
2. 如未运行，则调用对应平台的 `run_server` 脚本。
3. 等待 `serverport` 出现并建立 TCP 连接。
4. 发送 `generate` 请求。

### 方式二：Server Manager 手动启动

在 **Project Settings → Kimodo Server Manager** 中点击 **Start Server**。

适合以下场景：

- 第一次下载模型前，想先确认环境能启动。
- 需要观察 `log/setup.log` 或 `log/bridge_server.log`。
- 遇到旧 endpoint 或锁文件，需要先 Stop 再 Start。

### 方式三：命令行启动

Windows：

```bat
cd /d C:\path\to\NvlabKimodoQuickServer~
run_server.bat
```

macOS / Linux：

```bash
cd /path/to/NvlabKimodoQuickServer~
chmod +x ./run_server.sh
./run_server.sh
```

脚本会先执行必要 setup，再启动：

```text
python -m core.quickserver_cli run --output file
```

不要手动追加 `setup` 子命令；setup 是启动脚本内部自动处理的。

## Windows 与 Linux/macOS 启动逻辑差异

本节按当前 `run_server.bat`、`run_server.sh`、`quickserver.py` 和 `core/quickserver_cli.py` 校验。两边整体流程一致：解析运行目录 → 获取 `.bootstrap.lock` → 找到或下载 `uv` → 执行 `quickserver.py setup --output file` → 设置 `PYTHONPATH` → 启动 `core.quickserver_cli run`。

差异如下：

| 项目 | Windows：`run_server.bat` | macOS / Linux：`run_server.sh` |
| --- | --- | --- |
| 本地 uv 路径 | `program\exe\uv\uv.exe` | `program/exe/uv/uv`，也兼容 `uv.exe` |
| uv 下载包 | 固定 Windows x64 zip | 按 `uname` 选择 macOS/Linux 与 CPU 架构 |
| 自动安装 uv | 支持交互询问；也支持 `KIMODO_AUTO_INSTALL_UV` 跳过询问 | 支持交互询问；也支持 `KIMODO_AUTO_INSTALL_UV` 跳过询问，Unity 非交互启动时自动设置 |
| `--force-setup` | 支持，传给 setup 与 supervisor | 支持，传给 setup 与 supervisor |
| `--force` | 不作为独立参数处理 | 支持，传给 setup，并继续传给 supervisor |
| `--venv <path>` | 用于 setup 和选择 Python；不作为运行参数转发 | 用于 setup 和选择 Python；同时保留在运行参数中 |
| `--watchpid <pid>` | 显式解析并传给 supervisor | 不特殊解析，但会原样转发给 supervisor |
| `--hold-cli` | Windows 调试专用，启动后保持批处理窗口 | 不支持 |
| `--model/--models-root/--text-encoder-mode` | 不通过 bat 转发；推荐用 Unity 设置、环境变量或每次 `generate` 请求 | 会原样转发给 `core.quickserver_cli run` |
| 旧环境变量拦截 | 较少 | 会拦截 `KIMODO_TEST_VENV_PATH`、`KIMODO_TEST_SETUP_DEVICE`、`KIMODO_CPU_TEXT_ENCODER`、`CHECKPOINT_DIR` |

结论：

- Unity 生成链路下，两边最终都走同一个 Python supervisor 和同一套 TCP 协议。
- 命令行直启时，Linux/macOS 脚本更像“参数透传 wrapper”；Windows bat 更像“生命周期 wrapper”。
- 跨平台文档和自动化脚本应优先通过 TCP `generate` 请求设置 `model`、`models_root`、`text_encoder_mode`，不要依赖 Windows bat 转发这些高级参数。

### Windows example：强制重新 setup

```bat
run_server.bat --force-setup
```

### Windows example：复用指定虚拟环境

```bat
set KIMODO_VENV_PATH=D:\KimodoEnv
run_server.bat
```

或：

```bat
run_server.bat --venv D:\KimodoEnv
```

### Linux/macOS example：命令行指定默认模型目录

```bash
KIMODO_MODELS_ROOT=/mnt/models ./run_server.sh
```

也可以直接传给 supervisor：

```bash
./run_server.sh --models-root /mnt/models --text-encoder-mode high_performance
```

> Windows 用户不要依赖 `run_server.bat --models-root ...`。如需自定义模型目录，请使用 Unity 的 **Local Models Path**、环境变量 `KIMODO_MODELS_ROOT`，或在每次 TCP `generate` 请求中传 `models_root`。

### Integration test example：选择测试用例

Windows：

```bat
run_integration_tests.bat
run_integration_tests.bat --case T01
run_integration_tests.bat --range T15 T20
```

Linux/macOS：

```bash
./run_integration_tests.sh
./run_integration_tests.sh --case T01
./run_integration_tests.sh --range T15 T20
```

两边都会运行同一个 `core/integration_test_suite.py`。差异仅在 host Python 解析：Windows 优先 `py -3`，再找 `python`；Linux/macOS 优先 `python3`，再找 `python`。

## TCP 协议总览

QuickServer 使用 newline-delimited JSON。客户端连接 `serverport` 记录的 `host:port` 后，每条命令发送一行 JSON：

```json
{"cmd":"generate","prompt":"a person walks forward","duration":3.0}
```

每个 JSON 后必须跟 `\n`。服务端可能返回多条状态消息，最后返回 `done`、`error` 或 `cancelled`。

可用命令：

| cmd | 作用 |
| --- | --- |
| `session.open` | 在当前 TCP 连接上创建显式 Session |
| `generate` | 提交生成任务 |
| `cancel` | 取消队列中或正在运行的任务 |
| `session.close` | 关闭当前显式 Session；默认 Session 下会关闭服务器 |
| `quit` | 关闭 QuickServer |

通用字段：

| 字段 | 说明 |
| --- | --- |
| `request_id` | 客户端自定义请求 id；服务端所有响应该字段会原样回传 |
| `task_id` | 客户端自定义任务 id；不传时服务端自动生成 |
| `model` | 本次生成使用的模型；可覆盖 Session 默认值 |
| `models_root` | 本次生成使用的模型根目录 |
| `text_encoder_mode` | `high_precision` 或 `high_performance` |
| `simulate_free_vram_gb` | 模拟当前剩余显存；传 `0` 等价于 Force CPU |
| `prompt` | 生成提示词 |
| `duration` | 正数表示固定长度生成；ARDY 缺省 `duration` 表示流式生成 |
| `seed` | 随机种子 |
| `diffusion_steps` | 扩散步数；ARDY 受模型 profile 上限限制 |
| `text_weight` / `cfg_weight` | 文本 CFG 权重；`cfg_weight` 可传数组，第一项作为文本权重 |
| `constraints_json` | 内联 JSON 约束；对象或数组 |
| `output_format` | `json_compact`、`bvh` 或 `kmb_v1` |

## 输出协议

### `json_compact`（默认）

默认返回 JSON 文本，字段为：

```json
{
  "status": "done",
  "output_format": "json_compact",
  "motion_json_compact": "..."
}
```

这是当前 Unity 客户端默认使用的格式。

### `bvh`

请求中传 `"output_format":"bvh"`，或设置环境变量 `KIMODO_BRIDGE_OUTPUT_FORMAT=bvh`。

返回：

```json
{
  "status": "done",
  "output_format": "bvh",
  "motion_bvh": "HIERARCHY\n..."
}
```

BVH 适合外部工具快速预览，不建议在当前 Unity 客户端链路上强行改全局环境变量。

### `kmb_v1`

请求中传 `"output_format":"kmb_v1"`。

服务端先返回一行 JSON：

```json
{"status":"done","output_format":"kmb_v1","byte_length":123456}
```

紧接着发送 `byte_length` 字节的 KMB1 二进制 payload。客户端必须在读下一行 JSON 前先读完这段二进制。

ARDY 默认使用 KMB 直接传输；普通 Kimodo 也可以请求 `kmb_v1`。

## Example 1：最小 Python TCP 客户端

```python
import json
import socket
from pathlib import Path

root = Path(r"C:\path\to\NvlabKimodoQuickServer~")
host, port_text = (root / "serverport").read_text().strip().split(":")

with socket.create_connection((host, int(port_text)), timeout=10) as sock:
    file = sock.makefile("rwb")
    request = {
        "cmd": "generate",
        "request_id": "demo-1",
        "prompt": "a person walks forward",
        "duration": 3.0,
        "output_format": "json_compact"
    }
    file.write((json.dumps(request) + "\n").encode("utf-8"))
    file.flush()

    while True:
        response = json.loads(file.readline().decode("utf-8"))
        print(response["status"], response.get("message", ""))
        if response["status"] in ("done", "error", "cancelled"):
            break
```

## Example 2：普通 Kimodo 固定长度生成

```json
{
  "cmd": "generate",
  "request_id": "kimodo-json-001",
  "task_id": "walk-001",
  "model": "Kimodo-SOMA-RP-v1",
  "prompt": "a person walks forward and turns left",
  "duration": 5.0,
  "seed": 1234,
  "diffusion_steps": 100,
  "text_weight": 1.2,
  "output_format": "json_compact"
}
```

## Example 3：返回 BVH

```json
{
  "cmd": "generate",
  "request_id": "kimodo-bvh-001",
  "prompt": "a person waves with the right hand",
  "duration": 4.0,
  "output_format": "bvh"
}
```

如果想全局默认 BVH，可在启动前设置：

Windows：

```bat
set KIMODO_BRIDGE_OUTPUT_FORMAT=bvh
set KIMODO_BRIDGE_BVH_STANDARD_TPOSE=1
run_server.bat
```

Linux/macOS：

```bash
KIMODO_BRIDGE_OUTPUT_FORMAT=bvh KIMODO_BRIDGE_BVH_STANDARD_TPOSE=1 ./run_server.sh
```

## Example 4：读取 KMB 二进制

```python
import json
import socket
from pathlib import Path

root = Path(r"C:\path\to\NvlabKimodoQuickServer~")
host, port_text = (root / "serverport").read_text().strip().split(":")

def read_exact(file, size):
    chunks = []
    remaining = size
    while remaining:
        chunk = file.read(remaining)
        if not chunk:
            raise EOFError("connection closed while reading KMB payload")
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)

with socket.create_connection((host, int(port_text)), timeout=10) as sock:
    file = sock.makefile("rwb")
    file.write((json.dumps({
        "cmd": "generate",
        "request_id": "kmb-001",
        "prompt": "a person runs forward",
        "duration": 2.0,
        "output_format": "kmb_v1"
    }) + "\n").encode("utf-8"))
    file.flush()

    while True:
        response = json.loads(file.readline().decode("utf-8"))
        if response["status"] == "done" and response.get("output_format") == "kmb_v1":
            payload = read_exact(file, int(response["byte_length"]))
            Path("output.kmb").write_bytes(payload)
            break
        if response["status"] in ("error", "cancelled"):
            raise RuntimeError(response)
```

## Example 5：显式 Session，多次生成后关闭

```json
{"cmd":"session.open","request_id":"open-1"}
```

服务端返回：

```json
{"status":"done","request_id":"open-1","session_id":"session:1-..."}
```

之后同一条 TCP 连接上的 `generate` 都绑定到这个显式 Session：

```json
{"cmd":"generate","request_id":"s1-g1","task_id":"s1-g1","prompt":"walk forward","duration":2.0}
{"cmd":"generate","request_id":"s1-g2","task_id":"s1-g2","prompt":"turn around","duration":2.0}
```

关闭：

```json
{"cmd":"session.close","request_id":"close-1"}
```

## Example 6：取消任务

取消指定任务：

```json
{"cmd":"cancel","request_id":"cancel-1","task_id":"s1-g2"}
```

不传 `task_id` 时，服务端会取消当前 Session 内第一个可取消任务：

```json
{"cmd":"cancel","request_id":"cancel-next"}
```

可能返回：

```json
{
  "status": "done",
  "cancel_status": "cancelling",
  "task_id": "s1-g2",
  "message": "Cancellation requested for 's1-g2'."
}
```

## Example 7：Force CPU / 显存模拟

Unity 的 **Force CPU** 本质上是在请求里发送：

```json
{
  "cmd": "generate",
  "prompt": "a person walks slowly",
  "duration": 3.0,
  "simulate_free_vram_gb": 0
}
```

测试文本编码器路由时，也可以模拟剩余显存：

```json
{
  "cmd": "generate",
  "prompt": "a person jumps",
  "duration": 2.0,
  "text_encoder_mode": "high_performance",
  "simulate_free_vram_gb": 6
}
```

QuickServer 会先为 motion 模型预留约 2GB，再用剩余预算选择文本编码器路线。

## Example 8：ARDY 流式生成

ARDY 请求缺省 `duration` 时表示流式生成。客户端需要持续发送 Session 相对时间 `time_as_double`。

```json
{
  "cmd": "generate",
  "request_id": "ardy-stream-0",
  "task_id": "ardy-stream-0",
  "model": "ARDY-Core-RP-20FPS-Horizon40",
  "prompt": "a humanoid robot walks forward",
  "time_as_double": 0.0,
  "output_format": "kmb_v1"
}
```

继续推进播放头：

```json
{
  "cmd": "generate",
  "request_id": "ardy-stream-1",
  "task_id": "ardy-stream-1",
  "model": "ARDY-Core-RP-20FPS-Horizon40",
  "time_as_double": 1.0,
  "output_format": "kmb_v1"
}
```

流式语义：

- 缺省 `prompt` 表示沿用当前提示词。
- 缺省 `constraints_json` 表示沿用当前约束。
- `constraints_json: []` 表示清空约束。
- `time_as_double` 变小表示 seek，返回区间可能覆盖旧帧，客户端应从 `start_frame` 替换。

## Example 9：ARDY 固定长度生成

ARDY 请求带正数 `duration` 时，表示一次性固定长度生成；它不会继承流式 Session 的历史、随机状态或时间游标。

不同 Session 的 ARDY 请求若使用相同 runtime、history/window 形状、扩散步数和 CFG，QuickServer 会机会式合并为一次 motion batch，再按 batch 行写回各 Session。容量从 `1` 开始，ARDY Session 数超过容量时按 `1/2/4/8` 扩容，低于容量一半时减半；默认最大 batch 为 `8`，启动前设置 `KIMODO_ARDY_BATCH_SIZE=1-8` 可调整，设为 `1` 可关闭跨 Session batch。固定长度请求不会被同一 Session 的新 ARDY 请求替换；仅流式更新保留“等待队列只取最新值”的语义。

```json
{
  "cmd": "generate",
  "request_id": "ardy-fixed-1",
  "task_id": "ardy-fixed-1",
  "model": "ARDY-Core-RP-20FPS-Horizon40",
  "prompt": "a humanoid robot walks then waves",
  "duration": 4.0,
  "seed": 42,
  "diffusion_steps": 10,
  "output_format": "kmb_v1"
}
```

`duration: 0` 是非法值，不是流式别名。

## Example 10：简单 Root 目标约束

普通 Kimodo 使用 `smooth_root_2d`，ARDY 使用 `root_2d`。约束必须作为内联 JSON 字符串传入 `constraints_json`。

```json
{
  "cmd": "generate",
  "prompt": "walk to the target",
  "duration": 3.0,
  "constraints_json": "[{\"type\":\"smooth_root_2d\",\"frame\":60,\"x\":2.0,\"z\":1.0}]"
}
```

如果外部客户端更容易构造对象，也可以先在客户端把数组序列化成字符串后再放入 `constraints_json`。

## Example 11：History / Future KMB 附件

History/Future Clip 约束使用 `kmb_attachments` 清单，JSON 行后紧跟拼接的 KMB 二进制数据。

请求头示例：

```json
{
  "cmd": "generate",
  "model": "ARDY-Core-RP-20FPS-Horizon40",
  "prompt": "continue the previous motion",
  "duration": 2.0,
  "output_format": "kmb_v1",
  "kmb_attachments": [
    {"offset": 0, "length": 12345}
  ],
  "attachment_byte_length": 12345,
  "constraints_json": "[{\"type\":\"clip\",\"format\":\"kmb_attachment_v1\",\"attachment\":0,\"start_frame\":0,\"end_frame_exclusive\":40,\"is_history\":true}]"
}
```

发送顺序：

1. 发送上面的 JSON 行和 `\n`。
2. 立即发送 `attachment_byte_length` 字节的 KMB 拼接数据。
3. 按 `kmb_v1` 输出规则读取响应 JSON 和二进制结果。

Future clip 可设置 `is_history:false`，并提供完整 bool `mask`。mask 顺序为 `Root.x, Root.y, Root.z, RootHeading`，然后按骨骼顺序排列每个非 Root 关节的 XYZ 通道。

## 常见误区

- 不要在 Windows 上假设 `run_server.bat --models-root ...` 会生效；Windows bat 不转发这类高级参数。
- 不要把 `duration: 0` 当成 ARDY 流式请求；流式请求应省略 `duration`。
- 读取 `kmb_v1` 时，必须先读完 `byte_length` 字节，再继续读下一行 JSON。
- `session.close` 在默认 Session 下会关闭服务器；显式 Session 下只关闭当前 Session。
- 当前 Unity 客户端默认依赖 `json_compact` / KMB 链路；全局启用 BVH 更适合外部 TCP 客户端。
