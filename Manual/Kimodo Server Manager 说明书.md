# Kimodo Server Manager

## 概述

Kimodo Server Manager 是整个插件的控制台。生成动画的本地服务器、下载好的模型、以及一系列全局选项，都在这里集中管理。

它位于 **Project Settings → Kimodo Server Manager**。如果你是第一次使用插件、或者遇到了服务器相关的问题，这里通常是第一站。

页面顶部的 **QuickServer Directory** 是运行环境所在目录，其中应包含 `run_server` 启动脚本和 `package.json`。默认下载的模型和运行日志也保存在这个目录下。

<!-- 这里放一张 Server Manager 整体面板的截图 -->



## 初始化运行环境

当运行目录还不存在时，页面会提示 **"Directory does not exist"**，并显示一个 **Create Kimodo Server** 按钮。点击它，工具会创建运行目录和必需的服务器文件。

运行目录已经存在时，这个按钮会变成 **Reinstall Kimodo Server**，用于在环境损坏或想重新铺设时重装模板文件；已有的 `models` 目录会保留，不必重新下载模型。

旁边的 **Refresh** 按钮用于重新扫描运行目录和模型文件夹，并刷新服务器状态。

环境建好之后，页面会展开 Startup、Server、Detected Models、Actions 四个区域。

<!-- 这里放一张未初始化状态（Create Kimodo Server）的截图 -->



## Startup：默认生成与调试选项

这一区既是服务器的启动配置，也集中了大部分全局选项。

| 选项 | 说明 |
| --- | --- |
| **Default Model** | Timeline 等编辑器生成入口默认使用的模型。QuickServer 会按每次请求切换模型，不再依赖服务器启动时的固定模型。 |
| **Default Text Encoder Mode** | High Precision 使用 FP16；High Performance 使用 NF4/INT8。运行时会按剩余显存和后端能力自动选择设备。 |
| **Max Cached Clip** | 缓存目录（Assets/KimodoGeneratedClips/Cache）下保留的缓存片段上限，范围 1–1000。遇到卡顿可调到 100 左右。 |
| **Clear Clip Cache** | 清理缓存目录中没有被任何场景或资源引用的片段。大型项目上这个操作可能稍慢。 |
| **Timeline Constraint Cache Time** | Timeline 约束采样缓存的固定区间长度，以 30 FPS 帧数表示。一般保持默认。 |
| **Generate Timeout (sec)** | 生成请求的全局超时时间。 |
| **Force CPU** | 发送 `simulate_free_vram_gb=0`，让动作模型和文本编码器都走 CPU。 |
| **Debug: Write Resampled Cache Clips** | 调试 Timeline 约束采样时写出中间缓存片段；普通用户应保持关闭。 |
| **Local Models Path** | 可选外部模型目录。它会作为编辑器生成请求的模型根目录，也用于 Detected Models 扫描，但不会移动 QuickServer 运行目录。旁边的 **Browse...** 可以直接选文件夹。 |

下方还会显示 **Setup Profile**（当前运行环境的配置概况），供排查问题时参考。

<!-- 这里放一张 Startup 区域的截图 -->



## Server：启动与停止

这一区显示服务器当前的运行状态：

- **Server is connected**：Unity 已连接到服务器。
- **Server is not running**：服务器未运行。
- **compiling... / detect...**：编辑器正在编译或刚进入检测，稍等即可。
- 若出现 **"Detected stale endpoint file"**，表示有一个残留的端口记录但进程并未存活，属无害提示。

下方的按钮会根据状态在 **Start Server** 和 **Stop Server** 之间切换。模型、文本编码器模式和 CPU/GPU 路由由生成请求传给 QuickServer，可在请求之间切换；操作进行中会显示 **Processing...**。

> 提示：通常你不需要手动启动服务器——点击片段的 Generate & Bake 时它会自动拉起。这一区主要用于手动控制和排查。

<!-- 这里放一张服务器运行状态的截图 -->



## Detected Models：已安装模型

这里列出检测到的模型文件夹，顶部的 **Source** 显示它们来自哪个目录。

每个模型右侧有 **Delete** 按钮，可从磁盘删除该模型目录。如果你在 Startup 区设置了 **Local Models Path**（自定义模型路径），删除会被禁用，页面会提示 **"Custom models path is active. Delete is disabled."**——这是为了避免误删你自己管理的外部模型。

<!-- 这里放一张模型列表的截图 -->



## Actions：维护操作

这一区提供运行目录的清理操作，请谨慎使用：
- **Delete All Data**：删除整个 Kimodo 运行目录，**包括所有下载的模型和缓存**。这个操作不可撤销，点击后会再次弹窗确认。只有在想彻底重置时才使用。

<!-- 这里放一张 Actions 区域及删除确认弹窗的截图 -->



## 注意事项

- 第一次创建环境和下载模型可能需要较长时间；实际占用取决于所选模型与编码器，建议至少预留 16 GB 可用空间。
- Max Cached Clip、Timeline Constraint Cache Time、Generate Timeout 等选项改动后会立即保存，不需要额外确认。
- 服务器相关报错的具体处理，请参阅 **常见问题与报错处理** 一文。
