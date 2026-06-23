[演示视频](https://www.bilibili.com/video/BV1HG7361Env)
求start！求start！求start！
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
***

# KimodoUnityBridge
![](https://github.com/OneYoungMean/KimodoUnityBridge/blob/main/Manual/Kimodo%20Unity%20Bridge_01.png)

**开箱即用，完全运行在本地的免费 AI 人形动画生成系统**. 
* 基于 https://github.com/nv-tlabs/kimodo 
* 基于 https://github.com/OneYoungMean/NvlabKimodoQuickServer(感谢[Aero-Ex](https://gist.github.com/Aero-Ex) 他的文档解决了我很大问题)
* CPU/GPU模式自适应（CUDA大约5秒，CPU大约1一分钟）兼容Windows/Linux平台.
* 完全本地部署，你无需为任何内容付任何费用（也不必为此感到自责）！
* 一款开源AI插件, 可以根据提示词生成你想要的人物角色动画！

## 安装
1. 通过Unity Package Manager 安装:
   a. 复制https://github.com/OneYoungMean/KimodoUnityBridge.git   
   b. 打开项目中的packagemanager，点击add package from git url...并填入  
   c. 等待完成，如果一切正常，你会在菜单栏看到kimodo的菜单.   
   <img width="1061" height="526" alt="Unity_uAULwLfP7W" src="https://github.com/user-attachments/assets/5f18b33e-4a21-42cf-8acd-57c0e548d44d" />  
2. 通过安装包安装：  
   a. 下载https://github.com/OneYoungMean/KimodoUnityBridge/releases/download/v1.1.10/KimodoUnityBridge-v1.1.10.zip （如果有更新的版本先下载更新的，我还在研究怎么打latest version）  
   b. 解压到项目/Packages目录下面  
   c. 切换回unity等待完成，如果一切正常，你会在菜单栏看到kimodo的菜单.  
3. 下载FullDemo  
   a.下载https://github.com/OneYoungMean/KimodoUnityBridge_FullDemo 项目(Download Zip就可以）  
   b.运行项目查看效果  
   c.项目组件放在KimodoUnityBridge_FullDemo/Packages目录下  
   
## 快速开始  
**首次运行生成动画，脚本会自己下载模型+配置环境（大概需要10G），请耐心等待，如遇报错（一般是网络波动造成的),请重新生成即可解决**  
1. 点击Packge Manager 转到Kimdo Unity Animation Tool界面  
2. 点击sample一栏，并点击箭头指向的import按钮  
3. 在Project当中找到刚导入的lightSample场景并打开  
4. 在场景中找到Timeline游戏对象，打开上面挂载的PlayableDirector脚本当中的timeline资产  
5. 在timeline 窗口当中选择一个timeline clip  
6. 在inspector面板中点击生成(建议勾选一下random，不然动画会和原来一样）  
7. 运行查看效果  
<img width="3840" height="2064" alt="微信图片_20260623111822_83_24" src="https://github.com/user-attachments/assets/3d01af83-712c-45a9-99f6-8f33fa8dba6e" />  
***

## 要求
- Unity2021+（更低的平台尚未测试），Windows和Linux 平台。
- 内存>=8G,硬盘空间>=10G
- (Nvidia 显卡内存>=6G 可运行CUDA版本，这里不做强制限制）


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

### 说明书

施工中...

### 已知问题
CUDA平台生成clip的时候有小概率会走CPU生成管线.重启一下unity一般能解决问题.  
第一次Generator有小概率会失败，重新生成可以解决一下问题.  
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

