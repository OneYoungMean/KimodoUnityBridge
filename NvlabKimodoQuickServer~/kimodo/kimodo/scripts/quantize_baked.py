# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Script to permanently serialize the baked LLM2Vec model into 4-bit (NF4)."""

import torch
from kimodo.model.llm2vec import LLM2Vec
from transformers import BitsAndBytesConfig
import os
import glob

def main():
    source_model = "./models/baked_llm2vec"
    target_model = "./models/baked_llm2vec_nf4"
    
    print(f"\n🚀 QUANTIZING BAKED BRAIN TO 4-BIT (NF4)... ")
    print(f"--> Source: {source_model}")
    
    # 1. Define the 4-bit (NF4) configuration
    quant_config = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_compute_dtype=torch.float16,
        bnb_4bit_use_double_quant=True,
    )

    # 2. Load the baked model into 4-bit
    # (Using the LLM2Vec class so it preserves the Bidirectional architecture)
    llm2vec = LLM2Vec.from_pretrained(
        base_model_name_or_path=source_model,
        quantization_config=quant_config,
        torch_dtype=torch.float16,
        device_map="cuda"
    )

    print(f"\n💾 SAVING NF4 SERIALIZED MODEL...")
    print(f"--> Target: {target_model}")
    
    # 3. Save the serialized 4-bit weights
    # Modern transformers will save the quantization metadata in config.json
    llm2vec.save(target_model)

    print("\n✅ SUCCESS! NF4 Serialization complete.")
    
    # Verify file size
    model_files = glob.glob(os.path.join(target_model, "*.safetensors"))
    if not model_files:
        model_files = glob.glob(os.path.join(target_model, "*.bin"))
    total_size_gb = sum(os.path.getsize(f) for f in model_files) / (1024**3)
    
    print(f"--> New Total Size: {total_size_gb:.2f} GB (Should be ~5GB)")
    print(f"The folder '{target_model}' is now ready to be shared!")

if __name__ == "__main__":
    main()
