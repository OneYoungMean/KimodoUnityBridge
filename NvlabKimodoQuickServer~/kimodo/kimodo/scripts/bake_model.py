# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0
"""Script to permanently merge (bake) multiple LLM2Vec LoRA adapters into the base LLM."""

import argparse
import os
import glob
import torch
from peft import PeftModel
from kimodo.model.llm2vec import LLM2Vec

def main():
    parser = argparse.ArgumentParser(description="Double-Bake LLM2Vec MNTP and Supervised adapters into Meta-Llama-3.")
    parser.add_argument(
        "--base_model",
        type=str,
        default="meta-llama/Meta-Llama-3-8B-Instruct",
        help="The 'Grandfather' model (e.g. Meta-Llama-3-8B-Instruct).",
    )
    parser.add_argument(
        "--father_model",
        type=str,
        default="McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp",
        help="The 'Father' model (MNTP adapter).",
    )
    parser.add_argument(
        "--son_model",
        type=str,
        default="McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised",
        help="The 'Son' model (Supervised adapter).",
    )
    parser.add_argument(
        "--output_dir",
        type=str,
        required=True,
        help="Directory to save the final baked brain.",
    )
    parser.add_argument(
        "--dtype",
        type=str,
        default="float16",
        help="Data type to use (float16, bfloat16, float32).",
    )
    parser.add_argument(
        "--device",
        type=str,
        default="cuda" if torch.cuda.is_available() else "cpu",
        help="Device to use for merging.",
    )

    args = parser.parse_args()
    torch_dtype = getattr(torch, args.dtype)
    cache_dir = os.environ.get("HUGGINGFACE_CACHE_DIR")

    print("\n" + "="*50)
    print("🚀 STARTING THE MASTER DOUBLE-BAKE 🚀")
    print("="*50)

    print(f"\n[STEP 1 & 2/5] FETCHING FOUNDATION & FUSING 'FATHER' (MNTP Knowledge)...")
    print(f"--> Base: {args.base_model}")
    print(f"--> Father: {args.father_model}")
    
    # This automatically handles bidirectional conversion and the first merge
    llm2vec = LLM2Vec.from_pretrained(
        base_model_name_or_path=args.base_model,
        peft_model_name_or_path=args.father_model,
        merge_peft=True,
        enable_bidirectional=True,
        torch_dtype=torch_dtype,
        cache_dir=cache_dir
    )
    # Move to device
    llm2vec.model = llm2vec.model.to(args.device)

    print(f"\n[STEP 3/5] FUSING THE 'SON' (Action Knowledge)...")
    print(f"--> Son: {args.son_model}")
    
    # Apply the second adapter to the already bidirectional/merged base
    llm2vec.model = PeftModel.from_pretrained(llm2vec.model, args.son_model, cache_dir=cache_dir)
    print("--> Merging Son weights...")
    llm2vec.model = llm2vec.model.merge_and_unload()

    print(f"\n[STEP 4/5] SAVING INTEGRATED BRAIN TO DISK...")
    print(f"--> Target: {args.output_dir}")
    os.makedirs(args.output_dir, exist_ok=True)
    
    # Save the final merged model and config
    llm2vec.save(args.output_dir)

    print("\n[STEP 5/5] THE FINAL VERIFICATION...")
    model_files = glob.glob(os.path.join(args.output_dir, "*.safetensors"))
    if not model_files:
        model_files = glob.glob(os.path.join(args.output_dir, "*.bin"))
        
    total_size_gb = sum(os.path.getsize(f) for f in model_files) / (1024**3)
    
    if total_size_gb > 14:
        print(f"✅ SUCCESS! Found {total_size_gb:.1f} GB of model weights.")
        print("--> This confirms the entire 'Family Tree' is baked inside.")
    else:
        print(f"⚠️ WARNING: Output size is only {total_size_gb:.1f} GB.")
        print("--> Something went wrong! The final file is too small.")

    print("\nMission Accomplished! Your baked brain is ready.")
    print("-" * 50)
    print("To use this baked model in Kimodo, run these commands:")
    print(f"1. export TEXT_ENCODERS_DIR=\"{os.path.abspath(args.output_dir)}\"")
    print("2. python -m kimodo.demo")
    print("-" * 50)


if __name__ == "__main__":
    main()
