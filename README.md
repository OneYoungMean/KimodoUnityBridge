# License
[Apache License 2.0](https://github.com/OneYoungMean/KimodoUnityBridge/blob/main/LICENSE)

# 1.1.10更新点速览    
* **Runtime支持，现在你可以将server打包进入runtime当中运行了**
* **AnimatorTool 现在单独作为一个工具，你不必再timeline事先bake好动画再移植了**
* **[完整的Demo](https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo)，你不必再去看lightdemo了！**  
* 修复了unity 2021 报错的问题.
* 修复了在linux平台下显存不大于6G无法切换到CPU模式的问题.


## 更新注意事项
**你可能需要移除所有的KimodoAnimationCache，它们不再受支持，点击ProjectSetting/Kimodo Server Manager/Clear Clip Cache 来解决问题**.  
**如果遇到卡顿问题,尝试将Max Cached Clip设置为 100**  


# KimodoUnityBridge
![](https://z3.ax1x.com/2021/09/29/44E1Gn.png) 

**开箱即用，完全运行在本地的免费 AI 人形动画生成系统**. 
* 基于 https://github.com/nv-tlabs/kimodo 
* 基于 https://github.com/OneYoungMean/NvlabKimodoQuickServer(感谢[Aero-Ex](https://gist.github.com/Aero-Ex} 他的文档解决了我很大问题)
* 兼容CPU/GPU模式运行（CUDA大约5秒，CPU大约1一分钟）兼容Windows/Linux平台.
* 完全本地部署，你无需为任何内容付任何费用（也不必为此感到自责）！
* 一款开源AI插件, 可以根据提示词生成你想要的人物角色动画！

* ![](https://z3.ax1x.com/2021/09/29/45i1LF.gif)
![](https://z3.ax1x.com/2021/09/29/44EJMV.gif)
![](https://z3.ax1x.com/2021/09/29/44Kfn1.gif)  

[更多演示视频](https://www.bilibili.com/video/BV1wP4y187xE/)  

## 特性

- **即开即用的设计** 你无需担心该项目需要安装各种前置依赖/环境配置/设备限制等问题,作者已经完整测试过了，你也不用担心安装导致本地环境被破坏或者残留文件，所有的内容都是即开即用/即删即走的！

- **完整的Kimodo特性** Kimodo仓库提供的提示词/Fullbody约束/2D平面约束等内容，我们都支持！不用担心你错过了任何内容！

- **完整的Retarget支持** 产生的动画现在会根据你的角色自适应，如果你的角色是Generic的，那么它就只会给你骨骼动画，Humanoid的就会给你肌肉动画，无需担心各种动画Transition的问题！

- **极其低的学习曲线!** 作者已经帮你们把门槛踏平了!无需任何复杂的添加与操作,只需要输入提示词，放置约束，点击generate 然后等待结果生成就可以了！

- **runtime功能支持!** Kimodo Bridge Server现在支持 Runtime运行了！如果你的GPU足够的好（3080即以上）你就可以** 实时生成动画！**

- **高度自由的Constraint功能!** 你可以从一段已有的动画当中创建pose constraint，也可以手动创建一个pose constraint并编辑它们。你甚至可以生成一些kimodo动画，然后从里面挑选合适的姿势，放下constraint marker 采样它们！

- **简洁的操作界面!** 是的,我们已经将大部分能够优化的操作界面已经优化掉了,现在不会再有多余的选项出现,并且你可以直接在inspector看到统计的数据.  

- **完整的内部源码!** 不打包dll,提供所有的运行细节以及大量的注释!你可以任意定修改某一部分,已获得想要的物理效果与特殊性质,并且大可不必担心随之而来的耦合问题!  

- **免费!以及作者长期在线!** 作者只想让更多的Unity开发者能够用上便宜好用的动画！ 有issue必回!包君满意!

## 要求

- Unity2021+（更低的平台尚未测试），Windows和Linux 平台。
***

## 快速开始

施工中...

### 说明书

施工中...

### 最后,如果你喜欢本项目记得给本项目star!
```C#
[省略掉的吐槽很辛苦的话]
[省略掉的吐槽自己如何摆烂的话]
[省略掉的小声BB的话]
加油嗷~
```

