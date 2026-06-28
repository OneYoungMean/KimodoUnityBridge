# QuickServer 模型下载清单

这份说明给需要手动配置模型路径的用户看。模型加载只看目录名和关键文件名，确认本地放置是否匹配即可。

## 路径格式说明

默认模型目录格式：

`<models-root>\<模型目录名>\`

如果你用默认共享目录，大多数情况下就是：

`models\<模型目录名>\`

## 主模型

`Kimodo-SOMA-RP-v1`
- 路径：`models\Kimodo-SOMA-RP-v1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-RP-v1.1`

`Kimodo-SOMA-RP-v1.1`
- 路径：`models\Kimodo-SOMA-RP-v1.1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-RP-v1.1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SMPLX-RP-v1`
- 路径：`models\Kimodo-SMPLX-RP-v1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-SMPLX-RP-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SMPLX-RP-v1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

`Kimodo-G1-RP-v1`
- 路径：`models\Kimodo-G1-RP-v1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-G1-RP-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-G1-RP-v1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SOMA-SEED-v1`
- 路径：`models\Kimodo-SOMA-SEED-v1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-SEED-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-SEED-v1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SOMA-SEED-v1.1`
- 路径：`models\Kimodo-SOMA-SEED-v1.1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-SEED-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-SEED-v1.1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

`Kimodo-G1-SEED-v1`
- 路径：`models\Kimodo-G1-SEED-v1\`
- 下载地址：
  - HF: `https://huggingface.co/nvidia/Kimodo-G1-SEED-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-G1-SEED-v1`
- 关键文件：
  - `config.yaml`
  - `model.safetensors`

## 文本编码器

`KIMODO-Meta3_llm2vec_INT8`
- 使用场景：CPU，或显存小于 6GB
- 路径：`models\KIMODO-Meta3_llm2vec_INT8\`
- 下载地址：
  - HF: `https://huggingface.co/oneyoungmean/KIMODO-Meta3_llm2vec_INT8`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_INT8`
- 关键文件：
  - `config.json`
  - `llm2vec_config.json`
  - `quantization_meta.json`
  - `quantized_state_dict.pt`
  - `tokenizer.json`
  - `tokenizer_config.json`

`KIMODO-Meta3_llm2vec_NF4`
- 使用场景：非 `highvram`，且显存不少于 6GB
- 路径：`models\KIMODO-Meta3_llm2vec_NF4\`
- 下载地址：
  - HF: `https://huggingface.co/Aero-Ex/KIMODO-Meta3_llm2vec_NF4`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_NF4`
- 关键文件：
  - `config.json`
  - `llm2vec_config.json`
  - `model.safetensors`
  - `tokenizer.json`
  - `tokenizer_config.json`

`KIMODO-Meta3_llm2vec_FP16`
- 使用场景：`highvram` 默认新路径
- 路径：`models\KIMODO-Meta3_llm2vec_FP16\`
- 下载地址：
  - HF: `https://huggingface.co/Aero-Ex/KIMODO-Meta3_llm2vec_FP16`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_FP16`
- 关键文件：
  - `config.json`
  - `model.safetensors`
  - `tokenizer.json`
  - `tokenizer_config.json`

## Legacy HighVRAM 兼容加载

如果你开启了 `highvram`，并且本地已经有下面这两份老模型，QuickServer 会优先加载这套老路径，不会去下载新的 `KIMODO-Meta3_llm2vec_FP16`。

`Meta-Llama-3-8B-Instruct`
- 路径：`models\Meta-Llama-3-8B-Instruct\`
- 下载地址：
  - HF: `https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct`
  - ModelScope: `https://www.modelscope.cn/models/LLM-Research/Meta-Llama-3-8B-Instruct`
- 关键文件：
  - `config.json`
  - `model-00001-of-00004.safetensors`
  - `model-00002-of-00004.safetensors`
  - `model-00003-of-00004.safetensors`
  - `model-00004-of-00004.safetensors`
  - `model.safetensors.index.json`
  - `tokenizer.json`
  - `tokenizer_config.json`

`LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
- 路径：`models\LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised\`
- 下载地址：
  - HF: `https://huggingface.co/McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
- 关键文件：
  - `adapter_config.json`
  - `adapter_model.safetensors`

## 路由

CPU，或显存小于 6GB：
- 使用 `KIMODO-Meta3_llm2vec_INT8`

GPU 且显存不少于 6GB，未开启 `highvram`：
- 使用 `KIMODO-Meta3_llm2vec_NF4`

GPU 且显存不少于 6GB，开启 `highvram`：
- 如果 `Meta-Llama-3-8B-Instruct` 和 `LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised` 都齐全，优先走老路径
- 否则走 `KIMODO-Meta3_llm2vec_FP16`

---

# QuickServer Model Download Notes (EN)

This section is for users who need to manually place model folders. QuickServer checks folder names and required file names only.

## Path Format

Default model folder format:

`<models-root>\<model-folder-name>\`

If you use the default shared folder, it is usually:

`models\<model-folder-name>\`

## Main Models

`Kimodo-SOMA-RP-v1`
- Path: `models\Kimodo-SOMA-RP-v1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-RP-v1.1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SOMA-RP-v1.1`
- Path: `models\Kimodo-SOMA-RP-v1.1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-RP-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-RP-v1.1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SMPLX-RP-v1`
- Path: `models\Kimodo-SMPLX-RP-v1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-SMPLX-RP-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SMPLX-RP-v1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-G1-RP-v1`
- Path: `models\Kimodo-G1-RP-v1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-G1-RP-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-G1-RP-v1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SOMA-SEED-v1`
- Path: `models\Kimodo-SOMA-SEED-v1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-SEED-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-SEED-v1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-SOMA-SEED-v1.1`
- Path: `models\Kimodo-SOMA-SEED-v1.1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-SOMA-SEED-v1.1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-SOMA-SEED-v1.1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

`Kimodo-G1-SEED-v1`
- Path: `models\Kimodo-G1-SEED-v1\`
- Download:
  - HF: `https://huggingface.co/nvidia/Kimodo-G1-SEED-v1`
  - ModelScope: `https://www.modelscope.cn/models/nv-community/Kimodo-G1-SEED-v1`
- Required files:
  - `config.yaml`
  - `model.safetensors`

## Text Encoders

`KIMODO-Meta3_llm2vec_INT8`
- Used for: CPU, or VRAM below 6GB
- Path: `models\KIMODO-Meta3_llm2vec_INT8\`
- Download:
  - HF: `https://huggingface.co/oneyoungmean/KIMODO-Meta3_llm2vec_INT8`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_INT8`
- Required files:
  - `config.json`
  - `llm2vec_config.json`
  - `quantization_meta.json`
  - `quantized_state_dict.pt`
  - `tokenizer.json`
  - `tokenizer_config.json`

`KIMODO-Meta3_llm2vec_NF4`
- Used for: non-`highvram`, VRAM 6GB or higher
- Path: `models\KIMODO-Meta3_llm2vec_NF4\`
- Download:
  - HF: `https://huggingface.co/Aero-Ex/KIMODO-Meta3_llm2vec_NF4`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_NF4`
- Required files:
  - `config.json`
  - `llm2vec_config.json`
  - `model.safetensors`
  - `tokenizer.json`
  - `tokenizer_config.json`

`KIMODO-Meta3_llm2vec_FP16`
- Used for: default new `highvram` path
- Path: `models\KIMODO-Meta3_llm2vec_FP16\`
- Download:
  - HF: `https://huggingface.co/Aero-Ex/KIMODO-Meta3_llm2vec_FP16`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/KIMODO-Meta3_llm2vec_FP16`
- Required files:
  - `config.json`
  - `model.safetensors`
  - `tokenizer.json`
  - `tokenizer_config.json`

## Legacy HighVRAM Compatibility

If `highvram` is enabled and both old folders below are complete, QuickServer will prefer the old local layout instead of downloading `KIMODO-Meta3_llm2vec_FP16`.

`Meta-Llama-3-8B-Instruct`
- Path: `models\Meta-Llama-3-8B-Instruct\`
- Download:
  - HF: `https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct`
  - ModelScope: `https://www.modelscope.cn/models/LLM-Research/Meta-Llama-3-8B-Instruct`
- Required files:
  - `config.json`
  - `model-00001-of-00004.safetensors`
  - `model-00002-of-00004.safetensors`
  - `model-00003-of-00004.safetensors`
  - `model-00004-of-00004.safetensors`
  - `model.safetensors.index.json`
  - `tokenizer.json`
  - `tokenizer_config.json`

`LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
- Path: `models\LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised\`
- Download:
  - HF: `https://huggingface.co/McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
  - ModelScope: `https://www.modelscope.cn/models/oneyoungmean/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised`
- Required files:
  - `adapter_config.json`
  - `adapter_model.safetensors`

## Routing Summary

CPU, or VRAM below 6GB:
- Use `KIMODO-Meta3_llm2vec_INT8`

GPU with VRAM 6GB or higher, `highvram` disabled:
- Use `KIMODO-Meta3_llm2vec_NF4`

GPU with VRAM 6GB or higher, `highvram` enabled:
- If both `Meta-Llama-3-8B-Instruct` and `LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised` are complete, the legacy path is used first
- Otherwise use `KIMODO-Meta3_llm2vec_FP16`
