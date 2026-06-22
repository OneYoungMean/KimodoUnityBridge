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
* 完全本地部署，你无需为任何内容付费（也不必为此感到自责）！
* 一款开源AI插件, 可以根据提示词生成你想要的人物角色动画！

* ![](https://z3.ax1x.com/2021/09/29/45i1LF.gif)
![](https://z3.ax1x.com/2021/09/29/44EJMV.gif)
![](https://z3.ax1x.com/2021/09/29/44Kfn1.gif)  

[更多演示视频](https://www.bilibili.com/video/BV1wP4y187xE/)  

## 特性

- **即开即用的设计** 你无需担心该项目需要安装各种前置依赖/环境配置/平台限制等问题,作者已经完整测试过了，你也不用担心安装导致本地环境被破坏或者残留文件，所有的内容都是即开即用/即删即走的！

- **完整的Kimodo特性** Kimodo仓库提供的提示词/Fullbody约束/2D平面约束等内容，我们都支持！不用担心你错过了任何内容！

- **完整的Retarget支持** 产生的动画现在会根据你的角色自适应，如果你的角色是Generic的，那么它就只会给你骨骼动画，

- **极其低的学习曲线!** 作者已经帮你们把门槛踏平了!无需任何复杂的添加与操作,通过关键词识别与humanoid识别,只需要三分钟学习就可以**一键生成**你想要的bone与collider!

- **良好的报错系统!** 任何不正确的操作都会给出相关的提示,作者已经帮你们把能犯的错误犯过了!  

- **适应runtime的脚本!** 无需复杂的操作,只要简单设置参数,就能允许你在runtime过程中生成整套的物理特性,是的,你甚至不需要任何操作就可以完整套物理的生成!  

- **高度自由的物理与运行时保存系统!** 提供各种参数,可以自由的组合出你想要的的物理特性!你无需反复调试这些物理参数,因为你可以随时在runtime过程中修改并保存他们!  

- **独特的迭代与除颤机制!** 我们通过细分物体的运行轨迹,在一帧内同时计算多个细分位置的受力情况并加以综合,通常只需要四次你就能获得预期的效果,只要你迭代的足够多,你就能获得无限接近稳定的物理!  

- **完整且高效的Collider系统!** 支持球体,胶囊体,立方体的碰撞;支持杆件/点与collider的碰撞;无需创建transfrom,你可以在交互界面实现偏移,旋转等效果!;同时,该脚本提供了一套完整的collider过滤机制与距离近似估计的算法,大大提高了碰撞的运行效率,一切都是为了让你尽可能的快!  

- **简洁的操作界面!** 是的,我们已经将大部分能够优化的操作界面已经优化掉了,现在不会再有多余的选项出现,并且你可以直接在inspector看到统计的数据.  

- **完整的内部源码!** 不打包dll,提供所有的运行细节以及大量的注释!你可以任意定修改某一部分,已获得想要的物理效果与特殊性质,并且大可不必担心随之而来的耦合问题!  

- **免费!** 以及作者被dynaimc bone坑走了15美刀.** 并且<s>作者是MMD模型白嫖怪</s>MMD友好程度**极高!**

- **作者长期在线!** 有issue必回!包君满意!

## 要求

- Unity2018.4及以上,除webGL外所有支持unity jobs的平台.  
***

## 使用的项目
* [UnityBVA](https://github.com/bilibili/UnityBVA)

## 快速开始

施工中...

### 说明书

施工中...

### 最后,如果你喜欢本项目记得给本项目star!
```C#
[省略掉的吐槽很辛苦的话]
[省略掉的吐槽自己真的很穷的话]
[省略掉的小声BB的话]
加油嗷~
```

