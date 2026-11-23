# QuickServer 运行时说明

如果你明确计划在 Unity 中使用 Kimodo，强烈建议使用 Unity CLI。

本页记录项目自带 QuickServer 中容易与 Unity 公共命令混淆的细节。请求和响应字段仍以命令 schema 为准。

## 启动器下载超时

启动脚本会把 `KIMODO_UV_HTTP_TIMEOUT_SEC` 映射为 uv 使用的 `UV_HTTP_TIMEOUT`，控制安装和运行时操作中的包下载请求超时，默认 300 秒。包源较慢时，可在启动 QuickServer 前设置该变量；例如 PowerShell 使用 `$env:KIMODO_UV_HTTP_TIMEOUT_SEC = '900'`，Unix shell 使用 `export KIMODO_UV_HTTP_TIMEOUT_SEC=900`。它与限制 uv 可执行文件下载时间的 `KIMODO_UV_INSTALL_TIMEOUT_SEC`、限制下载源探测时间的 `KIMODO_UV_PROBE_TIMEOUT_SEC` 分开。

## 可选 MP4 输出

TCP `generate` 请求可以设置 `output_mp4: true`。QuickServer 会保留所选的动作 `output_format` 作为二进制响应，并在 JSON 结果中增加绝对路径 `mp4_path`。MP4 是额外的渲染结果，不会替换 KMB、BVH 或紧凑 JSON 动作数据。

渲染器使用 UniMate 的 plotting 模块。如果 UniMate 不在 QuickServer 内置目录中，请设置 `KIMODO_UNIMATE_ROOT`。该变量只影响 MP4 渲染，不用于指定 mocap 或 GVHMR 源码。查找顺序包括配置路径、内置的 `vendor/UniMate` 和支持的相邻 checkout，不依赖固定的开发机路径。渲染环境还需要运行时提示的 Python 包（`matplotlib`、`imageio` 和 `imageio-ffmpeg`）。

## 媒体姿态分析

Unity 命令 `pose.fromvideo` 和 `pose.frompicture` 都是异步命令。命令返回 `request_id`，使用 `generation.get` 轮询，并且不会修改 Unity 场景资产。Unity 命令字段是绝对路径 `video_path` 和 `image_path`；底层 QuickServer TCP 适配器使用同一操作的 `media_path` 字段。

`pose.fromvideo` 接受 MP4、MOV、M4V、AVI 和 MKV，文件上限为 2 GiB。可选的 `person` 值（`left` 或 `right`）按画面左右选择表演者。`pose.frompicture` 接受 PNG、JPG 和 JPEG，文件上限为 2 GiB，并返回一个姿态样本。内部会把静态图像按 24 FPS 重复成一秒视频以运行视频估计器，然后返回中间帧的估计结果。

结果是 SMPL-X 姿态数组：旋转为弧度制轴角，位移为米，并包含源帧率、帧数、关节名称、体型参数（`betas`）、静止关节、重力对齐的 Y-up 坐标和可选的 OpenCV 相机坐标字段。这些数据用于后续重定向或构造约束，本身不是 Unity AnimationClip。

## 运行时来源与资源

默认使用内置的 Mixamo LLM Mocap pipeline；设置 `KIMODO_MOCAP_ROOT` 可以指定其他 checkout。`KIMODO_MOCAP_PYTHON` 指定 GVHMR 使用的 Python 解释器；未设置时使用 QuickServer 当前解释器。`GVHMR_ROOT` 指定 GVHMR 运行时和检查点目录，默认是 `vendor/GVHMR`。

分析时如果条件允许，QuickServer 会补齐缺失的 GVHMR 源码和检查点；随后要求检查点旁存在内置的轻量 SMPL-X 资源 `SMPLX_NEUTRAL_LITE_V437.npz`（3,539,332 字节，约 3.54 MB）。仅做姿态分析不需要完整 SMPL-X 模型。源码、解释器、媒体或检查点缺失时会返回分析错误，应先修复后再重试请求。
