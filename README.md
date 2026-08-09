[演示视频](https://www.bilibili.com/video/BV1HG7361Env) . [完整的Demo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo) .[快速开始](FastBegin.md) . [使用说明](Manual/README.md)

# License
[Apache License 2.0](https://github.com/OneYoungMean/KimodoUnityBridge/blob/main/LICENSE)

# 2.0.12 更新点速览
- **新增 ARDY 流式生成**、History/seek、Playback Reserve，以及 KMB History/Future Clip 约束协议/API。
- **新增 `KimodoRuntimeMotionDriver`** ，支持 Runtime 连续生成、实时重定向、提示词更新与运行时约束。
- **支持 Windows、macOS、Linux**；NVIDIA CUDA 是当前最完整的加速路线，Apple MPS、AMD/ROCm 与 Intel XPU 为实验性支持。
- 增加曲线路径生成（实验性）
- 重构生成管线和 QuickServer 通讯，统一 Session、任务状态、取消与 KMB 直接传输。
- 缩减服务器包体，改进下载站点探测、错误提示和生成进度反馈。
- 普通 Kimodo 的超过 10 秒生成现在由 QuickServer 自动均分为连续片段；后续片段复用前段末尾姿态完成过渡，整条约束仍按原始时间轴处理。
- 改进 Timeline 首尾约束、约束预览、Avatar 自动解析和前后片段对齐。


## 更新注意事项
**从 1.x 升级时，请先在 Server Manager 停止服务器，再点击 `Reinstall Kimodo Server` 重铺运行目录；该操作会保留已有的 `models` 目录。**

自动化入口应完整读取根目录的 [Kimodo Animation Skill](SKILL.md)，并按其中的最小生成自检与 API 契约执行。中文对照见 [SKILL-zh.md](SKILL-zh.md)。它们是当前 Timeline Session、查询、生成、分析与 Bake 的唯一操作规范；不同 Unity 使用入口见 [Manual/自动化使用入口](Manual/Command%20API%20说明书.md)。
***

# KimodoUnityBridge
![](Manual/Kimodo%20Unity%20Bridge_01.png)
**开箱即用，完全运行在本地的免费 AI 人形动画生成系统**[快速开始](FastBegin.md) .   
* 基于 https://github.com/nv-tlabs/kimodo  |  https://github.com/nv-tlabs/ardy
* 基于 https://github.com/OneYoungMean/NvlabKimodoQuickServer (感谢[Aero-Ex](https://gist.github.com/Aero-Ex) 他的文档解决了我很大问题)
* CPU/GPU 模式自适应；支持 Windows、macOS、Linux，无法使用加速器时会回退 CPU。
* 完全本地部署，你无需为任何内容付任何费用（也不必为此感到自责）！
* 一款开源AI插件, 可以根据提示词生成你想要的人物角色动画！

## 要求
- Unity 2022.3+，Windows、macOS 和 Linux 平台。
- 内存>=8G,硬盘空间>=16G
- NVIDIA CUDA 是当前最完整的 GPU 路线；Apple MPS、AMD/ROCm 与 Intel XPU 属于实验性支持。
- 始终可用，但 CPU 生成速度会明显慢于 GPU。

## 特性

- **即开即用的设计** 你无需担心该项目需要安装各种前置依赖/环境配置/设备限制等问题,作者已经完整测试过了，你也不用担心安装导致本地环境被破坏或者残留文件，所有的内容都是即开即用/即删即走的！

- **Kimodo 核心生成能力** 支持提示词、FullBody、Root2D 与末端约束，以及完整 Root Motion。具体可用项以当前模型 Profile 和工具面板为准。

- **自适应Retarget动画** 产生的动画现在会根据你的角色自适应，如果你的角色是Generic的，那么它就只会给你骨骼动画，Humanoid的就会给你肌肉动画，无需担心各种动画Transition的问题！

- **极其低的学习曲线!** 作者已经帮你们把门槛踏平了!无需任何复杂的添加与操作,只需要输入提示词，放置约束，点击generate 然后等待结果生成就可以了！

- **Runtime 功能支持!** `KimodoRuntimeMotionDriver` 支持连续生成、实时重定向、提示词更新和运行时约束；发布前可通过 **Kimodo → Install Kimodo Runtime To StreamingAssets** 安装运行环境。

- **高度自由的Constraint功能!** 你可以从一段已有的动画当中创建pose constraint，也可以手动创建一个pose constraint并编辑它们。你甚至可以生成一些kimodo动画，然后从里面挑选合适的姿势，放下constraint marker 采样它们！

- **贴近实际的新功能**  KimodoUnityBridge支持收尾帧约束/自动匹配上一个动画末尾等独特功能，你可以用这些功能很方便做出长动画/Loop动画/过渡动画等效果！

- **Animator Tool路线** 我们的目标不仅仅是在timeline上使用，我们更希望能为用户提供Animator当中的各种功能，很快你就能看到一键优化状态机动画！一键优化过渡动画！甚至是基于Motion Matching的完整动画管线，我们都在考虑当中！

- **简洁的操作界面!** 是的,我们已经将大部分能够优化的操作界面已经优化掉了,现在不会再有多余的选项出现,并且你可以直接在inspector看到统计的数据.  

- **完整的内部源码!** 不打包dll,提供所有的运行细节以及大量的注释!你可以任意定修改某一部分,已获得想要的物理效果与特殊性质,并且大可不必担心随之而来的耦合问题!  

- **免费!以及作者长期在线!** 作者只想让更多的Unity开发者能够用上便宜好用的动画！ 有issue必回!包君满意!

### 已知问题
kimodo runtime生成暂时有点卡顿    
metal平台会暂时遇到模型不可用的问题  

### Bug Report
由于项目较大且开发时间较短，bug难免有所疏漏，在这里提前给用户老爷抱歉啦，如果你很不幸（或者说很幸运）遇到了bug，请提交一下[Editor.log](https://blog.csdn.net/codingriver/article/details/86551964)  ，方便作者改进和维护，感激不尽！

### 最后,如果你喜欢本项目记得给本项目star!
```C#
[省略掉的吐槽很辛苦的话]
[省略掉的吐槽自己如何摆烂的话]
[省略掉的小声BB的话]
肴核既尽，不知东方之既白
```
## 致谢 
感谢以下人员对本项目的付出！  
[AkiKurisu](https://github.com/AkiKurisu )     
