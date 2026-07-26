import openvino as ov
from transformers import AutoTokenizer
import numpy as np
import torch

model_path = '/teamspace/studios/this_studio/kimodo/models/KIMODO-Meta3_llm2vec-OpenVINO_INT8'
tokenizer = AutoTokenizer.from_pretrained(model_path)
core = ov.Core()
compiled_model = core.compile_model(model_path + '/openvino_model.xml', 'CPU')

print("\n" + "="*50)
print("🔍 OPENVINO PORT DIAGNOSTIC")
print("="*50)

# 1. Check Output Ports
for i, out in enumerate(compiled_model.outputs):
    print(f"PORT {i}: Name='{out.get_any_name()}' | Shape={out.get_partial_shape()}")

# 2. Test Inference (Check if outputs actually change)
prompts = ["a person jumping", "someone waving"]
request = compiled_model.create_infer_request()

for p in prompts:
    print(f"\n--- Testing Prompt: '{p}' ---")
    wrapped = f"<|start_header_id|>user<|end_header_id|>\n\n!@#$%^&*(){p}<|eot_id|>"
    inputs = tokenizer([wrapped], return_tensors="np")
    pos_ids = np.arange(inputs["input_ids"].shape[1], dtype=np.int64).reshape(1, -1)
    
    # Bind tensors
    request.set_tensor("input_ids", ov.Tensor(inputs["input_ids"]))
    request.set_tensor("attention_mask", ov.Tensor(inputs["attention_mask"]))
    request.set_tensor("position_ids", ov.Tensor(pos_ids))
    
    request.infer()
    
    # Print the first few values of EVERY output port
    for i in range(len(compiled_model.outputs)):
        data = request.get_output_tensor(i).data
        print(f"  [Port {i}] First 3 values: {data[0,0,:3].tolist()}")
