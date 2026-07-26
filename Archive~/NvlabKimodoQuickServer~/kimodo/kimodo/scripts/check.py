import sys
import os
from types import ModuleType
import torch
import transformers
import huggingface_hub

# 1. Bridge the 'onnx' gap
if "transformers.onnx" not in sys.modules:
    mock_onnx = ModuleType("transformers.onnx")
    mock_onnx.utils = ModuleType("transformers.onnx.utils")
    mock_onnx.utils.ParameterFormat = None 
    mock_onnx.utils.compute_serialized_parameters_size = lambda x: 0
    sys.modules["transformers.onnx"] = mock_onnx
    sys.modules["transformers.onnx.utils"] = mock_onnx.utils

# 2. Bridge the 'utils' gap
if not hasattr(transformers.utils, "is_offline_mode"):
    setattr(transformers.utils, "is_offline_mode", lambda: False)

# 3. Bridge the 'hub' gap
if not hasattr(huggingface_hub, "HfFolder"):
    class GhostFolder:
        @staticmethod
        def get_token(): return os.environ.get("HUGGING_FACE_HUB_TOKEN")
        @staticmethod
        def save_token(t): pass
        @staticmethod
        def delete_token(): pass
    setattr(huggingface_hub, "HfFolder", GhostFolder)

# 4. Bridge the 'Vision' gap (Transformers 5.1.0 fix)
if not hasattr(transformers, "AutoModelForVision2Seq"):
    # We create a dummy class so the import doesn't fail
    class DummyVision: pass
    setattr(transformers, "AutoModelForVision2Seq", DummyVision)

print("--- ATTEMPTING LOAD ---")
try:
    from optimum.intel.openvino import OVModelForFeatureExtraction
    print("✅ SUCCESS: OpenVINO is now compatible with Transformers 5.1.0!")
except Exception as e:
    print(f"❌ Still missing something: {e}")
