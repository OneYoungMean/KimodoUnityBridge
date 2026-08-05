# NvlabKimodoQuickServer 参数说明

## 1. `run_server.bat` / `run_server.sh`

启动脚本每次都会先检查 setup 状态，必要时自动准备环境，然后启动 TCP supervisor；没有面向用户的 `setup` 子命令。

### Windows 与 Linux/macOS 参数转发差异

| 项目 | Windows `run_server.bat` | macOS / Linux `run_server.sh` |
| --- | --- | --- |
| 最终入口 | `python -m core.quickserver_cli run --output file` | `python -m core.quickserver_cli run --output file` |
| `--force-setup` | 支持，传给 setup 与 supervisor | 支持，传给 setup 与 supervisor |
| `--force` | 不作为独立参数处理 | 支持，传给 setup，并继续传给 supervisor |
| `--venv <path>` | 用于 setup 和选择 Python | 用于 setup 和选择 Python，并保留在 supervisor 参数中 |
| `--watchpid <pid>` | 显式解析并转发 | 原样转发，supervisor 支持 |
| `--hold-cli` | 支持，Windows 调试用 | 不支持 |
| `--model/--models-root/--text-encoder-mode` | 不转发；请用 Unity 设置、环境变量或 `generate` 请求 | 原样转发给 supervisor |

跨平台自动化建议：把模型、模型目录、文本编码器模式和输出格式放到每次 TCP `generate` 请求里，或使用环境变量；不要依赖 Windows bat 转发高级运行参数。

- `--force-setup`: 归档 setup sentinel 并重新准备环境。
- `--venv <path>`: 复用指定虚拟环境。
- `--watchpid <pid>`: 让 supervisor 监视宿主进程；Windows 和 macOS/Linux 启动器均支持。
- `--hold-cli`: 仅 Windows 调试参数，让批处理等待 supervisor 退出。

关键 setup 变量：
- `KIMODO_AUTO_INSTALL_UV=1`: 缺少本地 uv 时跳过交互询问并自动下载；Unity 在 macOS/Linux 非交互启动时自动设置。
- `KIMODO_SETUP_DEVICE=auto|cpu`: setup 安装模式；设为 `cpu` 时强制准备 CPU torch 环境。macOS 的 `auto` 会安装通用 torch，并验证 MPS；不可用时回退 CPU。
- `KIMODO_VENV_PATH=<path>`: 复用指定虚拟环境；等价于启动时自动补 `--venv <path>`。

## 2. supervisor 高级参数

以下是内部 `quickserver_cli.py run` 的参数。Unity 通常通过每次生成请求提供模型相关设置。Windows `run_server.bat` 不转发这些参数；跨平台配置模型目录时优先使用 Unity 的 **Local Models Path** 或 `KIMODO_MODELS_ROOT`。

- `--model <name|alias>`: 默认 `Kimodo-SOMA-RP-v1`。
- `--text-encoder-mode <high_precision|high_performance>`: 文本编码器偏好，默认 `high_precision`；设备位置由 QuickServer 自动决定。
- `--force-hf-download`: 对允许竞速的资产强制使用 Hugging Face 下载；若命中 legacy 本地兼容布局，则不会触发下载。
- `--models-root <path>`: 指定外部模型根目录（存在即跳过下载流程）。
- `--output <console|file>`: 输出模式，默认 `console`。
- `--log <path>`: `file` 模式下主日志路径，默认 `log\bridge_server.log`。
- `bridge_server` 主日志固定为 `log\bridge_server.log`。

关键运行变量：
- `KIMODO_MODELS_ROOT`: 默认 models 根目录（可被 `--models-root` 覆盖）。
- `KIMODO_ALLOW_MULTI_SERVER=0|1`: 默认 `0`，同一份 QuickServer 根目录只允许一个 `run server` 实例；设为 `1` 时跳过运行单例锁。兼容别名 `ALLOWMULTISERVER` / `allowmultiserver`。
- `KIMODO_IDLE_TIMEOUT_SEC`: 服务空闲退出秒数（当前设定 `600`）。
- `KIMODO_RUNTIME_IDLE_UNLOAD_SEC`: 模型资源空闲回收秒数，默认 `900`；设为更大值可延后显存释放。
- `KIMODO_ARDY_BATCH_SIZE`: ARDY 同模型兼容请求的最大 batch 和准备线程数，默认 `8`，有效范围 `1-8`。实际 batch 容量从 `1` 开始，Session 数超过容量时按 `1/2/4/8` 扩容，低于容量一半时减半；设为 `1` 可关闭跨 Session batch。
- `KIMODO_BRIDGE_OUTPUT_FORMAT=json_compact|bvh`: bridge TCP `generate` 返回格式。默认 `json_compact`；设为 `bvh` 时，仅返回 `motion_bvh`，不再返回 `motion_json_compact`。
- `KIMODO_BRIDGE_BVH_STANDARD_TPOSE=0|1`: 仅在 `KIMODO_BRIDGE_OUTPUT_FORMAT=bvh` 时生效。设为 `1` 时，BVH 以标准 T-pose 作为 rest pose 导出。
- 下载站点默认是自动探测 HF / ModelScope 后择优；`--force-hf-download` 会跳过探测并强制走 HF。

INT8 资产说明：
- 默认低显存文本编码器目录为 `models\KIMODO-Meta3_llm2vec_INT8`。
- 对默认 `models\` 目录：若缺少 INT8 资产，会尝试从 `oneyoungmean/KIMODO-Meta3_llm2vec_INT8` 下载。
- 对外部 `--models-root`：不会自动下载，缺失时直接报错。

文本编码器路由说明：
- QuickServer 使用实时剩余显存，先要求 motion 模型至少有约 `2GB` 可用空间；不足时直接返回显存不足错误。
- motion 模型加载后会再次读取剩余显存，再决定文本编码器放在 GPU 还是 CPU。
- `high_precision`：为 motion 模型预留空间后，文本编码器剩余预算 `>= 16GB` 且设备支持 FP16 时使用 FP16 加速器，否则 FP16 文本编码器走 CPU。
- `high_performance`：
  - 设备支持 NF4 且剩余显存 `>= 6GB`：NF4 GPU。
  - NF4 放不下但支持 INT8 且剩余显存 `>= 8GB`：INT8 GPU。
  - 其他情况：INT8 CPU。
- `simulate_free_vram_gb` 模拟的是当前总剩余显存；QuickServer 会先扣除 motion 模型约 2GB 预留，再按剩余预算选择文本编码器。未发送表示自动检测，显式发送 `0` 表示全部强制 CPU。
- 不检测系统内存；CPU 路径允许操作系统使用虚拟内存。

### 启动说明
- `run_server.bat` / `run_server.sh` 会自动执行必要的 setup，再启动 supervisor。
- `serverport` 仅由当前 TCP supervisor 写入；Unity 侧只读取 `serverport` 并建立 TCP 连接，不再做独立 ping 探活。
- `KIMODO_BRIDGE_OUTPUT_FORMAT=bvh` 是给直接消费 QuickServer TCP 返回值的外部客户端使用的。现有 Unity 客户端仍然依赖 `motion_json_compact`，不应在 Unity 这条链路上开启。
- QuickServer TCP 现在以 `task_id` 作为协议真相：`generate` 可选传 `task_id`，未传时会在入队前自动补齐。
- 同一任务的所有中间态和终态响应都会回传同一个 `task_id`；终态固定为 `done / error / cancelled`。
- `cancel` 支持显式 `task_id`；若未传，则命中队列中的第一个可取消任务，并在响应里回传解析后的 `task_id`。
- 同一条 TCP 连接可以连续发送多个 `generate / cancel / quit` 命令，不要求每个 generate 独占一条连接生命周期。

已移除变量：
- `CHECKPOINT_DIR`: 改用 `KIMODO_MODELS_ROOT`。
- `KIMODO_CPU_TEXT_ENCODER`: CPU 文本编码器不再由外部选择，QuickServer 会自动切到本地 INT8。
- `KIMODO_TEXT_ENCODER_DEVICE_HINT`: QuickServer 直接写入 `TEXT_ENCODER_DEVICE`，不再接受该提示变量。
- `KIMODO_TEST_SETUP_DEVICE`: 改用 `KIMODO_SETUP_DEVICE`。
- `KIMODO_TEST_VENV_PATH`: 改用 `KIMODO_VENV_PATH`。

## 3. 日志约定
- 默认所有日志写入 `log\`。
- 典型文件：
  - `log\setup.log`
  - `log\bridge_server.log`（run/bridge 主日志）
