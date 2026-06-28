import torch
import numpy as np
import os
import sys

# 1. Use your high-fidelity wrapper
from kimodo.model.llm2vec.llm2vec_wrapper import LLM2VecEncoder

def get_emb_from_wrapper(path, name, sentences):
    print(f"\n🚀 Loading {name}...")
    encoder = LLM2VecEncoder(
        base_model_name_or_path=path,
        peft_model_name_or_path=None,
        dtype="float16",
        llm_dim=4096
    )
    embeddings, _ = encoder(sentences)
    return embeddings

# THE PATHS
PATH_FP16 = "/teamspace/studios/this_studio/kimodo/models/KIMODO-Meta3_llm2vec-SUPER_BAKED"
PATH_INT8 = "/teamspace/studios/this_studio/kimodo/models/KIMODO-Meta3_llm2vec-OpenVINO"

TEST_PROMPTS = [
    "a person jumping high in the air",
    "someone waving their hands slowly",
    "a person sitting down on a chair"
]

try:
    # 1. Get Baseline (High Quality GPU)
    emb_fp16 = get_emb_from_wrapper(PATH_FP16, "BAKED FP16 (GPU)", TEST_PROMPTS)
    
    # 2. Get INT8 (OpenVINO CPU)
    emb_int8 = get_emb_from_wrapper(PATH_INT8, "OpenVINO INT8 (CPU)", TEST_PROMPTS)

    print("\n" + "="*50)
    print("📊 FINAL SEMANTIC FIDELITY REPORT")
    print("="*50)
    
    for i, prompt in enumerate(TEST_PROMPTS):
        # FIX: Squeeze to ensure we have a flat 4096 vector
        v1 = emb_fp16[i].detach().cpu().reshape(1, -1)
        v2 = emb_int8[i].detach().cpu().reshape(1, -1)
        
        # Calculate Similarity
        sim = torch.nn.functional.cosine_similarity(v1, v2).item()
        mae = torch.mean(torch.abs(v1 - v2)).item()
        
        print(f"\nPrompt: '{prompt}'")
        status = "✅ PERFECT" if sim > 0.99 else "⚠️ MINOR LOSS" if sim > 0.98 else "❌ BRAIN DEAD"
        print(f"Result: {status} (Similarity: {sim:.6f})")
        print(f"MAE:    {mae:.6f}")
        print(f"FP16 (first 3): {v1[0,:3].tolist()}")
        print(f"INT8 (first 3): {v2[0,:3].tolist()}")

except Exception as e:
    print(f"\n❌ ERROR during evaluation: {e}")
