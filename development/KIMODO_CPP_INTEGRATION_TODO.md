# kimodo.cpp 原生后端接入 TODO

## 目标

在不破坏现有 Python/PyTorch QuickServer 的前提下，尝试接入
[localai-org/kimodo.cpp](https://github.com/localai-org/kimodo.cpp)，先验证
“缓存 text embedding 后，Kimodo motion denoiser 是否值得原生化”。

目标运行链路：

```text
缓存的 [1, 1, 4096] F32 embedding
    -> kimodo_generate_embedding()
    -> C++/GGML denoiser + DDIM
    -> local rotations/root positions
    -> 现有 KMB1/Unity 播放链路
```

## 范围边界

- 第一阶段只做无约束、单 prompt、单 sample 的 Kimodo RP 模型。
- 优先验证 SOMA 或 G1；ARDY、通用约束和现有复杂 Clip 约束继续走 Python。
- 不在第一阶段删除 Python runtime，也不改变现有 TCP 协议。
- text encoder 先由现有 Python 路径生成 embedding；native text bundle 作为后续任务。

## TODO

### P0：建立可比较的基线

- [ ] 记录当前 Python/PyTorch denoiser 的 warm/cold 延迟、峰值显存和输出摘要。
- [ ] 固定比较条件：模型、prompt、帧数、diffusion steps、seed 和初始噪声。
- [ ] 明确测试设备：CUDA、CPU、Vulkan；不要把不同后端结果直接混比。
- [ ] 选定一条无约束 `generate` 请求作为回归 fixture。

验收：同一输入可以重复得到可比较的 Python 基线数据。

### P1：准备 kimodo.cpp 原生构建

- [ ] 固定上游 commit，并初始化 GGML 子模块。
- [ ] 在 Windows/MSVC 上构建 `kimodo` DLL 和最小 CLI smoke test。
- [ ] 验证 CPU；再验证 Vulkan backend 和驱动加载。
- [ ] 保存构建产物、编译选项和运行时依赖，不把临时 build 目录提交进包。
- [ ] 确认 `kimodo_abi_version()`、错误缓冲区和释放函数可用。

验收：可以加载一个 native motion GGUF，并成功调用一次 `kimodo_generate_embedding()`。

### P1：准备模型与 embedding

- [ ] 为目标 RP 模型准备对应的 native motion GGUF。
- [ ] 暂时使用现有 Python text encoder 生成 4096 维 F32 embedding。
- [ ] 写一个小工具把 embedding、seed、frames、steps 固定下来，便于重复 benchmark。
- [ ] 校验 embedding 的长度、有限值和模型版本匹配。

验收：native 和 Python 使用同一 embedding、同一初始噪声完成一次 denoiser 运行。

### P1：性能与数值验证

- [ ] 分别测量 motion denoiser 首次运行和 warm 运行。
- [ ] 测量总耗时、每个 diffusion step 耗时、峰值 RAM/VRAM。
- [ ] 比较 root positions、local rotations 的最大绝对误差和相对误差。
- [ ] 分别比较 CPU、CUDA/PyTorch、Vulkan；不要仅凭语言或后端名称判断快慢。
- [ ] 如果 Vulkan 速度不佳，保留 CPU/Vulkan 作为可选后端，不阻塞 Python 默认路径。

验收：形成一份 benchmark 结果，并明确 native 的收益是速度、显存、部署简化，还是没有收益。

### P2：接入 QuickServer（最小切换）

- [ ] 在 `NvlabKimodoQuickServer~/core` 增加 native backend 封装。
- [ ] 首选通过稳定 C ABI 调用；避免第一版直接暴露 GGML 对象。
- [ ] 让 native backend 接收缓存 embedding，调用 `kimodo_generate_embedding()`。
- [ ] 使用现有模型规格补齐 `fps`、joint names、parent indices 和 model name。
- [ ] 将 C++ 返回的 root positions/local XYZW rotations 包装成现有 `KmbMotion`，复用 `encode_kmb1()`。
- [ ] 保持现有 response envelope、task id、cancel 和日志格式不变。
- [ ] native 不可用、模型不支持或请求包含约束时，自动回退 Python。

验收：Unity 无需改协议即可完成一次 native 生成，并能正常播放生成的 KMB。

### P2：Unity/资产回归

- [ ] 用现有 Unity Bridge 生成同一 prompt 的 native clip。
- [ ] 验证帧数、FPS、关节数、关节顺序、四元数顺序（XYZW）和根位移坐标系。
- [ ] 验证 Timeline 播放、保存、重新打开和分析流程。
- [ ] SOMA 若返回 30 joints，明确实现 30→77 扩展，或在第一版禁止 SOMA77 播放。
- [ ] 用至少一个项目内生成/播放 smoke case 回归。

验收：native 生成不会破坏已有 Python 生成、KMB 解析或 Unity 资产持久化。

### P3：后续增强

- [ ] 转换并验证 native LLM2Vec text bundle。
- [ ] 评估 embedding cache 的 key：模型、tokenizer、adapter、prompt 和版本。
- [ ] 评估 text encoder 常驻与按需加载的延迟/显存取舍。
- [ ] 增加原生 multiprompt sequence 接口适配。
- [ ] 评估约束支持；在上游未支持通用约束前，不宣称功能等价。
- [ ] 为 Windows 打包 DLL、Vulkan loader/依赖和模型文件校验流程。

## 暂不做

- [ ] 不直接替换现有 QuickServer 启动器。
- [ ] 不删除 PyTorch、bitsandbytes 或 ARDY 路径。
- [ ] 不把 SMPL-X 权重或其转换产物重新分发；遵守上游模型许可证。
- [ ] 不以“C++”本身作为性能结论，所有结论必须来自固定条件 benchmark。

## 关键参考

- [kimodo.cpp README](https://github.com/localai-org/kimodo.cpp/blob/main/README.md)
- [kimodo.cpp C API](https://github.com/localai-org/kimodo.cpp/blob/main/include/kimodo/kimodo_capi.h)
- [当前 QuickServer runtime](../NvlabKimodoQuickServer~/core/kimodo_runtime.py)
- [当前 QuickServer 协议客户端](../Runtime/Bridge/BridgeProtocolClient.cs)
- [当前 KMB 编解码](../NvlabKimodoQuickServer~/core/protocol/kmb_motion.py)
