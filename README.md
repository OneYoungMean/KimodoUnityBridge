[演示视频](https://www.bilibili.com/video/BV1HG7361Env) . [完整的Demo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo) .[快速开始](FastBegin.md) . [使用说明](Manual/README.md)

# License
[Apache License 2.0](https://github.com/OneYoungMean/KimodoUnityBridge/blob/main/LICENSE)

# 1.2.0更新点速览    
* **支持amd显卡，xpu显卡支持（Experimental）**
* **增加Mac 平台支持，现在Linux，windows，mac 都可以正常工作了（Experimental）**
* **大幅优化服务器体积**
* **更完整的报错，更流畅的反馈机制**
* **大幅度优化Generate pipe不稳定的问题**
* **小幅度优化Quick Server性能,使用FaltPatten优化QickServer与Bridge通讯机制**
* 修复了若干前后动画不匹配的问题.
* 增加了RuntimeDemo新的api,方便用户调用.
* 完善了说明书.

## 更新注意事项
**老用户请删除项目目录\NvlabKimodoQuickServer~并重新运行，代码会自动拉取最新的包！否则运行会报错!**
***

# KimodoUnityBridge
![](Manual/Kimodo%20Unity%20Bridge_01.png)
**开箱即用，完全运行在本地的免费 AI 人形动画生成系统**[快速开始](FastBegin.md) .   
* 基于 https://github.com/nv-tlabs/kimodo 
* 基于 https://github.com/OneYoungMean/NvlabKimodoQuickServer (感谢[Aero-Ex](https://gist.github.com/Aero-Ex) 他的文档解决了我很大问题)
* CPU/GPU模式自适应（CUDA大约5秒，CPU大约1一分钟）兼容Windows/Linux平台.
* 完全本地部署，你无需为任何内容付任何费用（也不必为此感到自责）！
* 一款开源AI插件, 可以根据提示词生成你想要的人物角色动画！

## 要求
- Unity2021+（更低的平台尚未测试），Windows和Linux 平台。
- 内存>=8G,硬盘空间>=16G
- Windows,Mac,Linux平台
- 对部分平台（Nvidia10系以上， AMD7000以上，部分XPU）会启动CUDA加速

## 特性

- **即开即用的设计** 你无需担心该项目需要安装各种前置依赖/环境配置/设备限制等问题,作者已经完整测试过了，你也不用担心安装导致本地环境被破坏或者残留文件，所有的内容都是即开即用/即删即走的！

- **完整的Kimodo特性** Kimodo仓库提供的提示词/Fullbody约束/2D平面Constraint等内容，我们都支持！不用担心你错过了任何内容！**所有的动画都有完整的RootMotion** ！

- **自适应Retarget动画** 产生的动画现在会根据你的角色自适应，如果你的角色是Generic的，那么它就只会给你骨骼动画，Humanoid的就会给你肌肉动画，无需担心各种动画Transition的问题！

- **极其低的学习曲线!** 作者已经帮你们把门槛踏平了!无需任何复杂的添加与操作,只需要输入提示词，放置约束，点击generate 然后等待结果生成就可以了！

- **runtime功能支持!** Kimodo Bridge Server现在支持 Runtime运行了！如果你的GPU足够的好（3080即以上）你就可以** 实时生成动画！**

- **高度自由的Constraint功能!** 你可以从一段已有的动画当中创建pose constraint，也可以手动创建一个pose constraint并编辑它们。你甚至可以生成一些kimodo动画，然后从里面挑选合适的姿势，放下constraint marker 采样它们！

- **贴近实际的新功能**  KimodoUnityBridge支持收尾帧约束/自动匹配上一个动画末尾等独特功能，你可以用这些功能很方便做出长动画/Loop动画/过渡动画等效果！

- **Animator Tool路线** 我们的目标不仅仅是在timeline上使用，我们更希望能为用户提供Animator当中的各种功能，很快你就能看到一键优化状态机动画！一键优化过渡动画！甚至是基于Motion Matching的完整动画管线，我们都在考虑当中！

- **简洁的操作界面!** 是的,我们已经将大部分能够优化的操作界面已经优化掉了,现在不会再有多余的选项出现,并且你可以直接在inspector看到统计的数据.  

- **完整的内部源码!** 不打包dll,提供所有的运行细节以及大量的注释!你可以任意定修改某一部分,已获得想要的物理效果与特殊性质,并且大可不必担心随之而来的耦合问题!  

- **免费!以及作者长期在线!** 作者只想让更多的Unity开发者能够用上便宜好用的动画！ 有issue必回!包君满意!

### 已知问题

Constraint Edit保存的的时候有概率没有写入成功.  

### Bug Report
由于项目较大且开发时间较短，bug难免有所疏漏，在这里提前给用户老爷抱歉啦，如果你很不幸（或者说很幸运）遇到了bug，请提交一下log，方便作者改进和维护，感激不尽：  
如果你遇到的是unity报错：请提交[Editor.log](https://blog.csdn.net/codingriver/article/details/86551964)  
如果你遇到的是server报错（例如server exit with code 1）请将项目路径\NvlabKimodoQuickServer~\log下的内容发送给我（Runtime在StreamingAseets目录下）  
再次诚恳表示抱歉！Orz

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
