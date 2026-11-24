# QuickServer 运行时说明

如果你明确计划在 Unity 中使用 Kimodo，强烈建议使用 Unity CLI。

本页记录项目自带 QuickServer 中容易与 Unity 公共命令混淆的细节。请求和响应字段仍以命令 schema 为准。

## 启动器下载超时

启动脚本会把 `KIMODO_UV_HTTP_TIMEOUT_SEC` 映射为 uv 使用的 `UV_HTTP_TIMEOUT`，控制安装和运行时操作中的包下载请求超时，默认 300 秒。包源较慢时，可在启动 QuickServer 前设置该变量；例如 PowerShell 使用 `$env:KIMODO_UV_HTTP_TIMEOUT_SEC = '900'`，Unix shell 使用 `export KIMODO_UV_HTTP_TIMEOUT_SEC=900`。它与限制 uv 可执行文件下载时间的 `KIMODO_UV_INSTALL_TIMEOUT_SEC`、限制下载源探测时间的 `KIMODO_UV_PROBE_TIMEOUT_SEC` 分开。

## 可选 MP4 输出

TCP `generate` 请求可以设置 `output_mp4: true`。QuickServer 会保留所选的动作 `output_format` 作为二进制响应，并在 JSON 结果中增加绝对路径 `mp4_path`。MP4 是额外的渲染结果，不会替换 KMB、BVH 或紧凑 JSON 动作数据。

渲染器使用 UniMate 的 plotting 模块。如果 UniMate 不在 QuickServer 内置目录中，请设置 `KIMODO_UNIMATE_ROOT`。该变量只影响 MP4 渲染，不用于指定 mocap 或 GVHMR 源码。查找顺序包括配置路径、内置的 `vendor/UniMate` 和支持的相邻 checkout，不依赖固定的开发机路径。渲染环境还需要运行时提示的 Python 包（`matplotlib`、`imageio` 和 `imageio-ffmpeg`）。

## 媒体姿态分析

Unity 命令 `pose.fromvideo` 和 `pose.frompicture` 都是异步命令。它们返回 `request_id`，使用 `generation.get` 轮询，并且不会修改 Unity 场景资产。Unity 命令字段是绝对路径 `video_path` 和 `image_path`；底层 QuickServer TCP 适配器使用同一操作的 `media_path` 字段。

`pose.fromvideo` 接受 MP4、MOV、M4V、AVI 和 MKV，文件上限为 2 GiB。`pose.frompicture` 接受 PNG、JPG 和 JPEG，文件上限为 2 GiB，并返回一个姿态样本。处理图片时，QuickServer 会将静态图像重复为 30 FPS、时长一秒的视频，然后保留中间帧的估计结果。

返回结果是 KMB1 附件，包含 FlowHMR 的 52 关节 SMPL-H 动作，以及源帧率和帧数。可用时还会包含相机、逐帧全局时间和人体检测轨道；这些数据可用于在 Unity 中重定向或构造约束。

## 运行时来源与资源

`pose.fromvideo` 和 `pose.frompicture` 使用随包提供的 FlowHMR 源码。Setup 会为 FlowHMR 创建独立的 Python 3.10 环境；设置 `KIMODO_SETUP_FLOWHMR=0` 可跳过该环境，设置 `KIMODO_FLOWHMR_PYTHON` 可指定外部管理的解释器。FlowHMR 权重和受限的人体模型资源按上游访问条件及许可证要求放在 `models/FlowHMR` 下。
