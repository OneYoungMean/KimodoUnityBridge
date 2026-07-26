import os
import sys
import builtins
from types import ModuleType
import huggingface_hub
import transformers
import torch

# --- THE UNIVERSAL BRIDGE ---
class GhostModel: pass
orig_import = builtins.__import__
def hooked_import(name, globals=None, locals=None, fromlist=(), level=0):
    if name == 'transformers' and fromlist:
        mod = orig_import(name, globals, locals, fromlist, level)
        for item in ['AutoModelForVision2Seq', 'AutoModelForSpeechSeq2Seq']:
            if item in fromlist and not hasattr(mod, item):
                setattr(mod, item, GhostModel)
        return mod
    return orig_import(name, globals, locals, fromlist, level)
builtins.__import__ = hooked_import

import transformers.masking_utils
original_prepare_padding_mask = transformers.masking_utils.prepare_padding_mask
def patched_prepare_padding_mask(*args, **kwargs):
    kwargs.pop('_slice', None)
    return original_prepare_padding_mask(*args, **kwargs)
transformers.masking_utils.prepare_padding_mask = patched_prepare_padding_mask
# ----------------------------

# LAUNCH THE EXPORT FROM THE HIGH-QUALITY SOURCE
from optimum.commands.optimum_cli import main
sys.argv = [
    'optimum-cli', 'export', 'openvino', 
    '--model', '/teamspace/studios/this_studio/kimodo/models/KIMODO-Meta3_llm2vec-SUPER_BAKED', 
    '--task', 'feature-extraction', 
    '--weight-format', 'int8', 
    './models/KIMODO-Meta3_llm2vec-OpenVINO_INT8'
]
print("🚀 Launching High-Precision INT8 Export from FP16 Source...")
main()
