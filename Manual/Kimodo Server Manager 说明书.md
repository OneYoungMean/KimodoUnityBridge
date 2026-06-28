# Kimodo Server Manager

## 概述

Kimodo Server Manager 是整个插件的控制台。生成动画的本地服务器、下载好的模型、以及一系列全局选项，都在这里集中管理。

它位于 **Project Settings → Kimodo Server Manager**。如果你是第一次使用插件、或者遇到了服务器相关的问题，这里通常是第一站。

页面顶部会显示 **Runtime Root**——也就是运行环境所在的目录。所有下载的模型和缓存都放在它下面。

<!-- 这里放一张 Server Manager 整体面板的截图 -->



## 初始化运行环境

当运行目录还不存在时，页面会提示 **"Directory does not exist"**，并显示一个 **Create Kimodo Server** 按钮。点击它，工具会创建运行目录和必需的服务器文件。

运行目录已经存在时，这个按钮会变成 **Reinstall Kimodo Server**，用于在环境损坏或想重新铺设时，按模板重装一遍运行目录。

旁边的 **Refresh** 按钮用于重新扫描运行目录和模型文件夹，并刷新服务器状态。

环境建好之后，页面会展开 Startup、Server、Detected Models、Actions 四个区域。

<!-- 这里放一张未初始化状态（Create Kimodo Server）的截图 -->



## Startup：启动与全局选项

这一区既是服务器的启动配置，也集中了大部分全局选项。

| 选项 | 说明 |
| --- | --- |
| **Model** | 从这个页面启动服务器时默认使用的模型。 |
| **VRAM Mode** | Low 使用量化编码器（约 4G）；High 使用完整模型栈（约 16G）。下方会估算所选模式的显存占用。 |
| **Max Cached Clip** | 缓存目录（Assets/KimodoGeneratedClips/Cache）下保留的缓存片段上限，范围 1–1000。遇到卡顿可调到 100 左右。 |
| **Clear Clip Cache** | 清理缓存目录中没有被任何场景或资源引用的片段。大型项目上这个操作可能稍慢。 |
| **Generate Timeout (sec)** | 生成请求的全局超时时间。 |
| **Enable Floating UI** | 是否在 Timeline 和 Animator 窗口上显示 Kimodo 的浮动提示词面板。 |
| **Idle Shutdown (min)** | 服务器空闲多少分钟后自动关闭，填 0 表示不自动关闭。 |
| **Local Models Path** | 可选项，指定一个外部模型目录作为检测来源。它只改变检测列表，不会移动运行目录本身。旁边的 **Browse...** 可以直接选文件夹。 |

下方还会显示 **Setup Profile**（当前运行环境的配置概况），供排查问题时参考。

<!-- 这里放一张 Startup 区域的截图 -->



### 两个实验性选项

这两项默认关闭，仅在特定场景下使用，名字后面都带 **(Experimental)**：

- **Always Keep Server**：让服务器在编译、程序集重载、进入/退出 Play Mode 时保持存活。好处是省去反复重启，但可能造成内存泄漏或残留状态，按需开启。
- **Keep CPU Force**：强制服务器以 CPU 模式启动，仅用于测试 CPU 启动路径，正常使用不需要打开。

<!-- 这里放一张实验性选项及其警告提示的截图 -->



## Server：启动与停止

这一区显示服务器当前的运行状态：

- **Running at 127.0.0.1:端口**：服务器正在运行。
- **Server is not running**：服务器未运行。
- **compiling... / detect...**：编辑器正在编译或刚进入检测，稍等即可。
- 若出现 **"Detected stale endpoint file"**，表示有一个残留的端口记录但进程并未存活，属无害提示。

下方的按钮会根据状态在 **Start Server** 和 **Stop Server** 之间切换，使用上面 Startup 区设定的模型和显存模式来启动。操作进行中会显示 **Processing...**。

> 提示：通常你不需要手动启动服务器——点击片段的 Generate & Bake 时它会自动拉起。这一区主要用于手动控制和排查。

<!-- 这里放一张服务器运行状态的截图 -->



## Detected Models：已安装模型

这里列出检测到的模型文件夹，顶部的 **Source** 显示它们来自哪个目录。

每个模型右侧有 **Delete** 按钮，可从磁盘删除该模型目录。如果你在 Startup 区设置了 **Local Models Path**（自定义模型路径），删除会被禁用，页面会提示 **"Custom models path is active. Delete is disabled."**——这是为了避免误删你自己管理的外部模型。

<!-- 这里放一张模型列表的截图 -->



## Actions：维护操作

这一区是两个较重的维护操作，请谨慎使用：

- **Try Fix (delete and reconfigure)**：触发自动修复流程，清理损坏的部分并重新配置运行环境。当服务器反复启动失败、或安装内容不完整时用它。
- **Delete All Data**：删除整个 Kimodo 运行目录，**包括所有下载的模型和缓存**。这个操作不可撤销，点击后会再次弹窗确认。只有在想彻底重置时才使用。

<!-- 这里放一张 Actions 区域及删除确认弹窗的截图 -->



## 注意事项

- 第一次创建环境、下载模型需要较长时间和约 10G 空间，请预留好磁盘。
- Max Cached Clip、Generate Timeout 这些选项改动后会立即保存，不需要额外确认。
- 服务器相关报错的具体处理，请参阅 **常见问题与报错处理** 一文。
