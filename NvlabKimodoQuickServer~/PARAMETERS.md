# NvlabKimodoQuickServer 参数说明

## 1. `run_server.bat setup` / `run_server.sh setup`
- `--output <console|file>`: 输出模式，默认 `console`。
- `--log <path>`: `file` 模式下日志文件路径，默认 `log\setup.log`。
- `--force`: 强制重新 setup（会归档旧 sentinel）。

关键 setup 变量：
- `KIMODO_SETUP_DEVICE=auto|cpu`: setup 安装模式；设为 `cpu` 时强制准备 CPU torch 环境。
- `KIMODO_VENV_PATH=<path>`: 复用指定虚拟环境；等价于启动时自动补 `--venv <path>`。

## 2. `run_server.bat` / `run_server.sh`
- `--model <name|alias>`: 默认 `Kimodo-SOMA-RP-v1`。
- `--text-encoder-mode <high_precision|high_performance>`: 文本编码器偏好，默认 `high_precision`；设备位置由 QuickServer 自动决定。
- `--force-hf-download`: 对允许竞速的资产强制使用 Hugging Face 下载；若命中 legacy 本地兼容布局，则不会触发下载。
- `--models-root <path>`: 指定外部模型根目录（存在即跳过下载流程）。
- `--output <console|file>`: 输出模式，默认 `console`。
- `--log <path>`: `file` 模式下主日志路径，默认 `log\bridge_server.log`。
- `bridge_server` 主日志固定为 `log\bridge_server.log`。
- `--force-setup`: 归档 setup sentinel 后重新 setup。

关键运行变量：
- `KIMODO_MODELS_ROOT`: 默认 models 根目录（可被 `--models-root` 覆盖）。
- `KIMODO_ALLOW_MULTI_SERVER=0|1`: 默认 `0`，同一份 QuickServer 根目录只允许一个 `run server` 实例；设为 `1` 时跳过运行单例锁。兼容别名 `ALLOWMULTISERVER` / `allowmultiserver`。
- `KIMODO_IDLE_TIMEOUT_SEC`: 服务空闲退出秒数（当前设定 `600`）。
- `KIMODO_BRIDGE_OUTPUT_FORMAT=json_compact|bvh`: bridge TCP `generate` 返回格式。默认 `json_compact`；设为 `bvh` 时，仅返回 `motion_bvh`，不再返回 `motion_json_compact`。
- `KIMODO_BRIDGE_BVH_STANDARD_TPOSE=0|1`: 仅在 `KIMODO_BRIDGE_OUTPUT_FORMAT=bvh` 时生效。设为 `1` 时，BVH 以标准 T-pose 作为 rest pose 导出。
- 下载站点默认是自动探测 HF / ModelScope 后择优；`--force-hf-download` 会跳过探测并强制走 HF。

INT8 资产说明：
- 默认低显存文本编码器目录为 `models\KIMODO-Meta3_llm2vec_INT8`。
- 若本地已有 `C:\nvlab\LLMVec-GGUF\KIMODO-Meta3_llm2vec_FP16`，可先执行 `tools\build_llm2vec_int8.py` 生成 INT8 资产。
- 对默认 `models\` 目录：若缺少 INT8 资产，会尝试从 `oneyoungmean/KIMODO-Meta3_llm2vec_INT8` 下载。
- 对外部 `--models-root`：不会自动下载，缺失时直接报错。

文本编码器路由说明：
- Kimodo 在加速设备上固定预留约 `2GB`；有效显存 `< 2GB` 时 Kimodo 和文本编码器全部走 CPU。
- `high_precision`：有效显存 `>= 18GB` 且设备支持 FP16 时使用 FP16 加速器，否则 FP16 文本编码器走 CPU。
- `high_performance`：
  - 设备支持 NF4 且有效显存 `>= 6GB`：NF4 加速器。
  - 不支持 NF4、支持 INT8 且有效显存 `>= 8GB`：INT8 加速器。
  - 其他情况：INT8 CPU。
- `simulate_vram_gb` 未发送表示自动检测；显式发送 `0` 表示全部强制 CPU。
- 不检测系统内存；CPU 路径允许操作系统使用虚拟内存。

### 启动说明
- `run_server.bat setup` / `run_server.sh setup` 都是同一条 Python 入口的子命令，用于单独执行 setup。
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

## 3. `example\example_run_server_tpose.bat`
- 当前 `example\` 目录仍保留旧示例脚本，尚未完全迁移到最新 TCP 生命周期；请优先使用 `run_integration_tests.bat/.sh` 与 `integration_test_suite.py`。
- 通过判定：客户端退出码 `0` 且出现 `status=done`。

相关环境变量：
- `KIMODO_TEST_OUTPUT=console|file`（默认 `console`）
- `KIMODO_TEST_WAIT_TIMEOUT_SEC`（默认 `1800`）
- `KIMODO_TEST_MODEL`
- `KIMODO_TEST_FORCE_HF_DOWNLOAD=0|1`
- `KIMODO_TEST_MODELS_ROOT=<path>`
- `KIMODO_TEST_SERVER_WINDOW_STYLE=Normal|Hidden|Minimized|Maximized`

## 4. 日志约定
- 默认所有日志写入 `log\`。
- 典型文件：
  - `log\setup.log`
  - `log\bridge_server.log`（run/bridge 主日志）
  - `log\example_run_server_tpose.log`
  - `log\example_run_server_tpose_client.log`
