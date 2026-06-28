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
- `--highvram`: 启用 high-vram 模式。
- `--force-hf-download`: 对允许竞速的资产强制使用 Hugging Face 下载；包括 `highvram/fp16` 的 FP16 文本编码器；若命中 legacy 本地兼容布局，则不会触发下载。
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
- CPU 或显存 `< 6GB`：使用 `models\KIMODO-Meta3_llm2vec_INT8`
- GPU 且显存 `>= 6GB`，未开启 `--highvram`：使用 `models\KIMODO-Meta3_llm2vec_NF4`
- GPU 且显存 `>= 6GB`，开启 `--highvram`：
  - 若同时存在且有效：`models\Meta-Llama-3-8B-Instruct` + `models\LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`，优先走 legacy 本地兼容加载
  - 否则使用 `models\KIMODO-Meta3_llm2vec_FP16`

### 启动与 watchdog
- `KIMODO_WATCHDOG_STARTUP_INTERVAL_SEC`: 启动阶段等待 `serverport` 的轮询间隔（默认 `1` 秒）。
- `KIMODO_WATCHDOG_STARTUP_MAX_FAILS`: 启动阶段等待 `serverport` 的最大轮询次数（默认 `180`）。
- `KIMODO_WATCHDOG_RUNTIME_INTERVAL_SEC`: 运行阶段检查 `log\bridge_server.log` 更新时间间隔（默认 `1` 秒）。
- `KIMODO_WATCHDOG_IDLE_NOLOG_MAX`: 运行阶段日志未更新的最大连续次数（默认 `300`），超过则自动关闭进程。

说明：
- 默认启动等待窗口约 `180s`（`1s * 180`）。
- 不做 `serverport` 回填、不做 TCP 探活；`serverport` 仅由 bridge server 写入。
- `run_server.bat setup` / `run_server.sh setup` 都是同一条 Python 入口的子命令，用于单独执行 setup。
- `KIMODO_BRIDGE_OUTPUT_FORMAT=bvh` 是给直接消费 QuickServer TCP 返回值的外部客户端使用的。现有 Unity 客户端仍然依赖 `motion_json_compact`，不应在 Unity 这条链路上开启。

已移除变量：
- `CHECKPOINT_DIR`: 改用 `KIMODO_MODELS_ROOT`。
- `KIMODO_CPU_TEXT_ENCODER`: CPU 文本编码器不再由外部选择，QuickServer 会自动切到本地 INT8。
- `KIMODO_TEXT_ENCODER_DEVICE_HINT`: QuickServer 直接写入 `TEXT_ENCODER_DEVICE`，不再接受该提示变量。
- `KIMODO_TEST_SETUP_DEVICE`: 改用 `KIMODO_SETUP_DEVICE`。
- `KIMODO_TEST_VENV_PATH`: 改用 `KIMODO_VENV_PATH`。

## 3. `example\example_run_server_tpose.bat`
- 默认流程：后台启动 `run_server` -> 读取 `serverport` -> 发送 `ping/generate(tpose)/quit`。
- 通过判定：客户端退出码 `0` 且出现 `status=done`。

相关环境变量：
- `KIMODO_TEST_OUTPUT=console|file`（默认 `console`）
- `KIMODO_TEST_WAIT_TIMEOUT_SEC`（默认 `600`）
- `KIMODO_TEST_MODEL`
- `KIMODO_TEST_HIGHVRAM=0|1`
- `KIMODO_TEST_FORCE_HF_DOWNLOAD=0|1`
- `KIMODO_TEST_MODELS_ROOT=<path>`
- `KIMODO_TEST_SERVER_WINDOW_STYLE=Normal|Hidden|Minimized|Maximized`

## 4. 日志约定
- 默认所有日志写入 `log\`。
- 典型文件：
  - `log\setup.log`
  - `log\bridge_server.log`（run/bridge 主日志）
  - `log\watchdog.log`
  - `log\example_run_server_tpose.log`
  - `log\example_run_server_tpose_client.log`
